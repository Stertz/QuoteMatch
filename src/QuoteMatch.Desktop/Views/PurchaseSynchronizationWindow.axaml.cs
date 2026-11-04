using Avalonia.Controls;
using Avalonia.Interactivity;
using QuoteMatch.Desktop.ViewModels;

namespace QuoteMatch.Desktop.Views;

public partial class PurchaseSynchronizationWindow : Window
{
    public QuotationAnalysisViewModel? ReviewAnalysis { get; init; }
    public Func<Task<bool>>? RefreshPreparation { get; init; }
    public PurchaseSynchronizationWindow()
    {
        InitializeComponent();
        Closing += (_, args) => { if (DataContext is PurchaseSynchronizationViewModel { IsBusy: true }) args.Cancel = true; };
    }
    private void CloseWindow(object? sender, RoutedEventArgs args) => Close();
    private async void BuildPreview(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not PurchaseSynchronizationViewModel { CanPreview: true } model) return;
        while (!model.IsUpdate && model.SelectedBrand is { } brand && brand.Proposal.Lines.Any(line => line.RequestedQuantity is null))
        {
            var warning = new DemandWarningWindow
            {
                DataContext = new DemandWarningViewModel(model.Strings,
                brand.Proposal.Lines.Count(line => line.RequestedQuantity is null), ReviewAnalysis is not null)
            };
            var action = await warning.ShowDialog<DemandReviewAction>(this);
            if (action == DemandReviewAction.Cancel) return;
            if (action == DemandReviewAction.Continue) break;
            if (ReviewAnalysis is null) return;
            var review = await ReviewAnalysis.CreateProductReviewAsync();
            if (review is null) return;
            review.OnlyPending = false; review.ShowDesiredQuantity = true; review.SelectedTabIndex = action == DemandReviewAction.Reference ? 1 : 0;
            await new ProductReviewWindow { DataContext = review }.ShowDialog(this);
            if (!review.IsSaved || RefreshPreparation is null || !await RefreshPreparation()) return;
            var branch = model.Branch; var date = model.NewOrderDate;
            await model.LoadAsync(brand.Proposal.QuotationId);
            model.SelectedBrand = model.Brands.FirstOrDefault(row => row.Proposal.Brand == brand.Proposal.Brand) ?? model.SelectedBrand;
            model.Branch = branch; model.NewOrderDate = date;
            if (!model.CanPreview) return;
        }
        await model.PrepareOrderPreviewAsync();
    }
}
