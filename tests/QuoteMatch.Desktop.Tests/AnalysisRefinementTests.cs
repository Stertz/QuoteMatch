using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuoteMatch.Desktop.Controls;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductSearchSelection_RemainsVisibleAfterPointerLeavesAndKeyboardChangesIt(bool dark)
    {
        await session.Session.Dispatch(() =>
        {
            ProductSearchViewModel model = new(new Localizer(), null);
            MappingLookup lookup = new();
            model.Results.Add(new(lookup.Product, "Synthetic active product"));
            model.Results.Add(new(lookup.Product with { ProductCode = "456", Name = "Other synthetic product" }, "Synthetic active product"));
            ProductSearchWindow window = new() { DataContext = model, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var list = window.FindControl<ListBox>("ProductSearchResults")!;
                var first = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
                var second = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
                Border Card(ListBoxItem item) => item.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("card"));
                Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
                Color selected = ColorOf(Assert.IsAssignableFrom<IBrush>(window.FindResource(window.ActualThemeVariant, "SelectionBrush")));
                Color surface = ColorOf(Assert.IsAssignableFrom<IBrush>(window.FindResource(window.ActualThemeVariant, "SurfaceBrush")));
                Point point = second.TranslatePoint(new Point(40, second.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point); window.MouseDown(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
                Assert.Equal(selected, ColorOf(Card(second).Background));
                var presenter = second.GetVisualDescendants().OfType<ContentPresenter>().First(control => control.TemplatedParent == second);
                Assert.Equal(Colors.Transparent, ColorOf(presenter.Background));
                window.MouseUp(point, MouseButton.Left);
                window.MouseMove(new Point(10, 10)); Dispatcher.UIThread.RunJobs();
                Assert.Same(model.Results[1], model.SelectedProduct);
                Assert.Equal(selected, ColorOf(Card(second).Background));
                Assert.Equal(surface, ColorOf(Card(first).Background));
                second.Focus(); window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.None, null); window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.None, null); Dispatcher.UIThread.RunJobs();
                Assert.Same(model.Results[0], model.SelectedProduct);
                Assert.Equal(selected, ColorOf(Card(first).Background));
                Assert.Equal(surface, ColorOf(Card(second).Background));
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Relink_RequiresANewConversionWhenTheBaseUnitChanges()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic unit correction", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            var proposal = AnalysisProposal(quote.Id, "A", 1000, 2);
            await store.SaveProposalAsync(proposal, [1], TestContext.Current.CancellationToken);
            MappingLookup lookup = new(); QuotationAnalysisViewModel model = new(new Localizer(), store, lookup, store); await model.LoadAsync(quote.Id);
            model.SelectProduct(lookup.Product); model.ConversionFactor = 1; await model.LinkErpCommand.ExecuteAsync(null);
            model.SelectProduct(lookup.Product with { ProductCode = "999", BaseUnit = "KG" });
            Assert.Null(model.ConversionFactor); Assert.Equal("KG", model.BaseUnit);
            await model.LinkErpCommand.ExecuteAsync(null);
            Assert.Equal(model.Strings["AnalysisErrorInvalidLink"], model.Status);
            Assert.Equal("MT", Assert.Single((await store.GetAnalysisInputAsync(quote.Id)).State.Links).BaseUnit);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnalysisPriceCards_ShowPercentagesSeparateBadgesAndAccessibleFormulaDetails()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic price comparison", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            var proposals = new[] { AnalysisProposal(quote.Id, "A", 1500, 8.2m), AnalysisProposal(quote.Id, "B", 2000, 7.8m), AnalysisProposal(quote.Id, "C", 1500, 8m) };
            foreach (var proposal in proposals) await store.SaveProposalAsync(proposal, [1], TestContext.Current.CancellationToken);
            var input = await store.GetAnalysisInputAsync(quote.Id);
            await store.SaveAnalysisAsync(input.State with { Links = proposals.Select(proposal => new ItemLink(proposal.Id, proposal.Review[0].CaptureId, "manual:cable", "Synthetic cable", "MT", 1, null, DateTime.UtcNow)).ToArray(), Demands = [new("manual:cable", "MT", 1500)] });
            QuotationAnalysisViewModel model = new(new Localizer(), store, new MappingLookup()); await model.LoadAsync(quote.Id);
            Assert.Contains("4,88%", model.SelectedGroup!.SpreadHelp); Assert.Equal("R$ 7,8000/MT", model.SelectedGroup.BestPrice);
            var cheapestUnit = model.Offers.Single(row => row.Supplier == "B");
            Assert.True(cheapestUnit.IsBestUnitPrice); Assert.False(cheapestUnit.IsWinner);
            Assert.Equal(model.Strings["LowestUnitPriceBadge"], cheapestUnit.PriceBadge);
            Assert.Equal("0,00%", cheapestUnit.CompactDifference);
            Assert.Equal("+5,13%", model.Offers.Single(row => row.Supplier == "A").CompactDifference);
            var cheapestLot = model.Offers.Single(row => row.Supplier == "C");
            Assert.True(cheapestLot.IsWinner); Assert.False(cheapestLot.IsBestUnitPrice); Assert.Equal("+2,56%", cheapestLot.CompactDifference);
            QuotationAnalysisView view = new() { DataContext = model }; Window window = new() { Width = 1100, Height = 800, Content = view }; window.Show();
            try
            {
                model.ToggleGroupCommand.Execute(model.SelectedGroup!);
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("4,88%", model.SelectedGroup!.SpreadHelp);
                var help = view.GetVisualDescendants().OfType<HelpLabel>().Single(control => control.Label == model.Strings["SupplierColumn"]);
                var button = help.GetVisualDescendants().OfType<Button>().Single();
                Assert.Equal(model.Strings["PriceSpreadShortHelp"], ToolTip.GetTip(button));
                var details = Assert.IsType<TextBlock>(Assert.IsType<Flyout>(button.Flyout).Content);
                Assert.Contains("7,8/MT", details.Text); Assert.Contains("8,2/MT", details.Text); Assert.Contains("4,88%", details.Text);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Relink_UsesTheNewSimilarityAndRetainsTheEditedOfferAndConfirmedConversion(bool reuseConversion, bool filterOldGroup)
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic correction study", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            var first = AnalysisProposal(quote.Id, "A", 1000, 2);
            var edited = AnalysisProposal(quote.Id, "B", 1000, 1.8m);
            MappingLookup lookup = new();
            var small = lookup.Product with { ProductCode = "150", Name = "Synthetic cable 1.5 blue", SimilarityCode = "Synthetic cable 1.5 blue" };
            var large = lookup.Product with { ProductCode = "250", Name = "Synthetic cable 2.5 blue", SimilarityCode = "Synthetic cable 2.5 blue" };
            foreach (var proposal in new[] { first, edited }) await store.SaveProposalAsync(proposal, [1], TestContext.Current.CancellationToken);
            var input = await store.GetAnalysisInputAsync(quote.Id);
            await store.SaveAnalysisAsync(input.State with
            {
                Links = [
                new(first.Id, first.Review[0].CaptureId, ErpProductIdentity.GroupKey(large), large.Name, "MT", 1, large, DateTime.UtcNow),
                new(edited.Id, edited.Review[0].CaptureId, ErpProductIdentity.GroupKey(small), small.Name, "MT", 1, small, DateTime.UtcNow)]
            });
            QuotationAnalysisViewModel model = new(new Localizer(), store, lookup, store); await model.LoadAsync(quote.Id);
            model.SelectedGroup = model.Groups.Single(row => row.Group.Key == ErpProductIdentity.GroupKey(small));
            if (filterOldGroup) model.SearchText = small.SimilarityCode;
            model.EditIdentificationCommand.Execute(null);
            model.SelectProduct(large);
            if (reuseConversion) Assert.Equal(1m, model.ConversionFactor);
            else model.ConversionFactor = 1;
            await model.LinkErpCommand.ExecuteAsync(null);
            var saved = await store.GetAnalysisInputAsync(quote.Id);
            Assert.Equal(large.ProductCode, saved.State.Links.Single(link => link.ProposalId == edited.Id).Product!.ProductCode);
            Assert.Single(model.Groups); Assert.Equal(ErpProductIdentity.GroupKey(large), model.SelectedGroup!.Group.Key);
            Assert.Equal(edited.Id, model.SelectedOffer!.Offer.ProposalId);
            Assert.Equal("250", model.SelectedCandidate!.Code);
            var reference = SupplierProductReference.From(edited, edited.Review[0]);
            Assert.Equal("250", (await store.FindMappingAsync("synthetic-catalog", reference, TestContext.Current.CancellationToken))!.Product.ProductCode);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
