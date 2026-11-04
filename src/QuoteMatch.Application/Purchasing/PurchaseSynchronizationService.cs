using QuoteMatch.Application.Integration;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Application.Purchasing;

public sealed class PurchaseSynchronizationService(IPurchasePreparationRepository repository, IPurchaseOrderReader orders, IErpBuyingSupplierLookup suppliers)
{
    public async Task<SynchronizationPreview> PreviewAsync(BrandPurchaseProposal proposal, string branch, string? targetId,
        IReadOnlyList<SynchronizationPair>? pairs, CancellationToken token, IReadOnlyList<SupplierDocumentChoice>? documentChoices = null)
    {
        if (proposal.Brand is null) throw new SynchronizationException(SynchronizationError.MissingBrand);
        var input = await repository.GetPurchaseInputAsync(proposal.QuotationId, token);
        if (input.Preparation is not { } saved || saved.Version != proposal.PreparationVersion
            || saved.InputsRevision != input.Analysis.Quotation.Revision || saved.AnalysisVersion != input.Analysis.State.Version
            || saved.QuotationInputsVersion != input.Analysis.Inputs.Version
            || !PurchaseSynchronization.Proposals(saved).Any(current => SameProposal(current, proposal)))
            throw new SynchronizationException(SynchronizationError.Conflict);
        var target = targetId is null ? null : await orders.ReadAsync(targetId, token);
        var combination = targetId is null ? CreationDocumentRules.Create(input.Analysis, proposal, documentChoices) : null;
        if (targetId is not null && documentChoices?.Count > 0) throw new SynchronizationException(SynchronizationError.InvalidSelection);
        var supplier = await suppliers.FindAsync(proposal.Brand, branch, token) ?? throw new SynchronizationException(SynchronizationError.MissingSupplier);
        return PurchaseSynchronization.Compare(proposal, supplier, branch, target, pairs, combination);
    }

    private static bool SameProposal(BrandPurchaseProposal first, BrandPurchaseProposal second) => first.Brand == second.Brand
        && first.InputsRevision == second.InputsRevision && first.AnalysisVersion == second.AnalysisVersion
        && first.Lines.SequenceEqual(second.Lines) && first.FinancialSources.SequenceEqual(second.FinancialSources);

    public async Task ValidatePreviewAsync(SynchronizationPreview preview, CancellationToken token)
    {
        var current = await PreviewAsync(preview.Proposal, preview.Branch, preview.Target?.Header.Id, preview.Pairs, token, preview.CreationDocuments?.Choices);
        if (preview.Supplier.Brand != current.Supplier.Brand || preview.Supplier.Supplier != current.Supplier.Supplier
            || preview.Supplier.SourceOrderId != current.Supplier.SourceOrderId || preview.Supplier.SourceOrderRevision != current.Supplier.SourceOrderRevision
            || preview.Supplier.Branch != current.Supplier.Branch || preview.Supplier.OrderDate != current.Supplier.OrderDate
            || preview.Target?.Header != current.Target?.Header || preview.Target?.Revision != current.Target?.Revision
            || preview.Target is { } expected && !expected.Lines.SequenceEqual(current.Target!.Lines)
            || !CreationDocumentRules.Matches(preview.CreationDocuments, current.CreationDocuments))
            throw new SynchronizationException(SynchronizationError.Conflict);
    }
}
