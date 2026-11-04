using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using QuoteMatch.Application.Integration;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task LinkedOrders_ImportDemandAndSwitchReferenceOfflineWithoutPurchasePreparation()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync(); UnifiedErp erp = new(); Localizer strings = new();
            var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic linked study", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            foreach (string supplier in new[] { "A", "B" }) await store.SaveProposalAsync(AnalysisProposal(quotation.Id, supplier, 10, 250, "RL"), [1]);
            QuotationAnalysisViewModel model = new(strings, store, erp, store); await model.LoadAsync(quotation.Id);
            foreach (var row in model.Groups.ToArray())
            {
                model.SelectedGroup = model.Groups.Single(group => group.Group.Offers.Any(offer => offer.ProposalId == row.Group.Offers[0].ProposalId));
                model.SelectProduct(erp.Product); model.ConversionFactor = 100; await model.LinkErpCommand.ExecuteAsync(null);
            }
            QuotationOrderInputsViewModel editor = new(strings, store, erp); await editor.LoadAsync(quotation.Id);
            await editor.SearchCommand.ExecuteAsync(null); await editor.LinkCommand.ExecuteAsync(null); await editor.LinkCommand.ExecuteAsync(null);
            Assert.Single(editor.Orders); editor.PriceReferenceIndex = 1;
            var input = await store.GetAnalysisInputAsync(quotation.Id);
            await store.SaveQuotationWorkspaceAsync(input.Quotation, editor.Build(quotation.Id), false);
            await model.LoadAsync(quotation.Id);
            var group = Assert.Single(model.Groups); Assert.Equal(1000, group.Group.RequestedQuantity);
            Assert.Equal("R$ 2,4000/M", group.LastPurchase); Assert.All(group.Offers, offer => Assert.Equal("+4,17%", offer.PurchaseVariation));
            Assert.Equal(1000, model.RequestedQuantity); Assert.Contains("2 itens", model.DemandOriginLabel);
            Assert.Null((await store.GetPurchaseInputAsync(quotation.Id)).Preparation);
            Assert.Contains("order-1", group.LastPurchaseHelp); Assert.Contains("line-1", group.LastPurchaseHelp);
            var oldOrder = await store.GetQuotationInputsAsync(quotation.Id, 1);
            Assert.Equal(9, group.Offers[0].Offer.Link!.Product!.LastPurchaseUnitPrice);
            erp.Offline = true; await model.LoadAsync(quotation.Id); Assert.Equal("R$ 2,4000/M", model.Groups[0].LastPurchase);
            model.PriceReferenceIndex = 0; await model.ApplyPriceReferenceCommand.ExecuteAsync(null);
            Assert.Equal("R$ 9,0000/M", model.Groups[0].LastPurchase);
            Assert.Equal(1000, model.Groups[0].Group.RequestedQuantity);
            Assert.Equal(QuotationPriceReference.LinkedOrder, oldOrder.PriceReference);
            Assert.Equal(QuotationPriceReference.LastPurchase, (await store.GetQuotationInputsAsync(quotation.Id)).PriceReference);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ErpIdentification_PreservesManualDemandWhenAnOfflineGroupMovesToAnErpFamily()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync(); UnifiedErp erp = new();
            var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic manual demand", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            var proposal = AnalysisProposal(quotation.Id, "A", 7, 250, "RL"); await store.SaveProposalAsync(proposal, [1]);
            var input = await store.GetAnalysisInputAsync(quotation.Id);
            ItemLink manual = new(proposal.Id, proposal.Review[0].CaptureId, "manual:wire", "Synthetic wire", "M", 100, null, DateTime.UtcNow);
            await store.SaveAnalysisAsync(input.State with { Links = [manual], Demands = [new("manual:wire", "M", 900)] });
            input = await store.GetAnalysisInputAsync(quotation.Id);
            var order = erp.Order with { Lines = [erp.Order.Lines[0]] };
            await store.SaveQuotationWorkspaceAsync(input.Quotation, input.Inputs with { Orders = [order], GroupingVersion = ProductGroupingVersion.ProductNameFallback }, false);
            QuotationAnalysisViewModel model = new(new Localizer(), store, erp); await model.LoadAsync(quotation.Id);
            Assert.Equal(900, model.RequestedQuantity); Assert.Null(model.SelectedGroup!.Group.Winner);
            model.SelectProduct(erp.Product); await model.LinkErpCommand.ExecuteAsync(null);
            var saved = await store.GetAnalysisInputAsync(quotation.Id);
            var demand = Assert.Single(saved.State.Demands);
            Assert.Equal(900, demand.Quantity); Assert.Equal(DemandOrigin.Manual, demand.Origin);
            Assert.Equal(ErpProductIdentity.GroupKey(erp.Product, saved.Inputs.GroupingVersion), demand.GroupKey);
            Assert.Empty(demand.Sources); Assert.Equal(900, model.RequestedQuantity); Assert.Null(model.SelectedGroup!.Group.Winner);
            Assert.Equal(100, saved.State.Links[0].BaseUnitsPerQuotedUnit);
            input = await store.GetAnalysisInputAsync(quotation.Id);
            await store.SaveQuotationWorkspaceAsync(input.Quotation, input.Inputs, true);
            var replaced = (await store.GetAnalysisInputAsync(quotation.Id)).State.Demands.Single();
            Assert.Equal(600, replaced.Quantity); Assert.Equal(DemandOrigin.LinkedOrder, replaced.Origin);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LinkedOrderCancellation_RejectsALateResultAndAllowsAnotherLookup()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync(); UnifiedErp erp = new();
            QuotationOrderInputsViewModel model = new(new Localizer(), store, erp); await model.LoadAsync(null);
            await model.SearchCommand.ExecuteAsync(null);
            erp.PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task pending = model.LinkCommand.ExecuteAsync(null); Assert.True(model.IsBusy);
            model.CancelCommand.Execute(null); erp.PendingRead.SetResult(erp.Order); await pending;
            Assert.Empty(model.Orders); Assert.Equal(model.Strings["ImportCanceled"], model.Status);
            erp.PendingRead = null; await model.LinkCommand.ExecuteAsync(null); Assert.Single(model.Orders);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CheckingAlias_UsesTheSingleWorkspaceAndRequiresAnOrderOnlyWhenThatReferenceIsChosen()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); using WorkspaceViewModel model = new(CreateStore(), store); await model.InitializeAsync();
            await model.GoToCheckingCommand.ExecuteAsync(null); Assert.True(model.IsQuotationList); Assert.False(model.IsOrderTools);
            await model.NewQuotationCommand.ExecuteAsync(null); Assert.Equal(0, model.OrderInputs.PriceReferenceIndex);
            model.OrderInputs.PriceReferenceIndex = 1;
            model.QuotationName = "Synthetic checking"; await model.SaveQuotationCommand.ExecuteAsync(null);
            Assert.Equal(model.Strings["QuotationErrorInvalidInputs"], model.Status); Assert.True(model.IsEditingDetails);
            Assert.Empty(await store.GetQuotationsAsync());
            model.CancelEditCommand.Execute(null); model.GoHomeCommand.Execute(null);
            await model.NewQuotationCommand.ExecuteAsync(null); Assert.Equal(0, model.OrderInputs.PriceReferenceIndex);
            model.QuotationName = "Synthetic analysis"; await model.SaveQuotationCommand.ExecuteAsync(null);
            var quotation = model.CurrentQuotation!; Assert.True(model.CanImport);
            await model.GoToCheckingCommand.ExecuteAsync(null); model.SelectedQuotation = model.Quotations.Single(); await model.OpenQuotationCommand.ExecuteAsync(null);
            Assert.Equal(quotation.Id, model.CurrentQuotation!.Id); Assert.True(model.IsQuotationOpen); Assert.True(model.IsQuotations); Assert.Equal(1, model.PageIndex);
            Assert.Null((await store.GetPurchaseInputAsync(quotation.Id)).Preparation);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WorkspaceReferenceChange_RefreshesQuotationRevisionForFinalization()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync(); UnifiedErp erp = new();
            var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic reference", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            await store.SaveProposalAsync(AnalysisProposal(quotation.Id, "A", 2, 250, "RL"), [1]);
            var input = await store.GetAnalysisInputAsync(quotation.Id);
            await store.SaveQuotationWorkspaceAsync(input.Quotation, input.Inputs with { Orders = [erp.Order], PriceReference = QuotationPriceReference.LinkedOrder }, false);
            using WorkspaceViewModel model = new(CreateStore(), store); await model.InitializeAsync(); model.PageIndex = 1;
            model.SelectedQuotation = model.Quotations.Single(); await model.OpenQuotationCommand.ExecuteAsync(null);
            model.Analysis.PriceReferenceIndex = 0; await model.Analysis.ApplyPriceReferenceCommand.ExecuteAsync(null);
            Assert.Equal((await store.GetAnalysisInputAsync(quotation.Id)).Quotation.Revision, model.CurrentQuotation!.Revision);
            model.RequestFinalizeCommand.Execute(null); await model.ConfirmActionCommand.ExecuteAsync(null);
            Assert.True(model.IsFinalized);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ApplyingTheCurrentPriceReference_DoesNotInvalidatePurchaseInputs()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync(); UnifiedErp erp = new();
            var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic reference", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            QuotationAnalysisViewModel model = new(new Localizer(), store, erp); await model.LoadAsync(quotation.Id);
            await model.ApplyPriceReferenceCommand.ExecuteAsync(null);
            Assert.Equal(quotation.Revision, (await store.GetAnalysisInputAsync(quotation.Id)).Quotation.Revision);
            Assert.Equal(0, (await store.GetQuotationInputsAsync(quotation.Id)).Version);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ItemEditor_HasOneFactorAndKeepsRefreshedDataUnconfirmed()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync(); UnifiedErp erp = new();
            var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic packaging", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            await store.SaveProposalAsync(AnalysisProposal(quotation.Id, "A", 2, 250, "RL"), [1]);
            QuotationAnalysisViewModel model = new(new Localizer(), store, erp, store); await model.LoadAsync(quotation.Id);
            model.SelectProduct(erp.Product); model.ConversionFactor = 100; await model.LinkErpCommand.ExecuteAsync(null);
            model.RequestedQuantity = 1000; await model.ApplyDemandCommand.ExecuteAsync(null);
            QuotationItemEditorWindow window = new() { DataContext = model }; window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(); Assert.Equal(2, window.GetLogicalDescendants().OfType<NumericUpDown>().Count());
                Assert.Contains("2 RL × 100 = 200 M", model.ConversionEquation); Assert.Contains("M", model.DesiredQuantityLabel);
                erp.Product = erp.Product with { LastPurchaseUnitPrice = 3, AlternativeQuantity = 500 };
                await model.RefreshErpDataCommand.ExecuteAsync(null);
                Assert.Equal(100, model.ConversionFactor); Assert.Equal(9, (await store.GetAnalysisInputAsync(quotation.Id)).State.Links[0].Product!.LastPurchaseUnitPrice);
                await model.LinkErpCommand.ExecuteAsync(null); Assert.Equal(3, (await store.GetAnalysisInputAsync(quotation.Id)).State.Links[0].Product!.LastPurchaseUnitPrice);
                Assert.Equal(100, (await store.GetAnalysisInputAsync(quotation.Id)).State.Links[0].BaseUnitsPerQuotedUnit);
                erp.Product = erp.Product with { BaseUnit = "KG" }; await model.RefreshErpDataCommand.ExecuteAsync(null);
                Assert.Null(model.ConversionFactor); await model.LinkErpCommand.ExecuteAsync(null);
                Assert.Equal(model.Strings["AnalysisErrorInvalidLink"], model.Status);
                Assert.Equal("M", (await store.GetAnalysisInputAsync(quotation.Id)).State.Links[0].BaseUnit);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Synchronization_ShowsOnlyRealDifferencesStartsUncheckedAndRejectsAStaleTarget()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync(); UnifiedErp erp = new(); Localizer strings = new();
            var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic synchronization", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            var proposal = AnalysisProposal(quotation.Id, "A", 10, 250, "RL"); await store.SaveProposalAsync(proposal, [1]);
            var input = await store.GetAnalysisInputAsync(quotation.Id); string key = ErpProductIdentity.GroupKey(erp.Product);
            var state = await store.SaveAnalysisAsync(input.State with { Links = [new(proposal.Id, proposal.Review[0].CaptureId, key, "Synthetic wire", "M", 100, erp.Product, DateTime.UtcNow)], Demands = [new(key, "M", 1000)] });
            await store.PreparePurchaseAsync(new(quotation.Id, input.Quotation.Revision, state.Version, 0, [new(proposal.Id, proposal.Review[0].CaptureId)]));
            erp.Order = erp.Order with { Lines = [erp.Order.Lines[0] with { ProductCode = erp.Product.ProductCode, Quantity = 10, UnitPrice = 260, ReceivedQuantity = 0, Brand = erp.Product.Brand }] };
            PurchaseSynchronizationViewModel model = new(strings, store, erp, erp); await model.LoadAsync(quotation.Id); model.Branch = "branch-1"; model.KindIndex = 1;
            await model.SearchTargetsCommand.ExecuteAsync(null); model.SelectedTarget = model.Targets.Single();
            PurchaseSynchronizationWindow window = new() { DataContext = model }; window.Show();
            try
            {
                await model.PreviewCommand.ExecuteAsync(null); Assert.Equal(SynchronizationChangeKind.Price, Assert.Single(model.Differences).Difference.Kind);
                Assert.All(model.Differences, row => Assert.False(row.IsSelected)); Assert.False(model.HasSelection);
                model.Differences[0].IsSelected = true; await model.ReviewSelectionCommand.ExecuteAsync(null);
                Assert.Equal("distributor-9", model.Selection!.SupplierId); Assert.Equal(10, Assert.Single(model.Selection.Lines).Quantity);
                Assert.Equal(strings["SyncSafeValidationRequired"], model.Status);
                UnifiedSynchronizationWriter writer = new();
                PurchaseSynchronizationViewModel confirmed = new(strings, store, erp, erp, writer, store);
                await confirmed.LoadAsync(quotation.Id); confirmed.Branch = "branch-1"; confirmed.KindIndex = 1;
                await confirmed.SearchTargetsCommand.ExecuteAsync(null); confirmed.SelectedTarget = confirmed.Targets.Single();
                await confirmed.PreviewCommand.ExecuteAsync(null); confirmed.Differences[0].IsSelected = true;
                await confirmed.ReviewSelectionCommand.ExecuteAsync(null);
                PurchaseSynchronizationWindow confirmation = new() { DataContext = confirmed }; confirmation.Show();
                try
                {
                    var execute = confirmation.FindControl<Button>("ConfirmSynchronization")!;
                    Assert.True(execute.IsEnabled); Assert.Equal(0, writer.Writes);
                    Assert.Null(confirmation.FindControl<CheckBox>("ConfirmSynchronizationInput"));
                    await confirmed.ConfirmWriteCommand.ExecuteAsync(null);
                    Assert.Equal(1, writer.Writes); Assert.False(confirmed.CanConfirm); Assert.False(confirmed.HasSelection);
                    Assert.Equal(strings["OrderStateCommitted"], confirmed.Status);
                    var operation = Assert.Single(await store.GetSynchronizationOperationsAsync(quotation.Id, TestContext.Current.CancellationToken));
                    Assert.Equal(OrderOperationState.Committed, operation.State); Assert.Equal("order-1", operation.OrderId);
                    writer.LoseAcknowledgment = true;
                    await confirmed.PreviewCommand.ExecuteAsync(null);
                    confirmed.Differences[0].IsSelected = true; await confirmed.ReviewSelectionCommand.ExecuteAsync(null);
                    await confirmed.ConfirmWriteCommand.ExecuteAsync(null);
                    Assert.Equal(strings["OrderStateUnknown"], confirmed.Status); Assert.True(confirmed.HasPendingOperation);
                    Assert.False(confirmed.CanConfirm); Assert.True(confirmed.CanReconcile); Assert.Equal(2, writer.Writes);
                    PurchaseSynchronizationViewModel restarted = new(strings, store, erp, erp, writer, store);
                    await restarted.LoadAsync(quotation.Id);
                    Assert.True(restarted.HasPendingOperation); Assert.True(restarted.CanReconcile);
                    await restarted.ReconcileCommand.ExecuteAsync(null);
                    Assert.False(restarted.HasPendingOperation); Assert.Equal(strings["OrderStateCommitted"], restarted.Status);
                    Assert.Equal(2, writer.Writes);
                }
                finally { confirmation.Close(); }
                erp.Order = erp.Order with { Revision = "changed" }; await model.ReviewSelectionCommand.ExecuteAsync(null);
                Assert.Null(model.Selection); Assert.Equal(strings["SyncErrorConflict"], model.Status);
                erp.Order = erp.Order with { Lines = [erp.Order.Lines[0], erp.Order.Lines[0] with { Id = "line-2" }] };
                await model.PreviewCommand.ExecuteAsync(null); Assert.Equal(strings["SyncErrorInvalidPairing"], model.Status);
                await model.LoadPairingLinesCommand.ExecuteAsync(null); Assert.Equal(2, model.PairTargets.Count);
                model.AddPairCommand.Execute(null); Assert.Single(model.Pairs);
                await model.PreviewCommand.ExecuteAsync(null); Assert.Equal(2, model.Differences.Count);
                Assert.All(model.Differences, row => Assert.False(row.IsSelected));
                model.Differences.Single(row => row.Difference.Kind == SynchronizationChangeKind.Price).IsSelected = true;
                await model.ReviewSelectionCommand.ExecuteAsync(null); Assert.Equal(2, model.Selection!.Lines.Count);
                var changed = (await store.GetAnalysisInputAsync(quotation.Id)).Quotation;
                await store.SaveQuotationAsync(changed with { Name = "Changed source" });
                await model.ReviewSelectionCommand.ExecuteAsync(null); Assert.Null(model.Selection);
                Assert.Equal(strings["SyncErrorConflict"], model.Status);
                Assert.Equal(250, (await store.GetPurchaseInputAsync(quotation.Id)).Preparation!.Lines[0].Offer.Item.UnitPrice);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private sealed class UnifiedSynchronizationWriter : IPurchaseSynchronizationWriter
    {
        public int Writes { get; private set; }
        public bool LoseAcknowledgment { get; set; }
        public Task<SynchronizationWriteProfile> GetProfileAsync(OrderWriteKind kind, CancellationToken token) => Task.FromResult(new SynchronizationWriteProfile("synthetic-profile", 112) { Kind = kind });
        public Task<SynchronizationAllocation> ReserveAsync(SynchronizationWritePlan plan, CancellationToken token) => throw new InvalidOperationException("An update must not reserve creation controls.");
        public Task<OrderWriteReceipt> ExecuteAsync(SynchronizationWritePlan plan, CancellationToken token)
        { Writes++; if (LoseAcknowledgment) throw new OperationCanceledException(); return Task.FromResult(new OrderWriteReceipt(plan.OperationId, OrderOperationState.Committed, plan.TargetId)); }
        public Task<OrderWriteReceipt> ReconcileAsync(SynchronizationOperation operation, CancellationToken token) => Task.FromResult(new OrderWriteReceipt(operation.Plan.OperationId, OrderOperationState.Committed, operation.Plan.TargetId));
    }

    private sealed class UnifiedErp : IErpProductLookup, IErpProductSearch, IPurchaseOrderReader, IErpBuyingSupplierLookup
    {
        public bool Offline { get; set; }
        public TaskCompletionSource<PurchaseOrderSnapshot>? PendingRead { get; set; }
        private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        public ErpProductCandidate Product { get; set; } = new("000.01", "Synthetic wire", "wire-family", false, "manufacturer-1", "Synthetic manufacturer", "001-01", 100, "RL", new(2026, 10, 20), 9, "M", "M", "catalog", Now)
        { Brand = new("catalog", "manufacturer-1", "Synthetic brand") };
        public PurchaseOrderSnapshot Order { get; set; } = new("catalog", new("order-1", "42", "distributor-9", "Synthetic distributor", "branch-1", "open", new(2026, 10, 26), true), "revision-1", Now,
            [new("line-1", "other-brand", "Synthetic wire", "RL", "M", 6, 100, 200, 1200, 2, false) { SimilarityCode = "wire-family" }, new("line-2", "other-brand", "Synthetic wire", "RL", "M", 4, 100, 300, 1200, 0, false) { SimilarityCode = "wire-family" }]);
        private void Check(CancellationToken token) { token.ThrowIfCancellationRequested(); if (Offline) throw new ErpException(ErpError.ConnectionFailed); }
        public Task<string> GetCatalogIdentityAsync(CancellationToken token) { Check(token); return Task.FromResult("catalog"); }
        public Task<IReadOnlyList<ErpProductCandidate>> FindByCodeAsync(string code, CancellationToken token) { Check(token); return Task.FromResult<IReadOnlyList<ErpProductCandidate>>([Product with { SupplierProductCode = code }]); }
        public Task<IReadOnlyList<ErpProductCandidate>> FindBySimilarityAsync(string code, CancellationToken token) => FindByCodeAsync(code, token);
        public Task<IReadOnlyList<ErpProductCandidate>> FindByProductCodeAsync(string code, CancellationToken token) { Check(token); return Task.FromResult<IReadOnlyList<ErpProductCandidate>>([Product]); }
        public Task<IReadOnlyList<ErpProductCandidate>> SearchByNameAsync(string name, CancellationToken token) => FindByProductCodeAsync(name, token);
        public Task<IReadOnlyList<PurchaseOrderHeader>> SearchAsync(PurchaseOrderSearch search, CancellationToken token) { Check(token); return Task.FromResult<IReadOnlyList<PurchaseOrderHeader>>([Order.Header]); }
        public Task<PurchaseOrderSnapshot> ReadAsync(string id, CancellationToken token) { Check(token); return PendingRead?.Task ?? Task.FromResult(Order); }
        public Task<BuyingSupplierSnapshot?> FindAsync(ErpBrandIdentity brand, string branch, CancellationToken token) { Check(token); return Task.FromResult<BuyingSupplierSnapshot?>(new(brand, new("catalog", "distributor-9", "Synthetic distributor"), "history-order", "history-revision", branch, new(2026, 10, 26), Now)); }
    }
}
