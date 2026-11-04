using QuoteMatch.Application.Integration;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Tests;

public sealed partial class PurchaseSynchronizationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Creation_LeavesTheInitialStatusToTheInstalledScriptAndPreservesItInTheJournal(string? status)
    {
        await using var fixture = await ExecutionFixture.CreateAsync(create: true);
        var plan = await fixture.Service.PreviewAsync(fixture.Plan.Selection, fixture.Token, status, new(2026, 10, 27));
        Assert.Equal(status, plan.Status);
        Assert.Equal(0, fixture.Writer.Reservations); Assert.Equal(0, fixture.Writer.Writes);
        await fixture.Service.ConfirmAsync(plan, true, false, fixture.Token);
        var operation = Assert.Single(await fixture.Store.GetSynchronizationOperationsAsync(plan.Selection.Preview.Proposal.QuotationId, fixture.Token));
        Assert.Equal(status, operation.Plan.Status); Assert.Equal(OrderOperationState.Committed, operation.State);
        Assert.Equal(1, fixture.Writer.Reservations); Assert.Equal(1, fixture.Writer.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SynchronizationAndLegacyJournals_BlockPendingWorkAndNeverReuseAnOperationIdentity(bool legacyFirst)
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var plan = fixture.Plan;
        var preparation = (await fixture.Store.GetPurchaseInputAsync(plan.Selection.Preview.Proposal.QuotationId, fixture.Token)).Preparation!;
        var reference = PurchaseOrderReference.From(preparation, preparation.Lines[0].Offer.ProposalId);
        var line = Assert.Single(reference.Lines);
        OrderWritePlan legacy = new(plan.OperationId, OrderWriteKind.Create, reference, null, "branch-1", "open", new(2026, 10, 27), "synthetic-script",
            [new(null, line.Id, line.Quantity, line.UnitPrice, false)]);
        if (legacyFirst)
        {
            await fixture.Store.BeginOrderOperationAsync(legacy, fixture.Token);
            Assert.Equal(OrderError.PendingOperation, (await Assert.ThrowsAsync<OrderException>(() => fixture.Service.ConfirmAsync(plan, true, false, fixture.Token))).Code);
            await fixture.Store.CompleteOrderOperationAsync(new(plan.OperationId, OrderOperationState.Committed, "legacy-created"), fixture.Token);
            Assert.Equal(OrderError.PendingOperation, (await Assert.ThrowsAsync<OrderException>(() => fixture.Service.ConfirmAsync(plan, true, false, fixture.Token))).Code);
        }
        else
        {
            await fixture.Store.BeginSynchronizationAsync(plan, fixture.Token);
            Assert.Equal(OrderError.PendingOperation, (await Assert.ThrowsAsync<OrderException>(() => fixture.Store.BeginOrderOperationAsync(legacy, fixture.Token))).Code);
            await fixture.Store.CompleteSynchronizationAsync(new(plan.OperationId, OrderOperationState.Committed, "order-1"), fixture.Token);
            Assert.Equal(OrderError.PendingOperation, (await Assert.ThrowsAsync<OrderException>(() => fixture.Store.BeginOrderOperationAsync(legacy, fixture.Token))).Code);
        }
        Assert.Equal(0, fixture.Writer.Writes);
    }

    [Fact]
    public async Task Creation_PersistsReservedControlsAndNumberBeforeDispatchAndNeverReservesAgainAfterUncertainty()
    {
        await using var fixture = await ExecutionFixture.CreateAsync(create: true);
        var plan = fixture.Plan;
        Assert.Null(plan.Allocation);
        fixture.Writer.OnReserve = async current =>
        {
            var pending = Assert.Single(await new SqliteImportStore(fixture.Directory).GetSynchronizationOperationsAsync(current.Selection.Preview.Proposal.QuotationId, fixture.Token));
            Assert.Null(pending.Plan.Allocation); Assert.Equal(OrderOperationState.Pending, pending.State);
            return new("reserved-order", "43", ["reserved-line"]);
        };
        fixture.Writer.OnWrite = async current =>
        {
            var pending = Assert.Single(await new SqliteImportStore(fixture.Directory).GetSynchronizationOperationsAsync(current.Selection.Preview.Proposal.QuotationId, fixture.Token));
            Assert.Equal("reserved-order", pending.Plan.Allocation!.OrderId); Assert.Equal("43", pending.Plan.Allocation.Number);
            Assert.Equal(["reserved-line"], pending.Plan.Allocation.LineControls); Assert.Equal(current.TargetId, pending.Plan.TargetId);
            throw new OperationCanceledException();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ConfirmAsync(plan, true, false, fixture.Token));
        var unknown = Assert.Single(await fixture.Store.GetSynchronizationOperationsAsync(plan.Selection.Preview.Proposal.QuotationId, fixture.Token));
        Assert.Equal(OrderOperationState.Unknown, unknown.State); Assert.Equal("reserved-order", unknown.Plan.TargetId);
        Assert.Equal(OrderError.PendingOperation, (await Assert.ThrowsAsync<OrderException>(() => fixture.Service.ConfirmAsync(plan with { OperationId = Guid.NewGuid() }, true, false, fixture.Token))).Code);
        Assert.Equal(1, fixture.Writer.Reservations); Assert.Equal(1, fixture.Writer.Writes);
        fixture.Writer.Receipt = new(plan.OperationId, OrderOperationState.Committed, "reserved-order");
        await fixture.Service.ReconcileAsync(unknown, fixture.Token);
        Assert.Equal(1, fixture.Writer.Reservations); Assert.Equal(1, fixture.Writer.Writes);
        Assert.Equal(OrderError.InvalidChange, (await Assert.ThrowsAsync<OrderException>(() => fixture.Store.SaveSynchronizationAllocationAsync(unknown.Plan, fixture.Token))).Code);
    }

    [Fact]
    public async Task Creation_RejectsInvalidOrDuplicateAllocationsWithoutDispatchingOrderInserts()
    {
        await using var fixture = await ExecutionFixture.CreateAsync(create: true);
        fixture.Writer.OnReserve = _ => Task.FromResult(new SynchronizationAllocation("same-control", "43", ["same-control"]));
        Assert.Equal(OrderError.InvalidResult, (await Assert.ThrowsAsync<OrderException>(() => fixture.Service.ConfirmAsync(fixture.Plan, true, false, fixture.Token))).Code);
        Assert.Equal(0, fixture.Writer.Writes); Assert.Equal(1, fixture.Writer.Reservations);
        Assert.Equal(OrderOperationState.Unknown, Assert.Single(await fixture.Store.GetSynchronizationOperationsAsync(fixture.Plan.Selection.Preview.Proposal.QuotationId, fixture.Token)).State);
    }
    [Fact]
    public async Task Synchronization_JournalsBeforeDispatchAndReconcilesWithoutRepeatingUnknownWrites()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var service = fixture.Service;
        var plan = fixture.Plan;
        Assert.Equal(OrderError.ConfirmationRequired, (await Assert.ThrowsAsync<OrderException>(() => service.ConfirmAsync(plan, false, false, fixture.Token))).Code);
        Assert.Empty(await fixture.Store.GetSynchronizationOperationsAsync(plan.Selection.Preview.Proposal.QuotationId, fixture.Token));
        fixture.Writer.OnWrite = async current =>
        {
            var pending = Assert.Single(await new SqliteImportStore(fixture.Directory).GetSynchronizationOperationsAsync(current.Selection.Preview.Proposal.QuotationId, fixture.Token));
            Assert.Equal(OrderOperationState.Pending, pending.State); Assert.Equal(current.OperationId, pending.Plan.OperationId);
            Assert.Equal(112, pending.Plan.Profile.OperatorCode);
            throw new OperationCanceledException();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ConfirmAsync(plan, true, false, fixture.Token));
        var unknown = Assert.Single(await fixture.Store.GetSynchronizationOperationsAsync(plan.Selection.Preview.Proposal.QuotationId, fixture.Token));
        Assert.Equal(OrderOperationState.Unknown, unknown.State);
        Assert.Equal(OrderError.PendingOperation, (await Assert.ThrowsAsync<OrderException>(() => service.ConfirmAsync(plan with { OperationId = Guid.NewGuid() }, true, false, fixture.Token))).Code);
        Assert.Equal(1, fixture.Writer.Writes);
        await service.ReconcileAsync(unknown, fixture.Token);
        Assert.Equal(OrderOperationState.Unknown, Assert.Single(await fixture.Store.GetSynchronizationOperationsAsync(plan.Selection.Preview.Proposal.QuotationId, fixture.Token)).State);
        fixture.Writer.Receipt = new(plan.OperationId, OrderOperationState.Committed, "order-1");
        await service.ReconcileAsync(unknown, fixture.Token);
        Assert.Equal(1, fixture.Writer.Writes);
        Assert.Equal(OrderOperationState.Committed, Assert.Single(await fixture.Store.GetSynchronizationOperationsAsync(plan.Selection.Preview.Proposal.QuotationId, fixture.Token)).State);
        Assert.Equal(OrderError.PendingOperation, (await Assert.ThrowsAsync<OrderException>(() => service.ConfirmAsync(plan, true, false, fixture.Token))).Code);
        var quotation = (await fixture.Store.GetAnalysisInputAsync(plan.Selection.Preview.Proposal.QuotationId, fixture.Token)).Quotation;
        Assert.Equal(QuotationError.OrderHistory, (await Assert.ThrowsAsync<QuotationException>(() => fixture.Store.DeleteQuotationAsync(quotation.Id, quotation.Revision, fixture.Token))).Code);
    }

    [Fact]
    public async Task Synchronization_RejectsForgedSelectionStaleSupplierAndStaleSourcesBeforeJournaling()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var plan = fixture.Plan;
        var forged = plan with { Selection = plan.Selection with { Lines = [plan.Selection.Lines[0] with { Quantity = 999 }] } };
        Assert.Equal(OrderError.InvalidChange, (await Assert.ThrowsAsync<OrderException>(() => fixture.Service.ConfirmAsync(forged, true, false, fixture.Token))).Code);
        fixture.Erp.Supplier = fixture.Erp.Supplier with { SourceOrderRevision = "changed" };
        Assert.Equal(SynchronizationError.Conflict, (await Assert.ThrowsAsync<SynchronizationException>(() => fixture.Service.ConfirmAsync(plan, true, false, fixture.Token))).Code);
        fixture.Erp.Supplier = Supplier();
        var input = await fixture.Store.GetAnalysisInputAsync(plan.Selection.Preview.Proposal.QuotationId, fixture.Token);
        await fixture.Store.SaveAnalysisAsync(input.State, fixture.Token);
        Assert.Equal(SynchronizationError.Conflict, (await Assert.ThrowsAsync<SynchronizationException>(() => fixture.Service.ConfirmAsync(plan, true, false, fixture.Token))).Code);
        Assert.Empty(await fixture.Store.GetSynchronizationOperationsAsync(plan.Selection.Preview.Proposal.QuotationId, fixture.Token));
        Assert.Equal(0, fixture.Writer.Writes);
    }

    [Fact]
    public async Task Synchronization_RequiresRemovalConsentAndRejectsInvalidReceiptsOrAbsenceAsProof()
    {
        await using var fixture = await ExecutionFixture.CreateAsync(includeExtra: true);
        var plan = fixture.Plan;
        Assert.Equal(OrderError.ConfirmationRequired, (await Assert.ThrowsAsync<OrderException>(() => fixture.Service.ConfirmAsync(plan, true, false, fixture.Token))).Code);
        fixture.Writer.Receipt = new(plan.OperationId, OrderOperationState.Committed, "unrelated-control");
        var result = await fixture.Service.ConfirmAsync(plan, true, true, fixture.Token);
        Assert.Equal(OrderOperationState.Unknown, result.State);
        var operation = Assert.Single(await fixture.Store.GetSynchronizationOperationsAsync(plan.Selection.Preview.Proposal.QuotationId, fixture.Token));
        Assert.Equal(OrderError.InvalidResult, (await Assert.ThrowsAsync<OrderException>(() => fixture.Service.ReconcileAsync(operation, fixture.Token))).Code);
        fixture.Writer.Receipt = new(plan.OperationId, OrderOperationState.NotApplied, null);
        Assert.Equal(OrderError.InvalidResult, (await Assert.ThrowsAsync<OrderException>(() => fixture.Service.ReconcileAsync(operation, fixture.Token))).Code);
        fixture.Writer.Receipt = new(plan.OperationId, OrderOperationState.Committed, "order-1");
        var receipt = await fixture.Service.ReconcileAsync(operation, fixture.Token);
        Assert.Equal(OrderOperationState.Committed, receipt.State);
        Assert.Equal(OrderError.InvalidChange, (await Assert.ThrowsAsync<OrderException>(() => fixture.Service.ReconcileAsync(operation with { State = OrderOperationState.Committed }, fixture.Token))).Code);
        Assert.Equal(1, fixture.Writer.Writes);
    }

    private sealed class ExecutionFixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "QuoteMatch.Synchronization.Tests", Guid.NewGuid().ToString("N"));
        public CancellationToken Token => TestContext.Current.CancellationToken;
        public SqliteImportStore Store { get; private set; } = null!;
        public SynchronizationErp Erp { get; } = new();
        public SynchronizationWriter Writer { get; } = new();
        public SynchronizationWriteService Service { get; private set; } = null!;
        public SynchronizationWritePlan Plan { get; private set; } = null!;
        public static async Task<ExecutionFixture> CreateAsync(bool includeExtra = false, bool create = false, DocumentFinancialCapture? documentFinancials = null)
        {
            ExecutionFixture result = new();
            result.Store = new(result.Directory); await result.Store.InitializeAsync(result.Token);
            var source = Source(); var offer = source.Offer;
            var quotation = await result.Store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic synchronization", "", Now, Now, QuotationState.Draft, 0), result.Token);
            SavedProposal saved = new(offer.ProposalId, Now, "synthetic.pdf", "synthetic-hash", new PdfTemplate { Id = Guid.NewGuid(), Version = 1, SupplierName = "Synthetic seller" },
                new([], "Synthetic"), new([], [], null, null) { DocumentFinancials = documentFinancials ?? new(new(), []) }, [offer.Item])
            { QuotationId = quotation.Id };
            await result.Store.SaveProposalAsync(saved, [1, 2, 3], result.Token);
            var input = await result.Store.GetAnalysisInputAsync(quotation.Id, result.Token);
            var analysis = await result.Store.SaveAnalysisAsync(input.State with { Links = [offer.Link!], Demands = [new(source.GroupKey, "M", 150)] }, result.Token);
            var preparation = await result.Store.PreparePurchaseAsync(new(quotation.Id, input.Quotation.Revision, analysis.Version, 0, [new(offer.ProposalId, offer.Item.CaptureId)]), result.Token);
            result.Erp.Order = includeExtra ? Order(Line() with { UnitPrice = 210, Total = 420 }, Line("extra") with { ProductCode = "extra", SimilarityCode = "extra" }) : Order(Line() with { UnitPrice = 210, Total = 420 });
            PurchaseSynchronizationService previews = new(result.Store, result.Erp, result.Erp);
            result.Service = new(result.Store, previews, result.Writer);
            var preview = await previews.PreviewAsync(Assert.Single(PurchaseSynchronization.Proposals(preparation)), "branch-1", create ? null : "order-1", [], result.Token);
            var selection = PurchaseSynchronization.Select(preview, preview.Differences.Select(change => change.Id).ToArray(), true);
            result.Plan = await result.Service.PreviewAsync(selection, result.Token, "open", new(2026, 10, 27));
            return result;
        }
        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
            return ValueTask.CompletedTask;
        }
    }
    private sealed class SynchronizationErp : IPurchaseOrderReader, IErpBuyingSupplierLookup
    {
        public PurchaseOrderSnapshot Order { get; set; } = null!;
        public BuyingSupplierSnapshot Supplier { get; set; } = PurchaseSynchronizationTests.Supplier();
        public Task<PurchaseOrderSnapshot> ReadAsync(string orderId, CancellationToken token) => Task.FromResult(Order);
        public Task<IReadOnlyList<PurchaseOrderHeader>> SearchAsync(PurchaseOrderSearch search, CancellationToken token) => Task.FromResult<IReadOnlyList<PurchaseOrderHeader>>([Order.Header]);
        public Task<BuyingSupplierSnapshot?> FindAsync(ErpBrandIdentity brand, string branch, CancellationToken token) => Task.FromResult<BuyingSupplierSnapshot?>(Supplier);
    }
    private sealed class SynchronizationWriter : IPurchaseSynchronizationWriter
    {
        public int Writes { get; private set; }
        public int Reservations { get; private set; }
        public OrderWriteReceipt? Receipt { get; set; }
        public Func<SynchronizationWritePlan, Task<OrderWriteReceipt>>? OnWrite { get; set; }
        public Func<SynchronizationWritePlan, Task<SynchronizationAllocation>>? OnReserve { get; set; }
        public Func<SynchronizationWritePlan, SynchronizationFinancialPreview>? FinancialPreview { get; set; }
        public int FinancialReads { get; private set; }
        public Task<SynchronizationWriteProfile> GetProfileAsync(OrderWriteKind kind, CancellationToken token) => Task.FromResult(new SynchronizationWriteProfile("synthetic-script", 112) { Kind = kind, SupportsFinancialPolicies = FinancialPreview is not null });
        public Task<SynchronizationFinancialPreview> PreviewFinancialsAsync(SynchronizationWritePlan plan, CancellationToken token)
        { FinancialReads++; return Task.FromResult(FinancialPreview?.Invoke(plan) ?? throw new OrderException(OrderError.FinancialPolicyUnsupported)); }
        public Task<SynchronizationAllocation> ReserveAsync(SynchronizationWritePlan plan, CancellationToken token)
        {
            Reservations++;
            return OnReserve is not null ? OnReserve(plan) : Task.FromResult(new SynchronizationAllocation("created-order", "43", plan.Selection.Lines.Select((_, index) => "created-line-" + index).ToArray()));
        }
        public async Task<OrderWriteReceipt> ExecuteAsync(SynchronizationWritePlan plan, CancellationToken token)
        { Writes++; return OnWrite is not null ? await OnWrite(plan) : Receipt ?? new(plan.OperationId, OrderOperationState.Committed, plan.TargetId); }
        public Task<OrderWriteReceipt> ReconcileAsync(SynchronizationOperation operation, CancellationToken token) => Task.FromResult(Receipt ?? new(operation.Plan.OperationId, OrderOperationState.Unknown, null));
    }
}
