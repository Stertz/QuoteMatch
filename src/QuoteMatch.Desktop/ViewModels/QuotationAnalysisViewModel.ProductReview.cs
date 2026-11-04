using QuoteMatch.Application.Analysis;
using QuoteMatch.Application.Integration;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Desktop.ViewModels;

public partial class QuotationAnalysisViewModel
{
    public ProductReviewViewModel CreateProductReview()
    {
        if (_input is null) throw new AnalysisException(AnalysisError.ReadOnly);
        var linked = _input.State.Links.ToDictionary(link => (link.ProposalId, link.CaptureId));
        var rows = _input.Proposals.SelectMany(proposal => proposal.Review
            .Where(item => !item.IsExcluded)
            .Select(item =>
            {
                var source = QuoteMatch.Domain.Integration.SupplierProductReference.From(proposal, item);
                var result = _identifications.GetValueOrDefault(source.Key);
                linked.TryGetValue((proposal.Id, item.CaptureId), out var link);
                var candidates = link?.Product is { } product ? [product] : result?.Candidates ?? _candidatesByCode.GetValueOrDefault(source.LookupKey) ?? [];
                var demand = _input.State.Demands.FirstOrDefault(demand => demand.GroupKey == link?.GroupKey);
                return new ProductReviewRow(proposal, item, candidates, result?.Mapping, Strings, link, demand);
            })).ToArray();
        var model = new ProductReviewViewModel(this, _input, rows, Strings, CanRememberMapping && preferences?.Load().RememberProductMappings == true);
        var settings = preferences?.Load(); model.ShowDesiredQuantity = settings?.ShowDesiredQuantity == true; model.OnlyPending = settings?.OnlyPendingProducts ?? true;
        return model;
    }
    public async Task<ProductReviewViewModel?> CreateProductReviewAsync()
    {
        ProductReviewViewModel? model = null;
        await RunAsync(async token =>
        {
            if (_input is null) throw new AnalysisException(AnalysisError.ReadOnly);
            var current = await repository.GetAnalysisInputAsync(_input.Quotation.Id, token);
            bool changed = current.Quotation.Revision != _input.Quotation.Revision || current.State.Version != _input.State.Version || current.Inputs.Version != _input.Inputs.Version;
            _input = current;
            if (changed) Recalculate();
            model = CreateProductReview();
            try { await model.InitializeAsync(); }
            catch { model.Dispose(); model = null; throw; }
        });
        return model;
    }

    internal async Task ConfirmProductReviewAsync(AnalysisInput input, IReadOnlyList<ProductConfirmation> confirmations, bool rememberMappings, CancellationToken token, ProductReviewOptions? options = null)
    {
        if (IsBusy || !CanEdit || _input?.Quotation.Id != input.Quotation.Id) throw new AnalysisException(AnalysisError.Conflict);
        IsBusy = true;
        try
        {
            string catalog = erp is IErpProductSearch search ? await search.GetCatalogIdentityAsync(token) : confirmations.FirstOrDefault()?.Product.CatalogIdentity ?? string.Empty;
            var saved = await new ProductReviewService(repository, mappings).ConfirmAsync(input, confirmations, rememberMappings, catalog, token, options);
            foreach (var mapping in saved.Mappings) _identifications[mapping.Source.Key] = new([mapping.Product], ProductIdentificationOrigin.SavedMapping, mapping);
            _input = saved.Input; Recalculate(); QuotationUpdated?.Invoke(_input.Quotation);
            Status = confirmations.Count > 0 ? Strings.Format("ProductReviewSaved", confirmations.Count) : Strings["ReviewSaved"];
            SaveMappingPreference(rememberMappings);
        }
        finally { IsBusy = false; }
    }
    internal void SaveReviewDisplayPreferences(bool onlyPending, bool showDesiredQuantity)
    {
        if (preferences is not null && !preferences.TrySave(preferences.Load() with { OnlyPendingProducts = onlyPending, ShowDesiredQuantity = showDesiredQuantity })
            && !Status.Contains(Strings["PreferenceError"], StringComparison.Ordinal)) Status += " " + Strings["PreferenceError"];
    }

    private void SaveMappingPreference(bool rememberMappings)
    {
        if (preferences is not null && !preferences.TrySave(preferences.Load() with { RememberProductMappings = rememberMappings }))
            Status += " " + Strings["PreferenceError"];
    }
}
