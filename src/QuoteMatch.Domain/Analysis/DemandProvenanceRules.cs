using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Domain.Analysis;

public static class DemandProvenanceRules
{
    public static void Validate(IReadOnlyList<GroupDemand> demands, QuotationInputs inputs, IReadOnlyList<SavedProposal>? proposals = null, IReadOnlyList<ItemLink>? links = null)
    {
        HashSet<(string Catalog, string Order, string Line)> used = [];
        foreach (var demand in demands)
        {
            if (!Enum.IsDefined(demand.Origin) || demand.Quantity <= 0 || string.IsNullOrWhiteSpace(demand.GroupKey)
                || string.IsNullOrWhiteSpace(demand.BaseUnit)
                || demand.Origin is DemandOrigin.Manual or DemandOrigin.Document && demand.Sources.Count != 0
                || demand.Origin is DemandOrigin.LinkedOrder or DemandOrigin.ReferenceOrder && demand.Sources.Count == 0
                || demand.Origin != DemandOrigin.Document && demand.DocumentSource is not null)
                throw new AnalysisException(AnalysisError.InvalidDemand);
            if (demand.Origin == DemandOrigin.Manual) continue;
            if (demand.Origin == DemandOrigin.Document)
            {
                var source = demand.DocumentSource;
                var proposal = proposals?.SingleOrDefault(proposal => proposal.Id == source?.ProposalId);
                var item = proposal?.Review.SingleOrDefault(item => item.CaptureId == source?.CaptureId && !item.IsExcluded);
                var link = links?.SingleOrDefault(link => link.ProposalId == source?.ProposalId && link.CaptureId == source?.CaptureId);
                if (source is null || item?.Quantity is not > 0 || proposal?.SourceSha256 != source.SourceSha256 || link is null
                    || link.GroupKey != demand.GroupKey || link.BaseUnit != demand.BaseUnit || link.BaseUnitsPerQuotedUnit != source.ConversionFactor)
                    throw new AnalysisException(AnalysisError.InvalidDemand);
                try { if (item.Quantity * source.ConversionFactor != demand.Quantity) throw new AnalysisException(AnalysisError.InvalidDemand); }
                catch (OverflowException) { throw new AnalysisException(AnalysisError.InvalidDemand); }
                continue;
            }
            decimal quantity = 0;
            foreach (var source in demand.Sources)
            {
                PurchaseOrderSnapshot? order = inputs.Orders.SingleOrDefault(order => order.CatalogIdentity == source.CatalogIdentity
                    && order.Header.Id == source.OrderId && order.Revision == source.OrderRevision);
                PurchaseOrderLine? line = order?.Lines.SingleOrDefault(line => line.Id == source.LineId);
                if (line is null || line.IsCancelled || line.Quantity <= 0
                    || !string.Equals(line.BaseUnit.Trim(), demand.BaseUnit.Trim(), StringComparison.OrdinalIgnoreCase)
                    || !used.Add((source.CatalogIdentity, source.OrderId, source.LineId)))
                    throw new AnalysisException(AnalysisError.InvalidDemand);
                try { quantity += line.Quantity * line.BaseUnitsPerUnit; }
                catch (OverflowException) { throw new AnalysisException(AnalysisError.InvalidDemand); }
            }
            if (quantity != demand.Quantity) throw new AnalysisException(AnalysisError.InvalidDemand);
        }
    }
}
