using QuoteMatch.Application.Integration;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Application.Analysis;

public sealed class ProductReviewService(IQuotationAnalysisRepository repository, IProductMappingRepository? mappings)
{
    public async Task<ProductReviewResult> ConfirmAsync(AnalysisInput input, IReadOnlyList<ProductConfirmation> confirmations,
        bool rememberMappings, string catalogIdentity, CancellationToken token = default, ProductReviewOptions? options = null)
    {
        if (confirmations.Count > 0 && (string.IsNullOrWhiteSpace(catalogIdentity) || confirmations.Any(row => row.Product.CatalogIdentity != catalogIdentity)))
            throw new ProductMappingException(ProductMappingError.Invalid);
        var now = DateTime.UtcNow;
        var reviewed = options?.Inputs is { } inputs ? input with { Inputs = inputs } : input;
        var state = ProductReviewRules.Apply(reviewed, confirmations, now, options?.AllowLinkedUpdates == true, options?.Demands,
            options?.ReplaceManualDemand == true, options?.Inputs is not null || options?.Demands?.Count > 0 || options?.ReplaceManualDemand == true);
        List<SupplierProductMapping> savedMappings = [];
        if (rememberMappings)
        {
            if (mappings is null) throw new ProductMappingException(ProductMappingError.Invalid);
            var items = input.Proposals.SelectMany(proposal => proposal.Review.Select(item => (Proposal: proposal, Item: item)))
                .ToDictionary(source => (source.Proposal.Id, source.Item.CaptureId));
            var sources = confirmations.Select(row =>
            {
                var source = items[(row.ProposalId, row.CaptureId)];
                return (Confirmation: row, Source: SupplierProductReference.From(source.Proposal, source.Item));
            });
            foreach (var group in sources.GroupBy(row => row.Source.Key))
            {
                var first = group.First();
                if (group.Any(row => row.Confirmation.Product.ProductCode != first.Confirmation.Product.ProductCode
                    || !string.Equals(row.Confirmation.BaseUnit.Trim(), first.Confirmation.BaseUnit.Trim(), StringComparison.OrdinalIgnoreCase)
                    || row.Confirmation.ConversionFactor != first.Confirmation.ConversionFactor))
                    throw new ProductMappingException(ProductMappingError.ConflictingConversion);
                var previous = await mappings.FindMappingAsync(catalogIdentity, first.Source, token);
                var mapping = new SupplierProductMapping(previous?.Id ?? Guid.NewGuid(), previous?.Version ?? 0, first.Source,
                    first.Confirmation.Product, first.Confirmation.BaseUnit.Trim(), first.Confirmation.ConversionFactor,
                    ProductMappingState.Active, previous?.CreatedAtUtc ?? now, now);
                mapping.Validate(); savedMappings.Add(mapping);
            }
        }
        return await repository.SaveProductReviewAsync(state, savedMappings, token, options);
    }
}
