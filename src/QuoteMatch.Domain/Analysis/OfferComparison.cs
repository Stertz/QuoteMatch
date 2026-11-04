namespace QuoteMatch.Domain.Analysis;

public static class OfferComparison
{
    public static ComparisonResult Calculate(decimal requestedQuantity, IReadOnlyList<SupplierOffer> offers)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedQuantity);
        ArgumentNullException.ThrowIfNull(offers);

        if (offers.Count == 0)
        {
            throw new ArgumentException("At least one supplier offer is required.", nameof(offers));
        }

        foreach (SupplierOffer offer in offers)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(offer.Quantity);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(offer.UnitPrice);
        }

        decimal lowestPrice = offers.Min(offer => offer.UnitPrice);
        decimal highestPrice = offers.Max(offer => offer.UnitPrice);
        decimal? lowestCost = offers
            .Where(offer => offer.Quantity >= requestedQuantity)
            .Select(offer => (decimal?)(offer.Quantity * offer.UnitPrice))
            .Min();

        OfferEvaluation[] evaluations = offers.Select(offer =>
        {
            decimal total = offer.Quantity * offer.UnitPrice;
            bool canFulfill = offer.Quantity >= requestedQuantity;
            return new OfferEvaluation(
                offer,
                total,
                Math.Max(offer.Quantity - requestedQuantity, 0m),
                Math.Max(requestedQuantity - offer.Quantity, 0m),
                canFulfill,
                offer.UnitPrice == lowestPrice,
                canFulfill && total == lowestCost);
        }).ToArray();

        return new ComparisonResult(
            requestedQuantity,
            lowestPrice,
            highestPrice,
            (highestPrice - lowestPrice) / highestPrice * 100m,
            lowestCost,
            evaluations);
    }
}
