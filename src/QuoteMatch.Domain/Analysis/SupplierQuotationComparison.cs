using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Domain.Analysis;

public sealed record SupplierQuotationTotal(Guid ProposalId, string Supplier, int IncludedItems, decimal? QuotedTotal,
    int CommonGroups, decimal? CommonTotal, decimal? PremiumPercent)
{
    public int LowestPriceItems { get; init; }
    public int TiedLowestPriceItems { get; init; }
    public int PendingGroups { get; init; }
    public PackageComparisonPending PendingReason { get; init; }
    public decimal? LastPurchaseTotal { get; init; }
    public decimal? PurchaseVariationPercent => PriceVariation.Percent(QuotedTotal, LastPurchaseTotal);
}

public enum PackageComparisonPending { None, TooFewProposals, MissingIdentity, IncompatibleUnits, MissingProducts, AmbiguousOffers, MissingDemand, InsufficientQuantity, UnavailableTotal, AmbiguousVariants }

public static class SupplierQuotationComparison
{
    public static IReadOnlyList<SupplierQuotationTotal> Calculate(IReadOnlyList<SavedProposal> proposals, IReadOnlyList<AnalyzedGroup> groups, QuotationInputs? inputs = null)
    {
        var active = proposals.Where(proposal => proposal.Review.Any(item => !item.IsExcluded)).ToArray();
        var evaluated = groups.Select(group => (Group: group, Pending: Evaluate(group, active))).ToArray();
        var common = active.Length < 2 ? [] : evaluated.Where(row => row.Pending == PackageComparisonPending.None).Select(row => row.Group).ToArray();
        var totals = active.Select(proposal => new SupplierQuotationTotal(proposal.Id, proposal.Template.SupplierName,
            proposal.IncludedItemCount, Sum(proposal.Review.Where(item => !item.IsExcluded).Select(item => item.ReportedTotal)),
            common.Length, common.Length == 0 ? null : Sum(common.Select(group => Eligible(group, proposal.Id).Single().Item.ReportedTotal)), null)
        {
            LastPurchaseTotal = Sum(groups.SelectMany(group => group.Offers.Where(offer => offer.ProposalId == proposal.Id).Select(offer => ReferenceCost(offer, group, inputs)))),
            LowestPriceItems = groups.Count(group => LowestProposals(group) is { Length: 1 } lowest && lowest[0] == proposal.Id),
            TiedLowestPriceItems = groups.Count(group => LowestProposals(group) is { Length: > 1 } lowest && lowest.Contains(proposal.Id)),
            PendingGroups = groups.Count - common.Length,
            PendingReason = active.Length < 2 ? PackageComparisonPending.TooFewProposals
                : common.Length > 0 ? PackageComparisonPending.None : evaluated.Select(row => row.Pending).FirstOrDefault(reason => reason != PackageComparisonPending.None)
        }).ToArray();
        decimal? lowest = totals.All(total => total.CommonTotal is not null) ? totals.Min(total => total.CommonTotal) : null;
        return totals.Select(total =>
        {
            decimal? premium = PriceVariation.Percent(total.CommonTotal, lowest);
            return total with
            {
                PremiumPercent = premium,
                PendingReason = common.Length > 0 && premium is null ? PackageComparisonPending.UnavailableTotal : total.PendingReason
            };
        }).ToArray();
    }

    private static Guid[] LowestProposals(AnalyzedGroup group) => !group.IsLinked || group.HasUnitConflict || group.HasVariantConflict || group.LowestUnitPrice is null || group.Offers.Select(offer => offer.ProposalId).Distinct().Count() < 2 ? []
        : group.Offers.Where(offer => offer.BaseUnitPrice == group.LowestUnitPrice && offer.Link?.Product?.IsInactive != true).Select(offer => offer.ProposalId).Distinct().ToArray();

    private static decimal? LastPurchaseCost(AnalyzedOffer offer)
    {
        if (offer.BaseQuantity is not > 0 || offer.Link is not { Product: { IsInactive: false, LastPurchaseUnitPrice: > 0 } product } link
            || !string.Equals(product.BaseUnit, link.BaseUnit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(product.LastPurchaseUnit ?? product.BaseUnit, link.BaseUnit, StringComparison.OrdinalIgnoreCase)) return null;
        try { return offer.BaseQuantity * product.LastPurchaseUnitPrice; }
        catch (OverflowException) { return null; }
    }
    private static decimal? ReferenceCost(AnalyzedOffer offer, AnalyzedGroup group, QuotationInputs? inputs)
    {
        if (inputs?.PriceReference != QuotationPriceReference.LinkedOrder) return LastPurchaseCost(offer);
        if (offer.BaseQuantity is not > 0 || OrderInputRules.Price(group, inputs) is not { } reference) return null;
        try { return offer.BaseQuantity * reference.UnitPrice; }
        catch (OverflowException) { return null; }
    }

    private static PackageComparisonPending Evaluate(AnalyzedGroup group, IReadOnlyList<SavedProposal> proposals)
    {
        if (!group.IsLinked) return PackageComparisonPending.MissingIdentity;
        if (group.HasUnitConflict) return PackageComparisonPending.IncompatibleUnits;
        if (group.HasVariantConflict) return PackageComparisonPending.AmbiguousVariants;
        if (proposals.Any(proposal => !group.Offers.Any(offer => offer.ProposalId == proposal.Id))) return PackageComparisonPending.MissingProducts;
        var candidates = proposals.Select(proposal => Eligible(group, proposal.Id).ToArray()).ToArray();
        if (candidates.Any(offers => offers.Length == 0)) return PackageComparisonPending.InsufficientQuantity;
        if (candidates.Any(offers => offers.Length > 1)) return PackageComparisonPending.AmbiguousOffers;
        return group.RequestedQuantity is > 0 || candidates.Select(offers => offers[0].BaseQuantity).Distinct().Count() == 1
            ? PackageComparisonPending.None : PackageComparisonPending.MissingDemand;
    }

    private static IEnumerable<AnalyzedOffer> Eligible(AnalyzedGroup group, Guid proposalId) => group.Offers.Where(offer =>
        offer.ProposalId == proposalId && offer.Link?.Product?.IsInactive != true && offer.BaseQuantity is > 0
        && (group.RequestedQuantity is not > 0 || offer.BaseQuantity >= group.RequestedQuantity));

    private static decimal? Sum(IEnumerable<decimal?> values)
    {
        decimal sum = 0;
        try { foreach (decimal? value in values) { if (value is null) return null; sum += value.Value; } return sum; }
        catch (OverflowException) { return null; }
    }
}
