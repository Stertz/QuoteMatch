using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Infrastructure.Persistence;

public sealed partial class SqliteImportStore : IPurchasePreparationRepository
{
    public Task<PurchaseInput> GetPurchaseInputAsync(Guid quotationId, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await using ImportDbContext context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        AnalysisInput analysis = await ReadAnalysisInputAsync(context, quotationId, cancellationToken);
        PurchasePreparationRow? latest = await context.Purchases.AsNoTracking().Where(row => row.QuotationId == quotationId)
            .OrderByDescending(row => row.Version).FirstOrDefaultAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PurchaseInput(analysis, latest is null ? null : JsonSerializer.Deserialize<PurchasePreparation>(latest.DataJson, JsonOptions));
    }, cancellationToken);

    public Task<PurchasePreparation> PreparePurchaseAsync(PreparePurchaseRequest request, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await using ImportDbContext context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await context.Quotations.AnyAsync(row => row.Id == request.QuotationId, cancellationToken)) throw new PurchaseException(PurchaseError.Conflict);
        AnalysisInput analysis = await ReadAnalysisInputAsync(context, request.QuotationId, cancellationToken);
        if (analysis.Quotation.Revision != request.InputsRevision || analysis.State.Version != request.AnalysisVersion
            || analysis.State.QuotationInputsVersion != analysis.Inputs.Version)
            throw new PurchaseException(PurchaseError.Conflict);
        if (analysis.Quotation.State != QuotationState.Draft) throw new PurchaseException(PurchaseError.ReadOnly);
        int latest = await context.Purchases.Where(row => row.QuotationId == request.QuotationId).Select(row => (int?)row.Version).MaxAsync(cancellationToken) ?? 0;
        if (latest != request.ExpectedVersion) throw new PurchaseException(PurchaseError.Conflict);
        PurchasePreparation saved = PurchasePreparationRules.Create(analysis, request.Selections, latest + 1, DateTime.UtcNow);
        context.Purchases.Add(new() { QuotationId = saved.QuotationId, Version = saved.Version, DataJson = JsonSerializer.Serialize(saved, JsonOptions) });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }, cancellationToken);
}
