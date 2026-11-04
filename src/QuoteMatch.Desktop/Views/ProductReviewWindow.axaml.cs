using Avalonia.Controls;
using Avalonia.Interactivity;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Desktop.Views;

public sealed partial class ProductReviewWindow : Window
{
    public ProductReviewWindow()
    {
        InitializeComponent();
        Closing += (_, args) => { if (DataContext is ProductReviewViewModel model && (model.IsBusy || model.OrderInputs?.IsBusy == true || model.OrderHistory?.IsBusy == true)) args.Cancel = true; };
        Closed += (_, _) => (DataContext as ProductReviewViewModel)?.Dispose();
    }
    private async void ConfirmSelected(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ProductReviewViewModel model) return;
        await model.SaveCommand.ExecuteAsync(null);
        if (model.IsSaved) Close();
    }
    private void CloseReview(object? sender, RoutedEventArgs args) => Close();
    private async void SearchProduct(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ProductReviewViewModel { IsBusy: false } model || sender is not Control { DataContext: ProductReviewRow row }) return;
        ProductSearchWindow search = new() { DataContext = model.CreateProductSearch(row) };
        if (await search.ShowDialog<ErpProductCandidate?>(this) is { } product) row.SelectProduct(product);
    }
}
