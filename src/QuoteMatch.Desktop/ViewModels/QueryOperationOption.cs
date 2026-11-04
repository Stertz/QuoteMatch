using CommunityToolkit.Mvvm.ComponentModel;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Desktop.ViewModels;

public sealed class QueryOperationOption(ErpOperation operation, Localizer strings) : ObservableObject
{
    public ErpOperation Operation { get; } = operation;
    public string Label => strings["QueryName" + Operation];
    public string Detail => strings[Operation switch
    {
        ErpOperation.FindProduct or ErpOperation.FindProductById or ErpOperation.GetEquivalentProducts or ErpOperation.SearchProducts => "QueryCategoryProducts",
        ErpOperation.SearchPurchaseOrders or ErpOperation.ReadPurchaseOrder or ErpOperation.FindBuyingSupplier => "QueryCategoryOrderReads",
        ErpOperation.CreatePurchaseOrder or ErpOperation.UpdatePurchaseOrder or ErpOperation.ReconcilePurchaseOperation => "QueryCategoryLegacy",
        ErpOperation.PreviewPurchaseFinancials => "QueryCategoryFinancials",
        _ => "QueryCategoryOrderWrites"
    }] + " · " + Operation;
    public void RefreshLabels() { OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(Detail)); }
}
