namespace QuoteMatch.Domain.Analysis;

public sealed record QuotationLineOffers(
    string ItemCode,
    decimal RequestedQuantity,
    IReadOnlyList<SupplierOffer> Offers);

public sealed record QuotationLineResult(
    string ItemCode,
    ComparisonResult Comparison,
    OfferEvaluation? Winner)
{
    public bool HasTie => Winner is null && Comparison.LowestFulfillmentCost.HasValue;
}

public sealed record SupplierAllocation(string SupplierCode, int ItemCount, decimal Total);

public sealed record QuotationSummary(
    IReadOnlyList<QuotationLineResult> Items,
    IReadOnlyList<SupplierAllocation> Suppliers,
    decimal SelectedTotal,
    int PendingCount)
{
    public bool IsComplete => PendingCount == 0;
}

public static class QuotationAnalysis
{
    public static QuotationSummary Calculate(IReadOnlyList<QuotationLineOffers> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Select(item => item.ItemCode).Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            throw new ArgumentException("Quotation item codes must be unique.", nameof(items));
        }

        QuotationLineResult[] lines = items.Select(item =>
        {
            ComparisonResult comparison = OfferComparison.Calculate(item.RequestedQuantity, item.Offers);
            OfferEvaluation[] candidates = comparison.Offers.Where(offer => offer.IsRecommended).ToArray();
            return new QuotationLineResult(item.ItemCode, comparison, candidates.Length == 1 ? candidates[0] : null);
        }).ToArray();

        OfferEvaluation[] winners = lines.Where(line => line.Winner is not null).Select(line => line.Winner!).ToArray();
        SupplierAllocation[] suppliers = items.SelectMany(item => item.Offers)
            .Select(offer => offer.SupplierCode)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(code => new SupplierAllocation(
                code,
                winners.Count(winner => winner.Offer.SupplierCode == code),
                winners.Where(winner => winner.Offer.SupplierCode == code).Sum(winner => winner.Total)))
            .ToArray();

        return new QuotationSummary(lines, suppliers, winners.Sum(winner => winner.Total), lines.Length - winners.Length);
    }
}
