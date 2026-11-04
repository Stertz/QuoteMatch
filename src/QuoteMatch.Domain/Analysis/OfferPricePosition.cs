namespace QuoteMatch.Domain.Analysis;

public sealed record OfferPricePosition(bool CanCompare, bool IsLowest, bool IsTiedLowest, decimal? PremiumPercent)
{
    public static OfferPricePosition Calculate(AnalyzedGroup group, AnalyzedOffer offer)
    {
        if (!group.IsLinked || group.HasUnitConflict || group.HasVariantConflict || group.Offers.Count < 2 || !group.Offers.Contains(offer)
            || group.LowestUnitPrice is not > 0 || offer.BaseUnitPrice is not > 0)
            return new(false, false, false, null);

        decimal lowest = group.LowestUnitPrice.Value;
        decimal price = offer.BaseUnitPrice.Value;
        bool isLowest = price == lowest;
        bool tied = isLowest && group.Offers.Count(row => row.BaseUnitPrice == lowest) > 1;
        decimal? premium;
        try { premium = (price - lowest) / lowest * 100m; }
        catch (OverflowException) { premium = null; }
        return new(true, isLowest, tied, premium);
    }
}
