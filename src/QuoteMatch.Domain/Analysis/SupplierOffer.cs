namespace QuoteMatch.Domain.Analysis;

public sealed record SupplierOffer(string SupplierCode, decimal Quantity, decimal UnitPrice);

public sealed record OfferEvaluation(
    SupplierOffer Offer,
    decimal Total,
    decimal Excess,
    decimal Shortfall,
    bool CanFulfill,
    bool HasLowestUnitPrice,
    bool IsRecommended);

public sealed record ComparisonResult(
    decimal RequestedQuantity,
    decimal LowestUnitPrice,
    decimal HighestUnitPrice,
    decimal UnitPriceReductionPercent,
    decimal? LowestFulfillmentCost,
    IReadOnlyList<OfferEvaluation> Offers);
