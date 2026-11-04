using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Domain.Purchasing;

public enum DocumentQuantityChoice { Pending, KeepSelected, Combine }
public enum DocumentPriceChoice { NotRequired, Highest, WeightedAverage, Separate }
public sealed record SupplierDocumentChoice(string SupplierKey, DocumentQuantityChoice Quantities, DocumentPriceChoice Prices = DocumentPriceChoice.NotRequired);
public sealed record PurchaseSourceDocument(Guid ProposalId, string FileName, string SourceSha256);
public sealed record SupplierDocumentGroup(string SupplierKey, string SupplierName, IReadOnlyList<PurchaseSourceDocument> Documents);
public sealed record CreationDocumentSource(string AnchorGroupKey, PurchaseLine Line);
public sealed record CreationDocumentCombination(IReadOnlyList<SupplierDocumentChoice> Choices,
    IReadOnlyList<SupplierDocumentGroup> Groups, IReadOnlyList<CreationDocumentSource> Sources,
    IReadOnlyList<PurchaseDocumentFinancialSource> FinancialSources);

public static class CreationDocumentRules
{
    public static bool Matches(CreationDocumentCombination? first, CreationDocumentCombination? second) => first is null || second is null
        ? first is null && second is null
        : first.Choices.SequenceEqual(second.Choices) && first.Sources.SequenceEqual(second.Sources)
            && first.FinancialSources.SequenceEqual(second.FinancialSources) && first.Groups.Count == second.Groups.Count
            && first.Groups.Zip(second.Groups).All(pair => pair.First.SupplierKey == pair.Second.SupplierKey
                && pair.First.SupplierName == pair.Second.SupplierName && pair.First.Documents.SequenceEqual(pair.Second.Documents));

    public static string SupplierKey(string name) => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    public static IReadOnlyList<SupplierDocumentGroup> Groups(AnalysisInput input, BrandPurchaseProposal proposal)
    {
        var selected = proposal.Lines.Select(line => SupplierKey(line.Offer.SupplierName)).ToHashSet(StringComparer.Ordinal);
        return input.Proposals.Where(document => document.IncludedItemCount > 0)
            .GroupBy(document => SupplierKey(document.Template.SupplierName))
            .Where(group => group.Key.Length > 0 && selected.Contains(group.Key) && group.Count() >= 2)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new SupplierDocumentGroup(group.Key, proposal.Lines.First(line => SupplierKey(line.Offer.SupplierName) == group.Key).Offer.SupplierName,
                group.OrderBy(document => document.Id).Select(document => new PurchaseSourceDocument(document.Id, document.FileName, document.SourceSha256)).ToArray())).ToArray();
    }

    public static IReadOnlySet<string> PriceConflictSuppliers(AnalysisInput input, BrandPurchaseProposal proposal)
    {
        var groups = Groups(input, proposal);
        if (groups.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        var analysis = ImportedQuotationAnalysis.Calculate(input.Proposals, input.State);
        var documents = input.Proposals.ToDictionary(document => document.Id);
        return groups.Where(group => HasDifferentPrices(Sources(analysis, documents, proposal, group.SupplierKey).Select(source => source.Line)))
            .Select(group => group.SupplierKey).ToHashSet(StringComparer.Ordinal);
    }

    public static CreationDocumentCombination? Create(AnalysisInput input, BrandPurchaseProposal proposal, IReadOnlyList<SupplierDocumentChoice>? choices)
    {
        var groups = Groups(input, proposal);
        if (groups.Count == 0)
        {
            if (choices?.Count > 0) throw new SynchronizationException(SynchronizationError.InvalidSelection);
            return null;
        }
        if (choices is null || choices.Count != groups.Count || choices.Select(choice => choice.SupplierKey).Distinct().Count() != groups.Count
            || groups.Any(group => !choices.Any(choice => choice.SupplierKey == group.SupplierKey)))
            throw new SynchronizationException(SynchronizationError.DocumentChoiceRequired);
        List<CreationDocumentSource> sources = [];
        bool combines = choices.Any(choice => choice.Quantities == DocumentQuantityChoice.Combine);
        var analysis = combines ? ImportedQuotationAnalysis.Calculate(input.Proposals, input.State) : [];
        var documents = combines ? input.Proposals.ToDictionary(document => document.Id) : null;
        foreach (var choice in choices.OrderBy(choice => choice.SupplierKey, StringComparer.Ordinal))
        {
            if (!Enum.IsDefined(choice.Quantities) || choice.Quantities == DocumentQuantityChoice.Pending || !Enum.IsDefined(choice.Prices))
                throw new SynchronizationException(SynchronizationError.DocumentChoiceRequired);
            var selected = proposal.Lines.Where(line => SupplierKey(line.Offer.SupplierName) == choice.SupplierKey)
                .Select(line => new CreationDocumentSource(line.GroupKey, line)).ToArray();
            var additions = choice.Quantities == DocumentQuantityChoice.Combine ? Sources(analysis, documents!, proposal, choice.SupplierKey) : selected;
            bool needsPrice = choice.Quantities == DocumentQuantityChoice.Combine && HasDifferentPrices(additions.Select(source => source.Line));
            if (needsPrice && choice.Prices == DocumentPriceChoice.NotRequired)
                throw new SynchronizationException(SynchronizationError.DocumentPriceChoiceRequired);
            if (!needsPrice && choice.Prices != DocumentPriceChoice.NotRequired)
                throw new SynchronizationException(SynchronizationError.InvalidSelection);
            sources.AddRange(additions);
        }
        sources.AddRange(proposal.Lines.Where(line => !groups.Any(group => group.SupplierKey == SupplierKey(line.Offer.SupplierName)))
            .Select(line => new CreationDocumentSource(line.GroupKey, line)));
        var usedDocuments = sources.Select(source => source.Line.Offer.ProposalId).ToHashSet();
        var financials = input.Proposals.Where(document => usedDocuments.Contains(document.Id)).OrderBy(document => document.Id)
            .Select(document => new PurchaseDocumentFinancialSource(document.Id, document.SourceSha256, document.Template.Id,
                document.Template.Version, document.IncludedItemCount, document.Extraction.DocumentFinancials.Values)).ToArray();
        return new(choices.OrderBy(choice => choice.SupplierKey, StringComparer.Ordinal).ToArray(), groups,
            sources.OrderBy(source => source.AnchorGroupKey, StringComparer.Ordinal).ThenBy(source => source.Line.Offer.ProposalId).ThenBy(source => source.Line.Offer.Item.CaptureId).ToArray(), financials);
    }

    private static IReadOnlyList<CreationDocumentSource> Sources(IReadOnlyList<AnalyzedGroup> analysis,
        IReadOnlyDictionary<Guid, SavedProposal> documents, BrandPurchaseProposal proposal, string supplierKey)
    {
        var anchors = proposal.Lines.Where(line => SupplierKey(line.Offer.SupplierName) == supplierKey).ToArray();
        List<CreationDocumentSource> sources = anchors.Select(line => new CreationDocumentSource(line.GroupKey, line)).ToList();
        var used = sources.Select(source => source.Line.Offer.SourceKey).ToHashSet(StringComparer.Ordinal);
        foreach (var group in analysis)
            foreach (var offer in group.Offers.Where(offer => SupplierKey(offer.SupplierName) == supplierKey && !used.Contains(offer.SourceKey)))
            {
                var anchor = anchors.FirstOrDefault(line => SameProduct(line.Offer, offer));
                if (anchor is null) continue;
                var document = documents[offer.ProposalId];
                sources.Add(new(anchor.GroupKey, new(group.Key, group.Name, group.BaseUnit, anchor.RequestedQuantity, offer,
                    document.SourceSha256, document.FileName, document.Template.Id, document.Template.Version)));
                used.Add(offer.SourceKey);
            }
        return sources;
    }

    private static bool SameProduct(AnalyzedOffer first, AnalyzedOffer second) => first.Link?.Product is { } product
        && second.Link?.Product is { IsInactive: false } other && product.CatalogIdentity == other.CatalogIdentity
        && product.ProductCode == other.ProductCode && product.Brand == other.Brand
        && string.Equals(first.Item.Color.Trim(), second.Item.Color.Trim(), StringComparison.OrdinalIgnoreCase);

    private static (string Supplier, string Catalog, string Product, string Unit, string BaseUnit, decimal Factor, string Color) CompatibleKey(PurchaseLine source) =>
    (
        SupplierKey(source.Offer.SupplierName),
        source.Offer.Link!.Product!.CatalogIdentity,
        source.Offer.Link.Product.ProductCode,
        source.Offer.Item.Unit.Trim().ToUpperInvariant(),
        source.BaseUnit.Trim().ToUpperInvariant(),
        source.Offer.Link.BaseUnitsPerQuotedUnit,
        source.Offer.Item.Color.Trim().ToUpperInvariant()
    );

    private static bool HasDifferentPrices(IEnumerable<PurchaseLine> sources) => sources.GroupBy(CompatibleKey)
        .Any(group => group.Select(line => line.Offer.Item.UnitPrice).Distinct().Count() > 1);

    public static IReadOnlyList<SynchronizedLine> Lines(SynchronizationPreview preview, IEnumerable<string> selectedGroups)
    {
        var combination = preview.CreationDocuments ?? throw new SynchronizationException(SynchronizationError.InvalidSelection);
        if (preview.Target is not null || combination.Choices.Any(choice => choice.Quantities == DocumentQuantityChoice.Pending))
            throw new SynchronizationException(SynchronizationError.InvalidSelection);
        var selected = selectedGroups.ToHashSet(StringComparer.Ordinal);
        var sources = combination.Sources.Where(source => selected.Contains(source.AnchorGroupKey)).Select(source => source.Line).ToArray();
        if (sources.Select(source => source.Offer.SourceKey).Distinct().Count() != sources.Length)
            throw new SynchronizationException(SynchronizationError.InvalidSelection);
        List<SynchronizedLine> result = [];
        try
        {
            foreach (var supplier in sources.GroupBy(source => SupplierKey(source.Offer.SupplierName)))
            {
                var choice = combination.Choices.SingleOrDefault(choice => choice.SupplierKey == supplier.Key);
                foreach (var compatible in supplier.GroupBy(CompatibleKey))
                {
                    foreach (var part in Parts(compatible, choice))
                    {
                        var first = part[0]; var offer = first.Offer; var item = offer.Item;
                        decimal quantity = part.Sum(line => line.Offer.Item.Quantity!.Value);
                        decimal total = part.Sum(line => line.Offer.Item.ReportedTotal!.Value);
                        decimal price = item.UnitPrice!.Value;
                        bool differentPrices = part.Select(line => line.Offer.Item.UnitPrice).Distinct().Count() > 1;
                        if (differentPrices && choice?.Prices == DocumentPriceChoice.Highest)
                        { price = part.Max(line => line.Offer.Item.UnitPrice!.Value); total = quantity * price; }
                        else if (differentPrices && choice?.Prices == DocumentPriceChoice.WeightedAverage)
                        { total = part.Sum(line => line.Offer.Item.Quantity!.Value * line.Offer.Item.UnitPrice!.Value); price = total / quantity; }
                        result.Add(new(null, offer.Link!.Product!.ProductCode, offer.Link.Product.Name, item.Unit, first.BaseUnit,
                            quantity, offer.Link.BaseUnitsPerQuotedUnit, price, total, preview.Proposal.Brand!));
                    }
                }
            }
            _ = result.Sum(line => line.Total);
        }
        catch (OverflowException) { throw new SynchronizationException(SynchronizationError.NumericLimit); }
        return result;
    }

    private static IEnumerable<PurchaseLine[]> Parts(IEnumerable<PurchaseLine> lines, SupplierDocumentChoice? choice)
    {
        if (choice?.Quantities != DocumentQuantityChoice.Combine) return lines.Select(line => new[] { line });
        if (choice.Prices is DocumentPriceChoice.Highest or DocumentPriceChoice.WeightedAverage) return [lines.ToArray()];
        return lines.GroupBy(line => line.Offer.Item.UnitPrice).Select(group => group.ToArray());
    }
}
