using Avalonia.Controls;
using Avalonia.Interactivity;
using QuoteMatch.Desktop.ViewModels;

namespace QuoteMatch.Desktop.Views;

public sealed partial class PurchasePreparationView : UserControl
{
    public PurchasePreparationView() => InitializeComponent();
    private async void OpenSynchronization(object? sender, RoutedEventArgs args)
    {
        if (TopLevel.GetTopLevel(this) is Window owner && DataContext is PurchasePreparationViewModel model
            && await model.CreateSynchronizationAsync() is { } synchronization)
            await new PurchaseSynchronizationWindow { DataContext = synchronization, ReviewAnalysis = model.Analysis, RefreshPreparation = model.RefreshAfterReviewAsync }.ShowDialog(owner);
    }
    private void ChooseSupplierPackage(object? sender, RoutedEventArgs args)
    {
        if (DataContext is PurchasePreparationViewModel model && sender is Control { DataContext: PurchasePackageRow package })
            model.ChooseSupplierPackageCommand.Execute(package);
    }
}
