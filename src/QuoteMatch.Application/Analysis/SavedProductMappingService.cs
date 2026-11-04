using QuoteMatch.Application.Integration;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Application.Analysis;

public sealed class SavedProductMappingService(IQuotationAnalysisRepository repository, IProductMappingRepository mappings, IErpProductSearch catalog)
{
    public async Task<int> ApplyAsync(Guid quotationId, Guid proposalId, CancellationToken token = default)
    {
        var input = await repository.GetAnalysisInputAsync(quotationId, token);
        if (input.Quotation.State != QuotationState.Draft) return 0;
        string identity = await catalog.GetCatalogIdentityAsync(token);
        var linked = input.State.Links.Select(link => (link.ProposalId, link.CaptureId)).ToHashSet();
        List<ProductConfirmation> confirmations = [];
        Dictionary<string, SupplierProductMapping> required = [];
        foreach (var proposal in input.Proposals.Where(proposal => proposal.Id == proposalId))
            foreach (var item in proposal.Review.Where(item => !item.IsExcluded && !linked.Contains((proposal.Id, item.CaptureId))))
            {
                var source = SupplierProductReference.From(proposal, item);
                var mapping = await mappings.FindMappingAsync(identity, source, token);
                if (mapping is not { State: ProductMappingState.Active, ConversionFactor: > 0 } || mapping.Source.Key != source.Key || mapping.Product.CatalogIdentity != identity) continue;
                var confirmation = new ProductConfirmation(proposal.Id, item.CaptureId, mapping.Product, mapping.BaseUnit, mapping.ConversionFactor);
                try
                {
                    mapping.Validate();
                    _ = ProductReviewRules.CreateLink(confirmation, proposal, item, OrderInputRules.GroupingVersion(input, proposal.Id), DateTime.UtcNow);
                }
                catch (Exception exception) when (exception is ProductMappingException or AnalysisException) { continue; }
                confirmations.Add(confirmation); required[source.Key] = mapping;
            }
        if (confirmations.Count == 0) return 0;
        var state = ProductReviewRules.Apply(input, confirmations, DateTime.UtcNow);
        await repository.SaveProductReviewAsync(state, [], token, new() { RequiredMappings = required.Values.ToArray() });
        return confirmations.Count;
    }
}
