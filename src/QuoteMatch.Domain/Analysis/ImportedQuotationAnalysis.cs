using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Domain.Analysis;

public sealed record ItemLink(Guid ProposalId, Guid CaptureId, string GroupKey, string GroupName,
    string BaseUnit, decimal BaseUnitsPerQuotedUnit, ErpProductCandidate? Product, DateTime ConfirmedAtUtc)
{
    public ProductGroupingVersion GroupingVersion { get; init; } = ProductGroupingVersion.ProductCodeFallback;
}
public enum DemandOrigin { Manual = 0, LinkedOrder = 1, Document = 2, ReferenceOrder = 3 }
public sealed record OrderDemandSource(string CatalogIdentity, string OrderId, string OrderRevision, string LineId);
public sealed record DocumentDemandSource(Guid ProposalId, Guid CaptureId, string SourceSha256, decimal ConversionFactor);
public sealed record GroupDemand(string GroupKey, string BaseUnit, decimal Quantity)
{
    public DemandOrigin Origin { get; init; } = DemandOrigin.Manual;
    public IReadOnlyList<OrderDemandSource> Sources { get; init; } = [];
    public DocumentDemandSource? DocumentSource { get; init; }
}
public sealed record AnalysisState(Guid QuotationId, int Version, int InputsRevision, DateTime CreatedAtUtc,
    IReadOnlyList<ItemLink> Links, IReadOnlyList<GroupDemand> Demands)
{
    public int QuotationInputsVersion { get; init; }
}
public sealed record AnalysisInput(Quotation Quotation, IReadOnlyList<SavedProposal> Proposals, AnalysisState State)
{
    public QuotationInputs Inputs { get; init; } = QuotationInputs.Legacy(Quotation.Id);
}
public sealed record AnalyzedOffer(Guid ProposalId, ReviewedItem Item, string SupplierName, ItemLink? Link,
    decimal? BaseQuantity, decimal? BaseUnitPrice)
{
    public string SourceKey => $"{ProposalId:N}/{Item.CaptureId:N}";
}
public sealed record AnalyzedGroup(string Key, string Name, string BaseUnit, IReadOnlyList<AnalyzedOffer> Offers,
    bool IsLinked, bool HasUnitConflict, decimal? RequestedQuantity, decimal? LowestUnitPrice,
    decimal? HighestUnitPrice, AnalyzedOffer? Winner, bool HasTie)
{
    public bool HasVariantConflict { get; init; }
    public decimal? WinnerCost => Winner?.Item.ReportedTotal;
    public decimal? UnitPriceSpreadPercent => IsLinked && !HasUnitConflict && !HasVariantConflict && Offers.Count > 1
        && LowestUnitPrice is >= 0 && HighestUnitPrice is > 0
        ? (HighestUnitPrice.Value - LowestUnitPrice.Value) / HighestUnitPrice.Value * 100m : null;
}
public enum AnalysisError { InvalidLink, InvalidDemand, Conflict, ReadOnly, DemandReviewRequired }
public sealed class AnalysisException(AnalysisError code) : Exception($"Analysis operation failed: {code}.")
{
    public AnalysisError Code { get; } = code;
}
public static class ImportedQuotationAnalysis
{
    public static decimal? SelectedTotal(IReadOnlyList<AnalyzedGroup> groups)
    {
        try { return groups.Sum(group => group.WinnerCost ?? 0m); }
        catch (OverflowException) { return null; }
    }

    public static IReadOnlyList<AnalyzedGroup> Calculate(IReadOnlyList<SavedProposal> proposals, AnalysisState state)
    {
        List<AnalyzedOffer> offers = [];
        Dictionary<(Guid, Guid), ItemLink> links = state.Links.ToDictionary(link => (link.ProposalId, link.CaptureId));
        foreach (SavedProposal proposal in proposals)
            foreach (ReviewedItem item in proposal.Review.Where(item => !item.IsExcluded))
            {
                links.TryGetValue((proposal.Id, item.CaptureId), out ItemLink? link);
                if (link is not null) ValidateLink(link, item, proposal.Template.CodeKind);
                offers.Add(new(proposal.Id, item, proposal.Template.SupplierName, link,
                    link is null ? null : item.Quantity * link.BaseUnitsPerQuotedUnit,
                    link is null ? null : item.UnitPrice / link.BaseUnitsPerQuotedUnit));
            }
        return offers.GroupBy(offer => offer.Link?.GroupKey ?? "source:" + offer.SourceKey).Select(group =>
        {
            AnalyzedOffer[] rows = group.ToArray(); bool linked = rows.All(row => row.Link is not null);
            string[] units = rows.Where(row => row.Link is not null).Select(row => row.Link!.BaseUnit.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            bool conflict = units.Length > 1;
            bool variantConflict = rows.Any(row => row.Link is { GroupingVersion: ProductGroupingVersion.ProductNameFallback, Product: { } product }
                && string.IsNullOrWhiteSpace(product.SimilarityCode))
                && rows.Select(row => row.Item.Color.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            string unit = units.FirstOrDefault() ?? rows[0].Item.Unit;
            GroupDemand? demand = state.Demands.SingleOrDefault(demand => demand.GroupKey == group.Key);
            decimal? requested = demand is not null && string.Equals(demand.BaseUnit, unit, StringComparison.OrdinalIgnoreCase) ? demand.Quantity : null;
            AnalyzedOffer[] eligible = linked && !conflict && !variantConflict && requested is > 0
                ? rows.Where(row => row.Link!.Product?.IsInactive != true && row.BaseQuantity >= requested).ToArray() : [];
            decimal? cost = eligible.Select(row => row.Item.ReportedTotal).Min();
            AnalyzedOffer[] winners = eligible.Where(row => row.Item.ReportedTotal == cost).ToArray();
            return new AnalyzedGroup(group.Key, rows[0].Link?.GroupName ?? rows[0].Item.Description, unit, rows, linked, conflict,
                requested, linked && !conflict && !variantConflict ? rows.Min(row => row.BaseUnitPrice) : null,
                linked && !conflict && !variantConflict ? rows.Max(row => row.BaseUnitPrice) : null,
                winners.Length == 1 ? winners[0] : null, winners.Length > 1)
            { HasVariantConflict = variantConflict };
        }).OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static void ValidateLink(ItemLink link, ReviewedItem item, ProductCodeKind codeKind = ProductCodeKind.Supplier)
    {
        if (string.IsNullOrWhiteSpace(link.GroupKey) || string.IsNullOrWhiteSpace(link.GroupName) || string.IsNullOrWhiteSpace(link.BaseUnit)
            || link.BaseUnitsPerQuotedUnit <= 0 || link.Product?.IsInactive == true || !Enum.IsDefined(link.GroupingVersion)) throw new AnalysisException(AnalysisError.InvalidLink);
        if (link.Product?.BaseUnit is { Length: > 0 } unit && !string.Equals(unit.Trim(), link.BaseUnit.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new AnalysisException(AnalysisError.InvalidLink);
        if (link.Product is { } product && link.GroupKey != ErpProductIdentity.GroupKey(product, link.GroupingVersion))
            throw new AnalysisException(AnalysisError.InvalidLink);
        if (codeKind == ProductCodeKind.Internal && link.Product is { } exact
            && (link.GroupingVersion != ProductGroupingVersion.ExactProductCode
                || !string.Equals(exact.ProductCode.Trim(), item.ProductCode.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new AnalysisException(AnalysisError.InvalidLink);
        try { _ = item.Quantity * link.BaseUnitsPerQuotedUnit; _ = item.UnitPrice / link.BaseUnitsPerQuotedUnit; }
        catch (OverflowException) { throw new AnalysisException(AnalysisError.InvalidLink); }
    }
}
