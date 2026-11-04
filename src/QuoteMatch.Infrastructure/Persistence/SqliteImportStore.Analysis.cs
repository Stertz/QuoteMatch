using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuoteMatch.Application.Analysis;
using QuoteMatch.Application.Integration;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;
namespace QuoteMatch.Infrastructure.Persistence;

public sealed partial class SqliteImportStore : IQuotationAnalysisRepository, IErpConfigurationRepository
{
    public string DataDirectory => Path.GetFullPath(directory);
    public Task<AnalysisInput> GetAnalysisInputAsync(Guid quotationId, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await using ImportDbContext context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        AnalysisInput input = await ReadAnalysisInputAsync(context, quotationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return input;
    }, cancellationToken);
    private static async Task<AnalysisInput> ReadAnalysisInputAsync(ImportDbContext context, Guid quotationId, CancellationToken cancellationToken)
    {
        QuotationRow quotation = await context.Quotations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == quotationId, cancellationToken) ?? throw new AnalysisException(AnalysisError.Conflict);
        string[] json = await context.Proposals.AsNoTracking().Where(row => row.QuotationId == quotationId).Select(row => row.DataJson).ToArrayAsync(cancellationToken);
        AnalysisRevisionRow? latest = await context.Analyses.AsNoTracking().Where(row => row.QuotationId == quotationId).OrderByDescending(row => row.Version).FirstOrDefaultAsync(cancellationToken);
        var proposals = json.Select(value => JsonSerializer.Deserialize<SavedProposal>(value, JsonOptions)! with { QuotationId = quotationId }).ToArray();
        QuotationInputs inputs = await ReadQuotationInputsAsync(context, quotationId, null, cancellationToken);
        AnalysisState state = latest is null ? new(quotationId, 0, quotation.Revision, DateTime.UtcNow, [], [])
        { QuotationInputsVersion = inputs.Version } : JsonSerializer.Deserialize<AnalysisState>(latest.DataJson, JsonOptions)!;
        return new AnalysisInput(ToQuotation(quotation), proposals, state) { Inputs = inputs };
    }
    public async Task<AnalysisState> SaveAnalysisAsync(AnalysisState state, CancellationToken cancellationToken = default) => (await SaveProductReviewAsync(state, [], cancellationToken)).State;

    public Task<ProductReviewResult> SaveProductReviewAsync(AnalysisState state, IReadOnlyList<SupplierProductMapping> mappings, CancellationToken cancellationToken = default,
        ProductReviewOptions? options = null) => Task.Run(async () =>
    {
        await using ImportDbContext context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        QuotationRow? quotation = await context.Quotations.SingleOrDefaultAsync(row => row.Id == state.QuotationId, cancellationToken);
        if (quotation is null || quotation.Revision != state.InputsRevision) throw new AnalysisException(AnalysisError.Conflict);
        if (quotation.State != QuotationState.Draft) throw new AnalysisException(AnalysisError.ReadOnly);
        QuotationInputs inputs = await ReadQuotationInputsAsync(context, state.QuotationId, null, cancellationToken);
        if (state.QuotationInputsVersion != inputs.Version) throw new AnalysisException(AnalysisError.Conflict);
        int latest = await context.Analyses.Where(row => row.QuotationId == state.QuotationId).Select(row => (int?)row.Version).MaxAsync(cancellationToken) ?? 0;
        if (latest != state.Version) throw new AnalysisException(AnalysisError.Conflict);
        if (options?.Inputs is { } reviewedInputs)
        {
            if (reviewedInputs.QuotationId != state.QuotationId || reviewedInputs.Version != inputs.Version) throw new AnalysisException(AnalysisError.Conflict);
            QuotationInputRules.Validate(reviewedInputs);
            inputs = reviewedInputs with { Version = inputs.Version + 1, CreatedAtUtc = DateTime.UtcNow };
            quotation.Revision++; quotation.UpdatedAtUtc = inputs.CreatedAtUtc;
            context.QuotationInputs.Add(new() { QuotationId = inputs.QuotationId, Version = inputs.Version, DataJson = JsonSerializer.Serialize(inputs, JsonOptions) });
            state = state with { InputsRevision = quotation.Revision, QuotationInputsVersion = inputs.Version };
        }
        string[] json = await context.Proposals.AsNoTracking().Where(row => row.QuotationId == state.QuotationId).Select(row => row.DataJson).ToArrayAsync(cancellationToken);
        SavedProposal[] proposals = json.Select(value => JsonSerializer.Deserialize<SavedProposal>(value, JsonOptions)!).ToArray();
        var sources = proposals.SelectMany(proposal => proposal.Review.Select(item => (Proposal: proposal, Item: item)))
            .ToDictionary(source => (source.Proposal.Id, source.Item.CaptureId));
        if (state.Links.Select(link => (link.ProposalId, link.CaptureId)).Distinct().Count() != state.Links.Count) throw new AnalysisException(AnalysisError.InvalidLink);
        foreach (ItemLink link in state.Links)
        {
            if (!sources.TryGetValue((link.ProposalId, link.CaptureId), out var source) || source.Item.IsExcluded) throw new AnalysisException(AnalysisError.InvalidLink);
            ImportedQuotationAnalysis.ValidateLink(link, source.Item, source.Proposal.Template.CodeKind);
        }
        if (state.Demands.Select(demand => demand.GroupKey).Distinct().Count() != state.Demands.Count
            || state.Demands.Any(demand => demand.Quantity <= 0 || string.IsNullOrWhiteSpace(demand.BaseUnit))) throw new AnalysisException(AnalysisError.InvalidDemand);
        DemandProvenanceRules.Validate(state.Demands, inputs, proposals, state.Links);
        AnalysisState saved = state with { Version = latest + 1, CreatedAtUtc = DateTime.UtcNow };
        _ = ImportedQuotationAnalysis.Calculate(proposals, saved);
        if (mappings.DistinctBy(mapping => (mapping.Product.CatalogIdentity, mapping.Source.Key)).Count() != mappings.Count)
            throw new ProductMappingException(ProductMappingError.ConflictingConversion);
        var requiredMappings = options?.RequiredMappings ?? [];
        var mappedLinks = mappings.Count == 0 && requiredMappings.Count == 0 ? null : saved.Links.Select(link =>
        {
            var source = sources[(link.ProposalId, link.CaptureId)];
            return (Source: SupplierProductReference.From(source.Proposal, source.Item).Key, Product: link.Product, BaseUnit: link.BaseUnit.Trim().ToUpperInvariant(), Factor: link.BaseUnitsPerQuotedUnit);
        }).ToHashSet();
        foreach (var required in requiredMappings)
        {
            string key = required.Source.Key;
            var current = await context.ProductMappings.AsNoTracking().Where(row => row.CatalogIdentity == required.Product.CatalogIdentity && row.SourceKey == key)
                .OrderByDescending(row => row.Version).Select(row => row.DataJson).FirstOrDefaultAsync(cancellationToken);
            if (current is null || JsonSerializer.Deserialize<SupplierProductMapping>(current, JsonOptions) != required || required.State != ProductMappingState.Active
                || !mappedLinks!.Contains((required.Source.Key, required.Product, required.BaseUnit.Trim().ToUpperInvariant(), required.ConversionFactor)))
                throw new ProductMappingException(ProductMappingError.Conflict);
        }
        List<SupplierProductMapping> savedMappings = [];
        foreach (var mapping in mappings)
        {
            if (!mappedLinks!.Contains((mapping.Source.Key, mapping.Product, mapping.BaseUnit.Trim().ToUpperInvariant(), mapping.ConversionFactor)) || mapping.State != ProductMappingState.Active)
                throw new ProductMappingException(ProductMappingError.Invalid);
            savedMappings.Add(await AddMappingAsync(context, mapping, cancellationToken));
        }
        context.Analyses.Add(new() { QuotationId = saved.QuotationId, Version = saved.Version, InputsRevision = saved.InputsRevision, CreatedAtUtc = saved.CreatedAtUtc, DataJson = JsonSerializer.Serialize(saved, JsonOptions) });
        await context.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return new ProductReviewResult(new(ToQuotation(quotation), proposals, saved) { Inputs = inputs }, savedMappings);
    }, cancellationToken);
}
