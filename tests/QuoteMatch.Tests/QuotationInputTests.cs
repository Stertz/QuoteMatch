using System.Text.Json;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Tests;

public sealed class QuotationInputTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.Input.Tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ErpBrandIdentity Brand = new("synthetic-catalog", "brand-1", "Synthetic manufacturer");
    private static PurchaseOrderSnapshot Order(string id = "order-1") => new("synthetic-catalog",
        new(id, "42", "distributor-7", "Synthetic buying supplier", "branch-1", "open", new(2026, 10, 27), false), "revision-1", Now,
        [new("line-1", "000.01", "Synthetic wire", "RL", "M", 6, 100, 250.123456789m, 1500.740740734m, 2, false)
        { SimilarityCode = "wire-family", Brand = Brand },
        new("line-2", "000.02", "Synthetic wire", "RL", "M", 4, 100, 260, 1040, 0, false) { SimilarityCode = "wire-family", Brand = Brand }]);
    private static QuotationInputs Inputs(Guid quotationId) => QuotationInputs.Legacy(quotationId) with
    {
        PriceReference = QuotationPriceReference.LinkedOrder,
        GroupingVersion = ProductGroupingVersion.ProductNameFallback,
        Orders = [Order()],
        BuyingSuppliers = [new(Brand, new("synthetic-catalog", "distributor-7", "Synthetic buying supplier"), "history-order-9", "history-revision", "branch-1", new(2026, 10, 26), Now)]
    };
    private static GroupDemand Demand() => new("wire-family", "M", 1000)
    {
        Origin = DemandOrigin.LinkedOrder,
        Sources = [new("synthetic-catalog", "order-1", "revision-1", "line-1"), new("synthetic-catalog", "order-1", "revision-1", "line-2")]
    };
    private async Task<(SqliteImportStore Store, Quotation Quotation)> CreateAsync()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        Quotation quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic study", "", Now, Now, QuotationState.Draft, 0), Token);
        return (store, quotation);
    }

    [Fact]
    public async Task InputRefresh_AppendsOfflineHistoryAndKeepsOrderPricesSeparateFromLastPurchase()
    {
        var (store, quotation) = await CreateAsync();
        var legacy = await store.GetQuotationInputsAsync(quotation.Id, cancellationToken: Token);
        Assert.Equal(0, legacy.Version); Assert.Equal(QuotationPriceReference.LastPurchase, legacy.PriceReference);
        QuotationInputs first = await store.SaveQuotationInputsAsync(Inputs(quotation.Id), quotation.Revision, Token);
        AnalysisInput input = await store.GetAnalysisInputAsync(quotation.Id, Token);
        Assert.Equal(quotation.Revision + 1, input.Quotation.Revision);
        Assert.Equal(1, input.State.QuotationInputsVersion);
        AnalysisState state = await store.SaveAnalysisAsync(input.State with { Demands = [Demand()] }, Token);
        QuotationInputs second = await store.SaveQuotationInputsAsync(first with
        {
            CreatedAtUtc = DateTime.UnixEpoch,
            PriceReference = QuotationPriceReference.LastPurchase,
            Orders = [Order() with { Revision = "revision-2", Lines = [Order().Lines[0] with { UnitPrice = 300.000000001m }] }]
        }, input.Quotation.Revision, Token);
        Assert.Equal(2, second.Version); Assert.Equal(DateTimeKind.Utc, second.CreatedAtUtc.Kind);
        Assert.NotEqual(DateTime.UnixEpoch, second.CreatedAtUtc);
        SqliteImportStore restarted = new(_directory); await restarted.InitializeAsync(Token);
        var previous = await restarted.GetQuotationInputsAsync(quotation.Id, 1, Token);
        Assert.Equal(QuotationPriceReference.LinkedOrder, previous.PriceReference);
        Assert.Equal(250.123456789m, previous.Orders[0].Lines[0].UnitPrice);
        Assert.Equal(1500.740740734m, previous.Orders[0].Lines[0].Total);
        Assert.Equal("brand-1", previous.Orders[0].Lines[0].Brand!.Id);
        Assert.Equal("distributor-7", previous.Orders[0].Header.SupplierId);
        Assert.Equal("history-order-9", previous.BuyingSuppliers[0].SourceOrderId);
        Assert.Equal("distributor-7", previous.BuyingSuppliers[0].Supplier.Id);
        Assert.Equal(QuotationPriceReference.LastPurchase, (await restarted.GetQuotationInputsAsync(quotation.Id, cancellationToken: Token)).PriceReference);
        Assert.Equal(300.000000001m, (await restarted.GetQuotationInputsAsync(quotation.Id, 2, Token)).Orders[0].Lines[0].UnitPrice);
        var loaded = await restarted.GetAnalysisInputAsync(quotation.Id, Token);
        Assert.Equal(2, loaded.Inputs.Version); Assert.Equal(1, loaded.State.QuotationInputsVersion);
        Assert.Equal(state.Version, loaded.State.Version); Assert.Equal(1000, loaded.State.Demands[0].Quantity);
        Assert.Equal("revision-1", loaded.State.Demands[0].Sources[0].OrderRevision);
        Assert.Equal(0, (await restarted.GetQuotationInputsAsync(quotation.Id, 0, Token)).Version);
        Assert.Equal(QuotationError.Conflict, (await Assert.ThrowsAsync<QuotationException>(() => restarted.GetQuotationInputsAsync(quotation.Id, 3, Token))).Code);
        Assert.Equal(QuotationError.Conflict, (await Assert.ThrowsAsync<QuotationException>(() => restarted.GetQuotationInputsAsync(Guid.NewGuid(), cancellationToken: Token))).Code);
    }

    [Fact]
    public async Task InputChanges_RejectStaleAnalysisPreparationAndQuotationWrites()
    {
        var (store, quotation) = await CreateAsync();
        var input = await store.GetAnalysisInputAsync(quotation.Id, Token);
        var analysis = await store.SaveAnalysisAsync(input.State, Token);
        var first = await store.SaveQuotationInputsAsync(Inputs(quotation.Id), quotation.Revision, Token);
        Assert.Equal(QuotationError.Conflict, (await Assert.ThrowsAsync<QuotationException>(() => store.SaveQuotationAsync(quotation, Token))).Code);
        Assert.Equal(QuotationError.Conflict, (await Assert.ThrowsAsync<QuotationException>(() => store.SaveQuotationInputsAsync(first, quotation.Revision, Token))).Code);
        input = await store.GetAnalysisInputAsync(quotation.Id, Token);
        Assert.Equal(QuotationError.Conflict, (await Assert.ThrowsAsync<QuotationException>(() => store.SaveQuotationInputsAsync(first with { Version = 0 }, input.Quotation.Revision, Token))).Code);
        Assert.Equal(AnalysisError.Conflict, (await Assert.ThrowsAsync<AnalysisException>(() => store.SaveAnalysisAsync(analysis, Token))).Code);
        Assert.Equal(AnalysisError.Conflict, (await Assert.ThrowsAsync<AnalysisException>(() => store.SaveAnalysisAsync(analysis with { InputsRevision = input.Quotation.Revision }, Token))).Code);
        Assert.Equal(PurchaseError.Conflict, (await Assert.ThrowsAsync<PurchaseException>(() => store.PreparePurchaseAsync(new(quotation.Id, input.Quotation.Revision, analysis.Version, 0, []), Token))).Code);
        var adopted = await store.SaveAnalysisAsync(analysis with { InputsRevision = input.Quotation.Revision, QuotationInputsVersion = first.Version }, Token);
        var preparation = await store.PreparePurchaseAsync(new(quotation.Id, input.Quotation.Revision, adopted.Version, 0, []), Token);
        Assert.Equal(1, preparation.QuotationInputsVersion);
    }

    [Fact]
    public async Task FinalizedQuotation_RejectsInputChangesUntilExplicitReopening()
    {
        var (store, quotation) = await CreateAsync();
        SavedProposal proposal = new(Guid.NewGuid(), Now, "synthetic.pdf", "synthetic-hash", new PdfTemplate { SupplierName = "Synthetic" }, new([], "Synthetic"), new([], [], null, null), []) { QuotationId = quotation.Id };
        await store.SaveProposalAsync(proposal, [1, 2, 3], Token);
        quotation = (await store.GetAnalysisInputAsync(quotation.Id, Token)).Quotation;
        quotation = await store.SaveQuotationAsync(quotation with { State = QuotationState.Finalized }, Token);
        Assert.Equal(QuotationError.ReadOnly, (await Assert.ThrowsAsync<QuotationException>(() => store.SaveQuotationInputsAsync(Inputs(quotation.Id), quotation.Revision, Token))).Code);
        quotation = await store.SaveQuotationAsync(quotation with { State = QuotationState.Draft }, Token);
        await store.SaveQuotationInputsAsync(Inputs(quotation.Id), quotation.Revision, Token);
        quotation = (await store.GetAnalysisInputAsync(quotation.Id, Token)).Quotation;
        await store.DeleteQuotationAsync(quotation.Id, quotation.Revision, Token);
        Assert.Empty(await store.GetQuotationsAsync(Token));
    }

    [Fact]
    public void InvalidInputSettings_RejectMissingReferenceDuplicateOrdersAndMixedCatalogs()
    {
        var inputs = Inputs(Guid.NewGuid());
        QuotationInputRules.Validate(inputs);
        foreach (var invalid in new[]
        {
            inputs with { PriceReference = (QuotationPriceReference)9 }, inputs with { GroupingVersion = (ProductGroupingVersion)9 },
            inputs with { Orders = [] }, inputs with { Orders = [Order(), Order()] },
            inputs with { Orders = [Order() with { Lines = [Order().Lines[0] with { Quantity = -1 }] }] },
            inputs with { Orders = [Order(), Order("order-2") with { CatalogIdentity = "other" }] },
            inputs with { Orders = [Order() with { FetchedAtUtc = DateTime.SpecifyKind(Now, DateTimeKind.Unspecified) }] },
            inputs with { BuyingSuppliers = [inputs.BuyingSuppliers[0] with { Supplier = new("other", "supplier", "Synthetic") }] },
            inputs with { BuyingSuppliers = [inputs.BuyingSuppliers[0], inputs.BuyingSuppliers[0]] },
            inputs with { Orders = [Order() with { Lines = [Order().Lines[0] with { Brand = Brand with { CatalogIdentity = "other" } }] }] }
        }) Assert.Equal(QuotationError.InvalidInputs, Assert.Throws<QuotationException>(() => QuotationInputRules.Validate(invalid)).Code);
    }

    [Fact]
    public void DemandProvenance_PreservesCompleteOrderedQuantityIncludingReceiptsAndRejectsInventedInputs()
    {
        var inputs = Inputs(Guid.NewGuid());
        DemandProvenanceRules.Validate([Demand(), new("manual", "M", 10)], inputs);
        foreach (var invalid in new[]
        {
            Demand() with { Quantity = 800 }, Demand() with { Origin = DemandOrigin.Manual },
            Demand() with { Origin = (DemandOrigin)9 }, Demand() with { Sources = [] },
            Demand() with { BaseUnit = "KG" }, Demand() with { Sources = [Demand().Sources[0], Demand().Sources[0]] },
            Demand() with { Sources = [Demand().Sources[0] with { OrderRevision = "stale" }] },
            Demand() with { Sources = [Demand().Sources[0] with { CatalogIdentity = "other" }] },
            Demand() with { Sources = [Demand().Sources[0] with { LineId = "missing" }] }
        }) Assert.Equal(AnalysisError.InvalidDemand, Assert.Throws<AnalysisException>(() => DemandProvenanceRules.Validate([invalid], inputs)).Code);
        Assert.Throws<AnalysisException>(() => DemandProvenanceRules.Validate([Demand(), Demand() with { GroupKey = "another" }], inputs));
        Assert.Throws<AnalysisException>(() => DemandProvenanceRules.Validate([Demand()], inputs with
        { Orders = [Order() with { Lines = [Order().Lines[0] with { IsCancelled = true }, Order().Lines[1]] }] }));
        Assert.Throws<AnalysisException>(() => DemandProvenanceRules.Validate([Demand()], inputs with
        { Orders = [Order() with { Lines = [Order().Lines[0] with { Quantity = decimal.MaxValue }, Order().Lines[1]] }] }));
    }

    [Fact]
    public async Task Repository_RejectsDemandProvenanceThatDoesNotMatchThePinnedSnapshot()
    {
        var (store, quotation) = await CreateAsync();
        await store.SaveQuotationInputsAsync(Inputs(quotation.Id), quotation.Revision, Token);
        var input = await store.GetAnalysisInputAsync(quotation.Id, Token);
        Assert.Equal(AnalysisError.InvalidDemand, (await Assert.ThrowsAsync<AnalysisException>(() => store.SaveAnalysisAsync(input.State with { Demands = [Demand() with { Quantity = 800 }] }, Token))).Code);
        Assert.Equal(0, (await store.GetAnalysisInputAsync(quotation.Id, Token)).State.Version);
        var saved = await store.SaveAnalysisAsync(input.State with { Demands = [Demand()] }, Token);
        Assert.Equal(1, saved.Version);
    }

    [Fact]
    public void VersionedGrouping_KeepsLegacyProductKeysAndSeparatesNameFallbackByUnitAndCatalog()
    {
        ErpProductCandidate first = new("001.01", "Synthetic wire", "", false, "brand-1", "Synthetic", "000-01", 100, "RL", null, null, "M", null, "synthetic-catalog", Now);
        var second = first with { ProductCode = "001.02", SupplierId = "brand-2" };
        Assert.Equal("erp:synthetic-catalog:PRODUCT:001.01", ErpProductIdentity.GroupKey(first));
        Assert.NotEqual(ErpProductIdentity.GroupKey(first), ErpProductIdentity.GroupKey(second));
        Assert.Equal(ErpProductIdentity.GroupKey(first, ProductGroupingVersion.ProductNameFallback), ErpProductIdentity.GroupKey(second, ProductGroupingVersion.ProductNameFallback));
        foreach (var different in new[] { second with { Name = "Other wire" }, second with { BaseUnit = "KG" }, second with { CatalogIdentity = "other" } })
            Assert.NotEqual(ErpProductIdentity.GroupKey(first, ProductGroupingVersion.ProductNameFallback), ErpProductIdentity.GroupKey(different, ProductGroupingVersion.ProductNameFallback));
        Assert.Equal(ErpProductIdentity.GroupKey(first with { SimilarityCode = "wire" }), ErpProductIdentity.GroupKey(first with { SimilarityCode = "wire" }, ProductGroupingVersion.ProductNameFallback));
        Assert.Throws<ArgumentOutOfRangeException>(() => ErpProductIdentity.GroupKey(first, (ProductGroupingVersion)99));
        Assert.True(ErpProductIdentity.MatchesSupplierCode(first, "000-01"));
        Assert.False(ErpProductIdentity.MatchesSupplierCode(first, "00001"));
        ReviewedItem item = new(Guid.NewGuid(), "000-01", "Synthetic PDF description", 1, 200, 200, "RL", false, "");
        ItemLink link = new(Guid.NewGuid(), item.CaptureId, ErpProductIdentity.GroupKey(first, ProductGroupingVersion.ProductNameFallback), first.Name, "M", 100, first, Now)
        { GroupingVersion = ProductGroupingVersion.ProductNameFallback };
        ImportedQuotationAnalysis.ValidateLink(link, item);
        Assert.Throws<AnalysisException>(() => ImportedQuotationAnalysis.ValidateLink(link with { GroupingVersion = ProductGroupingVersion.ProductCodeFallback }, item));
    }

    [Fact]
    public void LegacyJson_UsesManualDemandAndLastPurchaseWithoutInferringBrandOrBuyingSupplier()
    {
        const string json = """{"GroupKey":"legacy","BaseUnit":"M","Quantity":123.4567890123456789}""";
        var demand = JsonSerializer.Deserialize<GroupDemand>(json, SqliteImportStore.JsonOptions)!;
        Assert.Equal(DemandOrigin.Manual, demand.Origin); Assert.Empty(demand.Sources);
        Assert.Equal(123.4567890123456789m, demand.Quantity);
        var inputs = QuotationInputs.Legacy(Guid.NewGuid());
        Assert.Equal(ProductGroupingVersion.ProductCodeFallback, inputs.GroupingVersion);
        Assert.Equal(QuotationPriceReference.LastPurchase, inputs.PriceReference);
        Assert.Empty(inputs.Orders); Assert.Empty(inputs.BuyingSuppliers);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
