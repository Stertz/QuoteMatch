using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task Navigation_UsesOneQuotationProcessAndReturnsFromHistoryWithoutLosingTheOpenAnalysis()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic unified study", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            using WorkspaceViewModel model = new(CreateStore(), store); await model.InitializeAsync();
            model.GoToQuotationsCommand.Execute(null); model.SelectedQuotation = model.Quotations.Single();
            await model.OpenQuotationCommand.ExecuteAsync(null); model.QuotationTabIndex = 1;
            model.Analysis.SearchText = "retained analysis filter"; model.Purchase.SearchText = "retained purchase filter";
            WorkspaceWindow window = new() { DataContext = model, InitializeOnOpen = false }; window.Show();
            try
            {
                var navigation = window.FindControl<ListBox>("WorkspaceNavigation")!;
                Assert.Equal(new[] { "NavHome", "NavQuotations", "TemplatesTab", "NavSettings" }.Select(key => model.Strings[key]),
                    navigation.Items.Cast<ListBoxItem>().Select(item => item.Content?.ToString()));
                Assert.NotNull(window.FindControl<Border>("HomeQuotationActions"));
                Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Button>(), button => Equals(button.Content, model.Strings["OpenChecking"]));
                Assert.Null(window.FindControl<Button>("OpenQuotationOrderHistory"));
                using var review = await model.Analysis.CreateProductReviewAsync();
                Assert.NotNull(review); Assert.Same(model.Orders, review.OrderHistory); Assert.Same(model.OrderInputs, review.OrderInputs);
                var grid = new ProductReviewWindow { DataContext = review }; grid.Show(window);
                Assert.Equal(3, grid.FindControl<TabControl>("ProductReviewTabs")!.ItemCount);
                grid.Close(); Dispatcher.UIThread.RunJobs();
                Assert.True(model.IsQuotationOpen); Assert.False(model.IsOrderTools); Assert.Equal(1, model.QuotationTabIndex);
                Assert.Equal("retained analysis filter", model.Analysis.SearchText); Assert.Equal("retained purchase filter", model.Purchase.SearchText);
                await model.GoToCheckingCommand.ExecuteAsync(null);
                Assert.Equal(1, model.PageIndex); Assert.Equal(quotation.Id, model.CurrentQuotation!.Id);
                navigation.SelectedIndex = 2; Dispatcher.UIThread.RunJobs(); Assert.True(model.IsTemplates);
                navigation.SelectedIndex = 3; Dispatcher.UIThread.RunJobs(); Assert.True(model.IsSettings);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ErpWrites_HaveOneEntryInPurchasePreparationWhileHistoryKeepsCoverageAndRecovery()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory);
            using WorkspaceViewModel model = new(CreateStore(), store); await model.InitializeAsync();
            QuotationAnalysisView analysis = new() { DataContext = model.Analysis };
            PurchasePreparationView purchase = new() { DataContext = model.Purchase };
            PurchaseOrdersView history = new() { DataContext = model.Orders };
            StackPanel views = new(); views.Children.Add(analysis); views.Children.Add(purchase); views.Children.Add(history);
            Window window = new() { Content = views }; window.Show();
            try
            {
                var synchronization = Assert.Single(views.GetLogicalDescendants().OfType<Button>(), button => Equals(button.Content, model.Strings["SynchronizeWithErp"]));
                Assert.Same(purchase.FindControl<Button>("SynchronizePurchase"), synchronization);
                Assert.NotNull(ToolTip.GetTip(synchronization));
                var historyButtons = history.GetLogicalDescendants().OfType<Button>().ToArray();
                Assert.Contains(historyButtons, button => Equals(button.Content, model.Strings["OrdersReconcile"]));
                Assert.Contains(historyButtons, button => Equals(button.Content, model.Strings["OrdersManualPair"]));
                Assert.DoesNotContain(historyButtons, button => Equals(button.Content, model.Strings["OrdersPreviewCreate"]) || Equals(button.Content, model.Strings["OrdersPreviewUpdate"])
                    || Equals(button.Content, model.Strings["OrdersConfirm"]) || Equals(button.Content, model.Strings["OrdersAddChange"]));
                Assert.NotNull(history.FindControl<ListBox>("OrderComparisonList"));
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
