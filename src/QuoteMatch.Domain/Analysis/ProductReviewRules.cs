using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Domain.Analysis;

public sealed record ProductConfirmation(Guid ProposalId, Guid CaptureId, ErpProductCandidate Product, string BaseUnit, decimal ConversionFactor);
public sealed record ProductDemandChange(Guid ProposalId, Guid CaptureId, decimal? Quantity, DemandOrigin Origin = DemandOrigin.Manual,
    DocumentDemandSource? Document = null, IReadOnlyList<OrderDemandSource>? Orders = null);

public static class ProductReviewRules
{
    public static AnalysisState Apply(AnalysisInput input, IReadOnlyList<ProductConfirmation> confirmations, DateTime confirmedAtUtc,
        bool allowUpdates = false, IReadOnlyList<ProductDemandChange>? demandChanges = null, bool replaceManualDemand = false, bool allowEmpty = false)
    {
        if (input.Quotation.State != QuotationState.Draft) throw new AnalysisException(AnalysisError.ReadOnly);
        if (confirmations.Count == 0 && !allowEmpty) throw new AnalysisException(AnalysisError.InvalidLink);
        var sources = input.Proposals.SelectMany(proposal => proposal.Review.Select(item => (Proposal: proposal, Item: item)))
            .ToDictionary(source => (source.Proposal.Id, source.Item.CaptureId));
        var links = input.State.Links.ToDictionary(link => (link.ProposalId, link.CaptureId));
        HashSet<(Guid, Guid)> confirmed = [];
        foreach (var row in confirmations)
        {
            if (!sources.TryGetValue((row.ProposalId, row.CaptureId), out var source) || source.Item.IsExcluded || !confirmed.Add((row.ProposalId, row.CaptureId))
                || !allowUpdates && links.ContainsKey((row.ProposalId, row.CaptureId)))
                throw new AnalysisException(AnalysisError.InvalidLink);
            var version = OrderInputRules.GroupingVersion(input, row.ProposalId);
            var link = CreateLink(row, source.Proposal, source.Item, version, confirmedAtUtc);
            links[(row.ProposalId, row.CaptureId)] = link;
        }
        var changes = (demandChanges ?? []).Select(change =>
        {
            if (!links.TryGetValue((change.ProposalId, change.CaptureId), out var link) || change.Quantity is <= 0)
                throw new AnalysisException(AnalysisError.InvalidDemand);
            return (Link: link, Change: change);
        }).GroupBy(row => row.Link.GroupKey).ToArray();
        var state = OrderInputRules.Apply(input, replaceManualDemand, links.Values.ToArray(), false, changes.Select(group => group.Key).ToHashSet());
        var demands = state.Demands.ToList();
        foreach (var group in changes)
        {
            var first = group.First();
            if (group.Any(row => row.Change.Quantity != first.Change.Quantity || row.Change.Origin != first.Change.Origin
                || row.Change.Document != first.Change.Document || !(row.Change.Orders ?? []).SequenceEqual(first.Change.Orders ?? [])))
                throw new AnalysisException(AnalysisError.DemandReviewRequired);
            demands.RemoveAll(demand => demand.GroupKey == group.Key);
            if (first.Change.Quantity is decimal quantity) demands.Add(new(group.Key, first.Link.BaseUnit, quantity)
            { Origin = first.Change.Origin, DocumentSource = first.Change.Document, Sources = first.Change.Orders ?? [] });
        }
        DemandProvenanceRules.Validate(demands, input.Inputs, input.Proposals, links.Values.ToArray());
        return state with { Demands = demands };
    }
    public static ItemLink CreateLink(ProductConfirmation row, SavedProposal proposal, ReviewedItem item, ProductGroupingVersion version, DateTime confirmedAtUtc)
    {
        if (row.ProposalId != proposal.Id || row.CaptureId != item.CaptureId || item.IsExcluded) throw new AnalysisException(AnalysisError.InvalidLink);
        var link = new ItemLink(row.ProposalId, row.CaptureId, ErpProductIdentity.GroupKey(row.Product, version),
            version == ProductGroupingVersion.ExactProductCode ? row.Product.Name : ErpProductIdentity.GroupName(row.Product),
            row.BaseUnit.Trim(), row.ConversionFactor, row.Product, confirmedAtUtc)
        { GroupingVersion = version };
        ImportedQuotationAnalysis.ValidateLink(link, item, proposal.Template.CodeKind);
        return link;
    }
}
