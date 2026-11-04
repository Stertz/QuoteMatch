using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Application.Integration;

public sealed class ErpIdentificationService(IErpProductLookup products)
{
    public async Task<IReadOnlyDictionary<string, ProductIdentificationResult>> ResolvePendingAsync(AnalysisInput input, string catalogIdentity,
        IProductMappingRepository mappings, CancellationToken cancellationToken = default)
    {
        Dictionary<string, ProductIdentificationResult> result = new(StringComparer.Ordinal);
        var linked = input.State.Links.Select(link => (link.ProposalId, link.CaptureId)).ToHashSet();
        ErpIdentificationService resolver = new(new CachedLookup(products));
        foreach (var proposal in input.Proposals)
            foreach (var item in proposal.Review.Where(item => !item.IsExcluded && !linked.Contains((proposal.Id, item.CaptureId))))
            {
                var source = SupplierProductReference.From(proposal, item);
                if (!result.ContainsKey(source.Key)) result.Add(source.Key, await resolver.ResolveAsync(source, catalogIdentity, mappings, cancellationToken));
            }
        return result;
    }

    private sealed class CachedLookup(IErpProductLookup source) : IErpProductLookup, IErpProductSearch
    {
        private readonly Dictionary<string, IReadOnlyList<ErpProductCandidate>> _codes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<ErpProductCandidate>> _internalCodes = new(StringComparer.OrdinalIgnoreCase);
        public async Task<IReadOnlyList<ErpProductCandidate>> FindByProductCodeAsync(string code, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (source is not IErpProductSearch search) throw new ErpException(ErpError.QueryMissing);
            if (!_internalCodes.TryGetValue(code, out var rows)) _internalCodes.Add(code, rows = await search.FindByProductCodeAsync(code, token));
            return rows;
        }
        public Task<string> GetCatalogIdentityAsync(CancellationToken token) => source is IErpProductSearch search ? search.GetCatalogIdentityAsync(token) : throw new ErpException(ErpError.QueryMissing);
        public Task<IReadOnlyList<ErpProductCandidate>> SearchByNameAsync(string name, CancellationToken token) => throw new NotSupportedException("Batch identification does not search descriptions.");
        public async Task<IReadOnlyList<ErpProductCandidate>> FindByCodeAsync(string code, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!_codes.TryGetValue(code, out var rows)) _codes.Add(code, rows = await source.FindByCodeAsync(code, token));
            return rows;
        }
        public Task<IReadOnlyList<ErpProductCandidate>> FindBySimilarityAsync(string similarityCode, CancellationToken token) => source.FindBySimilarityAsync(similarityCode, token);
    }

    public async Task<ProductIdentificationResult> ResolveAsync(SupplierProductReference source, string catalogIdentity,
        IProductMappingRepository mappings, CancellationToken cancellationToken = default)
    {
        bool offline = false;
        SupplierProductMapping? mapping = await mappings.FindMappingAsync(catalogIdentity, source, cancellationToken);
        try
        {
            var candidates = (await FindCandidatesAsync(source, cancellationToken))
                .Where(candidate => candidate.CatalogIdentity == catalogIdentity).ToArray();
            if (candidates.Length > 0)
            {
                var reusable = candidates.Length == 1 && mapping is { State: ProductMappingState.Active, ConversionFactor: > 0 }
                    && !candidates[0].IsInactive && mapping.Source.Key == source.Key && mapping.Product.CatalogIdentity == catalogIdentity
                    && mapping.Product.ProductCode == candidates[0].ProductCode && !string.IsNullOrWhiteSpace(candidates[0].BaseUnit)
                    && string.Equals(mapping.BaseUnit.Trim(), candidates[0].BaseUnit!.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? mapping : null;
                return new(candidates, source.CodeKind == ProductCodeKind.Internal ? ProductIdentificationOrigin.InternalCode : ProductIdentificationOrigin.AlternativeCode, reusable);
            }
        }
        catch (ErpException exception) when (exception.Code is ErpError.ConnectionFailed or ErpError.CredentialsUnavailable)
        { offline = true; }

        if (mapping is { State: ProductMappingState.Active } && !mapping.Product.IsInactive
            && mapping.Source.Key == source.Key && mapping.Product.CatalogIdentity == catalogIdentity
            && (source.CodeKind != ProductCodeKind.Internal || string.Equals(mapping.Product.ProductCode.Trim(), source.Code.Trim(), StringComparison.OrdinalIgnoreCase)))
            return new([mapping.Product], ProductIdentificationOrigin.SavedMapping, mapping, offline);
        if (offline) throw new ErpException(ErpError.ConnectionFailed);
        return new([], ProductIdentificationOrigin.NotFound);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ErpProductCandidate>>> LookupPendingAsync(
        AnalysisInput input, CancellationToken cancellationToken = default)
    {
        var linked = input.State.Links.Select(link => (link.ProposalId, link.CaptureId)).ToHashSet();
        SupplierProductReference[] sources = input.Proposals.SelectMany(proposal => proposal.Review
            .Where(item => !item.IsExcluded && !linked.Contains((proposal.Id, item.CaptureId)))
            .Select(item => SupplierProductReference.From(proposal, item))).DistinctBy(source => source.LookupKey, StringComparer.OrdinalIgnoreCase).ToArray();
        Dictionary<string, IReadOnlyList<ErpProductCandidate>> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(source.LookupKey, await FindCandidatesAsync(source, cancellationToken));
        }
        return result;
    }

    public async Task<IReadOnlyList<ErpProductCandidate>> FindCandidatesAsync(SupplierProductReference source, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(source.CodeKind)) throw new ErpException(ErpError.InvalidResult);
        IReadOnlyList<ErpProductCandidate> rows = source.CodeKind == ProductCodeKind.Internal
            ? products is IErpProductSearch search ? await search.FindByProductCodeAsync(source.Code, cancellationToken) : throw new ErpException(ErpError.QueryMissing)
            : await products.FindByCodeAsync(source.Code, cancellationToken);
        return rows.Where(candidate => source.CodeKind == ProductCodeKind.Internal
            ? string.Equals(candidate.ProductCode.Trim(), source.Code.Trim(), StringComparison.OrdinalIgnoreCase)
            : ErpProductIdentity.MatchesSupplierCode(candidate, source.Code)).ToArray();
    }
}
