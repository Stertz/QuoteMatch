using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuoteMatch.Application.Analysis;
using QuoteMatch.Application.Integration;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.Preferences;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Desktop.ViewModels;

public sealed partial class AnalysisGroupRow(AnalyzedGroup group, string detail) : ObservableObject
{
    public string ReferenceLabel { get; init; } = string.Empty;
    public bool HasCompetitors => Group.Offers.Select(offer => offer.ProposalId).Distinct().Count() > 1;
    public AnalyzedGroup Group { get; } = group;
    [ObservableProperty] private string _detail = detail;
    [ObservableProperty] private int? _identificationCandidateCount;
    [ObservableProperty] private bool _isExpanded;
    public Localizer Strings { get; init; } = null!;
    public IReadOnlyList<AnalysisOfferRow> Offers { get; init; } = [];
    public string LowestSupplier { get; init; } = string.Empty;
    public string BestPrice { get; init; } = "—";
    public string LastPurchase { get; init; } = "—";
    public string LastPurchaseHelp { get; init; } = string.Empty;
    public string SpreadHelp { get; init; } = string.Empty;
    public string Name => Group.IsLinked ? Offers.FirstOrDefault()?.Offer.Link?.Product?.Name ?? Group.Name : new SupplierProductReference(Group.Offers[0].SupplierName,
        Group.Offers[0].Item.ProductCode, Group.Offers[0].Item.Description, Group.Offers[0].Item.Color, Group.Offers[0].Item.Unit).Display;
}
public sealed record AnalysisOfferRow(AnalyzedOffer Offer, string Quantity, string Price, string Total, string ErpDetail, string History,
    string Normalized, string Coverage, bool IsWinner, string SelectionLabel)
{
    public bool HasCompetitors { get; init; }
    public string ReferenceLabel { get; init; } = string.Empty;
    public string Supplier => Offer.SupplierName;
    public string Description => Offer.Item.Description;
    public string Code => Offer.Item.ProductCode;
    public string Reference => string.Join(" · ", new[] { Code, Offer.Item.Color, Offer.Item.Unit }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public bool IsBestUnitPrice { get; init; }
    public string PriceBadge { get; init; } = string.Empty;
    public string PriceDifferenceHelp { get; init; } = string.Empty;
    public string BasePrice { get; init; } = string.Empty;
    public string BaseQuantity { get; init; } = string.Empty;
    public string CompactDifference { get; init; } = "—";
    public string LastPurchase { get; init; } = "—";
    public string PurchaseVariation { get; init; } = "—";
    public string PurchaseVariationHelp { get; init; } = string.Empty;
    public bool IsPriceHigher { get; init; }
    public bool IsPurchaseIncrease { get; init; }
    public bool IsPurchaseDecrease { get; init; }
    public Localizer Strings { get; init; } = null!;
    public bool HasCoverage => Coverage.Length > 0;
}
public sealed record SupplierTotalRow(SupplierQuotationTotal Value, string Total, string CommonTotal, string Difference, string Detail)
{
    public string Supplier => Value.Supplier;
    public bool IsLowest => Value.PremiumPercent == 0;
    public bool IsHigher => Value.PremiumPercent > 0;
    public string PurchaseVariation { get; init; } = string.Empty;
    public string PurchaseVariationHelp { get; init; } = string.Empty;
    public Localizer Strings { get; init; } = null!;
    public bool IsPurchaseIncrease => Value.PurchaseVariationPercent > 0;
    public bool IsPurchaseDecrease => Value.PurchaseVariationPercent < 0;
}
public sealed record ErpCandidateRow(ErpProductCandidate Product, string Detail)
{
    public string Name => Product.Name;
    public string Code => Product.ProductCode;
}
public partial class QuotationAnalysisViewModel(Localizer strings, IQuotationAnalysisRepository repository, IErpProductLookup erp,
    IProductMappingRepository? mappings = null, PreferenceStore? preferences = null,
    QuotationOrderInputsViewModel? orderInputs = null, PurchaseOrdersViewModel? orderHistory = null) : ObservableObject
{
    public event Action<Quotation>? QuotationUpdated;
    private AnalysisInput? _input;
    private IReadOnlyList<AnalyzedGroup> _groups = [];
    [ObservableProperty] private int _priceReferenceIndex;
    public bool HasLinkedOrders => _input?.Inputs.Orders.Count > 0;
    public string DemandOriginLabel => _input?.State.Demands.FirstOrDefault(demand => demand.GroupKey == SelectedGroup?.Group.Key) is not { } demand ? Strings["DemandPending"]
        : demand.Origin switch
        {
            DemandOrigin.Manual => Strings["DemandOriginManual"],
            DemandOrigin.Document => Strings.Format("DemandOriginDocument", _input.Proposals.FirstOrDefault(proposal => proposal.Id == demand.DocumentSource?.ProposalId)?.FileName ?? Strings["NotAvailable"]),
            DemandOrigin.ReferenceOrder => Strings["DemandOriginSelectedOrder"],
            _ => Strings.Format("DemandOriginOrder", demand.Sources.Count, _input.Inputs.CreatedAtUtc.ToLocalTime().ToString("g", Strings.Culture))
        };
    public string DemandOriginHelp => SelectedGroup is not { } selected || _input is null ? Strings["DemandPending"]
        : _input.State.Demands.FirstOrDefault(demand => demand.GroupKey == selected.Group.Key) is not { } demand ? Strings["DemandPending"]
        : demand.Origin == DemandOrigin.Manual ? Strings["ManualDemandHelp"]
        : demand.Origin == DemandOrigin.Document ? DocumentDemandHelp(demand)
        : Strings["OrderDemandFormula"] + Environment.NewLine + string.Join(Environment.NewLine,
            OrderInputRules.MatchLines(selected.Group, _input.Inputs).Where(line => demand.Sources.Any(source => source.CatalogIdentity == line.Order.CatalogIdentity
                && source.OrderId == line.Order.Header.Id && source.LineId == line.Line.Id)).Select(source => QuantityState(source.Line)));
    private string DocumentDemandHelp(GroupDemand demand)
    {
        var proposal = _input?.Proposals.FirstOrDefault(proposal => proposal.Id == demand.DocumentSource?.ProposalId);
        var item = proposal?.Review.FirstOrDefault(item => item.CaptureId == demand.DocumentSource?.CaptureId);
        return item?.Quantity is not decimal quantity ? Strings["NotAvailable"]
            : proposal!.FileName + Environment.NewLine + Strings.Format("DocumentDemandHelp", Strings.Quantity(quantity, item.Unit),
                demand.DocumentSource!.ConversionFactor.ToString("G29", Strings.Culture), Strings.Quantity(demand.Quantity, demand.BaseUnit));
    }
    [RelayCommand]
    private Task ApplyPriceReferenceAsync() => RunAsync(async token =>
    {
        if (_input is null || !CanEdit || repository is not Application.Quotations.IQuotationRepository quotations) return;
        if (_input.Inputs.PriceReference == (QuotationPriceReference)PriceReferenceIndex) return;
        await quotations.SaveQuotationWorkspaceAsync(_input.Quotation, _input.Inputs with { PriceReference = (QuotationPriceReference)PriceReferenceIndex }, false, token);
        _input = await repository.GetAnalysisInputAsync(_input.Quotation.Id, token); QuotationUpdated?.Invoke(_input.Quotation); Recalculate();
    });
    private readonly Dictionary<string, IReadOnlyList<ErpProductCandidate>> _candidatesByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProductIdentificationResult> _identifications = new(StringComparer.Ordinal);
    [ObservableProperty] private bool _rememberMapping;
    [ObservableProperty] private string _identificationStatus = string.Empty;
    [ObservableProperty] private bool _isIdentificationOpen;
    public bool CanRememberMapping => mappings is not null;
    public QuotationOrderInputsViewModel? OrderInputs => orderInputs;
    public PurchaseOrdersViewModel? OrderHistory => orderHistory;
    public bool CanReviewProducts => _input is not null && !IsBusy;
    public bool CanSearchSimilarity => SelectedReference?.CodeKind != QuoteMatch.Domain.Importing.ProductCodeKind.Internal;
    public SupplierProductReference? SelectedReference => _input is null || SelectedOffer is null ? null
        : SupplierProductReference.From(_input.Proposals.Single(proposal => proposal.Id == SelectedOffer.Offer.ProposalId), SelectedOffer.Offer.Item);
    public string SourceReference => SelectedReference?.Display ?? string.Empty;
    public string SourceSupplier => SelectedReference?.Supplier ?? string.Empty;
    public string ConfirmedProductLabel => SelectedOffer?.Offer.Link?.Product is { } product ? product.ProductCode + " · " + product.Name : string.Empty;
    public bool HasConfirmedProduct => SelectedOffer?.Offer.Link?.Product is not null;
    public string ConfirmProductLabel => Strings[SelectedReference?.CodeKind == QuoteMatch.Domain.Importing.ProductCodeKind.Internal ? "ConfirmConversion" : "ConfirmProduct"];
    public string ProductActionLabel => Strings[HasConfirmedProduct ? "ChangeProduct" : "BrowseProducts"];
    public string PackagingLabel => Strings.Format("PackageContains", SelectedOffer?.Offer.Item.Unit ?? "—", BaseUnit);
    public string DesiredQuantityLabel => Strings.Format("DesiredQuantityInUnit", SelectedGroup?.Group.BaseUnit ?? BaseUnit);
    public string ConversionEquation
    {
        get
        {
            if (SelectedOffer?.Offer.Item is not { Quantity: { } quantity, UnitPrice: { } price } item || ConversionFactor is not > 0) return Strings["ConversionPending"];
            try { return Strings.Format("QuantityConversionEquation", Strings.Quantity(quantity, item.Unit), ConversionFactor.Value.ToString("G29", Strings.Culture), Strings.Quantity(quantity * ConversionFactor.Value, BaseUnit), Strings.Money(price / ConversionFactor.Value, 4), BaseUnit); }
            catch (OverflowException) { return Strings["ConversionPending"]; }
        }
    }
    public string ReferenceLabel => Strings[_input?.Inputs.PriceReference == QuotationPriceReference.LinkedOrder ? "ReferenceLinkedOrder" : "LastPurchaseColumn"];
    public bool HasCompetitors => _input?.Proposals.Count(proposal => proposal.IncludedItemCount > 0) > 1;
    public bool HasAlternativeConversion => SelectedCandidate?.Product is { AlternativeQuantity: > 0, AlternativeUnit.Length: > 0 };
    public string ConversionPreview => ConversionFactor is > 0 && SelectedReference is { } source
        ? Strings.Format("MappingConversionPreview", source.Unit, ConversionFactor.Value.ToString("G29", Strings.Culture), BaseUnit) : string.Empty;
    partial void OnConversionFactorChanged(decimal? value) { OnPropertyChanged(nameof(ConversionPreview)); OnPropertyChanged(nameof(ConversionEquation)); }
    partial void OnBaseUnitChanged(string value) { OnPropertyChanged(nameof(ConversionPreview)); OnPropertyChanged(nameof(ConversionEquation)); OnPropertyChanged(nameof(PackagingLabel)); }
    public string CandidateGroup => SelectedCandidate is null ? string.Empty : CanSearchSimilarity
        ? Strings.Format("CandidateTargetGroup", ErpProductIdentity.GroupName(SelectedCandidate.Product))
        : Strings.Format("CandidateExactProduct", SelectedCandidate.Product.ProductCode);
    [RelayCommand] private void EditIdentification() => IsIdentificationOpen = true;
    public ProductSearchViewModel CreateProductSearch() => new(Strings, erp as IErpProductSearch);
    public void SelectProduct(ErpProductCandidate product)
    {
        ItemLink? previous = SelectedOffer?.Offer.Link;
        bool keepConversion = previous is not null && string.Equals(previous.BaseUnit.Trim(), product.BaseUnit?.Trim(), StringComparison.OrdinalIgnoreCase);
        IsIdentificationOpen = true;
        SetCandidates([product]);
        BaseUnit = product.BaseUnit ?? string.Empty;
        ConversionFactor = keepConversion ? previous!.BaseUnitsPerQuotedUnit : null;
        RememberMapping = CanRememberMapping && (preferences is null || preferences.Load().RememberProductMappings);
        IdentificationStatus = Strings[keepConversion ? "IdentificationReplacement" : "IdentificationManual"];
    }
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private AnalysisGroupRow? _selectedGroup;
    [ObservableProperty] private AnalysisOfferRow? _selectedOffer;
    [ObservableProperty] private ErpCandidateRow? _selectedCandidate;
    [ObservableProperty] private string _groupName = string.Empty;
    [ObservableProperty] private string _baseUnit = string.Empty;
    [ObservableProperty] private decimal? _conversionFactor = 1m;
    [ObservableProperty] private decimal? _requestedQuantity;
    [ObservableProperty] private string _similaritySearch = string.Empty;
    [ObservableProperty] private string _productCodeSearch = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStatus))] private string _status = string.Empty;
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    [ObservableProperty] private bool _isBusy;
    public Localizer Strings => strings;
    public ObservableCollection<AnalysisGroupRow> Groups { get; } = [];
    public bool LastBatchIdentificationSucceeded { get; private set; }
    public ObservableCollection<AnalysisOfferRow> Offers { get; } = [];
    public ObservableCollection<SupplierTotalRow> SupplierTotals { get; } = [];
    public string SupplierTotalsHelp => Strings.Format("SupplierTotalsHelp", SupplierTotals.FirstOrDefault()?.Value.CommonGroups ?? 0,
        SupplierTotals.All(row => row.Value.CommonTotal is not null) && SupplierTotals.Select(row => row.Value.CommonTotal).Min() is decimal reference ? Strings.Money(reference) : Strings["NotAvailable"]);
    public string SupplierTotalsCaption => SupplierTotals.FirstOrDefault()?.Value is not { CommonGroups: > 0 } total
        ? Strings["PackageComparison" + (SupplierTotals.FirstOrDefault()?.Value.PendingReason ?? PackageComparisonPending.TooFewProposals)]
        : Strings.Format("SupplierTotalsCaption", total.CommonGroups, total.PendingGroups);
    public ObservableCollection<ErpCandidateRow> Candidates { get; } = [];
    public bool CanEdit => _input?.Quotation.State == QuotationState.Draft;
    public bool HasOffer => SelectedOffer is not null;
    public bool HasCandidate => SelectedCandidate is not null;
    public bool CanIdentifyPending => CanEdit && !IsBusy && _groups.Any(group => !group.IsLinked);
    public string Summary => Strings.Format("AnalysisSummary", _groups.Count, _groups.Count(group => group.Winner is not null), _groups.Count(group => group.Winner is null));
    public string EstimatedTotal => ImportedQuotationAnalysis.SelectedTotal(_groups) is decimal total ? Strings.Money(total) : Strings["AnalysisTotalUnavailable"];
    public string ProposalQuantities(Guid proposalId)
    {
        try { return string.Join(" · ", (_input?.Proposals.FirstOrDefault(proposal => proposal.Id == proposalId)?.Review ?? []).Where(item => !item.IsExcluded).GroupBy(item => item.Unit).Select(group => Strings.Quantity(group.Sum(item => item.Quantity ?? 0m), group.Key))); }
        catch (OverflowException) { return Strings["QuantitiesByItem"]; }
    }
    public string CandidateConversion => SelectedCandidate is null ? string.Empty : !HasAlternativeConversion ? Strings["ManualCandidateConversion"]
        : Strings.Format("CandidateConversion", SelectedCandidate.Product.AlternativeQuantity?.ToString("G29", Strings.Culture) ?? "—", SelectedCandidate.Product.AlternativeUnit, SelectedCandidate.Product.BaseUnit ?? "—");
    partial void OnSelectedCandidateChanged(ErpCandidateRow? value)
    {
        if (value?.Product.BaseUnit is { Length: > 0 } unit)
        {
            if (!string.Equals(BaseUnit.Trim(), unit.Trim(), StringComparison.OrdinalIgnoreCase)) ConversionFactor = null;
            BaseUnit = unit;
        }
        OnPropertyChanged(nameof(CandidateConversion)); OnPropertyChanged(nameof(HasCandidate)); OnPropertyChanged(nameof(HasAlternativeConversion)); OnPropertyChanged(nameof(CandidateGroup));
    }
    [RelayCommand]
    private void UseErpFactor()
    {
        if (SelectedCandidate?.Product is not { AlternativeQuantity: > 0, BaseUnit: { Length: > 0 } } product) return;
        BaseUnit = product.BaseUnit; ConversionFactor = product.AlternativeQuantity;
    }
    [RelayCommand]
    private void UseBaseUnit()
    {
        if (SelectedCandidate?.Product.BaseUnit is not { Length: > 0 } unit) return;
        BaseUnit = unit; ConversionFactor = 1m;
    }
    public Task LoadAsync(Guid quotationId) => RunAsync(async token =>
    {
        _candidatesByCode.Clear(); _identifications.Clear(); _input = await repository.GetAnalysisInputAsync(quotationId, token);
        PriceReferenceIndex = (int)_input.Inputs.PriceReference; Recalculate(); OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(HasLinkedOrders));
    });
    public void Clear() { _input = null; _groups = []; _candidatesByCode.Clear(); _identifications.Clear(); Groups.Clear(); Offers.Clear(); SupplierTotals.Clear(); Candidates.Clear(); SelectedGroup = null; SelectedOffer = null; }
    public void RefreshLabels() { if (_input is not null) Recalculate(); }
    partial void OnSearchTextChanged(string value) => Filter();
    partial void OnFilterIndexChanged(int value) => Filter();
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanIdentifyPending)); OnPropertyChanged(nameof(CanReviewProducts)); }
    private bool Recalculate(string? selectKey = null, string? sourceKey = null)
    {
        if (_input is null) return false;
        _groups = ImportedQuotationAnalysis.Calculate(_input.Proposals, _input.State); Filter();
        bool resetFilters = selectKey is not null && _groups.Any(group => group.Key == selectKey) && !Groups.Any(row => row.Group.Key == selectKey);
        if (resetFilters) { SearchText = string.Empty; FilterIndex = 0; Filter(); }
        SelectedGroup = Groups.FirstOrDefault(row => row.Group.Key == selectKey) ?? Groups.FirstOrDefault();
        if (selectKey is not null && SelectedGroup is not null) SelectedGroup.IsExpanded = true;
        if (sourceKey is not null) SelectedOffer = Offers.FirstOrDefault(row => row.Offer.SourceKey == sourceKey) ?? SelectedOffer;
        SupplierTotals.Clear();
        foreach (var total in SupplierQuotationComparison.Calculate(_input.Proposals, _groups, _input.Inputs)
            .OrderBy(total => (total.CommonTotal ?? total.QuotedTotal) is null)
            .ThenBy(total => total.CommonTotal ?? total.QuotedTotal)
            .ThenBy(total => total.Supplier, StringComparer.Create(Strings.Culture, true)))
            SupplierTotals.Add(new(total, total.QuotedTotal is decimal quoted ? Strings.Money(quoted) : Strings["NotAvailable"],
                total.CommonTotal is decimal common ? Strings.Money(common) : Strings["PackagePending"],
                total.PremiumPercent is null ? string.Empty : total.PremiumPercent == 0 ? Strings["PackageReference"] : Strings.Format("PackagePremium", SignedPercent(total.PremiumPercent)),
                Strings.Format("SupplierPriceCounts", total.IncludedItems, total.LowestPriceItems, total.TiedLowestPriceItems))
            {
                Strings = Strings,
                PurchaseVariation = Strings.Format(_input.Inputs.PriceReference == QuotationPriceReference.LinkedOrder ? "SupplierOrderVariation" : "SupplierPurchaseVariation", total.PurchaseVariationPercent is null ? Strings["SupplierPurchaseMissing"] : SignedPercent(total.PurchaseVariationPercent)),
                PurchaseVariationHelp = total.PurchaseVariationPercent is null ? Strings[_input.Inputs.PriceReference == QuotationPriceReference.LinkedOrder ? "SupplierOrderUnavailable" : "SupplierPurchaseUnavailable"]
                    : Strings.Format(_input.Inputs.PriceReference == QuotationPriceReference.LinkedOrder ? "SupplierOrderHelp" : "SupplierPurchaseHelp", Strings.Money(total.QuotedTotal!.Value), Strings.Money(total.LastPurchaseTotal!.Value), SignedPercent(total.PurchaseVariationPercent))
                        + Environment.NewLine + SupplierReferenceInputs(total.ProposalId)
            });
        OnPropertyChanged(nameof(SupplierTotalsHelp));
        OnPropertyChanged(nameof(SupplierTotalsCaption));
        foreach (string property in new[] { nameof(Summary), nameof(EstimatedTotal), nameof(CanIdentifyPending), nameof(ReferenceLabel), nameof(HasCompetitors) }) OnPropertyChanged(property);
        return resetFilters;
    }
    private string SupplierReferenceInputs(Guid proposalId)
    {
        List<string> values = [];
        foreach (var group in _groups)
            foreach (var offer in group.Offers.Where(offer => offer.ProposalId == proposalId))
                values.Add(_input?.Inputs.PriceReference == QuotationPriceReference.LinkedOrder ? ReferenceHelp(group, offer.Link?.Product)
                    : offer.Link?.Product is { } product && offer.BaseQuantity is { } quantity && product.LastPurchaseUnitPrice is { } price
                        ? Strings.Format("SupplierPurchaseInput", product.ProductCode, Strings.Quantity(quantity, offer.Link.BaseUnit), Strings.Money(price, 4), product.LastPurchaseDate?.ToString("d", Strings.Culture) ?? Strings["NotAvailable"])
                        : Strings["SupplierPurchaseUnavailable"]);
        return string.Join(Environment.NewLine, values);
    }
    private void Filter()
    {
        string? old = SelectedGroup?.Group.Key;
        var expanded = Groups.Where(row => row.IsExpanded).Select(row => row.Group.Key).ToHashSet(StringComparer.Ordinal);
        Groups.Clear();
        string[] terms = SearchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (AnalyzedGroup group in _groups.Where(group => (FilterIndex != 1 || !group.IsLinked) && (FilterIndex != 2 || group.Winner is not null)
            && terms.All(term => Strings.Culture.CompareInfo.IndexOf(group.Name + " " + string.Join(" ", group.Offers.Select(offer => offer.SupplierName + " " + offer.Item.ProductCode + " " + offer.Item.Description + " " + offer.Item.Color + " " + offer.Item.Unit
                + " " + offer.Link?.Product?.ProductCode + " " + offer.Link?.Product?.Name)), term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)))
        {
            var offers = BuildOffers(group);
            var lowest = group.IsLinked && !group.HasUnitConflict ? offers.Where(row => row.Offer.BaseUnitPrice == group.LowestUnitPrice).ToArray() : [];
            var reference = lowest.FirstOrDefault()?.Offer.Link?.Product;
            int? candidateCount = IdentificationCandidateCount(group);
            Groups.Add(new(group, GroupDetail(group, candidateCount))
            {
                Strings = strings,
                IdentificationCandidateCount = candidateCount,
                ReferenceLabel = ReferenceLabel,
                Offers = offers,
                IsExpanded = expanded.Contains(group.Key),
                LowestSupplier = !group.IsLinked ? group.Offers[0].SupplierName : lowest.Length == 0 ? Strings["AnalysisUnlinked"] : string.Join(" / ", lowest.Select(row => row.Supplier).Distinct()),
                BestPrice = lowest.FirstOrDefault()?.BasePrice ?? "—",
                LastPurchase = PurchasePrice(reference, group.BaseUnit, group),
                LastPurchaseHelp = ReferenceHelp(group, reference),
                SpreadHelp = SpreadHelp(group)
            });
        }
        SelectedGroup = Groups.FirstOrDefault(row => row.Group.Key == old) ?? Groups.FirstOrDefault();
    }
    private int? IdentificationCandidateCount(AnalyzedGroup group)
    {
        if (group.IsLinked || _input is null) return null;
        var offer = group.Offers[0];
        var source = SupplierProductReference.From(_input.Proposals.Single(proposal => proposal.Id == offer.ProposalId), offer.Item);
        return _identifications.TryGetValue(source.Key, out var identification) ? identification.Candidates.Count
            : _candidatesByCode.TryGetValue(source.LookupKey, out var candidates) ? candidates.Count : null;
    }
    private void RefreshIdentificationRows()
    {
        foreach (var row in Groups.Where(row => !row.Group.IsLinked))
        {
            row.IdentificationCandidateCount = IdentificationCandidateCount(row.Group);
            row.Detail = GroupDetail(row.Group, row.IdentificationCandidateCount);
        }
    }
    private string GroupDetail(AnalyzedGroup group, int? candidateCount)
    {
        if (!group.IsLinked) return Strings[candidateCount switch { 0 => "IdentificationRowNotFound", 1 => "IdentificationRowFound", > 1 => "IdentificationRowAmbiguous", _ => "IdentificationPending" }];
        string detail = Strings.Format("GroupRowDetail", group.Offers.Count, group.BaseUnit);
        string? pending = group.HasUnitConflict ? "AnalysisUnitConflict" : group.HasVariantConflict ? "AnalysisVariantConflict" : group.HasTie ? "AnalysisTied" : null;
        return pending is null ? detail : detail + " · " + Strings[pending];
    }
    partial void OnSelectedGroupChanged(AnalysisGroupRow? value)
    {
        SelectedOffer = null;
        Offers.Clear(); Candidates.Clear(); SelectedCandidate = null;
        RequestedQuantity = value?.Group.RequestedQuantity;
        OnPropertyChanged(nameof(DemandOriginLabel));
        OnPropertyChanged(nameof(DemandOriginHelp));
        OnPropertyChanged(nameof(DesiredQuantityLabel));
        if (value is not null) foreach (var offer in value.Offers) Offers.Add(offer);
        SelectedOffer = Offers.FirstOrDefault();
    }

    private IReadOnlyList<AnalysisOfferRow> BuildOffers(AnalyzedGroup group)
    {
        List<AnalysisOfferRow> rows = [];
        foreach (AnalyzedOffer offer in group.Offers.OrderBy(offer => offer.BaseUnitPrice is null).ThenBy(offer => offer.BaseUnitPrice).ThenBy(offer => offer.SupplierName, StringComparer.OrdinalIgnoreCase))
        {
            string erpText = offer.Link?.Product is { } product ? product.ProductCode + " · " + product.Name
                : offer.Link is { } link ? Strings.Format("ManualGroupLink", link.GroupName) : Strings["AnalysisUnlinked"];
            string normalized = offer.BaseQuantity is decimal quantity && offer.BaseUnitPrice is decimal price
                ? Strings.Format("NormalizedOffer", Strings.Quantity(quantity, offer.Link!.BaseUnit), Strings.Money(price, 4), offer.Link.BaseUnit) : Strings["NotComparable"];
            string coverage = !group.HasUnitConflict && offer.BaseQuantity is decimal available && group.RequestedQuantity is decimal requested
                ? available == requested ? Strings["DemandCovered"] : Strings.Format(available > requested ? "OfferExcess" : "OfferShortage", Strings.Quantity(Math.Abs(available - requested), group.BaseUnit)) : string.Empty;
            var position = OfferPricePosition.Calculate(group, offer);
            decimal? purchase = PurchaseReference(offer.Link?.Product, offer.Link?.BaseUnit, group);
            decimal? variation = PriceVariation.Percent(offer.BaseUnitPrice, purchase);
            string percent = position.PremiumPercent?.ToString("N2", Strings.Culture) ?? "—";
            rows.Add(new(offer, Strings.Quantity(offer.Item.Quantity ?? 0m, offer.Item.Unit), Strings.Money(offer.Item.UnitPrice ?? 0m, 4) + "/" + offer.Item.Unit,
                Strings.Money(offer.Item.ReportedTotal ?? 0m), erpText, History(offer.Link?.Product), normalized, coverage, group.Winner == offer, Strings["SelectedLot"])
            {
                Strings = strings,
                IsBestUnitPrice = position.IsLowest,
                PriceBadge = Strings[position.IsTiedLowest ? "LowestUnitPriceTied" : "LowestUnitPriceBadge"],
                PriceDifferenceHelp = position.CanCompare ? Strings.Format("UnitPricePremiumHelp", offer.BaseUnitPrice!.Value.ToString("G29", Strings.Culture),
                    group.LowestUnitPrice!.Value.ToString("G29", Strings.Culture), offer.Link!.BaseUnit, percent) : Strings["PriceComparisonNeedsOffers"],
                CompactDifference = SignedPercent(position.PremiumPercent),
                IsPriceHigher = position.PremiumPercent > 0,
                ReferenceLabel = ReferenceLabel,
                HasCompetitors = group.Offers.Select(row => row.ProposalId).Distinct().Count() > 1,
                LastPurchase = PurchasePrice(offer.Link?.Product, offer.Link?.BaseUnit, group),
                PurchaseVariation = SignedPercent(variation),
                PurchaseVariationHelp = variation is null ? Strings[_input?.Inputs.PriceReference == QuotationPriceReference.LinkedOrder ? "OrderVariationUnavailable" : "PurchaseVariationUnavailable"] : Strings.Format(_input?.Inputs.PriceReference == QuotationPriceReference.LinkedOrder ? "OrderVariationHelp" : "PurchaseVariationHelp",
                    offer.BaseUnitPrice!.Value.ToString("G29", Strings.Culture), purchase!.Value.ToString("G29", Strings.Culture), offer.Link!.BaseUnit,
                    SignedPercent(variation), ReferenceHelp(group, offer.Link.Product)),
                IsPurchaseIncrease = variation > 0,
                IsPurchaseDecrease = variation < 0,
                BasePrice = offer.BaseUnitPrice is decimal basePrice ? Strings.Money(basePrice, 4) + "/" + offer.Link!.BaseUnit : Strings["NotComparable"],
                BaseQuantity = offer.BaseQuantity is decimal baseQuantity ? Strings.Quantity(baseQuantity, offer.Link!.BaseUnit) : string.Empty
            });
        }
        return rows;
    }

    private string SignedPercent(decimal? value) => value is decimal percent
        ? percent is > 0 and < 0.01m ? "+<0,01%".Replace(",", Strings.Culture.NumberFormat.NumberDecimalSeparator)
        : percent is < 0 and > -0.01m ? "−<0,01%".Replace(",", Strings.Culture.NumberFormat.NumberDecimalSeparator)
        : (percent > 0 ? "+" : "") + percent.ToString("N2", Strings.Culture) + "%" : "—";

    private decimal? PurchaseReference(ErpProductCandidate? product, string? unit, AnalyzedGroup? group = null) => _input?.Inputs.PriceReference == QuotationPriceReference.LinkedOrder
        ? group is null ? null : OrderInputRules.Price(group, _input.Inputs)?.UnitPrice : product?.LastPurchaseUnitPrice is >= 0
        && !string.IsNullOrWhiteSpace(unit) && string.Equals(product.BaseUnit, unit, StringComparison.OrdinalIgnoreCase)
        && string.Equals(product.LastPurchaseUnit ?? product.BaseUnit, unit, StringComparison.OrdinalIgnoreCase) ? product.LastPurchaseUnitPrice : null;
    private string PurchasePrice(ErpProductCandidate? product, string? unit, AnalyzedGroup? group = null) => PurchaseReference(product, unit, group) is decimal price ? Strings.Money(price, 4) + "/" + unit : "—";
    private string ReferenceHelp(AnalyzedGroup group, ErpProductCandidate? product)
    {
        if (_input?.Inputs.PriceReference != QuotationPriceReference.LinkedOrder) return product is null ? History(null) : product.ProductCode + " · " + product.Name + Environment.NewLine + History(product);
        var reference = OrderInputRules.Price(group, _input.Inputs);
        return reference is null ? Strings["OrderPriceUnavailable"] : Strings.Format("OrderPriceFormula", Strings.Money(reference.UnitPrice, 4), Strings.Quantity(reference.Quantity, group.BaseUnit))
            + Environment.NewLine + string.Join(Environment.NewLine, reference.Contributions.Select(source => Strings.Format("OrderPriceContribution", source.Order.Header.Number, source.Order.Header.Id, source.Line.Id,
                Strings.Quantity(source.Line.Quantity, source.Line.Unit), source.Line.BaseUnitsPerUnit.ToString("G29", Strings.Culture), Strings.Money(source.Line.UnitPrice, 4), source.Order.FetchedAtUtc.ToLocalTime().ToString("g", Strings.Culture)) + " " + QuantityState(source.Line)));
    }
    private string QuantityState(Domain.Purchasing.PurchaseOrderLine line) => Strings.Format("OrderQuantityState", Strings.Quantity(line.Quantity, line.Unit),
        Strings.Quantity(line.ReceivedQuantity, line.Unit), Strings.Quantity(Math.Max(0, line.Quantity - line.ReceivedQuantity), line.Unit));
    private string SpreadHelp(AnalyzedGroup group) => group.UnitPriceSpreadPercent is decimal percent
        ? Strings.Format("PriceSpreadHelp", group.LowestUnitPrice!.Value.ToString("G29", Strings.Culture), group.HighestUnitPrice!.Value.ToString("G29", Strings.Culture), group.BaseUnit, percent.ToString("N2", Strings.Culture))
        : Strings["PriceComparisonNeedsOffers"];

    [RelayCommand]
    private void ToggleGroup(AnalysisGroupRow row) { SelectedGroup = row; row.IsExpanded = !row.IsExpanded; }

    public void PrepareEditor(string sourceKey)
    {
        var group = Groups.FirstOrDefault(row => row.Offers.Any(offer => offer.Offer.SourceKey == sourceKey));
        if (group is null) return;
        SelectedGroup = group;
        SelectedOffer = Offers.First(offer => offer.Offer.SourceKey == sourceKey);
        IsIdentificationOpen = true;
    }
    partial void OnSelectedOfferChanged(AnalysisOfferRow? value)
    {
        SelectedCandidate = null; Candidates.Clear();
        GroupName = value?.Offer.Link?.GroupName ?? string.Empty;
        BaseUnit = value?.Offer.Link?.BaseUnit ?? value?.Offer.Item.Unit ?? string.Empty;
        ConversionFactor = value?.Offer.Link?.BaseUnitsPerQuotedUnit ?? 1m;
        SimilaritySearch = value?.Offer.Link?.Product?.SimilarityCode ?? string.Empty;
        ProductCodeSearch = value?.Code ?? string.Empty;
        RememberMapping = CanRememberMapping && preferences?.Load().RememberProductMappings == true;
        IsIdentificationOpen = value?.Offer.Link is null;
        IdentificationStatus = Strings[value?.Offer.Link is null ? "IdentificationPending" : "IdentificationConfirmed"];
        if (value?.Offer.Link?.Product is { } linkedProduct) SetCandidates([linkedProduct]);
        else if (SelectedReference is { } source && _identifications.TryGetValue(source.Key, out var identification)) ApplyIdentification(identification);
        else if (SelectedReference is { } reference && _candidatesByCode.TryGetValue(reference.LookupKey, out var candidates)) SetCandidates(candidates);
        OnPropertyChanged(nameof(HasOffer));
        OnPropertyChanged(nameof(CanSearchSimilarity));
        OnPropertyChanged(nameof(SourceReference)); OnPropertyChanged(nameof(SourceSupplier));
        OnPropertyChanged(nameof(ConfirmedProductLabel));
        OnPropertyChanged(nameof(HasConfirmedProduct)); OnPropertyChanged(nameof(ConversionPreview));
        OnPropertyChanged(nameof(ConfirmProductLabel));
        OnPropertyChanged(nameof(ProductActionLabel)); OnPropertyChanged(nameof(ConversionEquation)); OnPropertyChanged(nameof(PackagingLabel));
    }
    private string History(ErpProductCandidate? product)
    {
        if (product?.LastPurchaseUnitPrice is not decimal price) return Strings["NoPurchaseHistory"];
        return Strings.Format("ErpHistory", product.LastPurchaseDate?.ToString("d", Strings.Culture) ?? "—", Strings.Money(price, 4),
            product.BaseUnit ?? product.LastPurchaseUnit ?? Strings["HistoryUnitUnknown"], product.FetchedAtUtc.ToLocalTime().ToString("g", Strings.Culture));
    }
    [RelayCommand]
    private Task RefreshErpDataAsync() => RunAsync(async token =>
    {
        if (!CanEdit || SelectedOffer?.Offer.Link?.Product is not { } saved || erp is not IErpProductSearch search) return;
        var products = await search.FindByProductCodeAsync(saved.ProductCode, token);
        var fresh = products.Where(product => product.CatalogIdentity == saved.CatalogIdentity && product.ProductCode == saved.ProductCode).ToArray();
        SetCandidates(fresh); IsIdentificationOpen = true;
        IdentificationStatus = Strings["ErpRefreshPreview"]; Status = IdentificationStatus;
    });
    [RelayCommand]
    private Task SearchErpAsync() => RunAsync(async token =>
    {
        IsIdentificationOpen = true;
        Candidates.Clear(); SelectedCandidate = null;
        if (mappings is not null && erp is IErpProductSearch search && SelectedReference is { } source)
        {
            string catalog = await search.GetCatalogIdentityAsync(token);
            var result = await new ErpIdentificationService(erp).ResolveAsync(source, catalog, mappings, token);
            _identifications[source.Key] = result; RefreshIdentificationRows(); ApplyIdentification(result);
            return;
        }
        var lookup = SelectedReference is { } reference ? reference with { Code = ProductCodeSearch } : new SupplierProductReference("", ProductCodeSearch, "", "", "");
        var products = await new ErpIdentificationService(erp).FindCandidatesAsync(lookup, token);
        _candidatesByCode[lookup.LookupKey] = products; RefreshIdentificationRows(); SetCandidates(products);
        Status = Strings.Format("ErpCandidatesLoaded", Candidates.Count);
    });
    [RelayCommand]
    private Task SearchSimilarAsync() => RunAsync(async token =>
    {
        if (!CanSearchSimilarity) throw new ErpException(ErpError.InvalidQuery);
        SetCandidates(await erp.FindBySimilarityAsync(SimilaritySearch, token));
        Status = Strings.Format("ErpCandidatesLoaded", Candidates.Count);
    });
    [RelayCommand]
    private Task IdentifyPendingAsync() => RunAsync(async token =>
    {
        LastBatchIdentificationSucceeded = false;
        if (_input is null || !CanEdit) throw new AnalysisException(AnalysisError.ReadOnly);
        if (mappings is not null && erp is IErpProductSearch search)
        {
            var resolved = await new ErpIdentificationService(erp).ResolvePendingAsync(_input, await search.GetCatalogIdentityAsync(token), mappings, token);
            foreach (var pair in resolved) _identifications[pair.Key] = pair.Value;
            RefreshIdentificationRows();
            if (SelectedReference is { } source && _identifications.TryGetValue(source.Key, out var selected)) ApplyIdentification(selected);
            Status = Strings.Format("ErpBatchLoaded", resolved.Count, resolved.Count(pair => pair.Value.Candidates.Count > 0));
            LastBatchIdentificationSucceeded = true;
            return;
        }
        var results = await new ErpIdentificationService(erp).LookupPendingAsync(_input, token);
        foreach (var pair in results) _candidatesByCode[pair.Key] = pair.Value;
        RefreshIdentificationRows();
        if (SelectedReference is { } reference && _candidatesByCode.TryGetValue(reference.LookupKey, out var candidates)) SetCandidates(candidates);
        Status = Strings.Format("ErpBatchLoaded", results.Count, results.Count(pair => pair.Value.Count > 0));
        LastBatchIdentificationSucceeded = true;
    });
    private void SetCandidates(IReadOnlyList<ErpProductCandidate> products)
    {
        Candidates.Clear(); SelectedCandidate = null;
        foreach (ErpProductCandidate product in products)
            Candidates.Add(new(product, string.IsNullOrWhiteSpace(product.SupplierProductCode)
                ? Strings.Format("ErpDirectCandidateDetail", product.SupplierName, product.BaseUnit ?? "—", Strings[product.IsInactive ? "InactiveProduct" : "ActiveProduct"])
                : Strings.Format("ErpCandidateDetail", product.SupplierName, product.SupplierProductCode, product.AlternativeQuantity?.ToString("G29", Strings.Culture) ?? "—", product.AlternativeUnit,
                Strings[product.IsInactive ? "InactiveProduct" : "ActiveProduct"])));
        if (Candidates.Count == 1) SelectedCandidate = Candidates[0];
    }
    private void ApplyIdentification(ProductIdentificationResult result)
    {
        IsIdentificationOpen = true;
        SetCandidates(result.Candidates);
        IdentificationStatus = Strings[result.IsOffline ? "IdentificationOffline" : result.Candidates.Count > 1 ? "IdentificationRowAmbiguous" : "Identification" + result.Origin];
        if (result.Mapping is { } mapping)
        {
            BaseUnit = mapping.BaseUnit; ConversionFactor = mapping.ConversionFactor;
        }
    }
    [RelayCommand] private Task LinkErpAsync() => SaveLinkAsync(true);
    [RelayCommand] private Task LinkManualAsync() => SaveLinkAsync(false);
    public bool LastProductConfirmationSucceeded { get; private set; }
    private Task SaveLinkAsync(bool useErp) => RunAsync(async token =>
    {
        LastProductConfirmationSucceeded = false;
        bool mappingSaved = true;
        if (_input is null || SelectedOffer is null || !CanEdit) throw new AnalysisException(AnalysisError.ReadOnly);
        AnalyzedOffer offer = SelectedOffer.Offer;
        SupplierProductReference? source = SelectedReference;
        bool remember = useErp && RememberMapping && mappings is not null && source is not null;
        ErpProductCandidate? product = useErp ? SelectedCandidate?.Product ?? throw new AnalysisException(AnalysisError.InvalidLink) : null;
        var groupingVersion = OrderInputRules.GroupingVersion(_input, offer.ProposalId);
        string name = useErp ? groupingVersion == ProductGroupingVersion.ExactProductCode ? product!.Name : ErpProductIdentity.GroupName(product!) : GroupName.Trim();
        string key = useErp ? ErpProductIdentity.GroupKey(product!, groupingVersion) : "manual:" + name.ToUpperInvariant();
        ItemLink link = new(offer.ProposalId, offer.Item.CaptureId, key, name, BaseUnit.Trim(), ConversionFactor ?? 0m, product, DateTime.UtcNow)
        { GroupingVersion = groupingVersion };
        ImportedQuotationAnalysis.ValidateLink(link, offer.Item, source?.CodeKind ?? QuoteMatch.Domain.Importing.ProductCodeKind.Supplier);
        SupplierProductMapping? mappingToSave = null;
        if (remember)
        {
            string catalog = erp is IErpProductSearch search ? await search.GetCatalogIdentityAsync(token) : product!.CatalogIdentity;
            if (product!.CatalogIdentity != catalog) throw new ProductMappingException(ProductMappingError.Invalid);
            var previous = await mappings!.FindMappingAsync(catalog, source!, token);
            mappingToSave = new(previous?.Id ?? Guid.NewGuid(), previous?.Version ?? 0, source!, product, link.BaseUnit,
                link.BaseUnitsPerQuotedUnit, ProductMappingState.Active, previous?.CreatedAtUtc ?? DateTime.UtcNow, DateTime.UtcNow);
            mappingToSave.Validate();
        }
        var links = _input.State.Links.Where(old => (old.ProposalId, old.CaptureId) != (link.ProposalId, link.CaptureId)).Append(link).ToArray();
        var proposed = _input.State with { Links = links, InputsRevision = _input.Quotation.Revision };
        if (_input.Inputs.Orders.Count > 0) proposed = OrderInputRules.Apply(_input, false, links);
        AnalysisState saved = await repository.SaveAnalysisAsync(proposed, token);
        _input = _input with { State = saved }; bool resetFilters = Recalculate(key, offer.SourceKey); Status = Strings["AnalysisLinkSaved"];
        if (mappingToSave is not null)
        {
            try
            {
                var savedMapping = await mappings!.SaveMappingAsync(mappingToSave, token);
                _identifications[source!.Key] = new([savedMapping.Product], ProductIdentificationOrigin.SavedMapping, savedMapping);
                Status = Strings["MappingAndLinkSaved"];
            }
            catch (Exception exception) when (exception is ProductMappingException or Microsoft.Data.Sqlite.SqliteException
                or Microsoft.EntityFrameworkCore.DbUpdateException or IOException or UnauthorizedAccessException or OperationCanceledException)
            { Status = Strings["MappingSavePending"]; mappingSaved = false; }
        }
        if (resetFilters) Status += " " + Strings["AnalysisFiltersReset"];
        if (useErp)
        {
            SaveMappingPreference(remember);
            LastProductConfirmationSucceeded = mappingSaved;
        }
    });
    [RelayCommand]
    private Task UnlinkAsync() => RunAsync(async token =>
    {
        if (_input is null || SelectedOffer is null) return;
        AnalyzedOffer offer = SelectedOffer.Offer;
        AnalysisState saved = await repository.SaveAnalysisAsync(_input.State with
        {
            InputsRevision = _input.Quotation.Revision,
            Links = _input.State.Links.Where(link => (link.ProposalId, link.CaptureId) != (offer.ProposalId, offer.Item.CaptureId)).ToArray()
        }, token);
        _input = _input with { State = saved }; Recalculate();
    });
    [RelayCommand]
    private Task ApplyDemandAsync() => RunAsync(async token =>
    {
        if (_input is null || SelectedGroup is null || !SelectedGroup.Group.IsLinked || SelectedGroup.Group.HasUnitConflict || RequestedQuantity is not > 0) throw new AnalysisException(AnalysisError.InvalidDemand);
        AnalyzedGroup group = SelectedGroup.Group;
        var demands = _input.State.Demands.Where(demand => demand.GroupKey != group.Key).Append(new GroupDemand(group.Key, group.BaseUnit, RequestedQuantity.Value)).ToArray();
        AnalysisState saved = await repository.SaveAnalysisAsync(_input.State with { Demands = demands, InputsRevision = _input.Quotation.Revision }, token);
        _input = _input with { State = saved }; Recalculate(group.Key); Status = Strings["AnalysisDemandSaved"];
    });
    private CancellationTokenSource? _operation;
    [RelayCommand] private void Cancel() => _operation?.Cancel();
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return; using CancellationTokenSource operation = new(); _operation = operation; IsBusy = true; Status = Strings["ImportWorking"];
        try { await action(operation.Token); if (Status == Strings["ImportWorking"]) Status = string.Empty; }
        catch (OperationCanceledException) { Status = Strings["ImportCanceled"]; }
        catch (ErpException exception) { Status = Strings["ErpError" + exception.Code]; }
        catch (AnalysisException exception) { Status = Strings["AnalysisError" + exception.Code]; }
        catch (QuotationException exception) { Status = Strings["QuotationError" + exception.Code]; }
        catch (ProductMappingException exception) { Status = Strings["MappingError" + exception.Code]; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or Microsoft.EntityFrameworkCore.DbUpdateException) { Status = Strings["ImportStorageError"]; }
        finally { _operation = null; IsBusy = false; }
    }
}

