using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private async Task<(SqliteImportStore Store, Orders Erp)> SeedAsync()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic purchase", "", Now, Now, QuotationState.Draft, 0), Token);
        Guid proposal = Guid.NewGuid(), capture = Guid.NewGuid();
        await store.SaveProposalAsync(new(proposal, Now, "synthetic.pdf", "synthetic-hash", new PdfTemplate { Id = Guid.NewGuid(), Version = 1, SupplierName = "Synthetic supplier" },
            new([], "Synthetic"), new([], [], null, null), [new(capture, "SUP.001", "Synthetic wire", 10, 200, 2000, "ROLL", false, "")])
        { QuotationId = quotation.Id }, [1], Token);
        var input = await store.GetAnalysisInputAsync(quotation.Id, Token);
        ErpProductCandidate product = new("000.01", "Synthetic wire", "wire", false, "supplier", "Synthetic supplier", "SUP.001", 100, "ROLL", null, null, "M", "M", "synthetic-catalog", Now);
        string group = ErpProductIdentity.GroupKey(product);
        var analysis = await store.SaveAnalysisAsync(input.State with { Links = [new(proposal, capture, group, "Synthetic wire", "M", 100, product, Now)], Demands = [new(group, "M", 900)] }, Token);
        await store.PreparePurchaseAsync(new(quotation.Id, input.Quotation.Revision, analysis.Version, 0, [new(proposal, capture)]), Token);
        return (store, new());
    }
    private static PurchaseOrdersViewModel Model(SqliteImportStore store, Orders erp) => new(new Localizer(), store, store, store, erp, erp);
    private static async Task LoadAsync(PurchaseOrdersViewModel model)
    { await model.RefreshReferencesCommand.ExecuteAsync(null); await model.LoadReferenceCommand.ExecuteAsync(null); }
    private static async Task LinkAsync(PurchaseOrdersViewModel model, string id)
    { model.SelectedSearchResult = model.SearchResults.Single(row => row.Header.Id == id); await model.LinkCommand.ExecuteAsync(null); }

    [Fact]
    public async Task ComparisonLinksMultipleOrdersAndRestartsOfflineWithAcceptedShortage()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, erp) = await SeedAsync(); var model = Model(store, erp); await LoadAsync(model);
            PurchaseOrdersView view = new() { DataContext = model }; Window window = new() { Content = view, Width = 1280, Height = 900 }; window.Show();
            try
            {
                Assert.Single(model.References); Assert.True(model.HasReference);
                Assert.Equal(1000, Assert.Single(model.Comparison).Comparison.RemainingQuantity);
                model.Number = "1"; model.FromDate = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
                await model.SearchCommand.ExecuteAsync(null); Assert.Equal("1", erp.LastSearch!.Number); Assert.Equal(new DateOnly(2026, 10, 1), erp.LastSearch.From);
                var results = view.FindControl<ListBox>("OrderSearchResults")!; Assert.Same(model.SearchResults, results.ItemsSource);
                await LinkAsync(model, "order-a"); Assert.Equal(400, Assert.Single(model.Comparison).Comparison.RemainingQuantity);
                model.ShortagesAccepted = true; await model.SaveCommand.ExecuteAsync(null); Assert.Equal(model.Strings["OrdersSaved"], model.Status);
                erp.Offline = true;
                var restarted = Model(new SqliteImportStore(_directory), erp); await LoadAsync(restarted);
                Assert.True(restarted.ShortagesAccepted); Assert.Single(restarted.Orders); Assert.Equal(400, restarted.Comparison[0].Comparison.RemainingQuantity);
                await restarted.RefreshOrdersCommand.ExecuteAsync(null); Assert.Equal(restarted.Strings["ErpErrorConnectionFailed"], restarted.Status); Assert.Single(restarted.Orders);
                erp.Offline = false;
                await LinkAsync(model, "order-b"); Assert.Equal(0, model.Comparison[0].Comparison.RemainingQuantity); Assert.False(model.ShortagesAccepted);
                Assert.Equal(2, model.Comparison[0].Comparison.Contributions.Count); Assert.Contains("10%", model.Comparison[0].Contributions);
                model.MoveUpCommand.Execute(null); Assert.Equal("order-b", model.Orders[0].Snapshot.Header.Id);
                await LinkAsync(model, "order-b"); Assert.Equal(model.Strings["OrderErrorDuplicateOrder"], model.Status); Assert.Equal(2, model.Orders.Count);
                model.UnlinkCommand.Execute(null); Assert.Single(model.Orders); Assert.Equal(400, model.Comparison[0].Comparison.RemainingQuantity);
                model.RefreshLabels(); Dispatcher.UIThread.RunJobs();
            }
            finally { window.Close(); }
            return true;
        }, Token);
    }
    [Fact]
    public async Task PriceOnlyEditAndMissingQuantityCreationRequireSeparatePreviewAndConfirmation()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, erp) = await SeedAsync(); var model = Model(store, erp); await LoadAsync(model);
            await model.SearchCommand.ExecuteAsync(null); await LinkAsync(model, "order-a");
            Assert.False(model.ChangeQuantity); Assert.False(model.ChangePrice);
            model.ChangePrice = true; model.UnitPrice = 195.1234567890m; model.AddChangeCommand.Execute(null);
            Assert.Single(model.Changes); Assert.Null(model.Changes[0].Change.Quantity);
            await model.PreviewUpdateCommand.ExecuteAsync(null); Assert.True(model.IsPreviewing); Assert.False(model.CanWork); Assert.Empty(erp.Plans);
            Assert.Contains("Pedido 1", model.PreviewLabel, StringComparison.Ordinal);
            Assert.Contains("195,123456789", model.Changes[0].Label, StringComparison.Ordinal);
            model.DismissPreviewCommand.Execute(null); Assert.Empty(erp.Plans); Assert.Empty(model.Changes);
            model.ChangePrice = true; model.UnitPrice = 195; model.AddChangeCommand.Execute(null); await model.PreviewUpdateCommand.ExecuteAsync(null);
            await model.ConfirmCommand.ExecuteAsync(null); Assert.Single(erp.Plans); Assert.False(model.IsPreviewing);
            var edit = erp.Plans[0]; Assert.Equal("order-a", edit.ExpectedOrder!.Header.Id); Assert.Null(edit.Changes[0].Quantity); Assert.Equal(195, edit.Changes[0].UnitPrice);
            Assert.Equal(6, erp.Data["order-a"].Lines[0].Quantity); Assert.Equal(OrderOperationState.Committed, model.Operations[0].Operation.State);
            await model.PreviewCreateCommand.ExecuteAsync(null); Assert.True(model.IsPreviewing);
            Assert.Equal(4, model.Changes[0].Change.Quantity); Assert.Equal(200, model.Changes[0].Change.UnitPrice);
            Assert.Single(erp.Plans); await model.ConfirmCommand.ExecuteAsync(null);
            Assert.Equal(2, erp.Plans.Count); Assert.Equal(2, model.Orders.Count); Assert.Equal(0, model.Comparison[0].Comparison.RemainingQuantity);
            Assert.Single(erp.Plans[1].CoverageOrders); Assert.Empty(model.ExcessLines);
            await model.PreviewCreateCommand.ExecuteAsync(null); Assert.False(model.IsPreviewing); Assert.Equal(model.Strings["OrderErrorInvalidChange"], model.Status);
            return true;
        }, Token);
    }
    [Fact]
    public async Task ExplicitManualPairingPreservesCodesAndCanBeClearedOrSaved()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, erp) = await SeedAsync(); var model = Model(store, erp); await LoadAsync(model);
            erp.Data["order-b"] = erp.Data["order-b"] with { Lines = [erp.Data["order-b"].Lines[0] with { ProductCode = "different-product" }] };
            await model.SearchCommand.ExecuteAsync(null); await LinkAsync(model, "order-a"); await LinkAsync(model, "order-b");
            Assert.Equal(400, model.Comparison[0].Comparison.RemainingQuantity); Assert.Single(model.ExcessLines);
            model.PairSelectedLineCommand.Execute(null); Assert.Equal(0, model.Comparison[0].Comparison.RemainingQuantity); Assert.Empty(model.ExcessLines);
            Assert.Equal("different-product", model.SelectedLine!.ProductCode); Assert.Equal("000.01", model.Comparison[0].Comparison.Reference.ProductCode);
            await model.SaveCommand.ExecuteAsync(null);
            var restarted = Model(store, erp); await LoadAsync(restarted); Assert.Equal(0, restarted.Comparison[0].Comparison.RemainingQuantity);
            restarted.ClearManualPairsCommand.Execute(null); Assert.Equal(400, restarted.Comparison[0].Comparison.RemainingQuantity);
            restarted.UseLatestReferenceCommand.Execute(null); Assert.Equal(400, restarted.Comparison[0].Comparison.RemainingQuantity);
            return true;
        }, Token);
    }
    [Fact]
    public async Task CancellationRequiresAdditionalConfirmationAndUnknownOutcomeBlocksFurtherPreviews()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, erp) = await SeedAsync(); var model = Model(store, erp); await LoadAsync(model);
            await model.SearchCommand.ExecuteAsync(null); await LinkAsync(model, "order-a");
            model.CancelLine = true; model.AddChangeCommand.Execute(null); await model.PreviewUpdateCommand.ExecuteAsync(null);
            Assert.True(model.HasCancellations); await model.ConfirmCommand.ExecuteAsync(null); Assert.Empty(erp.Plans); Assert.True(model.IsPreviewing);
            model.CancellationConfirmed = true; erp.FailWrite = true; await model.ConfirmCommand.ExecuteAsync(null);
            Assert.Single(erp.Plans); Assert.Equal(OrderOperationState.Unknown, model.Operations[0].Operation.State); Assert.False(model.IsPreviewing);
            await model.PreviewCreateCommand.ExecuteAsync(null); Assert.Equal(model.Strings["OrderErrorPendingOperation"], model.Status);
            erp.FailWrite = false; await model.ReconcileCommand.ExecuteAsync(null); Assert.Equal(OrderOperationState.NotApplied, model.Operations[0].Operation.State);
            model.SelectedOrder = model.Orders[0]; model.SelectedLine = null; model.SelectedLine = model.Lines[0];
            model.ChangeQuantity = true; model.Quantity = 7; model.AddChangeCommand.Execute(null);
            Assert.Null(model.Changes[0].Change.UnitPrice); model.SelectedChange = model.Changes[0]; model.RemoveChangeCommand.Execute(null); Assert.Empty(model.Changes);
            model.CancelLine = true; model.AddChangeCommand.Execute(null); await model.PreviewUpdateCommand.ExecuteAsync(null);
            model.CancellationConfirmed = true; await model.ConfirmCommand.ExecuteAsync(null);
            Assert.True(erp.Data["order-a"].Lines[0].IsCancelled); Assert.Equal(1000, model.Comparison[0].Comparison.RemainingQuantity);
            return true;
        }, Token);
    }
    [Fact]
    public async Task InvalidUnitsReceivedLinesAndMissingOrderSettingsShowActionableErrors()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, erp) = await SeedAsync(); var model = Model(store, erp); await LoadAsync(model);
            await model.SearchCommand.ExecuteAsync(null); await LinkAsync(model, "order-a");
            erp.Data["order-b"] = erp.Data["order-b"] with { Lines = [erp.Data["order-b"].Lines[0] with { BaseUnit = "KG" }] };
            await LinkAsync(model, "order-b"); model.PairSelectedLineCommand.Execute(null); Assert.Equal(model.Strings["OrderErrorInvalidReference"], model.Status);
            model.SelectedOrder = model.Orders[0]; model.SelectedLine = model.SelectedLine! with { ReceivedQuantity = 1 };
            model.ChangePrice = true; model.AddChangeCommand.Execute(null); Assert.Empty(model.Changes); Assert.Equal(model.Strings["OrderErrorNotEditable"], model.Status);
            model.SelectedLine = model.Lines[0]; model.IncludeMissingCommand.Execute(null); Assert.Single(model.Changes);
            model.Branch = ""; model.OrderStatus = ""; await model.PreviewCreateCommand.ExecuteAsync(null);
            Assert.Equal(model.Strings["OrderErrorInvalidChange"], model.Status); Assert.Empty(erp.Plans);
            model.ToDate = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero); await model.RefreshOrdersCommand.ExecuteAsync(null);
            model.CancelCommand.Execute(null); return true;
        }, Token);
    }
    private sealed class Orders : IPurchaseOrderReader, IPurchaseOrderWriter
    {
        public Dictionary<string, PurchaseOrderSnapshot> Data { get; } = new() { ["order-a"] = Order("order-a", 6, 220), ["order-b"] = Order("order-b", 4, 180) };
        public List<OrderWritePlan> Plans { get; } = [];
        public PurchaseOrderSearch? LastSearch { get; private set; }
        public bool Offline { get; set; }
        public bool FailWrite { get; set; }
        private static PurchaseOrderSnapshot Order(string id, decimal quantity, decimal price) => new("synthetic-catalog",
            new(id, "1", "supplier", "Synthetic supplier", "branch", "open", new(2026, 10, 23), true), "revision-1", Now,
            [new(id + "-line", "000.01", "Synthetic wire", "ROLL", "M", quantity, 100, price, quantity * price, 0, false)]);
        public Task<IReadOnlyList<PurchaseOrderHeader>> SearchAsync(PurchaseOrderSearch search, CancellationToken token)
        { LastSearch = search; return Task.FromResult<IReadOnlyList<PurchaseOrderHeader>>(Data.Values.Select(order => order.Header).ToArray()); }
        public Task<PurchaseOrderSnapshot> ReadAsync(string orderId, CancellationToken token)
        { if (Offline) throw new ErpException(ErpError.ConnectionFailed); return Task.FromResult(Data[orderId]); }
        public Task<string> GetScriptIdentityAsync(OrderWriteKind kind, CancellationToken token) => Task.FromResult("synthetic-script");
        public Task<OrderWriteReceipt> ExecuteAsync(OrderWritePlan plan, CancellationToken token)
        {
            Plans.Add(plan); if (FailWrite) throw new OperationCanceledException();
            string id = plan.ExpectedOrder?.Header.Id ?? "created-" + Plans.Count;
            var order = plan.ExpectedOrder ?? Order(id, 0, 0) with { Lines = [] };
            var lines = order.Lines.ToList();
            foreach (var change in plan.Changes)
            {
                if (change.LineId is not null)
                {
                    int index = lines.FindIndex(line => line.Id == change.LineId); var line = lines[index];
                    lines[index] = line with { Quantity = change.Quantity ?? line.Quantity, UnitPrice = change.UnitPrice ?? line.UnitPrice, IsCancelled = change.Cancel };
                }
                else
                {
                    var reference = plan.Reference.Lines.Single(line => line.Id == change.ReferenceLineId);
                    lines.Add(new(id + "-" + lines.Count, reference.ProductCode, reference.Description, reference.Unit, reference.BaseUnit,
                        change.Quantity!.Value, reference.BaseUnitsPerUnit, change.UnitPrice!.Value, change.Quantity.Value * change.UnitPrice.Value, 0, false));
                }
            }
            Data[id] = order with { Lines = lines, Revision = "revision-" + Plans.Count };
            return Task.FromResult(new OrderWriteReceipt(plan.OperationId, OrderOperationState.Committed, id));
        }
        public Task<OrderWriteReceipt> ReconcileAsync(OrderOperation operation, CancellationToken token) => Task.FromResult(new OrderWriteReceipt(operation.Plan.OperationId, OrderOperationState.NotApplied, null));
    }
}
