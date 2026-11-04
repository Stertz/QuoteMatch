using Avalonia.Controls;
using Avalonia.Interactivity;
using QuoteMatch.Desktop.ViewModels;

namespace QuoteMatch.Desktop.Views;

public sealed partial class PurchaseReferenceSearchWindow : Window
{
    public PurchaseReferenceSearchWindow()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<TextBox>("PurchaseReferenceSearchText")?.Focus();
    }
    private void CloseSearch(object? sender, RoutedEventArgs args) => Close();
    private void SelectReference(object? sender, RoutedEventArgs args)
    {
        if (DataContext is PurchaseReferenceSearchViewModel { CanSelect: true, Selected: { } selected }) Close(new PurchaseReferenceSearchResult(selected, false));
    }
    private void PreparePurchase(object? sender, RoutedEventArgs args)
    {
        if (DataContext is PurchaseReferenceSearchViewModel { Selected: { } selected }) Close(new PurchaseReferenceSearchResult(selected, true));
    }
}
