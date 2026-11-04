using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Analysis;

namespace QuoteMatch.Desktop.ViewModels;

public sealed class SupplierTotalViewModel(SupplierAllocation allocation, Localizer strings)
{
    public string Name => strings.Format("SupplierName", allocation.SupplierCode);
    public string ItemCount => strings.Format("ItemCountFormat", allocation.ItemCount);
    public string Total => strings.Money(allocation.Total);
}
