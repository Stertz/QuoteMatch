using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Domain.Purchasing;

public sealed record BrandPurchaseProposal(Guid QuotationId, int PreparationVersion, int InputsRevision, int AnalysisVersion,
    ErpBrandIdentity? Brand, IReadOnlyList<PurchaseLine> Lines)
{
    public IReadOnlyList<PurchaseDocumentFinancialSource> FinancialSources { get; init; } = [];
}
public enum SynchronizationChangeKind { Supplier = 0, ProductAndPackage = 1, Quantity = 2, Price = 3, Include = 4, Remove = 5 }
public sealed record SynchronizationDifference(string Id, SynchronizationChangeKind Kind, PurchaseOrderLine? Target, PurchaseLine? Source);
public sealed record SynchronizationPair(string LineId, string GroupKey);
public sealed record SynchronizationPreview(BrandPurchaseProposal Proposal, BuyingSupplierSnapshot Supplier, PurchaseOrderSnapshot? Target,
    string Branch, IReadOnlyList<SynchronizationDifference> Differences)
{
    public IReadOnlyList<SynchronizationPair> Pairs { get; init; } = [];
    public CreationDocumentCombination? CreationDocuments { get; init; }
    public IReadOnlyList<PurchaseDocumentFinancialSource> FinancialSources => CreationDocuments?.FinancialSources ?? Proposal.FinancialSources;
}
public sealed record SynchronizedLine(string? Control, string ProductCode, string Description, string Unit, string BaseUnit,
    decimal Quantity, decimal Factor, decimal UnitPrice, decimal Total, ErpBrandIdentity Brand);
public sealed record SynchronizationSelection(SynchronizationPreview Preview, IReadOnlyList<string> SelectedChanges,
    string SupplierId, IReadOnlyList<SynchronizedLine> Lines, IReadOnlyList<string> RemovedControls)
{
    public decimal ItemSubtotal => Lines.Sum(line => line.Total);
}
public enum SynchronizationError { MissingBrand, MissingSupplier, InvalidPairing, InvalidSelection, MixedBrands, ReadOnly, Conflict, NumericLimit, DocumentChoiceRequired, DocumentPriceChoiceRequired }
public sealed class SynchronizationException(SynchronizationError code) : Exception($"Purchase synchronization failed: {code}.")
{
    public SynchronizationError Code { get; } = code;
}

public static class PurchaseSynchronization
{
    public static IReadOnlyList<BrandPurchaseProposal> Proposals(PurchasePreparation preparation)
    {
        return preparation.Lines.GroupBy(line => line.Offer.Link?.Product?.Brand is { } brand
            ? $"{brand.CatalogIdentity.Length}:{brand.CatalogIdentity}{brand.Id.Length}:{brand.Id}" : "unknown:" + line.Offer.SourceKey)
            .Select(group => new BrandPurchaseProposal(preparation.QuotationId, preparation.Version, preparation.InputsRevision, preparation.AnalysisVersion,
                group.First().Offer.Link?.Product?.Brand, group.ToArray())
            {
                FinancialSources = preparation.FinancialSources.Where(source => group.Any(line => line.Offer.ProposalId == source.ProposalId)).ToArray()
            }).ToArray();
    }

    public static SynchronizationPreview Compare(BrandPurchaseProposal proposal, BuyingSupplierSnapshot supplier, string branch,
        PurchaseOrderSnapshot? target, IReadOnlyList<SynchronizationPair>? pairs = null, CreationDocumentCombination? creationDocuments = null)
    {
        ValidateProposal(proposal);
        try { BuyingSupplierRules.Validate(supplier, proposal.Brand!, branch); }
        catch (ErpException) { throw new SynchronizationException(SynchronizationError.MissingSupplier); }
        if (target is not null)
        {
            PurchaseOrderRules.Validate(target);
            if (target.CatalogIdentity != proposal.Brand!.CatalogIdentity || target.Header.Branch != branch)
                throw new SynchronizationException(SynchronizationError.Conflict);
        }
        var active = target?.Lines.Where(line => !line.IsCancelled).ToArray() ?? [];
        List<SynchronizationDifference> differences = [];
        HashSet<string> used = [], pairedGroups = [];
        Dictionary<string, PurchaseOrderLine> matched = [];
        foreach (var pair in pairs ?? [])
        {
            var line = active.SingleOrDefault(line => line.Id == pair.LineId);
            if (line is null || !proposal.Lines.Any(source => source.GroupKey == pair.GroupKey) || !used.Add(pair.LineId) || !pairedGroups.Add(pair.GroupKey))
                throw new SynchronizationException(SynchronizationError.InvalidPairing);
            matched.Add(pair.GroupKey, line);
        }
        foreach (var source in proposal.Lines)
        {
            if (matched.ContainsKey(source.GroupKey)) continue;
            var product = source.Offer.Link!.Product!;
            var exact = active.Where(line => !used.Contains(line.Id) && line.ProductCode == product.ProductCode).ToArray();
            var candidates = exact.Length > 0 ? exact : active.Where(line => !used.Contains(line.Id) && SameFamily(source, line, target!.CatalogIdentity)).ToArray();
            if (candidates.Length == 1 && proposal.Lines.Count(other => other.Offer.Link!.Product!.ProductCode == product.ProductCode) == 1)
            { matched.Add(source.GroupKey, candidates[0]); used.Add(candidates[0].Id); }
            else if (candidates.Length > 0) throw new SynchronizationException(SynchronizationError.InvalidPairing);
        }
        if (target is not null && target.Header.SupplierId != supplier.Supplier.Id)
            differences.Add(new("supplier", SynchronizationChangeKind.Supplier, null, null));
        foreach (var source in proposal.Lines)
        {
            matched.TryGetValue(source.GroupKey, out var line);
            if (line is null) { differences.Add(new("include:" + source.GroupKey, SynchronizationChangeKind.Include, null, source)); continue; }
            var offer = source.Offer;
            bool packageChange = line.ProductCode != offer.Link!.Product!.ProductCode || line.Unit != offer.Item.Unit
                || !string.Equals(line.BaseUnit, source.BaseUnit, StringComparison.OrdinalIgnoreCase) || line.BaseUnitsPerUnit != offer.Link.BaseUnitsPerQuotedUnit;
            if (packageChange) differences.Add(new("replace:" + line.Id, SynchronizationChangeKind.ProductAndPackage, line, source));
            else
            {
                if (line.Quantity != offer.Item.Quantity) differences.Add(new("quantity:" + line.Id, SynchronizationChangeKind.Quantity, line, source));
                if (line.UnitPrice != offer.Item.UnitPrice) differences.Add(new("price:" + line.Id, SynchronizationChangeKind.Price, line, source));
            }
        }
        foreach (var line in active.Where(line => !used.Contains(line.Id))) differences.Add(new("remove:" + line.Id, SynchronizationChangeKind.Remove, line, null));
        if (target is not null && creationDocuments is not null) throw new SynchronizationException(SynchronizationError.InvalidSelection);
        return new(proposal, supplier, target, branch, differences) { Pairs = pairs?.ToArray() ?? [], CreationDocuments = creationDocuments };
    }

    private static bool SameFamily(PurchaseLine source, PurchaseOrderLine target, string catalog)
    {
        var product = source.Offer.Link!.Product!;
        var version = source.Offer.Link.GroupingVersion;
        return ErpProductIdentity.GroupKey(product, version) == ErpProductIdentity.GroupKey(catalog, target.ProductCode, target.Description, target.SimilarityCode, target.BaseUnit, version);
    }

    private static void ValidateProposal(BrandPurchaseProposal proposal)
    {
        if (proposal.Brand is not { } brand || string.IsNullOrWhiteSpace(brand.Id) || string.IsNullOrWhiteSpace(brand.Name)
            || proposal.Lines.Count == 0 || proposal.Lines.Any(line => line.Offer.Link?.Product?.Brand != brand
                || line.Offer.Link.Product.CatalogIdentity != brand.CatalogIdentity)) throw new SynchronizationException(SynchronizationError.MissingBrand);
        if (proposal.Lines.Select(line => line.GroupKey).Distinct().Count() != proposal.Lines.Count)
            throw new SynchronizationException(SynchronizationError.InvalidSelection);
    }

    public static SynchronizationSelection Select(SynchronizationPreview preview, IReadOnlyList<string> selectedIds, bool removalConfirmed, bool financialPolicySelected = false)
    {
        ValidateProposal(preview.Proposal);
        if (preview.Target?.Header.CanEdit == false) throw new SynchronizationException(SynchronizationError.ReadOnly);
        if (selectedIds.Count == 0 && !financialPolicySelected || selectedIds.Distinct().Count() != selectedIds.Count || selectedIds.Any(id => !preview.Differences.Any(change => change.Id == id)))
            throw new SynchronizationException(SynchronizationError.InvalidSelection);
        var selected = preview.Differences.Where(change => selectedIds.Contains(change.Id)).ToArray();
        if (preview.CreationDocuments is not null)
        {
            if (preview.Target is not null || selected.Any(change => change.Kind != SynchronizationChangeKind.Include))
                throw new SynchronizationException(SynchronizationError.InvalidSelection);
            var lines = CreationDocumentRules.Lines(preview, selected.Select(change => change.Source!.GroupKey));
            if (lines.Count == 0 || lines.Any(line => line.Brand != preview.Proposal.Brand)) throw new SynchronizationException(SynchronizationError.InvalidSelection);
            return new(preview, selectedIds.ToArray(), preview.Supplier.Supplier.Id, lines, []);
        }
        if (selected.Any(change => change.Kind == SynchronizationChangeKind.Remove) && !removalConfirmed)
            throw new SynchronizationException(SynchronizationError.InvalidSelection);
        if (selected.Any(change => change.Target?.ReceivedQuantity > 0)) throw new SynchronizationException(SynchronizationError.ReadOnly);
        if (preview.Target is not null && preview.Target.Header.SupplierId != preview.Supplier.Supplier.Id && !selectedIds.Contains("supplier"))
            throw new SynchronizationException(SynchronizationError.MissingSupplier);
        var discardedControls = selected.Where(change => change.Kind is SynchronizationChangeKind.Remove or SynchronizationChangeKind.ProductAndPackage)
            .Select(change => change.Target!.Id).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, SynchronizedLine> result = [];
        foreach (var line in preview.Target?.Lines.Where(line => !line.IsCancelled) ?? [])
        {
            if (discardedControls.Contains(line.Id)) continue;
            if (line.Brand is null) throw new SynchronizationException(SynchronizationError.MissingBrand);
            result.Add(line.Id, new(line.Id, line.ProductCode, line.Description, line.Unit, line.BaseUnit, line.Quantity, line.BaseUnitsPerUnit, line.UnitPrice, line.Total, line.Brand));
        }
        List<string> removed = [];
        foreach (var change in selected)
        {
            if (change.Kind == SynchronizationChangeKind.Supplier) continue;
            if (change.Kind == SynchronizationChangeKind.Remove) { result.Remove(change.Target!.Id); removed.Add(change.Target.Id); continue; }
            var source = change.Source!; var item = source.Offer.Item; var link = source.Offer.Link!;
            if (change.Kind is SynchronizationChangeKind.Include or SynchronizationChangeKind.ProductAndPackage)
            {
                result[change.Target?.Id ?? "new:" + source.GroupKey] = new(change.Target?.Id, link.Product!.ProductCode, link.Product.Name, item.Unit, source.BaseUnit,
                    item.Quantity!.Value, link.BaseUnitsPerQuotedUnit, item.UnitPrice!.Value, item.ReportedTotal!.Value, preview.Proposal.Brand!);
            }
            else
            {
                var previous = result[change.Target!.Id];
                var next = change.Kind == SynchronizationChangeKind.Quantity ? previous with { Quantity = item.Quantity!.Value } : previous with { UnitPrice = item.UnitPrice!.Value };
                try { result[change.Target.Id] = next with { Total = next.Quantity * next.UnitPrice }; }
                catch (OverflowException) { throw new SynchronizationException(SynchronizationError.NumericLimit); }
            }
        }
        if (result.Count == 0 || result.Values.Any(line => line.Brand != preview.Proposal.Brand)) throw new SynchronizationException(SynchronizationError.MixedBrands);
        var selection = new SynchronizationSelection(preview, selectedIds.ToArray(), preview.Supplier.Supplier.Id, result.Values.ToArray(), removed);
        try { _ = selection.ItemSubtotal; }
        catch (OverflowException) { throw new SynchronizationException(SynchronizationError.NumericLimit); }
        return selection;
    }
}
