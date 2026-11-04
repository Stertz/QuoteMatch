using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using QuoteMatch.Desktop.ViewModels;
namespace QuoteMatch.Desktop.Views;

public sealed partial class QuotationAnalysisView : UserControl
{
    public QuotationAnalysisView() => InitializeComponent();
    private async void IdentifyPending(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not QuotationAnalysisViewModel { CanReviewProducts: true } model) return;
        if (model.CanIdentifyPending)
        {
            await model.IdentifyPendingCommand.ExecuteAsync(null);
            if (!model.LastBatchIdentificationSucceeded) return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner || owner.OwnedWindows.Count != 0) return;
        var review = await model.CreateProductReviewAsync();
        if (review is not null) await new ProductReviewWindow { DataContext = review }.ShowDialog(owner);
    }
    private void ToggleGroup(object? sender, RoutedEventArgs args)
    {
        if (DataContext is QuotationAnalysisViewModel model && sender is Control { DataContext: AnalysisGroupRow row }) model.ToggleGroupCommand.Execute(row);
    }
    private void EditGroupDoubleTap(object? sender, TappedEventArgs args) { args.Handled = true; OpenEditor((sender as Control)?.DataContext as AnalysisGroupRow); }
    private void EditOfferDoubleTap(object? sender, TappedEventArgs args) { args.Handled = true; OpenEditor((sender as Control)?.DataContext as AnalysisOfferRow); }
    private void EditItemKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.F2 || args.KeyModifiers != KeyModifiers.None) return;
        args.Handled = true;
        if ((sender as Control)?.DataContext is AnalysisGroupRow group) OpenEditor(group);
        else OpenEditor((sender as Control)?.DataContext as AnalysisOfferRow);
    }
    private void OpenEditor(AnalysisGroupRow? row) => OpenEditor(row?.Offers.FirstOrDefault());
    private async void OpenEditor(AnalysisOfferRow? row)
    {
        if (row is null || TopLevel.GetTopLevel(this) is not Window owner || DataContext is not QuotationAnalysisViewModel { IsBusy: false } model || owner.OwnedWindows.Count != 0) return;
        model.PrepareEditor(row.Offer.SourceKey);
        await new QuotationItemEditorWindow { DataContext = model }.ShowDialog(owner);
    }
}
