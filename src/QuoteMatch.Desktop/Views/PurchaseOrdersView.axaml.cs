using Avalonia.Controls;
using Avalonia.Interactivity;
using QuoteMatch.Desktop.ViewModels;

namespace QuoteMatch.Desktop.Views;

public sealed partial class PurchaseOrdersView : UserControl
{
    public PurchaseOrdersView() => InitializeComponent();
    private async void FindReference(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not PurchaseOrdersViewModel { CanWork: true } model || TopLevel.GetTopLevel(this) is not Window owner) return;
        await model.RefreshReferencesCommand.ExecuteAsync(null);
        var search = new PurchaseReferenceSearchViewModel(model.Strings, model.ReferenceCandidates.ToArray());
        var result = await new PurchaseReferenceSearchWindow { DataContext = search }.ShowDialog<PurchaseReferenceSearchResult?>(owner);
        if (result is null) return;
        if (result.PreparePurchase)
        {
            if (owner.DataContext is WorkspaceViewModel workspace) await workspace.OpenPurchasePreparationAsync(result.Candidate.QuotationId);
        }
        else if (result.Candidate.Reference is { } reference)
        {
            model.SelectedReference = model.References.Single(row => row.Reference.QuotationId == reference.QuotationId && row.Reference.ProposalId == reference.ProposalId);
            await model.LoadReferenceCommand.ExecuteAsync(null);
        }
    }
}
