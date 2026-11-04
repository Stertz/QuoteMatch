using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Domain.Purchasing;

public sealed record PurchaseSelection(Guid ProposalId, Guid CaptureId);
public sealed record PurchaseLine(string GroupKey, string GroupName, string BaseUnit, decimal? RequestedQuantity,
    AnalyzedOffer Offer, string SourceSha256, string SourceFileName, Guid TemplateId, int TemplateVersion);
public sealed record PurchaseDocumentFinancialSource(Guid ProposalId, string SourceSha256, Guid TemplateId,
    int TemplateVersion, int SourceItemCount, FinancialAmounts Values);
public sealed record PurchasePreparation(Guid QuotationId, int Version, int InputsRevision, int AnalysisVersion,
    DateTime CreatedAtUtc, int PendingGroupCount, IReadOnlyList<PurchaseLine> Lines)
{
    public int QuotationInputsVersion { get; init; }
    public IReadOnlyList<PurchaseDocumentFinancialSource> FinancialSources { get; init; } = [];
    public decimal ItemSubtotal => Lines.Sum(line => line.Offer.Item.ReportedTotal!.Value);
}
public sealed record PurchaseInput(AnalysisInput Analysis, PurchasePreparation? Preparation);
public sealed record PreparePurchaseRequest(Guid QuotationId, int InputsRevision, int AnalysisVersion,
    int ExpectedVersion, IReadOnlyList<PurchaseSelection> Selections);
public enum PurchaseError { InvalidSelection, NumericLimit, Conflict, ReadOnly }
public sealed class PurchaseException(PurchaseError code) : Exception($"Purchase preparation failed: {code}.")
{
    public PurchaseError Code { get; } = code;
}

public static class PurchasePreparationRules
{
    public static IReadOnlyList<PurchaseSelection> RetainEligibleSelections(IReadOnlyList<AnalyzedGroup> groups, PurchasePreparation previous)
    {
        var saved = previous.Lines.ToDictionary(line => line.Offer.SourceKey);
        List<PurchaseSelection> choices = [];
        foreach (var group in groups)
        {
            var matching = group.Offers.Where(offer => saved.ContainsKey(offer.SourceKey)).ToArray();
            if (matching.Length != 1) continue;
            var offer = matching[0]; var prior = saved[offer.SourceKey];
            if (!IsEligible(group, offer) || !string.Equals(prior.BaseUnit, group.BaseUnit, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(prior.Offer.Item.Unit, offer.Item.Unit, StringComparison.OrdinalIgnoreCase)
                || prior.Offer.Item.Color != offer.Item.Color || prior.Offer.Link?.BaseUnitsPerQuotedUnit != offer.Link?.BaseUnitsPerQuotedUnit
                || prior.Offer.Link?.Product is { } product && (offer.Link?.Product is not { } current
                    || product.CatalogIdentity != current.CatalogIdentity || product.ProductCode != current.ProductCode)) continue;
            choices.Add(new(offer.ProposalId, offer.Item.CaptureId));
        }
        return choices;
    }

    public static IReadOnlyList<PurchaseSelection> SelectSingleSupplierOffers(IReadOnlyList<AnalyzedGroup> groups)
    {
        var suppliers = groups.SelectMany(group => group.Offers).Select(offer => CreationDocumentRules.SupplierKey(offer.SupplierName)).Distinct().ToArray();
        if (suppliers.Length != 1 || suppliers[0].Length == 0) return [];
        return groups.Select(group => group.Offers.Where(offer => IsEligible(group, offer)).ToArray())
            .Where(offers => offers.Length == 1).Select(offers => new PurchaseSelection(offers[0].ProposalId, offers[0].Item.CaptureId)).ToArray();
    }

    public static IReadOnlyList<PurchaseSelection> SelectSupplierPackage(IReadOnlyList<AnalyzedGroup> groups, Guid proposalId)
    {
        if (groups.Count == 0) throw new PurchaseException(PurchaseError.InvalidSelection);
        List<PurchaseSelection> selections = [];
        foreach (var group in groups)
        {
            var offers = group.Offers.Where(offer => offer.ProposalId == proposalId && IsEligible(group, offer)).ToArray();
            if (offers.Length != 1) throw new PurchaseException(PurchaseError.InvalidSelection);
            selections.Add(new(proposalId, offers[0].Item.CaptureId));
        }
        return selections;
    }

    public static bool IsEligible(AnalyzedGroup group, AnalyzedOffer offer) =>
        group.IsLinked && !group.HasUnitConflict && !group.HasVariantConflict
        && offer.Link is not null && offer.Link.Product?.IsInactive != true
        && offer.BaseQuantity is > 0 && (group.RequestedQuantity is null || group.RequestedQuantity is > 0 && offer.BaseQuantity >= group.RequestedQuantity)
        && offer.Item.ReportedTotal is >= 0
        && group.Offers.Contains(offer);

    public static PurchasePreparation Create(AnalysisInput input, IReadOnlyList<PurchaseSelection> selections,
        int version, DateTime createdAtUtc)
    {
        var groups = ImportedQuotationAnalysis.Calculate(input.Proposals, input.State);
        Dictionary<string, PurchaseLine> lines = [];
        foreach (PurchaseSelection selection in selections)
        {
            AnalyzedGroup? group = groups.SingleOrDefault(group => group.Offers.Any(offer =>
                offer.ProposalId == selection.ProposalId && offer.Item.CaptureId == selection.CaptureId));
            AnalyzedOffer? offer = group?.Offers.SingleOrDefault(offer =>
                offer.ProposalId == selection.ProposalId && offer.Item.CaptureId == selection.CaptureId);
            if (group is null || offer is null || !IsEligible(group, offer) || lines.ContainsKey(group.Key))
                throw new PurchaseException(PurchaseError.InvalidSelection);
            var proposal = input.Proposals.Single(proposal => proposal.Id == offer.ProposalId);
            lines.Add(group.Key, new(group.Key, group.Name, group.BaseUnit, group.RequestedQuantity,
                offer, proposal.SourceSha256, proposal.FileName, proposal.Template.Id, proposal.Template.Version));
        }
        PurchasePreparation preparation = new(input.Quotation.Id, version, input.Quotation.Revision, input.State.Version,
            createdAtUtc, groups.Count - lines.Count, lines.Values.OrderBy(line => line.GroupName, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            QuotationInputsVersion = input.State.QuotationInputsVersion,
            FinancialSources = input.Proposals.Where(proposal => lines.Values.Any(line => line.Offer.ProposalId == proposal.Id))
                .Select(proposal => new PurchaseDocumentFinancialSource(proposal.Id, proposal.SourceSha256, proposal.Template.Id,
                    proposal.Template.Version, proposal.IncludedItemCount, proposal.Extraction.DocumentFinancials.Values)).ToArray()
        };
        try { _ = preparation.ItemSubtotal; }
        catch (OverflowException) { throw new PurchaseException(PurchaseError.NumericLimit); }
        return preparation;
    }
}
