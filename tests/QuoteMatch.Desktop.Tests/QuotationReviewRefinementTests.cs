using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuoteMatch.Application.Analysis;
using QuoteMatch.Application.Importing;
using QuoteMatch.Application.Integration;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Pdf;
using QuoteMatch.Infrastructure.Persistence;
using QuoteMatch.Infrastructure.Spreadsheets;
using QuoteMatch.Testing;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateWithoutDesiredQuantity_WarnsBeforePreviewAndKeepsDispatchExplicit(bool proceed)
    {
        await session.Session.Dispatch(async () =>
        {
            await using var fixture = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken, new CombinationDocument("Synthetic supplier", 10, 5));
            var input = fixture.Input.Analysis;
            var state = await fixture.Store.SaveAnalysisAsync(input.State with { Demands = [] });
            await fixture.Store.PreparePurchaseAsync(new(input.Quotation.Id, input.Quotation.Revision, state.Version, fixture.Input.Preparation!.Version,
                [new(input.Proposals[0].Id, input.Proposals[0].Review[0].CaptureId)]));
            var model = new PurchaseSynchronizationViewModel(new(), fixture.Store, fixture, fixture, fixture, fixture.Store);
            await model.LoadAsync(input.Quotation.Id); model.Branch = "branch";
            var window = new PurchaseSynchronizationWindow { DataContext = model }; window.Show();
            try
            {
                window.FindControl<Button>("BuildSyncPreview")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                var warning = Assert.IsType<DemandWarningWindow>(Assert.Single(window.OwnedWindows));
                Assert.False(model.HasPreview); Assert.Equal(0, fixture.WriteCount);
                Assert.False(warning.FindControl<Button>("ReviewMissingDemand")!.IsEnabled);
                warning.FindControl<Button>(proceed ? "ContinueWithoutDemand" : "CancelMissingDemand")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Yield(); Dispatcher.UIThread.RunJobs();
                while (window.OwnedWindows.Count > 0 || model.IsBusy) { await Task.Yield(); Dispatcher.UIThread.RunJobs(); }
                Assert.True(model.HasPreview == proceed, model.Status); Assert.Equal(0, fixture.WriteCount); Assert.Equal(0, fixture.ReserveCount);
                if (proceed)
                {
                    model.Differences.Single().IsSelected = true; await model.ReviewSelectionCommand.ExecuteAsync(null);
                    Assert.Equal(10m, Assert.Single(model.Selection!.Lines).Quantity);
                    Assert.True(model.CanConfirm);
                    Assert.Equal(0, fixture.WriteCount); Assert.Equal(0, fixture.ReserveCount);
                }
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConversionGrid_ReviewsConfirmedRowsAndChoosesOneDocumentWithoutAddingQuantities()
    {
        await session.Session.Dispatch(async () =>
        {
            await using var fixture = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken,
                new("Synthetic A", 10, 5), new("Synthetic B", 20, 4));
            var preferences = CreateStore(); var parent = new QuotationAnalysisViewModel(new(), fixture.Store, new InternalBatchLookup(0), fixture.Store, preferences);
            await parent.LoadAsync(fixture.Input.Analysis.Quotation.Id);
            var grid = (await parent.CreateProductReviewAsync())!;
            Assert.Empty(grid.VisibleRows); Assert.Equal(2, grid.Rows.Count); Assert.False(grid.CanSave);
            grid.OnlyPending = false; grid.ShowDesiredQuantity = true; Assert.Equal(2, grid.VisibleRows.Count);
            grid.SelectedDocument = grid.Documents.Single(document => document.ProposalId == fixture.Input.Analysis.Proposals[1].Id);
            grid.UseDocumentQuantitiesCommand.Execute(null);
            Assert.All(grid.Rows, row => { Assert.Equal(20m, row.RequestedQuantity); Assert.Equal(DemandOrigin.Document, row.Demand!.Origin); Assert.True(row.IsSelected); });
            await grid.SaveCommand.ExecuteAsync(null); Assert.True(grid.IsSaved, grid.Status);
            var saved = await fixture.Store.GetAnalysisInputAsync(fixture.Input.Analysis.Quotation.Id);
            Assert.Equal(20m, Assert.Single(saved.State.Demands).Quantity);
            Assert.Equal(grid.SelectedDocument.ProposalId, saved.State.Demands[0].DocumentSource!.ProposalId);
            Assert.Contains("synthetic-1.xlsx", parent.DemandOriginLabel);
            Assert.DoesNotContain("{0}", parent.DemandOriginLabel); Assert.DoesNotContain("{0}", parent.DemandOriginHelp);
            grid.Dispose();
            var next = parent.CreateProductReview(); Assert.False(next.OnlyPending); Assert.True(next.ShowDesiredQuantity);
            next.Rows[0].RequestedQuantity = 25;
            Assert.All(next.Rows, row => Assert.Equal(25m, row.RequestedQuantity));
            Assert.All(next.Rows, row => Assert.Equal(DemandOrigin.Manual, row.Demand!.Origin));
            next.Rows[0].RequestedQuantity = null;
            Assert.All(next.Rows, row => Assert.Null(row.RequestedQuantity));
            Assert.True(next.CanSave); await next.SaveCommand.ExecuteAsync(null); Assert.True(next.IsSaved, next.Status);
            Assert.Empty((await fixture.Store.GetAnalysisInputAsync(saved.Quotation.Id)).State.Demands);
            next.Dispose();
            Assert.Equal(0, fixture.WriteCount);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_UsesSavedMappingsAutomaticallyAndKeepsTheSourceWhenMappingIsUnavailable(bool unavailable)
    {
        await session.Session.Dispatch(async () =>
        {
            Directory.CreateDirectory(_directory); string path = Path.Combine(_directory, "synthetic.xlsx");
            var bytes = SyntheticWorkbook.Xlsx(SpreadsheetRows); await File.WriteAllBytesAsync(path, bytes);
            var store = new SqliteImportStore(_directory); await store.InitializeAsync();
            var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic automatic mapping", "", Now, Now, Domain.Quotations.QuotationState.Draft, 0));
            var template = await store.SaveTemplateAsync(SpreadsheetTemplate with { CodeKind = ProductCodeKind.Internal });
            var lookup = new AutomaticImportCatalog(unavailable);
            var product = (await new InternalBatchLookup(1).FindByProductCodeAsync("00.1-U", TestContext.Current.CancellationToken)).Single();
            await store.SaveMappingAsync(new(Guid.NewGuid(), 0,
                new(template.SupplierName, "00.1-U", "Synthetic product", "", "un") { CodeKind = ProductCodeKind.Internal },
                product, "un", 2.5m, ProductMappingState.Active, Now, Now), TestContext.Current.CancellationToken);
            using var model = new ImportWorkspaceViewModel(new(), store, new PdfImportService(new PdfPigTextExtractor(), store, new ExcelWorkbookReader()), quotation.Id,
                automaticMappings: new SavedProductMappingService(store, store, lookup));
            await model.InitializeAsync(); model.SelectedTemplate = model.Templates.Single();
            await model.PreviewFileAsync(path); await model.ConfirmCommand.ExecuteAsync(null);
            Assert.True(model.IsConsulting); Assert.False(model.CanSave); var proposal = Assert.Single(model.Proposals);
            Assert.Equal(bytes, await store.GetOriginalPdfAsync(proposal.Id));
            var input = await store.GetAnalysisInputAsync(quotation.Id);
            if (unavailable)
            {
                Assert.Empty(input.State.Links); Assert.Contains(model.Strings["AutoMappingReviewPending"], model.Status);
            }
            else
            {
                var link = Assert.Single(input.State.Links); Assert.Equal(2.5m, link.BaseUnitsPerQuotedUnit); Assert.Equal(ProductGroupingVersion.ExactProductCode, link.GroupingVersion);
                Assert.Contains(model.Strings.Format("ImportedMappingsApplied", 1), model.Status);
            }
            Assert.Empty(await store.GetSynchronizationOperationsAsync(quotation.Id, TestContext.Current.CancellationToken));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Escape_ClosesOwnedReviewAndSearchDialogsWithoutClosingTheWorkspace()
    {
        await session.Session.Dispatch(async () =>
        {
            var (_, parent, _, _) = await BatchWorkspaceAsync();
            var owner = new Window(); owner.Show();
            try
            {
                var review = new ProductReviewWindow { DataContext = parent.CreateProductReview() }; review.Show(owner);
                var search = new ProductSearchWindow { DataContext = parent.CreateProductSearch() }; search.Show(review);
                Escape(search); Dispatcher.UIThread.RunJobs();
                Assert.False(search.IsVisible); Assert.True(review.IsVisible);
                Escape(review); Dispatcher.UIThread.RunJobs();
                Assert.False(review.IsVisible); Assert.True(owner.IsVisible);
                var warning = new DemandWarningWindow { DataContext = new DemandWarningViewModel(parent.Strings, 1, true) };
                var result = warning.ShowDialog<DemandReviewAction>(owner);
                Escape(warning); Assert.Equal(DemandReviewAction.Cancel, await result);
                Escape(owner); Assert.True(owner.IsVisible);
            }
            finally { owner.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static void Escape(Window window)
    {
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        if (window.IsVisible) window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
    }

    [Fact]
    public async Task ConversionGrid_UsesOnlyTheSelectedReferenceOrderAndSavesItWithTheReview()
    {
        await session.Session.Dispatch(async () =>
        {
            var store = new SqliteImportStore(_directory); await store.InitializeAsync(); var erp = new UnifiedErp();
            var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic reference choice", "", Now, Now, QuotationState.Draft, 0));
            var proposal = AnalysisProposal(quotation.Id, "Synthetic supplier", 20, 250, "RL"); await store.SaveProposalAsync(proposal, [1]);
            var references = new QuotationOrderInputsViewModel(new(), store, erp);
            var parent = new QuotationAnalysisViewModel(new(), store, erp, store, orderInputs: references);
            await parent.LoadAsync(quotation.Id); await parent.IdentifyPendingCommand.ExecuteAsync(null);
            using var grid = (await parent.CreateProductReviewAsync())!;
            await references.SearchCommand.ExecuteAsync(null); await references.LinkCommand.ExecuteAsync(null);
            erp.Order = erp.Order with { Header = erp.Order.Header with { Id = "order-2", Number = "43" }, Lines = [erp.Order.Lines[0] with { Quantity = 3 }] };
            await references.SearchCommand.ExecuteAsync(null); await references.LinkCommand.ExecuteAsync(null);
            Assert.Equal(2, references.Orders.Count);
            grid.SelectedDemandOrder = references.Orders.Single(order => order.Snapshot.Header.Id == "order-2");
            grid.UseOrderQuantitiesCommand.Execute(null);
            var row = Assert.Single(grid.Rows); Assert.Equal(300m, row.RequestedQuantity); Assert.Equal(DemandOrigin.ReferenceOrder, row.Demand!.Origin);
            Assert.Equal("order-2", Assert.Single(row.Demand.Sources).OrderId);
            await grid.SaveCommand.ExecuteAsync(null); Assert.True(grid.IsSaved, grid.Status);
            var saved = await store.GetAnalysisInputAsync(quotation.Id);
            Assert.Equal(2, saved.Inputs.Orders.Count); Assert.Equal(300m, Assert.Single(saved.State.Demands).Quantity);
            Assert.Equal(DemandOrigin.ReferenceOrder, saved.State.Demands[0].Origin);
            Assert.Equal("order-2", Assert.Single(saved.State.Demands[0].Sources).OrderId);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1280)]
    public async Task ConversionGrid_AlignsSelectorsAcrossRowsWithDifferentSourceHeights(int width)
    {
        await session.Session.Dispatch(() =>
        {
            var tall = ConversionRow(color: "Synthetic blue"); var shortRow = ConversionRow();
            var input = new AnalysisInput(new(Guid.NewGuid(), "Synthetic alignment", "", Now, Now, QuotationState.Draft, 0), [tall.Proposal, shortRow.Proposal], new(Guid.Empty, 0, 0, Now, [], []));
            var store = new SqliteImportStore(_directory);
            var parent = new QuotationAnalysisViewModel(new(), store, new InternalBatchLookup(0));
            using var model = new ProductReviewViewModel(parent, input, [tall, shortRow], new(), false);
            var window = new ProductReviewWindow { DataContext = model, Width = width }; window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var selectors = window.GetVisualDescendants().OfType<ComboBox>().Where(combo => combo.Name == "ReviewProductSelector").ToArray();
                Assert.Equal(2, selectors.Length);
                foreach (var selector in selectors)
                {
                    var container = selector.GetVisualAncestors().OfType<ListBoxItem>().First();
                    var factor = container.GetVisualDescendants().OfType<NumericUpDown>().Single(field => field.Name == "ReviewConversionFactor");
                    Assert.InRange(Math.Abs(selector.TranslatePoint(default, window)!.Value.Y - factor.TranslatePoint(default, window)!.Value.Y), 0, 1);
                }
                Assert.Equal(selectors[0].Bounds.Height, selectors[1].Bounds.Height);
                if (width == 1280)
                {
                    model.ShowDesiredQuantity = true; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    var quantities = window.GetVisualDescendants().OfType<NumericUpDown>().Where(field => field.Name == "ReviewDesiredQuantity").ToArray();
                    Assert.Equal(2, quantities.Length);
                    foreach (var quantity in quantities)
                    {
                        Assert.True(quantity.IsEffectivelyVisible);
                        Assert.True(quantity.TranslatePoint(default, window)!.Value.X + quantity.Bounds.Width <= window.ClientSize.Width - 16,
                            $"Desired field: {quantity.Bounds}, position: {quantity.TranslatePoint(default, window)}, window: {window.ClientSize}, minimum: {model.GridMinimumWidth}, list: {window.FindControl<ListBox>("ProductReviewRows")!.Bounds}, header: {window.FindControl<TextBlock>("DesiredQuantityHeader")!.Bounds}.");
                        Assert.InRange(Math.Abs(quantity.TranslatePoint(default, window)!.Value.X - window.FindControl<TextBlock>("DesiredQuantityHeader")!.TranslatePoint(default, window)!.Value.X), 0, 1);
                    }
                }
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NewOrder_CanPreviewWithoutAManualInitialStatus()
    {
        await session.Session.Dispatch(async () =>
        {
            await using var fixture = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken, new CombinationDocument("Synthetic A", 10, 5));
            var model = new PurchaseSynchronizationViewModel(new(), fixture.Store, fixture, fixture, fixture, fixture.Store);
            await model.LoadAsync(fixture.Proposal.QuotationId); model.Branch = "branch";
            Assert.True(model.CanPreview, model.PreviewBlockedReason);
            await model.PrepareOrderPreviewAsync();
            Assert.True(model.CanConfirm, model.Status);
            Assert.Equal(0, fixture.WriteCount); Assert.Equal(0, fixture.ReserveCount);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NewOrder_ValidatedPreviewEnablesExplicitCreationWithoutACheckbox()
    {
        await session.Session.Dispatch(async () =>
        {
            await using var fixture = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken, new CombinationDocument("Synthetic A", 10, 5));
            var model = new PurchaseSynchronizationViewModel(new(), fixture.Store, fixture, fixture, fixture, fixture.Store);
            await model.LoadAsync(fixture.Proposal.QuotationId); model.Branch = "branch";
            var window = new PurchaseSynchronizationWindow { DataContext = model }; window.Show();
            try
            {
                await model.PrepareOrderPreviewAsync(); Dispatcher.UIThread.RunJobs();
                Assert.True(model.CanConfirm, model.Status);
                Assert.True(window.FindControl<Button>("ConfirmSynchronization")!.IsEnabled);
                Assert.Null(window.FindControl<CheckBox>("ConfirmSynchronizationInput"));
                Assert.Null(window.FindControl<TextBox>("InitialOrderStatus"));
                Assert.Equal(0, fixture.WriteCount); Assert.Equal(0, fixture.ReserveCount);
                await model.ConfirmWriteCommand.ExecuteAsync(null);
                Assert.Equal(1, fixture.WriteCount); Assert.Equal(1, fixture.ReserveCount);
                Assert.Null(fixture.Dispatched!.Status);
                Assert.False(model.CanConfirm);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NewOrder_UsesOnePreviewAndOneExplicitCreateActionAndPreservesItemExclusions()
    {
        await session.Session.Dispatch(async () =>
        {
            await using var fixture = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken,
                new("Synthetic A", 10, 5, "P1"), new("Synthetic B", 10, 8, "P2"));
            await fixture.SelectProductsAsync(TestContext.Current.CancellationToken, "P1", "P2");
            var model = new PurchaseSynchronizationViewModel(new(), fixture.Store, fixture, fixture, fixture, fixture.Store);
            await model.LoadAsync(fixture.Proposal.QuotationId); model.Branch = "branch";
            Assert.True(model.CanPreview); Assert.Empty(model.PreviewBlockedReason); Assert.Empty(model.WriteConfigurationStatus);
            var window = new PurchaseSynchronizationWindow { DataContext = model }; window.Show();
            try
            {
                Assert.Equal(model.Strings["SyncCreatePreview"], window.FindControl<Button>("BuildSyncPreview")!.Content);
                var create = window.FindControl<Button>("ConfirmSynchronization")!;
                Assert.Equal(model.Strings["SyncCreateOrder"], create.Content); Assert.True(create.IsEffectivelyVisible); Assert.False(create.IsEnabled);
                Assert.Contains("ERP", model.FinancialPolicies[0].SourceHelp);
                await model.PrepareOrderPreviewAsync();
                Assert.Equal(2, model.Selection!.Lines.Count); Assert.True(model.CanConfirm); Assert.Equal(0, fixture.WriteCount); Assert.Equal(0, fixture.ReserveCount);
                model.Differences.Single(row => row.Difference.Source!.Offer.Item.ProductCode == "P2").IsSelected = false;
                Assert.False(model.HasSelection); await model.PrepareOrderPreviewAsync();
                Assert.Equal("P1", Assert.Single(model.Selection!.Lines).ProductCode);
                Dispatcher.UIThread.RunJobs(); Assert.True(create.IsEnabled);
                create.Focus(); window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                while (model.IsBusy) await Task.Yield();
                Assert.True(fixture.WriteCount == 1, model.Status); Assert.Equal(1, fixture.ReserveCount);
                Assert.Single(fixture.Dispatched!.Selection.Lines); Assert.False(model.CanConfirm);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(ErpOperation.CreatePurchaseSynchronization)]
    [InlineData(ErpOperation.ReservePurchaseSynchronization)]
    public async Task NewOrder_MissingCreationScriptNamesTheCorrectOperationAndCannotDispatch(ErpOperation missingOperation)
    {
        await session.Session.Dispatch(async () =>
        {
            await using var fixture = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken, new CombinationDocument("Synthetic A", 10, 5));
            var config = new ErpConfiguration { UseReadConnectionForWrites = true, WriteOperatorCode = 123 };
            if (missingOperation == ErpOperation.ReservePurchaseSynchronization)
                config.Queries[ErpOperation.CreatePurchaseSynchronization] = "EXEC dbo.SyntheticCreate @RequestJson";
            await fixture.Store.SaveErpConfigurationAsync(config);
            var writer = new QuoteMatch.Infrastructure.Erp.ErpPurchaseSynchronizationWriter(fixture.Store, new QuoteMatch.Infrastructure.Erp.ProtectedErpCredentials(fixture.DirectoryPath));
            var model = new PurchaseSynchronizationViewModel(new(), fixture.Store, fixture, fixture, writer, fixture.Store);
            await model.LoadAsync(fixture.Proposal.QuotationId);
            Assert.Contains(model.Strings["QueryName" + missingOperation], model.WriteConfigurationStatus);
            Assert.Contains(missingOperation.ToString(), model.WriteConfigurationStatus);
            Assert.DoesNotContain("FindProduct", model.WriteConfigurationStatus);
            model.Branch = "branch";
            await model.PrepareOrderPreviewAsync();
            Assert.Contains(model.Strings["QueryName" + missingOperation], model.Status);
            Assert.False(model.CanConfirm); Assert.Equal(0, fixture.WriteCount); Assert.Equal(0, fixture.ReserveCount);
            Assert.Empty(await fixture.Store.GetSynchronizationOperationsAsync(fixture.Proposal.QuotationId, TestContext.Current.CancellationToken));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NewOrder_MissingWriteSetupIsVisibleBeforePreviewAndCannotDispatch()
    {
        await session.Session.Dispatch(async () =>
        {
            await using var fixture = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken, new CombinationDocument("Synthetic A", 10, 5));
            var writer = new QuoteMatch.Infrastructure.Erp.ErpPurchaseSynchronizationWriter(fixture.Store, new QuoteMatch.Infrastructure.Erp.ProtectedErpCredentials(fixture.DirectoryPath));
            var model = new PurchaseSynchronizationViewModel(new(), fixture.Store, fixture, fixture, writer, fixture.Store);
            await model.LoadAsync(fixture.Proposal.QuotationId);
            Assert.NotEmpty(model.WriteConfigurationStatus);
            model.Branch = "branch";
            await model.PrepareOrderPreviewAsync();
            Assert.False(model.CanConfirm); Assert.Equal(0, fixture.WriteCount); Assert.Equal(0, fixture.ReserveCount);
            Assert.Empty(await fixture.Store.GetSynchronizationOperationsAsync(fixture.Proposal.QuotationId, TestContext.Current.CancellationToken));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private sealed class AutomaticImportCatalog(bool unavailable) : IErpProductSearch
    {
        public Task<string> GetCatalogIdentityAsync(CancellationToken token) => unavailable
            ? throw new ErpException(ErpError.NotConfigured) : Task.FromResult("synthetic-catalog");
        public Task<IReadOnlyList<ErpProductCandidate>> FindByProductCodeAsync(string code, CancellationToken token) => throw new InvalidOperationException("Automatic mapping must not query ERP.");
        public Task<IReadOnlyList<ErpProductCandidate>> SearchByNameAsync(string name, CancellationToken token) => throw new InvalidOperationException("Automatic mapping must not query ERP.");
    }
}
