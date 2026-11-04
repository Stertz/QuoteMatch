using QuoteMatch.Application.Analysis;
using QuoteMatch.Application.Integration;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Tests;

public sealed partial class ProductReviewTests
{
    [Fact]
    public async Task DocumentDemand_SelectsOneDocumentAndRecalculatesOnlyItsConversion()
    {
        var (store, input, rows) = await CreateAsync(true);
        var row = rows[0];
        var source = new DocumentDemandSource(row.ProposalId, row.CaptureId, input.Proposals.Single(p => p.Id == row.ProposalId).SourceSha256, 100);
        var saved = await new ProductReviewService(store, store).ConfirmAsync(input, rows, false, "catalog", Token,
            new() { Demands = [new(row.ProposalId, row.CaptureId, 200, DemandOrigin.Document, source)] });
        var demand = Assert.Single(saved.State.Demands);
        Assert.Equal(200m, demand.Quantity); Assert.Equal(source, demand.DocumentSource); Assert.Empty(demand.Sources);
        var reloaded = await store.GetAnalysisInputAsync(input.Quotation.Id, Token);
        var stored = Assert.Single(reloaded.State.Demands);
        Assert.Equal(demand.Quantity, stored.Quantity); Assert.Equal(demand.DocumentSource, stored.DocumentSource); Assert.Equal(demand.Origin, stored.Origin);
        var updated = ProductReviewRules.Apply(reloaded, [row with { ConversionFactor = 50 }], Now, true);
        Assert.Equal(100m, Assert.Single(updated.Demands).Quantity);
        Assert.Equal(50m, updated.Demands[0].DocumentSource!.ConversionFactor);
        Assert.Equal(200m, demand.Quantity);
    }

    [Fact]
    public async Task ExplicitDemand_OverridesAnOldUnitOnlyWhenTheUserReviewsIt()
    {
        var (_, input, rows) = await CreateAsync();
        var row = rows[0];
        var old = new ItemLink(row.ProposalId, row.CaptureId, "manual:old", "Synthetic", "KG", 1, null, Now);
        input = input with { State = input.State with { Links = [old], Demands = [new("manual:old", "KG", 12)] } };
        Assert.Equal(AnalysisError.DemandReviewRequired, Assert.Throws<AnalysisException>(() => ProductReviewRules.Apply(input, [row], Now, true)).Code);
        var reviewed = ProductReviewRules.Apply(input, [row], Now, true, [new(row.ProposalId, row.CaptureId, 250)]);
        Assert.Equal("MT", Assert.Single(reviewed.Demands).BaseUnit); Assert.Equal(250m, reviewed.Demands[0].Quantity);
    }

    [Fact]
    public async Task EqualQuantitiesFromDifferentDocuments_RequireAnExplicitSourceWhenGroupsMerge()
    {
        var (_, input, rows) = await CreateAsync(true);
        var first = rows[0]; var second = rows[2];
        var one = ProductReviewRules.CreateLink(first, input.Proposals.Single(p => p.Id == first.ProposalId),
            input.Proposals.Single(p => p.Id == first.ProposalId).Review[0], input.Inputs.GroupingVersion, Now);
        var two = ProductReviewRules.CreateLink(second, input.Proposals.Single(p => p.Id == second.ProposalId),
            input.Proposals.Single(p => p.Id == second.ProposalId).Review[0], input.Inputs.GroupingVersion, Now);
        input = input with
        {
            State = input.State with
            {
                Links = [one with { GroupKey = "old:one" }, two with { GroupKey = "old:two" }],
                Demands =
            [new("old:one", "MT", 200) { Origin = DemandOrigin.Document, DocumentSource = new(first.ProposalId, first.CaptureId, "synthetic-hash", 100) },
             new("old:two", "MT", 200) { Origin = DemandOrigin.Document, DocumentSource = new(second.ProposalId, second.CaptureId, "synthetic-hash", 100) }]
            }
        };
        Assert.Equal(AnalysisError.DemandReviewRequired, Assert.Throws<AnalysisException>(() => ProductReviewRules.Apply(input, [first, second], Now, true)).Code);
        var chosen = input.State.Demands[0].DocumentSource;
        var state = ProductReviewRules.Apply(input, [first, second], Now, true, [new(first.ProposalId, first.CaptureId, 200, DemandOrigin.Document, chosen)]);
        Assert.Equal(chosen, Assert.Single(state.Demands).DocumentSource);
    }

    [Fact]
    public async Task ReferenceOnlyReview_AndExplicitDemandReplacement_SaveVersionedInputsAtomically()
    {
        var (store, input, rows) = await CreateAsync();
        var result = await new ProductReviewService(store, store).ConfirmAsync(input, rows, false, "catalog", Token,
            new() { Demands = [new(rows[1].ProposalId, rows[1].CaptureId, 4)] });
        var row = rows[1];
        var order = new QuoteMatch.Domain.Purchasing.PurchaseOrderSnapshot("catalog", new("order", "42", "supplier", "Synthetic", "branch", "open", new(2026, 1, 1), true), "v1", Now,
            [new("line", row.Product.ProductCode, row.Product.Name, "UN", "UN", 9, 1, 8, 72, 0, false)]);
        var service = new ProductReviewService(store, store);
        result = await service.ConfirmAsync(result.Input, [], false, "", Token, new() { Inputs = result.Input.Inputs with { Orders = [order] } });
        Assert.Equal(1, result.Input.Inputs.Version); Assert.Equal(4m, Assert.Single(result.State.Demands).Quantity);
        var before = result.Input;
        result = await service.ConfirmAsync(result.Input, [], false, "", Token, new() { ReplaceManualDemand = true });
        Assert.Equal(9m, Assert.Single(result.State.Demands).Quantity); Assert.Equal(DemandOrigin.LinkedOrder, result.State.Demands[0].Origin);
        Assert.Equal(before.Quotation.Revision, result.Input.Quotation.Revision);
        var reloaded = await store.GetAnalysisInputAsync(input.Quotation.Id, Token);
        Assert.Equal(result.State.Version, reloaded.State.Version); Assert.Equal(1, reloaded.Inputs.Version);
    }

    [Fact]
    public async Task AutomaticMappings_ReusesConfirmedFactorsForPdfAndSpreadsheetWithoutRemoteQueriesOrMapRevisions()
    {
        var (store, input, rows) = await CreateAsync();
        foreach (var row in rows) await store.SaveMappingAsync(Mapping(input, row), Token);
        var automatic = new SavedProductMappingService(store, store, new OfflineCatalog());
        foreach (var row in rows) Assert.Equal(1, await automatic.ApplyAsync(input.Quotation.Id, row.ProposalId, Token));
        var saved = await store.GetAnalysisInputAsync(input.Quotation.Id, Token);
        Assert.Equal(2, saved.State.Links.Count); Assert.Equal(100m, saved.State.Links.Single(l => l.ProposalId == rows[0].ProposalId).BaseUnitsPerQuotedUnit);
        foreach (var row in rows)
        {
            Assert.Equal(0, await automatic.ApplyAsync(input.Quotation.Id, row.ProposalId, Token));
            Assert.Equal(1, (await store.FindMappingAsync("catalog", Source(input, row), Token))!.Version);
        }
        Assert.Empty(saved.State.Demands);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("catalog")]
    [InlineData("namespace")]
    public async Task AutomaticMappings_LeavesUnapprovedOrUnrelatedMappingsPending(string invalid)
    {
        var (store, input, rows) = await CreateAsync(); var row = rows[0];
        var mapping = Mapping(input, row);
        mapping = invalid switch
        {
            "disabled" => mapping with { State = ProductMappingState.Disabled },
            "catalog" => mapping with { Product = mapping.Product with { CatalogIdentity = "another-catalog" } },
            _ => mapping with { Source = mapping.Source with { Supplier = "another-supplier" } }
        };
        await store.SaveMappingAsync(mapping, Token);
        Assert.Equal(0, await new SavedProductMappingService(store, store, new OfflineCatalog()).ApplyAsync(input.Quotation.Id, row.ProposalId, Token));
        Assert.Empty((await store.GetAnalysisInputAsync(input.Quotation.Id, Token)).State.Links);
    }

    [Fact]
    public async Task MappingDisabledDuringAutomaticReview_RollsBackLinksAndStagedReferenceInputs()
    {
        var (store, input, rows) = await CreateAsync();
        var mapping = await store.SaveMappingAsync(Mapping(input, rows[0]), Token);
        var state = ProductReviewRules.Apply(input, [rows[0]], Now);
        await store.SaveMappingAsync(mapping with { State = ProductMappingState.Disabled }, Token);
        var options = new ProductReviewOptions { RequiredMappings = [mapping], Inputs = input.Inputs with { GroupingVersion = ProductGroupingVersion.ProductNameFallback } };
        Assert.Equal(ProductMappingError.Conflict, (await Assert.ThrowsAsync<ProductMappingException>(() => store.SaveProductReviewAsync(state, [], Token, options))).Code);
        var reloaded = await store.GetAnalysisInputAsync(input.Quotation.Id, Token);
        Assert.Empty(reloaded.State.Links); Assert.Equal(input.Quotation.Revision, reloaded.Quotation.Revision); Assert.Equal(input.Inputs.Version, reloaded.Inputs.Version);
    }

    private sealed class OfflineCatalog : IErpProductSearch
    {
        public Task<string> GetCatalogIdentityAsync(CancellationToken token) => Task.FromResult("catalog");
        public Task<IReadOnlyList<ErpProductCandidate>> FindByProductCodeAsync(string code, CancellationToken token) => throw new InvalidOperationException("Automatic mapping must not query ERP products.");
        public Task<IReadOnlyList<ErpProductCandidate>> SearchByNameAsync(string name, CancellationToken token) => throw new InvalidOperationException("Automatic mapping must not search descriptions.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticMappings_UsesTheExistingCompositeKeyAndTrimmedUnitContract(bool paddedUnit)
    {
        var (store, input, rows) = await CreateAsync(); var row = rows[0]; var mapping = Mapping(input, row);
        mapping = paddedUnit ? mapping with { BaseUnit = " MT " }
            : mapping with { Source = mapping.Source with { Supplier = "  SYNTHETIC   SUPPLIER  ", Description = "SYNTHETIC PRODUCT", Unit = "rl" } };
        await store.SaveMappingAsync(mapping, Token);
        Assert.Equal(1, await new SavedProductMappingService(store, store, new OfflineCatalog()).ApplyAsync(input.Quotation.Id, row.ProposalId, Token));
        var saved = await store.GetAnalysisInputAsync(input.Quotation.Id, Token);
        Assert.Equal("MT", Assert.Single(saved.State.Links).BaseUnit);
        Assert.Equal(100m, saved.State.Links[0].BaseUnitsPerQuotedUnit);
    }
}
