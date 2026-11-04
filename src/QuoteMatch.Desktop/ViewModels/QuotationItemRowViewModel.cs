using QuoteMatch.Desktop.Demo;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Analysis;

namespace QuoteMatch.Desktop.ViewModels;

public sealed class QuotationItemRowViewModel(DemoQuotationItem item, QuotationLineResult analysis, Localizer strings)
{
    public string ItemCode => item.Line.ItemCode;
    public string Name => strings[item.NameKey];
    public string Specification => strings.Format("SampleVariant", item.Variant);
    public string Description => $"{ItemCode} · {Specification}";
    public string Unit => strings[item.UnitKey];
    public string Quantity => strings.Quantity(item.Line.RequestedQuantity, Unit);
    public bool HasWinner => analysis.Winner is not null;
    public bool IsPending => !HasWinner;
    public string? WinnerCode => analysis.Winner?.Offer.SupplierCode;
    public string WinnerName => WinnerCode is { } code ? strings.Format("SupplierName", code) : strings[analysis.HasTie ? "TiedOffers" : "PendingItem"];
    public string Total => analysis.Winner is { } winner ? strings.Money(winner.Total) : strings["NotAvailable"];
    public string UnitPrice => analysis.Winner is { } winner ? strings.Money(winner.Offer.UnitPrice, 4) : strings["NotAvailable"];
    public decimal? TotalValue => analysis.Winner?.Total;
    public DemoQuotationItem Item => item;
    public QuotationLineResult Analysis => analysis;
}
