using Avalonia.Controls;
using Avalonia.Interactivity;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Desktop.Views;

public sealed partial class QuotationItemEditorWindow : Window
{
    public QuotationItemEditorWindow()
    {
        InitializeComponent();
        Closing += (_, args) => { if (DataContext is QuotationAnalysisViewModel { IsBusy: true }) args.Cancel = true; };
    }

    private void CloseEditor(object? sender, RoutedEventArgs args) => Close();
    private async void ConfirmConversion(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not QuotationAnalysisViewModel { IsBusy: false } model) return;
        await model.LinkErpCommand.ExecuteAsync(null);
        if (model.LastProductConfirmationSucceeded) Close();
    }

    private async void OpenProductSearch(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not QuotationAnalysisViewModel { CanEdit: true, HasOffer: true } model) return;
        ProductSearchWindow dialog = new() { DataContext = model.CreateProductSearch() };
        if (await dialog.ShowDialog<ErpProductCandidate?>(this) is { } product) model.SelectProduct(product);
    }
}
