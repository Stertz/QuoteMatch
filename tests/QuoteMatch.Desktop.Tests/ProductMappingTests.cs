using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuoteMatch.Application.Integration;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task ImportColor_PreservesCaptureEvidenceAndUsesTheReviewedVariantInTheReference()
    {
        await session.Session.Dispatch(() =>
        {
            PdfTemplate template = new()
            {
                Name = "Synthetic",
                SupplierName = "Synthetic supplier",
                DefaultUnit = "RL",
                RecordStartPattern = @"^001\s",
                RecordPattern = @"^(?<code>001)\s+(?<description>Cable)\s+(?<color>Blue)\s+(?<quantity>2)\s+(?<unitPrice>180)\s+(?<total>360)$"
            };
            var captured = Assert.Single(QuoteMatch.Application.Importing.TemplateExtraction.Extract(new([new(1, "001 Cable Blue 2 180 360")], "Synthetic"), template).Items);
            ImportItemViewModel model = new(captured, new Localizer()); Assert.Equal("Blue", model.Color);
            model.Color = "Red";
            ReviewedItem reviewed = model.ToReview(); Assert.Equal("Red", reviewed.Color); Assert.Equal("Blue", captured.OriginalFields["color"]);
            Assert.Contains("Red", new SupplierProductReference(template.SupplierName, reviewed.ProductCode, reviewed.Description, reviewed.Color, reviewed.Unit).Display);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MappingSaveFailure_LeavesTheConfirmedItemAndReportsThePendingReference()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic study", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            var proposal = AnalysisProposal(quote.Id, "Supplier A", 2, 180, "RL");
            await store.SaveProposalAsync(proposal, [1], TestContext.Current.CancellationToken);
            MappingLookup erp = new();
            QuotationAnalysisViewModel model = new(new Localizer(), store, erp, new UnavailableMappingStore(store)); await model.LoadAsync(quote.Id);
            model.SelectProduct(erp.Product); model.ConversionFactor = 100;
            await model.LinkErpCommand.ExecuteAsync(null);
            Assert.Equal(model.Strings["MappingSavePending"], model.Status);
            Assert.Equal("123", Assert.Single((await store.GetAnalysisInputAsync(quote.Id)).State.Links).Product!.ProductCode);
            Assert.Null(await store.FindMappingAsync("synthetic-catalog", SupplierProductReference.From(proposal, proposal.Review[0]), TestContext.Current.CancellationToken));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private sealed class UnavailableMappingStore(SqliteImportStore store) : IProductMappingRepository
    {
        public Task<SupplierProductMapping?> FindMappingAsync(string catalogIdentity, SupplierProductReference source, CancellationToken token) => store.FindMappingAsync(catalogIdentity, source, token);
        public Task<SupplierProductMapping> SaveMappingAsync(SupplierProductMapping mapping, CancellationToken token) => throw new IOException("Synthetic unavailable storage.");
    }

    [Fact]
    public async Task ProductMapping_PersistsAcrossQuotationsWithoutRewritingEarlierLinks()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic first study", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            var proposal = AnalysisProposal(quote.Id, "Supplier A", 2, 180, "RL");
            proposal = proposal with { Review = [proposal.Review[0] with { Color = "Blue" }] };
            await store.SaveProposalAsync(proposal, [1], TestContext.Current.CancellationToken);
            MappingLookup erp = new();
            QuotationAnalysisViewModel model = new(new Localizer(), store, erp, store); await model.LoadAsync(quote.Id);
            Assert.Contains("Blue", model.SourceReference); Assert.Contains("RL", model.SourceReference);
            await model.SearchErpCommand.ExecuteAsync(null); Assert.Empty(model.Candidates);
            model.SelectProduct(erp.Product);
            Assert.True(model.RememberMapping); Assert.Null(model.ConversionFactor);
            await model.LinkErpCommand.ExecuteAsync(null);
            Assert.Equal(model.Strings["AnalysisErrorInvalidLink"], model.Status);
            Assert.Empty((await store.GetAnalysisInputAsync(quote.Id)).State.Links);
            model.ConversionFactor = 100;
            await model.LinkErpCommand.ExecuteAsync(null);
            Assert.Equal(model.Strings["MappingAndLinkSaved"], model.Status);
            Assert.Equal("123", model.SelectedCandidate!.Code); Assert.False(model.IsIdentificationOpen);
            var reference = SupplierProductReference.From(proposal, proposal.Review[0]);
            var saved = await store.FindMappingAsync("synthetic-catalog", reference, TestContext.Current.CancellationToken);
            Assert.NotNull(saved); Assert.Equal("123", saved.Product.ProductCode); Assert.Equal(100m, saved.ConversionFactor);

            var nextQuote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic next study", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            var nextProposal = proposal with { Id = Guid.NewGuid(), QuotationId = nextQuote.Id, Review = [proposal.Review[0] with { CaptureId = Guid.NewGuid(), Quantity = 3, ReportedTotal = 540 }] };
            await store.SaveProposalAsync(nextProposal, [2], TestContext.Current.CancellationToken);
            QuotationAnalysisViewModel next = new(new Localizer(), store, erp, store); await next.LoadAsync(nextQuote.Id);
            await next.IdentifyPendingCommand.ExecuteAsync(null);
            Assert.Equal("123", next.SelectedCandidate!.Code); Assert.Equal(100m, next.ConversionFactor);
            Assert.Equal(next.Strings["IdentificationSavedMapping"], next.IdentificationStatus);
            Assert.Empty((await store.GetAnalysisInputAsync(nextQuote.Id)).State.Links);
            next.SelectProduct(erp.Product with { ProductCode = "456" }); next.ConversionFactor = 250;
            await next.LinkErpCommand.ExecuteAsync(null);
            Assert.Equal("456", next.SelectedCandidate!.Code); Assert.False(next.IsIdentificationOpen);
            Assert.Equal("456", (await store.FindMappingAsync("synthetic-catalog", reference, TestContext.Current.CancellationToken))!.Product.ProductCode);
            var oldLink = Assert.Single((await store.GetAnalysisInputAsync(quote.Id)).State.Links);
            Assert.Equal("123", oldLink.Product!.ProductCode); Assert.Equal(100m, oldLink.BaseUnitsPerQuotedUnit);

            erp.Fail = true;
            QuotationAnalysisViewModel offline = new(new Localizer(), store, erp, store); await offline.LoadAsync(nextQuote.Id);
            await offline.SearchErpCommand.ExecuteAsync(null);
            Assert.Equal(offline.Strings["IdentificationOffline"], offline.IdentificationStatus);
            Assert.Equal("456", offline.SelectedCandidate!.Code); Assert.Equal(250m, offline.ConversionFactor);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ProductSearchModal_SearchesInternalCodeOrNameAndRequiresAnActiveSelection()
    {
        await session.Session.Dispatch(async () =>
        {
            MappingLookup erp = new();
            var model = new ProductSearchViewModel(new Localizer(), erp);
            ProductSearchWindow window = new() { DataContext = model }; window.Show();
            try
            {
                var mode = window.FindControl<ComboBox>("ProductSearchMode")!;
                var text = window.FindControl<TextBox>("ProductSearchText")!;
                var list = window.FindControl<ListBox>("ProductSearchResults")!;
                var select = window.FindControl<Button>("SelectProductButton")!;
                Assert.False(select.IsEnabled);
                text.Text = "123"; Dispatcher.UIThread.RunJobs(); await model.SearchCommand.ExecuteAsync(null);
                Assert.Equal("123", erp.ProductCodeRequest); Assert.Single(model.Results);
                list.SelectedItem = model.Results[0]; Dispatcher.UIThread.RunJobs(); Assert.True(select.IsEnabled);
                erp.Product = erp.Product with { IsInactive = true };
                Assert.Null(await model.ConfirmSelectionAsync(TestContext.Current.CancellationToken));
                Assert.Equal(model.Strings["ProductSelectionUnavailable"], model.Status);
                erp.Product = erp.Product with { IsInactive = false };
                mode.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); Assert.Empty(model.Results); Assert.False(select.IsEnabled);
                text.Text = "Synthetic cable"; Dispatcher.UIThread.RunJobs(); await model.SearchCommand.ExecuteAsync(null);
                Assert.Equal("Synthetic cable", erp.NameRequest); Assert.Single(model.Results);
                erp.Product = erp.Product with { IsInactive = true };
                await model.SearchCommand.ExecuteAsync(null); Assert.Empty(model.Results);
                var includeInactive = window.FindControl<ToggleSwitch>("IncludeInactiveProducts")!;
                Assert.False(includeInactive.IsChecked);
                includeInactive.IsChecked = true; Dispatcher.UIThread.RunJobs(); await model.SearchCommand.ExecuteAsync(null);
                Assert.True(model.IncludeInactive);
                list.SelectedItem = Assert.Single(model.Results); Dispatcher.UIThread.RunJobs(); Assert.False(select.IsEnabled);
                includeInactive.IsChecked = false; Dispatcher.UIThread.RunJobs(); Assert.Empty(model.Results); Assert.Null(model.SelectedProduct);
                erp.Fail = true; await model.SearchCommand.ExecuteAsync(null);
                Assert.Equal(model.Strings["ErpErrorConnectionFailed"], model.Status); Assert.Empty(model.Results);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Analysis_SearchButtonOpensModalAndSelectionReturnsToTheReference()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic study", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            await store.SaveProposalAsync(AnalysisProposal(quote.Id, "A", 2, 180, "RL"), [1], TestContext.Current.CancellationToken);
            QuotationAnalysisViewModel model = new(new Localizer(), store, new MappingLookup(), store); await model.LoadAsync(quote.Id);
            QuotationAnalysisView view = new() { DataContext = model }; Window owner = new() { Content = view }; owner.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var edit = view.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("comparisonToggle"));
                edit.Focus(); owner.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.None, null);
                owner.KeyRelease(Key.F2, RawInputModifiers.None, PhysicalKey.None, null);
                Dispatcher.UIThread.RunJobs();
                var editor = Assert.IsType<QuotationItemEditorWindow>(Assert.Single(owner.OwnedWindows));
                editor.FindControl<Button>("OpenProductSearchButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                var modal = Assert.IsType<ProductSearchWindow>(Assert.Single(editor.OwnedWindows));
                var search = Assert.IsType<ProductSearchViewModel>(modal.DataContext);
                search.SearchText = "123"; await search.SearchCommand.ExecuteAsync(null); search.SelectedProduct = search.Results[0];
                modal.FindControl<Button>("SelectProductButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Yield(); Dispatcher.UIThread.RunJobs();
                Assert.Equal("123", model.SelectedCandidate!.Code); Assert.Null(model.ConversionFactor); Assert.True(model.RememberMapping);
                Assert.Empty((await store.GetAnalysisInputAsync(quote.Id)).State.Links);
                editor.Close();
            }
            finally { owner.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private sealed class MappingLookup : IErpProductLookup, IErpProductSearch
    {
        public ErpProductCandidate Product { get; set; } = new("123", "Synthetic cable", "Synthetic group", false, "1", "Supplier A", "", null, "", null, null, "MT", "MT", "synthetic-catalog", DateTime.UtcNow);
        public bool Fail { get; set; }
        public string? ProductCodeRequest { get; private set; }
        public string? NameRequest { get; private set; }
        public Task<string> GetCatalogIdentityAsync(CancellationToken token) => Task.FromResult("synthetic-catalog");
        public Task<IReadOnlyList<ErpProductCandidate>> FindByCodeAsync(string code, CancellationToken token)
        {
            if (Fail) throw new ErpException(ErpError.ConnectionFailed);
            return Task.FromResult<IReadOnlyList<ErpProductCandidate>>([]);
        }
        public Task<IReadOnlyList<ErpProductCandidate>> FindBySimilarityAsync(string code, CancellationToken token) => throw new InvalidOperationException();
        public Task<IReadOnlyList<ErpProductCandidate>> FindByProductCodeAsync(string code, CancellationToken token) { ProductCodeRequest = code; return Search(); }
        public Task<IReadOnlyList<ErpProductCandidate>> SearchByNameAsync(string name, CancellationToken token) { NameRequest = name; return Search(); }
        private Task<IReadOnlyList<ErpProductCandidate>> Search()
        {
            if (Fail) throw new ErpException(ErpError.ConnectionFailed);
            return Task.FromResult<IReadOnlyList<ErpProductCandidate>>([Product]);
        }
    }
}
