using Avalonia.Controls;
using Avalonia.Interactivity;
using QuoteMatch.Desktop.ViewModels;

namespace QuoteMatch.Desktop.Views;

public sealed partial class DemandWarningWindow : Window
{
    public DemandWarningWindow() => InitializeComponent();
    private void ReviewQuantities(object? sender, RoutedEventArgs args) => Close(DemandReviewAction.Quantities);
    private void ReviewReference(object? sender, RoutedEventArgs args) => Close(DemandReviewAction.Reference);
    private void ContinueReview(object? sender, RoutedEventArgs args) => Close(DemandReviewAction.Continue);
    private void CancelReview(object? sender, RoutedEventArgs args) => Close(DemandReviewAction.Cancel);
}
