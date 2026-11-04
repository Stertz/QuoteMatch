using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuoteMatch.Desktop.Controls;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task QuotationTabs_KeepTheSamePositionWhenSwitchingSections()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, _) = await PurchaseStoreAsync();
            using WorkspaceViewModel model = new(CreateStore(), store); await model.InitializeAsync();
            model.SelectedQuotation = model.Quotations.Single(); await model.OpenQuotationCommand.ExecuteAsync(null);
            WorkspaceWindow window = new() { DataContext = model, InitializeOnOpen = false }; window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var tabs = window.FindControl<TabControl>("QuotationTabs")!;
                double supplierTop = tabs.TranslatePoint(default, window)!.Value.Y;
                tabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
                Assert.Equal(supplierTop, tabs.TranslatePoint(default, window)!.Value.Y);
                tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
                Assert.Equal(supplierTop, tabs.TranslatePoint(default, window)!.Value.Y);
                tabs.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
                Assert.True(window.FindControl<Button>("ImportIntoQuotation")!.IsEffectivelyVisible);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(1100)]
    [InlineData(1920)]
    public async Task SupplierTotals_KeepEachPremiumBesideItsOwnAmount(int width)
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, quotationId) = await PurchaseStoreAsync();
            QuotationAnalysisViewModel model = new(new(), store, new SyntheticProductLookup()); await model.LoadAsync(quotationId);
            model.SupplierTotals[0] = model.SupplierTotals[0] with { Value = model.SupplierTotals[0].Value with { Supplier = "Synthetic supplier with a longer registered name" } };
            QuotationAnalysisView view = new() { DataContext = model };
            Window window = new() { Content = view, Width = width, Height = 740 }; window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var totals = view.FindControl<ItemsControl>("SupplierTotalsList")!;
                totals.Width = width - 200; window.UpdateLayout();
                foreach (var row in model.SupplierTotals)
                {
                    var texts = totals.GetVisualDescendants().OfType<TextBlock>().Where(text => ReferenceEquals(text.DataContext, row)).ToArray();
                    var premium = Assert.Single(texts, text => text.Text == row.Difference);
                    var amount = texts.Last(text => text.Text == row.CommonTotal);
                    double gap = premium.TranslatePoint(default, window)!.Value.X - amount.TranslatePoint(default, window)!.Value.X - amount.TextLayout.Width;
                    Assert.InRange(gap, 4, 16);
                }
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HelpMarker_IsSmallRaisedAndStillOpensItsExplanation()
    {
        await session.Session.Dispatch(() =>
        {
            HelpLabel help = new() { Label = "MT", Help = "Synthetic formula and inputs" };
            Window window = new() { Content = help, Width = 300, Height = 150 }; window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var button = Assert.Single(help.GetVisualDescendants().OfType<Button>());
                Assert.InRange(button.Bounds.Width, 12, 18); Assert.InRange(button.Bounds.Height, 12, 18);
                Assert.True(button.Focusable);
                button.Focus(); window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null); Dispatcher.UIThread.RunJobs();
                Assert.True(button.Flyout!.IsOpen);
                Assert.Equal("Synthetic formula and inputs", Assert.IsType<TextBlock>(Assert.IsType<Flyout>(button.Flyout).Content).Text);
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PurchaseSummary_ShowsAvailableSuppliersAndSavesTheClickedPackage()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, quotationId) = await PurchaseStoreAsync(2);
            PurchasePreparationViewModel model = new(new(), store); await model.LoadAsync(quotationId);
            PurchasePreparationView view = new() { DataContext = model };
            Window window = new() { Content = view, Width = 1100, Height = 740 }; window.Show();
            try
            {
                model.ShowSummaryCommand.Execute(null); Dispatcher.UIThread.RunJobs();
                var visible = view.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToArray();
                Assert.Contains("A", visible); Assert.Contains("B", visible); Assert.Contains("C", visible);
                Assert.DoesNotContain(visible, text => text?.Contains("não avaliados", StringComparison.OrdinalIgnoreCase) == true);
                Assert.Null((await store.GetPurchaseInputAsync(quotationId)).Preparation);
                var button = view.FindControl<ItemsControl>("SupplierPackageList")!.GetVisualDescendants().OfType<Button>()
                    .Single(control => control.DataContext is PurchasePackageRow { Name: "A" });
                Assert.True(button.IsEffectivelyEnabled); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (model.IsBusy) await Task.Yield();
                var saved = (await store.GetPurchaseInputAsync(quotationId)).Preparation!;
                Assert.Equal(800m, saved.ItemSubtotal); Assert.Equal(2, saved.Lines.Count);
                Assert.All(saved.Lines, line => Assert.Equal("A", line.Offer.SupplierName));
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task QuotationSelection_UsesRoundedPresenterInsideItsContainer()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, _) = await PurchaseStoreAsync();
            using WorkspaceViewModel model = new(CreateStore(), store); await model.InitializeAsync(); model.GoToQuotationsCommand.Execute(null);
            WorkspaceWindow window = new() { DataContext = model, InitializeOnOpen = false }; window.Show();
            try
            {
                var list = window.FindControl<ListBox>("QuotationList")!; list.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
                var item = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
                var presenter = item.GetVisualDescendants().OfType<ContentPresenter>().First(control => control.TemplatedParent == item);
                Assert.True(presenter.CornerRadius.TopLeft >= 8);
                Assert.True(list.ClipToBounds);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
