using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Domain.Analysis;

public sealed record LinkedOrderLine(PurchaseOrderSnapshot Order, PurchaseOrderLine Line);
public sealed record OrderPriceReference(decimal UnitPrice, decimal Quantity, IReadOnlyList<LinkedOrderLine> Contributions);

public static class OrderInputRules
{
    public static IReadOnlyList<LinkedOrderLine> MatchLines(AnalyzedGroup group, QuotationInputs inputs)
    {
        if (!group.IsLinked || group.HasUnitConflict || group.HasVariantConflict || group.Offers.Any(offer => offer.Link?.Product is null)) return [];
        var products = group.Offers.Select(offer => offer.Link!.Product!).ToArray();
        var versions = group.Offers.Select(offer => offer.Link!.GroupingVersion).Distinct().ToArray();
        bool exactProduct = versions.Contains(ProductGroupingVersion.ExactProductCode);
        if (exactProduct && versions.Length != 1) return [];
        ProductGroupingVersion version = exactProduct ? ProductGroupingVersion.ExactProductCode : inputs.GroupingVersion;
        var keys = products.Select(product => ErpProductIdentity.GroupKey(product, version)).Distinct().ToArray();
        if (keys.Length != 1) return [];
        List<LinkedOrderLine> matches = [];
        foreach (var order in inputs.Orders)
            foreach (var line in order.Lines.Where(line => !line.IsCancelled && line.Quantity > 0))
            {
                string key = line.SimilarityCode is null
                    && products.FirstOrDefault(product => product.CatalogIdentity == order.CatalogIdentity && product.ProductCode == line.ProductCode) is { } exact
                        ? ErpProductIdentity.GroupKey(exact, version)
                        : ErpProductIdentity.GroupKey(order.CatalogIdentity, line.ProductCode, line.Description, line.SimilarityCode, line.BaseUnit, version);
                if (key != keys[0]) continue;
                if (!string.Equals(line.BaseUnit.Trim(), group.BaseUnit.Trim(), StringComparison.OrdinalIgnoreCase)) return [];
                matches.Add(new(order, line));
            }
        return matches;
    }

    public static AnalysisState Apply(AnalysisInput input, bool replaceManualDemand, IReadOnlyList<ItemLink>? proposedLinks = null, bool validateDemands = true,
        IReadOnlySet<string>? reviewedDemandGroups = null)
    {
        var links = (proposedLinks ?? input.State.Links).Select(link => link.Product is { } product ? link with
        {
            GroupKey = ErpProductIdentity.GroupKey(product, GroupingVersion(input, link.ProposalId)),
            GroupingVersion = GroupingVersion(input, link.ProposalId)
        } : link).ToArray();
        var proposed = input.State with { Links = links, Demands = [], InputsRevision = input.Quotation.Revision, QuotationInputsVersion = input.Inputs.Version };
        List<GroupDemand> demands = [];
        var previousLinks = input.State.Links.ToDictionary(link => (link.ProposalId, link.CaptureId));
        foreach (var group in ImportedQuotationAnalysis.Calculate(input.Proposals, proposed))
        {
            if (reviewedDemandGroups?.Contains(group.Key) == true) continue;
            var oldKeys = group.Offers.Select(offer => previousLinks.GetValueOrDefault((offer.ProposalId, offer.Item.CaptureId))?.GroupKey).ToHashSet();
            var manual = input.State.Demands.Where(demand => demand.Origin != DemandOrigin.LinkedOrder && oldKeys.Contains(demand.GroupKey)).ToArray();
            var lines = MatchLines(group, input.Inputs);
            if (manual.Length > 0 && (!replaceManualDemand || lines.Count == 0))
            {
                if (manual.Any(demand => !string.Equals(demand.BaseUnit, manual[0].BaseUnit, StringComparison.OrdinalIgnoreCase)
                    || demand.Quantity != manual[0].Quantity || demand.Origin != manual[0].Origin || demand.DocumentSource != manual[0].DocumentSource
                    || !demand.Sources.SequenceEqual(manual[0].Sources)))
                    throw new AnalysisException(AnalysisError.DemandReviewRequired);
                var retained = manual[0] with { GroupKey = group.Key };
                if (retained.Origin == DemandOrigin.Document)
                {
                    var source = retained.DocumentSource;
                    var offer = group.Offers.SingleOrDefault(offer => offer.ProposalId == source?.ProposalId && offer.Item.CaptureId == source?.CaptureId);
                    if (offer?.BaseQuantity is not > 0) continue;
                    retained = retained with
                    {
                        BaseUnit = group.BaseUnit,
                        Quantity = offer.BaseQuantity.Value,
                        DocumentSource = source! with { ConversionFactor = offer.Link!.BaseUnitsPerQuotedUnit }
                    };
                }
                else if (retained.Origin == DemandOrigin.ReferenceOrder)
                {
                    var selected = lines.Where(line => retained.Sources.Any(source => source.CatalogIdentity == line.Order.CatalogIdentity
                        && source.OrderId == line.Order.Header.Id && source.LineId == line.Line.Id)).ToArray();
                    if (selected.Length == 0) continue;
                    try
                    {
                        retained = retained with
                        {
                            BaseUnit = group.BaseUnit,
                            Quantity = selected.Sum(source => source.Line.Quantity * source.Line.BaseUnitsPerUnit),
                            Sources = selected.Select(source => new OrderDemandSource(source.Order.CatalogIdentity, source.Order.Header.Id, source.Order.Revision, source.Line.Id)).ToArray()
                        };
                    }
                    catch (OverflowException) { throw new AnalysisException(AnalysisError.InvalidDemand); }
                }
                else if (!string.Equals(retained.BaseUnit, group.BaseUnit, StringComparison.OrdinalIgnoreCase)) throw new AnalysisException(AnalysisError.DemandReviewRequired);
                demands.Add(retained);
            }
            else if (lines.Count > 0)
            {
                decimal quantity;
                try { quantity = lines.Sum(source => source.Line.Quantity * source.Line.BaseUnitsPerUnit); }
                catch (OverflowException) { throw new AnalysisException(AnalysisError.InvalidDemand); }
                demands.Add(new(group.Key, group.BaseUnit, quantity)
                {
                    Origin = DemandOrigin.LinkedOrder,
                    Sources = lines.Select(source => new OrderDemandSource(source.Order.CatalogIdentity, source.Order.Header.Id, source.Order.Revision, source.Line.Id)).ToArray()
                });
            }
        }
        if (validateDemands) DemandProvenanceRules.Validate(demands, input.Inputs, input.Proposals, links);
        return proposed with { Demands = demands };
    }

    public static ProductGroupingVersion GroupingVersion(AnalysisInput input, Guid proposalId) =>
        input.Proposals.Single(proposal => proposal.Id == proposalId).Template.CodeKind == ProductCodeKind.Internal
            ? ProductGroupingVersion.ExactProductCode : input.Inputs.GroupingVersion;

    public static OrderPriceReference? Price(AnalyzedGroup group, QuotationInputs inputs)
    {
        var sources = MatchLines(group, inputs);
        if (sources.Count == 0) return null;
        try
        {
            decimal quantity = sources.Sum(source => source.Line.Quantity * source.Line.BaseUnitsPerUnit);
            decimal cost = sources.Sum(source => source.Line.UnitPrice / source.Line.BaseUnitsPerUnit * (source.Line.Quantity * source.Line.BaseUnitsPerUnit));
            return quantity <= 0 ? null : new(cost / quantity, quantity, sources);
        }
        catch (OverflowException) { return null; }
    }
}
