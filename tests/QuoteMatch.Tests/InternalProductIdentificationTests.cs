using QuoteMatch.Application.Integration;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Tests;

public sealed class InternalProductIdentificationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.InternalCode.Tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private static ErpProductCandidate Product(string code) => new(code, "Synthetic " + code, "Shared similarity", false,
        "brand", "Synthetic brand", "DIFFERENT-SUPPLIER-CODE", 1, "un", new(2026, 1, 1), 10m, "un", "un", "catalog", Now)
    { Brand = new("catalog", "brand", "Synthetic brand") };
    private static SavedProposal Proposal(Guid quotationId, ProductCodeKind kind, params string[] codes) => new(Guid.NewGuid(), Now,
        "synthetic.xlsx", "synthetic-hash", new PdfTemplate { Name = "Synthetic", SupplierName = "Synthetic supplier", SourceFormat = ImportSourceFormat.Excel, CodeKind = kind },
        new([], "Synthetic"), new([], [], null, null), codes.Select(code => new ReviewedItem(Guid.NewGuid(), code, "Synthetic product", 2, 12m, 24m, "un", false, "")).ToArray())
    { QuotationId = quotationId };

    [Fact]
    public async Task BatchIdentification_KeepsInternalAndSupplierQueriesAndCachesSeparate_WithoutSimilaritySearch()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        Guid id = Guid.NewGuid();
        SavedProposal direct = Proposal(id, ProductCodeKind.Internal, "000.01", "000.01");
        SavedProposal supplier = Proposal(id, ProductCodeKind.Supplier, "000.01");
        AnalysisInput input = new(new(id, "Synthetic", "", Now, Now, QuotationState.Draft, 0), [direct, supplier], new(id, 0, 0, Now, [], []));
        Lookup lookup = new();
        var result = await new ErpIdentificationService(lookup).ResolvePendingAsync(input, "catalog", store, Token);
        Assert.Equal(["000.01"], lookup.InternalRequests); Assert.Equal(["000.01"], lookup.SupplierRequests);
        var internalResult = result[SupplierProductReference.From(direct, direct.Review[0]).Key];
        var supplierResult = result[SupplierProductReference.From(supplier, supplier.Review[0]).Key];
        Assert.Equal(ProductIdentificationOrigin.InternalCode, internalResult.Origin);
        Assert.Equal("000.01", Assert.Single(internalResult.Candidates).ProductCode);
        Assert.Equal(ProductIdentificationOrigin.AlternativeCode, supplierResult.Origin);
        Assert.Equal("OTHER-PRODUCT", Assert.Single(supplierResult.Candidates).ProductCode);
        Assert.Null(internalResult.Mapping); Assert.Empty(input.State.Links);
        var pending = await new ErpIdentificationService(lookup).LookupPendingAsync(input, Token);
        Assert.Equal("000.01", Assert.Single(pending["internal:000.01"]).ProductCode);
        Assert.Equal("OTHER-PRODUCT", Assert.Single(pending["000.01"]).ProductCode);
    }

    [Fact]
    public async Task InternalCode_NeverFallsBackToSupplierCode_AndSavedMappingsCannotRedirectIt()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        var source = new SupplierProductReference("Synthetic supplier", "000.01", "Synthetic", "", "un") { CodeKind = ProductCodeKind.Internal };
        Lookup lookup = new() { ReturnWrongInternalCode = true };
        var notFound = await new ErpIdentificationService(lookup).ResolveAsync(source, "catalog", store, Token);
        Assert.Empty(notFound.Candidates); Assert.Empty(lookup.SupplierRequests); Assert.Equal(ProductIdentificationOrigin.NotFound, notFound.Origin);
        var mapping = new SupplierProductMapping(Guid.NewGuid(), 0, source, Product("OTHER"), "un", 1, ProductMappingState.Active, Now, Now);
        Assert.Equal(ProductMappingError.Invalid, Assert.Throws<ProductMappingException>(mapping.Validate).Code);
        var saved = await store.SaveMappingAsync(mapping with { Product = Product("000.01"), ConversionFactor = 5m }, Token);
        Assert.Null(await store.FindMappingAsync("catalog", source with { CodeKind = ProductCodeKind.Supplier }, Token));
        lookup.ReturnWrongInternalCode = false;
        var exact = await new ErpIdentificationService(lookup).ResolveAsync(source, "catalog", store, Token);
        Assert.Equal(saved.Id, exact.Mapping!.Id); Assert.Equal(5m, exact.Mapping.ConversionFactor);
        lookup.IsOffline = true;
        var offline = await new ErpIdentificationService(lookup).ResolveAsync(source, "catalog", store, Token);
        Assert.True(offline.IsOffline); Assert.Equal(ProductIdentificationOrigin.SavedMapping, offline.Origin);
        Assert.Equal("000.01", Assert.Single(offline.Candidates).ProductCode);
    }

    [Fact]
    public void DirectProductGrouping_KeepsSameSimilarityProductsSeparate_AndLinkedOrdersUseOnlyTheExactProduct()
    {
        Guid id = Guid.NewGuid(); var proposal = Proposal(id, ProductCodeKind.Internal, "000.01", "000.02");
        var products = new[] { Product("000.01"), Product("000.02") };
        var links = products.Select((product, index) => new ItemLink(proposal.Id, proposal.Review[index].CaptureId,
            ErpProductIdentity.GroupKey(product, ProductGroupingVersion.ExactProductCode), product.Name, "un", 1, product, Now)
        { GroupingVersion = ProductGroupingVersion.ExactProductCode }).ToArray();
        AnalysisInput input = new(new(id, "Synthetic", "", Now, Now, QuotationState.Draft, 0), [proposal], new(id, 0, 0, Now, links, []));
        var groups = ImportedQuotationAnalysis.Calculate(input.Proposals, input.State);
        Assert.Equal(2, groups.Count); Assert.NotEqual(groups[0].Key, groups[1].Key);
        PurchaseOrderSnapshot order = new("catalog", new("order", "42", "supplier", "Synthetic buyer", "branch", "open", new(2026, 1, 1), true), "revision", Now,
            products.Select((product, index) => new PurchaseOrderLine("line" + index, product.ProductCode, product.Name, "un", "un", index == 0 ? 3m : 100m, 1, 10, index == 0 ? 30 : 1000, 0, false)
            { SimilarityCode = product.SimilarityCode, Brand = product.Brand }).ToArray());
        input = input with { Inputs = QuotationInputs.Legacy(id) with { Orders = [order], GroupingVersion = ProductGroupingVersion.ProductNameFallback } };
        var applied = OrderInputRules.Apply(input, false);
        Assert.All(applied.Links, link => Assert.Equal(ProductGroupingVersion.ExactProductCode, link.GroupingVersion));
        Assert.Equal(3m, applied.Demands.Single(demand => demand.GroupKey == links[0].GroupKey).Quantity);
        Assert.Equal(100m, applied.Demands.Single(demand => demand.GroupKey == links[1].GroupKey).Quantity);
        var invalid = links[0] with { GroupingVersion = ProductGroupingVersion.ProductCodeFallback, GroupKey = ErpProductIdentity.GroupKey(products[0]) };
        Assert.Equal(AnalysisError.InvalidLink, Assert.Throws<AnalysisException>(() => ImportedQuotationAnalysis.Calculate([proposal], input.State with { Links = [invalid, links[1]] })).Code);
        PurchaseLine source = new(groups[0].Key, groups[0].Name, "un", 2, groups[0].Offers[0], "synthetic-hash", "synthetic.xlsx", Guid.NewGuid(), 1);
        var sourceProduct = source.Offer.Link!.Product!;
        var otherLine = order.Lines.Single(line => line.ProductCode != sourceProduct.ProductCode);
        var supplier = new BuyingSupplierSnapshot(sourceProduct.Brand!, new("catalog", "supplier", "Synthetic buyer"), "history", "revision", "branch", new(2026, 1, 1), Now);
        var preview = PurchaseSynchronization.Compare(new(id, 1, 0, 0, sourceProduct.Brand, [source]), supplier, "branch", order with { Lines = [otherLine] });
        Assert.Contains(preview.Differences, difference => difference.Kind == SynchronizationChangeKind.Include);
        Assert.Contains(preview.Differences, difference => difference.Kind == SynchronizationChangeKind.Remove);
        Assert.DoesNotContain(preview.Differences, difference => difference.Kind == SynchronizationChangeKind.ProductAndPackage);
    }

    private sealed class Lookup : IErpProductLookup, IErpProductSearch
    {
        public List<string> SupplierRequests { get; } = [];
        public List<string> InternalRequests { get; } = [];
        public bool ReturnWrongInternalCode { get; set; }
        public bool IsOffline { get; set; }
        public Task<IReadOnlyList<ErpProductCandidate>> FindByCodeAsync(string code, CancellationToken token)
        { SupplierRequests.Add(code); return Task.FromResult<IReadOnlyList<ErpProductCandidate>>([Product("OTHER-PRODUCT") with { SupplierProductCode = code }]); }
        public Task<IReadOnlyList<ErpProductCandidate>> FindByProductCodeAsync(string code, CancellationToken token)
        {
            InternalRequests.Add(code); if (IsOffline) throw new ErpException(ErpError.ConnectionFailed);
            return Task.FromResult<IReadOnlyList<ErpProductCandidate>>([Product(ReturnWrongInternalCode ? "OTHER-PRODUCT" : code)]);
        }
        public Task<string> GetCatalogIdentityAsync(CancellationToken token) => Task.FromResult("catalog");
        public Task<IReadOnlyList<ErpProductCandidate>> SearchByNameAsync(string name, CancellationToken token) => throw new InvalidOperationException("Identification must not search descriptions.");
        public Task<IReadOnlyList<ErpProductCandidate>> FindBySimilarityAsync(string similarity, CancellationToken token) => throw new InvalidOperationException("Identification must not search similarities.");
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
