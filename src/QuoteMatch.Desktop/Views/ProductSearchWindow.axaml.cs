using Avalonia.Controls;
using Avalonia.Interactivity;
using QuoteMatch.Desktop.ViewModels;

namespace QuoteMatch.Desktop.Views;

public sealed partial class ProductSearchWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    public ProductSearchWindow()
    {
        InitializeComponent();
        Closing += (_, _) => { _lifetime.Cancel(); (DataContext as ProductSearchViewModel)?.Dispose(); };
        Opened += (_, _) => this.FindControl<TextBox>("ProductSearchText")?.Focus();
    }
    private void CloseSearch(object? sender, RoutedEventArgs args) => Close();
    private async void SelectProduct(object? sender, RoutedEventArgs args)
    {
        if (DataContext is ProductSearchViewModel { CanSelect: true } model
            && await model.ConfirmSelectionAsync(_lifetime.Token) is { } product) Close(product);
    }
}
