using CommunityToolkit.Mvvm.Input;

using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Analysis;

namespace QuoteMatch.Desktop.ViewModels;

public sealed class OfferRowViewModel
{
    public OfferRowViewModel(OfferEvaluation evaluation, Localizer strings, Action showDetails, string unit = "m", bool hasTie = false)
    {
        SupplierName = strings.Format("SupplierName", evaluation.Offer.SupplierCode);
        SupplierInitial = evaluation.Offer.SupplierCode;
        Quantity = strings.Quantity(evaluation.Offer.Quantity, unit);
        UnitPrice = $"{strings.Money(evaluation.Offer.UnitPrice, 4)} / {unit}";
        Total = strings.Money(evaluation.Total);
        IsRecommended = evaluation.IsRecommended && !hasTie;
        Status = strings[evaluation.IsRecommended && hasTie ? "TiedOffers" : evaluation.IsRecommended ? "Recommended" :
            !evaluation.CanFulfill ? "InsufficientQuantity" :
            evaluation.HasLowestUnitPrice ? "LowestUnitPrice" : "ComparableOffer"];
        QuantityNote = evaluation.Shortfall > 0
            ? strings.Format("ShortfallFormat", strings.Quantity(evaluation.Shortfall, unit))
            : evaluation.Excess > 0
                ? strings.Format("ExcessFormat", strings.Quantity(evaluation.Excess, unit))
                : strings["ExactQuantity"];
        DetailsLabel = strings.Format("OfferDetailsLabel", SupplierName);
        HasQuantityDifference = evaluation.Excess > 0 || evaluation.Shortfall > 0;
        ShowDetailsCommand = new RelayCommand(showDetails);
    }

    public string SupplierName { get; }
    public string SupplierInitial { get; }
    public string Quantity { get; }
    public string UnitPrice { get; }
    public string Total { get; }
    public string Status { get; }
    public string QuantityNote { get; }
    public string DetailsLabel { get; }
    public bool IsRecommended { get; }
    public bool HasQuantityDifference { get; }
    public string CostCalculation => $"{Quantity} × {UnitPrice}";
    public IRelayCommand ShowDetailsCommand { get; }
}
