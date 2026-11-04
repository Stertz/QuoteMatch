using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Desktop.ViewModels;

public partial class ProductReviewRow : ObservableObject
{
    private readonly SupplierProductMapping? _mapping;
    private bool _updatingQuantity;
    private bool _initializing = true;
    private bool _settingDemand;
    private readonly ItemLink? _confirmed;
    private readonly GroupDemand? _originalDemand;
    public ProductReviewRow(SavedProposal proposal, ReviewedItem item, IReadOnlyList<ErpProductCandidate> candidates, SupplierProductMapping? mapping, Localizer strings,
        ItemLink? confirmed = null, GroupDemand? demand = null)
    {
        Proposal = proposal; Item = item; Strings = strings; _mapping = mapping; _confirmed = confirmed; _originalDemand = demand;
        foreach (var product in candidates) Candidates.Add(new(product, product.BaseUnit ?? string.Empty));
        if (Candidates.Count == 1) SelectedCandidate = Candidates[0];
        UseDemand(demand); _initializing = false;
        IsSelected = !IsConfirmed && SelectedCandidate is { Product.IsInactive: false };
    }
    public SavedProposal Proposal { get; }
    public ReviewedItem Item { get; }
    public Localizer Strings { get; }
    public string OriginalProduct => Item.ProductCode + " · " + Item.Description;
    public string Source => Proposal.Template.SupplierName + " · " + Proposal.FileName;
    public bool HasVariant => !string.IsNullOrWhiteSpace(Item.Color);
    public string Variant => HasVariant ? Strings.Format("BatchSourceColor", Item.Color) : string.Empty;
    public string OriginalQuantity => Item.Quantity is decimal quantity ? Strings.Quantity(quantity, Item.Unit) : "—";
    public string OriginalPrice => Item.UnitPrice is decimal price ? Strings.Money(price, 4) + "/" + Item.Unit : "—";
    public string BaseUnit => SelectedCandidate?.Product.BaseUnit ?? string.Empty;
    public ObservableCollection<ErpCandidateRow> Candidates { get; } = [];
    [ObservableProperty] private ErpCandidateRow? _selectedCandidate;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private decimal? _conversionFactor;
    [ObservableProperty] private decimal? _systemQuantity;
    [ObservableProperty] private decimal? _requestedQuantity;
    [ObservableProperty] private GroupDemand? _demand;
    [ObservableProperty] private bool _showDesiredQuantity;
    public bool IsConfirmed => _confirmed?.Product is not null;
    public bool HasDemandChange => !SameDemand(_originalDemand, Demand);
    public bool IsPending => !IsConfirmed || SelectedCandidate?.Product != _confirmed?.Product || ConversionFactor != _confirmed?.BaseUnitsPerQuotedUnit || HasDemandChange;
    public string DemandSourceLabel => Demand is null ? string.Empty : Strings[Demand.Origin switch
    { DemandOrigin.Document => "DemandSourceDocument", DemandOrigin.LinkedOrder or DemandOrigin.ReferenceOrder => "DemandSourceOrder", _ => "DemandSourceManual" }];
    public string DesiredUnit => Demand?.BaseUnit ?? BaseUnit;
    public string SystemPrice
    {
        get
        {
            try { return ConversionFactor is > 0 && Item.UnitPrice is decimal price ? Strings.Money(price / ConversionFactor.Value, 4) + "/" + BaseUnit : "—"; }
            catch (OverflowException) { return "—"; }
        }
    }
    public string LastPurchase => SelectedCandidate?.Product is { LastPurchaseUnitPrice: decimal price } product
        && price >= 0
        && string.Equals(product.LastPurchaseUnit ?? product.BaseUnit, BaseUnit, StringComparison.OrdinalIgnoreCase)
        ? Strings.Format("BatchLastPurchase", Strings.Money(price, 4) + "/" + BaseUnit) : string.Empty;
    public string ConversionValidation
    {
        get
        {
            if (SelectedCandidate is null) return Strings[Candidates.Count > 1 ? "IdentificationRowAmbiguous" : "IdentificationPending"];
            if (SelectedCandidate.Product.IsInactive) return Strings["InactiveProduct"];
            if (Proposal.Template.CodeKind == ProductCodeKind.Internal && !string.Equals(Item.ProductCode.Trim(), SelectedCandidate.Code.Trim(), StringComparison.OrdinalIgnoreCase))
                return Strings["BatchExactCodeRequired"];
            if (string.IsNullOrWhiteSpace(BaseUnit)) return Strings["BatchBaseUnitMissing"];
            if (Item.Quantity is not > 0) return Strings["BatchSourceQuantityMissing"];
            if (ConversionFactor is not > 0) return Strings["BatchFactorRequired"];
            try
            {
                if (SystemQuantity != Item.Quantity * ConversionFactor) return Strings["BatchQuantityPrecision"];
                _ = Item.UnitPrice / ConversionFactor;
            }
            catch (OverflowException) { return Strings["BatchNumericLimit"]; }
            return string.Empty;
        }
    }
    public string Validation => ConversionValidation.Length > 0 ? ConversionValidation
        : RequestedQuantity is <= 0 || Demand is not null && !string.Equals(Demand.BaseUnit, BaseUnit, StringComparison.OrdinalIgnoreCase)
            ? Strings["ReviewDesiredQuantity"] : string.Empty;
    public bool HasValidation => Validation.Length > 0;
    public ProductConfirmation Confirmation() => new(Proposal.Id, Item.CaptureId, SelectedCandidate!.Product, BaseUnit, ConversionFactor!.Value);
    partial void OnSelectedCandidateChanged(ErpCandidateRow? value)
    {
        var product = value?.Product;
        ConversionFactor = SuggestedFactor(product);
        IsSelected = product is { IsInactive: false };
        OnPropertyChanged(nameof(BaseUnit)); OnPropertyChanged(nameof(LastPurchase)); RefreshValues();
    }
    private decimal? SuggestedFactor(ErpProductCandidate? product)
    {
        if (product is null) return null;
        if (_confirmed?.Product?.ProductCode == product.ProductCode && string.Equals(_confirmed.BaseUnit, product.BaseUnit, StringComparison.OrdinalIgnoreCase))
            return _confirmed.BaseUnitsPerQuotedUnit;
        if (_mapping is { ConversionFactor: > 0 } mapping && mapping.Product.ProductCode == product.ProductCode
            && string.Equals(mapping.BaseUnit, product.BaseUnit, StringComparison.OrdinalIgnoreCase)) return mapping.ConversionFactor;
        if (Proposal.Template.CodeKind == ProductCodeKind.Supplier && product.AlternativeQuantity is > 0
            && string.Equals(Item.Unit.Trim(), product.AlternativeUnit.Trim(), StringComparison.OrdinalIgnoreCase)) return product.AlternativeQuantity;
        return string.Equals(Item.Unit.Trim(), product.BaseUnit?.Trim(), StringComparison.OrdinalIgnoreCase) ? 1m : null;
    }
    partial void OnConversionFactorChanged(decimal? value)
    {
        if (!_updatingQuantity)
        {
            _updatingQuantity = true;
            try { SystemQuantity = Item.Quantity * value; }
            catch (OverflowException) { SystemQuantity = null; }
            finally { _updatingQuantity = false; }
        }
        RefreshValues();
    }
    partial void OnSystemQuantityChanged(decimal? value)
    {
        if (!_updatingQuantity)
        {
            _updatingQuantity = true;
            try { ConversionFactor = Item.Quantity is > 0 ? value / Item.Quantity : null; }
            catch (OverflowException) { ConversionFactor = null; }
            finally { _updatingQuantity = false; }
        }
        RefreshValues();
    }
    private void RefreshValues()
    {
        OnPropertyChanged(nameof(SystemPrice)); OnPropertyChanged(nameof(Validation)); OnPropertyChanged(nameof(HasValidation));
        OnPropertyChanged(nameof(IsPending)); OnPropertyChanged(nameof(DesiredUnit));
        if (!_initializing && IsPending && SelectedCandidate is { Product.IsInactive: false }) IsSelected = true;
    }
    partial void OnRequestedQuantityChanged(decimal? value)
    {
        if (_settingDemand) return;
        Demand = value is decimal quantity ? new(_confirmed?.GroupKey ?? string.Empty, BaseUnit, quantity) : null;
        RefreshDemand();
    }
    public void UseDemand(GroupDemand? demand, bool select = false)
    {
        _settingDemand = true;
        try { Demand = demand; RequestedQuantity = demand?.Quantity; }
        finally { _settingDemand = false; }
        if (select) IsSelected = true;
        RefreshDemand();
    }
    private void RefreshDemand()
    {
        OnPropertyChanged(nameof(HasDemandChange)); OnPropertyChanged(nameof(DemandSourceLabel)); RefreshValues();
    }
    public ProductDemandChange DemandChange() => new(Proposal.Id, Item.CaptureId, RequestedQuantity, Demand?.Origin ?? DemandOrigin.Manual, Demand?.DocumentSource, Demand?.Sources);
    private static bool SameDemand(GroupDemand? first, GroupDemand? second) => first is null || second is null ? first is null && second is null
        : first.Quantity == second.Quantity && first.Origin == second.Origin && first.DocumentSource == second.DocumentSource
            && string.Equals(first.BaseUnit, second.BaseUnit, StringComparison.OrdinalIgnoreCase) && first.Sources.SequenceEqual(second.Sources);
    public void SelectProduct(ErpProductCandidate product)
    {
        var candidate = new ErpCandidateRow(product, product.BaseUnit ?? string.Empty);
        Candidates.Add(candidate); SelectedCandidate = candidate;
    }
}
