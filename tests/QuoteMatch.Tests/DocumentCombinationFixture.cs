using QuoteMatch.Application.Integration;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Testing;

internal sealed record CombinationDocument(string Supplier, decimal Quantity, decimal Price, string Product = "P1", string Unit = "UN", decimal Factor = 1, string Color = "", bool Link = true, decimal? ReportedTotal = null, FinancialAmounts? Financials = null);

internal sealed class DocumentCombinationFixture : IAsyncDisposable, IPurchaseOrderReader, IErpBuyingSupplierLookup, IPurchaseSynchronizationWriter
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "QuoteMatch.DocumentCombination.Tests", Guid.NewGuid().ToString("N"));
    public SqliteImportStore Store { get; private set; } = null!;
    public PurchaseInput Input { get; private set; } = null!;
    public BrandPurchaseProposal Proposal => PurchaseSynchronization.Proposals(Input.Preparation!).Single();
    public PurchaseSynchronizationService Previews => new(Store, this, this);
    public SynchronizationWriteService Writes => new(Store, Previews, this);
    public static ErpBrandIdentity Brand { get; } = new("synthetic-catalog", "synthetic-brand", "Synthetic brand");
    public int WriteCount { get; private set; }
    public int ReserveCount { get; private set; }
    public bool LoseAcknowledgment { get; set; }
    public SynchronizationWritePlan? Dispatched { get; private set; }
    public static async Task<DocumentCombinationFixture> CreateAsync(CancellationToken token, params CombinationDocument[] documents)
    {
        DocumentCombinationFixture fixture = new(); fixture.Store = new(fixture.DirectoryPath); await fixture.Store.InitializeAsync(token);
        var quotation = await fixture.Store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic combined order", "", Now, Now, QuotationState.Draft, 0), token);
        List<ItemLink> links = []; List<SavedProposal> proposals = [];
        for (int index = 0; index < documents.Length; index++)
        {
            var definition = documents[index];
            var item = new ReviewedItem(Guid.NewGuid(), definition.Product, "Synthetic product " + definition.Product, definition.Quantity, definition.Price,
                definition.ReportedTotal ?? definition.Quantity * definition.Price, definition.Unit, false, "")
            { Color = definition.Color, Financials = definition.Financials ?? new() };
            SavedProposal proposal = new(Guid.NewGuid(), Now, "synthetic-" + index + ".xlsx", "synthetic-hash-" + index,
                new() { Id = Guid.NewGuid(), Version = 1, SupplierName = definition.Supplier, CodeKind = ProductCodeKind.Internal, SourceFormat = ImportSourceFormat.Excel },
                new([], "Synthetic"), new([], [], null, null) { DocumentFinancials = new(new() { Freight = index + 1 }, []) }, [item])
            { QuotationId = quotation.Id };
            await fixture.Store.SaveProposalAsync(proposal, [(byte)index], token); proposals.Add(proposal);
            var product = new ErpProductCandidate(definition.Product, item.Description, "Shared similarity", false, "manufacturer", "Synthetic manufacturer", "Other supplier code",
                definition.Factor, definition.Unit, new(2026, 1, 1), 4, "UN", "UN", Brand.CatalogIdentity, Now)
            { Brand = Brand };
            if (definition.Link) links.Add(new(proposal.Id, item.CaptureId, ErpProductIdentity.GroupKey(product, ProductGroupingVersion.ExactProductCode), item.Description, "UN", definition.Factor, product, Now)
            { GroupingVersion = ProductGroupingVersion.ExactProductCode });
        }
        var input = await fixture.Store.GetAnalysisInputAsync(quotation.Id, token);
        var state = await fixture.Store.SaveAnalysisAsync(input.State with { Links = links, Demands = links.DistinctBy(link => link.GroupKey).Select(link => new GroupDemand(link.GroupKey, "UN", 1)).ToArray() }, token);
        await fixture.Store.PreparePurchaseAsync(new(quotation.Id, input.Quotation.Revision, state.Version, 0, [new(proposals[0].Id, proposals[0].Review[0].CaptureId)]), token);
        fixture.Input = await fixture.Store.GetPurchaseInputAsync(quotation.Id, token);
        return fixture;
    }
    public async Task SelectProductsAsync(CancellationToken token, params string[] codes)
    {
        var selections = codes.Select(code => Input.Analysis.Proposals.SelectMany(proposal => proposal.Review
            .Where(item => item.ProductCode == code).Select(item => new PurchaseSelection(proposal.Id, item.CaptureId))).First()).ToArray();
        await Store.PreparePurchaseAsync(new(Proposal.QuotationId, Input.Analysis.Quotation.Revision, Input.Analysis.State.Version, Input.Preparation!.Version, selections), token);
        Input = await Store.GetPurchaseInputAsync(Proposal.QuotationId, token);
    }
    public Task<BuyingSupplierSnapshot?> FindAsync(ErpBrandIdentity brand, string branch, CancellationToken token) => Task.FromResult<BuyingSupplierSnapshot?>(new(brand,
        new(brand.CatalogIdentity, "buying-supplier", "Synthetic buying supplier"), "history", "history-revision", branch, new(2026, 1, 1), Now));
    public Task<IReadOnlyList<PurchaseOrderHeader>> SearchAsync(PurchaseOrderSearch search, CancellationToken token) => Task.FromResult<IReadOnlyList<PurchaseOrderHeader>>([]);
    public Task<PurchaseOrderSnapshot> ReadAsync(string id, CancellationToken token) => Task.FromResult(new PurchaseOrderSnapshot(Brand.CatalogIdentity,
        new(id, "42", "buying-supplier", "Synthetic buying supplier", "branch", "open", new(2026, 1, 1), true), "revision", Now,
        [new("line", "P1", "Synthetic product P1", "UN", "UN", 10, 1, 6, 60, 0, false) { Brand = Brand, SimilarityCode = "Shared similarity" }]));
    public Task<SynchronizationWriteProfile> GetProfileAsync(OrderWriteKind kind, CancellationToken token) => Task.FromResult(new SynchronizationWriteProfile("synthetic-profile", 112) { Kind = kind });
    public Task<SynchronizationAllocation> ReserveAsync(SynchronizationWritePlan plan, CancellationToken token)
    { ReserveCount++; return Task.FromResult(new SynchronizationAllocation("created-order", "43", plan.Selection.Lines.Select((_, index) => "created-line-" + index).ToArray())); }
    public async Task<OrderWriteReceipt> ExecuteAsync(SynchronizationWritePlan plan, CancellationToken token)
    {
        var operation = (await new SqliteImportStore(DirectoryPath).GetSynchronizationOperationsAsync(plan.Selection.Preview.Proposal.QuotationId, token)).Single();
        if (operation.State != OrderOperationState.Pending || !CreationDocumentRules.Matches(operation.Plan.Selection.Preview.CreationDocuments, plan.Selection.Preview.CreationDocuments))
            throw new InvalidOperationException("The confirmed source decisions must be journaled before dispatch.");
        WriteCount++; Dispatched = plan;
        if (LoseAcknowledgment) throw new OperationCanceledException();
        return new(plan.OperationId, OrderOperationState.Committed, plan.TargetId);
    }
    public Task<OrderWriteReceipt> ReconcileAsync(SynchronizationOperation operation, CancellationToken token) => Task.FromResult(new OrderWriteReceipt(operation.Plan.OperationId, OrderOperationState.Committed, operation.Plan.TargetId));
    public ValueTask DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); return ValueTask.CompletedTask; }
}
