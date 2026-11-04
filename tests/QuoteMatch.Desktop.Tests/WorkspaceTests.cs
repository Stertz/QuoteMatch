using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;

using QuoteMatch.Desktop.Demo;
using QuoteMatch.Desktop.Preferences;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Analysis;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests(DesktopSession session) : IClassFixture<DesktopSession>, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.Desktop.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Window_OpensLastOfFiftyItemsAndNavigatesBackToFirst()
    {
        await session.Session.Dispatch(() =>
        {
            MainWindowViewModel viewModel = CreateViewModel();
            MainWindow window = new() { DataContext = viewModel };
            window.Show();
            try
            {
                ListBox list = window.FindControl<ListBox>("QuotationItemList")!;
                Assert.Same(viewModel.VisibleItems, list.ItemsSource);
                Assert.Equal(50, viewModel.VisibleItems.Count);
                Assert.False(viewModel.IsItemDetailsOpen);

                list.SelectedItem = viewModel.VisibleItems[^1];
                list.ScrollIntoView(list.SelectedItem);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("ITEM-050", viewModel.SelectedItem!.ItemCode);
                Assert.True(window.FindControl<Border>("ItemDetailsPanel")!.IsVisible);
                Assert.Equal(3, viewModel.Offers.Count);
                Assert.False(viewModel.NextItemCommand.CanExecute(null));

                int visited = 1;
                while (viewModel.PreviousItemCommand.CanExecute(null))
                {
                    viewModel.PreviousItemCommand.Execute(null);
                    visited++;
                }

                Assert.Equal(50, visited);
                Assert.Equal("ITEM-001", viewModel.SelectedItem.ItemCode);
                Assert.Equal("Fornecedor C", viewModel.SelectedItemWinner);
                Assert.Equal("R$ 12.000,00", viewModel.SelectedItemCost);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FiltersAndSearch_DoNotChangeQuotationOrSupplierTotals()
    {
        await session.Session.Dispatch(() =>
        {
            MainWindowViewModel viewModel = CreateViewModel();
            string total = viewModel.GrandTotal;
            viewModel.SelectedFilterIndex = 2;

            Assert.NotEmpty(viewModel.VisibleItems);
            Assert.All(viewModel.VisibleItems, item => Assert.Equal("A", item.WinnerCode));
            Assert.Equal(total, viewModel.GrandTotal);
            Assert.Equal(3, viewModel.SupplierTotals.Count);

            viewModel.ClearFiltersCommand.Execute(null);
            viewModel.SearchText = "ITEM-050";
            Assert.Equal("ITEM-050", Assert.Single(viewModel.VisibleItems).ItemCode);
            Assert.Equal(total, viewModel.GrandTotal);

            viewModel.SelectedItem = viewModel.VisibleItems[0];
            viewModel.SearchText = "no matching product";
            Assert.True(viewModel.HasNoResults);
            Assert.False(viewModel.IsItemDetailsOpen);
            Assert.Equal(total, viewModel.GrandTotal);

            viewModel.ClearFiltersCommand.Execute(null);
            Assert.Equal(50, viewModel.VisibleItems.Count);
            Assert.False(viewModel.HasNoResults);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LanguageAndTheme_UpdateLabelsWithoutLosingSelectedItem()
    {
        await session.Session.Dispatch(() =>
        {
            MainWindowViewModel viewModel = CreateViewModel();
            MainWindow window = new() { DataContext = viewModel };
            window.Show();
            try
            {
                viewModel.SelectedItem = viewModel.VisibleItems[0];
                Assert.Equal("Comparação de cotações", window.FindControl<TextBlock>("ComparisonHeading")!.Text);

                viewModel.SelectedLanguageIndex = 1;
                viewModel.SelectedAppearanceIndex = (int)AppearanceMode.Dark;
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("Quotation comparison", window.FindControl<TextBlock>("ComparisonHeading")!.Text);
                Assert.Equal(ThemeVariant.Dark, Avalonia.Application.Current!.RequestedThemeVariant);
                Assert.Equal("R$ 98,847.00", viewModel.GrandTotal);
                Assert.Equal("ITEM-001", viewModel.SelectedItem!.ItemCode);
                Assert.Equal("Supplier C", viewModel.SelectedItemWinner);

                MainWindowViewModel reopened = CreateViewModel();
                Assert.Equal(1, reopened.SelectedLanguageIndex);
                Assert.Equal((int)AppearanceMode.Dark, reopened.SelectedAppearanceIndex);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OfferHelp_ExplainsExcessAndChangesWithSelectedItem()
    {
        await session.Session.Dispatch(() =>
        {
            MainWindowViewModel viewModel = CreateViewModel();
            viewModel.SelectedItem = viewModel.VisibleItems[0];
            viewModel.Offers.Single(offer => offer.SupplierInitial == "B").ShowDetailsCommand.Execute(null);

            Assert.True(viewModel.IsDetailsOpen);
            Assert.Contains("500 m", viewModel.DetailsText);
            Assert.Contains("15.600,00", viewModel.DetailsText);

            viewModel.NextItemCommand.Execute(null);
            Assert.False(viewModel.IsDetailsOpen);
            Assert.Equal("ITEM-002", viewModel.SelectedItem!.ItemCode);
            Assert.All(viewModel.Offers, offer => Assert.Contains("/ un", offer.UnitPrice));

            viewModel.CloseItemDetailsCommand.Execute(null);
            Assert.False(viewModel.IsItemDetailsOpen);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RecalculateQuantity_UpdatesItemWinnerAndAllOrderTotals()
    {
        await session.Session.Dispatch(() =>
        {
            MainWindowViewModel viewModel = CreateViewModel();
            viewModel.SelectedItem = viewModel.VisibleItems[0];
            viewModel.RequestedQuantity = null;
            Assert.True(viewModel.HasQuantityError);
            Assert.False(viewModel.ApplyRequestedQuantityCommand.CanExecute(null));
            Assert.Equal(98847m, viewModel.Summary.SelectedTotal);

            viewModel.RequestedQuantity = 1800m;
            viewModel.ApplyRequestedQuantityCommand.Execute(null);

            Assert.Equal(102447m, viewModel.Summary.SelectedTotal);
            Assert.Equal("B", viewModel.SelectedItem!.WinnerCode);
            Assert.Equal("R$ 15.600,00", viewModel.SelectedItemCost);
            Assert.Equal("ITEM-001", viewModel.SelectedItem.ItemCode);

            viewModel.RequestedQuantity = 2500m;
            viewModel.ApplyRequestedQuantityCommand.Execute(null);

            Assert.Equal(86847m, viewModel.Summary.SelectedTotal);
            Assert.True(viewModel.HasPendingItems);
            Assert.True(viewModel.SelectedItem!.IsPending);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TiedOrInsufficientItems_StayPendingAndDoNotInflateTheOrderTotal()
    {
        await session.Session.Dispatch(() =>
        {
            DemoQuotationItem[] items =
            [
                new("SamplePaper", "BoxUnit", 1, new QuotationLineOffers("001", 10m, [new("A", 10m, 2m), new("B", 10m, 2m)])),
                new("SamplePaper", "BoxUnit", 2, new QuotationLineOffers("002", 20m, [new("A", 5m, 1m)])),
                new("SampleAdapter", "PieceUnit", 1, new QuotationLineOffers("003", 5m, [new("B", 5m, 3m)]))
            ];
            MainWindowViewModel viewModel = new(CreateStore(), items);

            Assert.Equal("R$ 15,00", viewModel.GrandTotal);
            Assert.True(viewModel.HasPendingItems);
            Assert.Equal("Subtotal selecionado · incompleto", viewModel.TotalLabel);

            viewModel.SelectedFilterIndex = 1;
            Assert.Equal(2, viewModel.VisibleItems.Count);
            Assert.All(viewModel.VisibleItems, item => Assert.True(item.IsPending));
            viewModel.SelectedItem = viewModel.VisibleItems[0];
            Assert.DoesNotContain(viewModel.Offers, offer => offer.IsRecommended);
        }, TestContext.Current.CancellationToken);
    }

    private PreferenceStore CreateStore() => new(Path.Combine(_directory, "preferences.json"));
    private MainWindowViewModel CreateViewModel() => new(CreateStore());

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}



