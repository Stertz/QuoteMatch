using System.Collections.ObjectModel;

using Avalonia;
using Avalonia.Styling;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using QuoteMatch.Desktop.Demo;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.Preferences;
using QuoteMatch.Domain.Analysis;

namespace QuoteMatch.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly PreferenceStore _preferenceStore;
    private readonly List<DemoQuotationItem> _items;
    private readonly List<QuotationItemRowViewModel> _allRows = [];
    private bool _isInitializing = true;
    private string? _selectedSupplier;

    [ObservableProperty] private int _selectedLanguageIndex;
    [ObservableProperty] private int _selectedAppearanceIndex;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _selectedFilterIndex;
    [ObservableProperty] private int _selectedSortIndex;
    [ObservableProperty] private QuotationItemRowViewModel? _selectedItem;
    [ObservableProperty] private decimal? _requestedQuantity;
    [ObservableProperty] private bool _isDetailsOpen;
    [ObservableProperty] private bool _hasPreferenceError;

    public MainWindowViewModel(PreferenceStore preferenceStore, IReadOnlyList<DemoQuotationItem>? items = null)
    {
        _preferenceStore = preferenceStore;
        _items = (items ?? DemoQuotation.Create()).ToList();
        Summary = QuotationAnalysis.Calculate(_items.Select(item => item.Line).ToArray());
        UserPreferences preferences = preferenceStore.Load();
        Strings.SetCulture(preferences.CultureName);
        SelectedLanguageIndex = preferences.CultureName == "en-US" ? 1 : 0;
        SelectedAppearanceIndex = (int)preferences.Appearance;
        HasPreferenceError = preferenceStore.HasReadError;
        _isInitializing = false;
        ApplyAppearance();
        RefreshLabels();
    }

    public Localizer Strings { get; } = new();
    public QuotationSummary Summary { get; private set; }
    public ObservableCollection<QuotationItemRowViewModel> VisibleItems { get; } = [];
    public ObservableCollection<OfferRowViewModel> Offers { get; } = [];
    public ObservableCollection<SupplierTotalViewModel> SupplierTotals { get; } = [];
    public bool IsItemDetailsOpen => SelectedItem is not null;
    public bool HasNoResults => VisibleItems.Count == 0;
    public bool HasPendingItems => Summary.PendingCount > 0;
    public string GrandTotal => Strings.Money(Summary.SelectedTotal);
    public string TotalLabel => Strings[Summary.IsComplete ? "OrderTotal" : "SelectionSubtotal"];
    public string QuotationOverview => Strings.Format("QuotationOverviewFormat", Summary.Items.Count, Summary.Suppliers.Count);
    public string ItemCountText => Strings.Format("VisibleItemCountFormat", VisibleItems.Count, Summary.Items.Count);
    public string PendingCountText => Strings.Format("PendingCountFormat", Summary.PendingCount);
    public string ItemPosition => SelectedItem is null ? string.Empty :
        Strings.Format("ItemPositionFormat", VisibleItems.IndexOf(SelectedItem) + 1, VisibleItems.Count);
    public string SelectedItemCost => SelectedItem?.Total ?? string.Empty;
    public string SelectedItemWinner => SelectedItem?.WinnerName ?? string.Empty;
    public bool IsQuantityValid => RequestedQuantity is > 0m;
    public bool HasQuantityError => SelectedItem is not null && !IsQuantityValid;
    public bool CanApplyQuantity => SelectedItem is not null && IsQuantityValid && RequestedQuantity != SelectedItem.Item.Line.RequestedQuantity;
    public bool CanGoToPrevious => SelectedItem is not null && VisibleItems.IndexOf(SelectedItem) > 0;
    public bool CanGoToNext => SelectedItem is not null && VisibleItems.IndexOf(SelectedItem) < VisibleItems.Count - 1;
    public string DetailsTitle => _selectedSupplier is null ? Strings["CalculationDetails"] : Strings.Format("SupplierName", _selectedSupplier);

    public string DetailsText
    {
        get
        {
            if (SelectedItem is not { } selected)
            {
                return string.Empty;
            }

            ComparisonResult comparison = selected.Analysis.Comparison;
            if (_selectedSupplier is not null)
            {
                OfferEvaluation offer = comparison.Offers.Single(item => item.Offer.SupplierCode == _selectedSupplier);
                return Strings.Format("ItemOfferCalculationFormat",
                    Strings.Quantity(offer.Offer.Quantity, selected.Unit), Strings.Money(offer.Offer.UnitPrice, 4),
                    Strings.Money(offer.Total), selected.Quantity,
                    Strings.Quantity(offer.Excess, selected.Unit), Strings.Quantity(offer.Shortfall, selected.Unit),
                    Strings[offer.CanFulfill ? "CanFulfill" : "CannotFulfill"], selected.Unit);
            }

            return Strings.Format("ItemCalculationOverviewFormat", selected.Quantity,
                Strings.Money(comparison.LowestUnitPrice, 4), selected.Unit, selected.Total, selected.WinnerName);
        }
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (_isInitializing)
        {
            return;
        }

        Strings.SetCulture(value == 1 ? "en-US" : "pt-BR");
        RefreshLabels();
        SavePreferences();
    }

    partial void OnSelectedAppearanceIndexChanged(int value)
    {
        if (!_isInitializing)
        {
            ApplyAppearance();
            SavePreferences();
        }
    }

    partial void OnSearchTextChanged(string value) => RefreshVisibleItems();
    partial void OnSelectedFilterIndexChanged(int value) => RefreshVisibleItems();
    partial void OnSelectedSortIndexChanged(int value) => RefreshVisibleItems();

    partial void OnSelectedItemChanged(QuotationItemRowViewModel? value)
    {
        RequestedQuantity = value?.Item.Line.RequestedQuantity;
        _selectedSupplier = null;
        IsDetailsOpen = false;
        RefreshOffers();
        NotifySelection();
    }

    partial void OnRequestedQuantityChanged(decimal? value)
    {
        OnPropertyChanged(nameof(IsQuantityValid));
        OnPropertyChanged(nameof(HasQuantityError));
        OnPropertyChanged(nameof(CanApplyQuantity));
        ApplyRequestedQuantityCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanApplyQuantity))]
    private void ApplyRequestedQuantity()
    {
        if (SelectedItem is not { } selected || RequestedQuantity is not > 0m)
        {
            return;
        }

        int index = _items.FindIndex(item => item.Line.ItemCode == selected.ItemCode);
        _items[index] = selected.Item with { Line = selected.Item.Line with { RequestedQuantity = RequestedQuantity.Value } };
        Summary = QuotationAnalysis.Calculate(_items.Select(item => item.Line).ToArray());
        RefreshLabels();
    }

    [RelayCommand]
    private void CloseItemDetails() => SelectedItem = null;

    [RelayCommand(CanExecute = nameof(CanGoToPrevious))]
    private void PreviousItem() => SelectedItem = VisibleItems[VisibleItems.IndexOf(SelectedItem!) - 1];

    [RelayCommand(CanExecute = nameof(CanGoToNext))]
    private void NextItem() => SelectedItem = VisibleItems[VisibleItems.IndexOf(SelectedItem!) + 1];

    [RelayCommand]
    private void ShowCalculationDetails()
    {
        _selectedSupplier = null;
        NotifyDetails();
        IsDetailsOpen = true;
    }

    [RelayCommand]
    private void CloseDetails() => IsDetailsOpen = false;

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedFilterIndex = 0;
        SelectedSortIndex = 0;
    }

    private void RefreshLabels()
    {
        string? selectedCode = SelectedItem?.ItemCode;
        _allRows.Clear();
        foreach (DemoQuotationItem item in _items)
        {
            QuotationLineResult analysis = Summary.Items.Single(line => line.ItemCode == item.Line.ItemCode);
            _allRows.Add(new QuotationItemRowViewModel(item, analysis, Strings));
        }

        SupplierTotals.Clear();
        foreach (SupplierAllocation supplier in Summary.Suppliers)
        {
            SupplierTotals.Add(new SupplierTotalViewModel(supplier, Strings));
        }

        RefreshVisibleItems(selectedCode);
        OnPropertyChanged(nameof(GrandTotal));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasPendingItems));
        OnPropertyChanged(nameof(TotalLabel));
        OnPropertyChanged(nameof(QuotationOverview));
        OnPropertyChanged(nameof(PendingCountText));
    }

    private void RefreshVisibleItems() => RefreshVisibleItems(SelectedItem?.ItemCode);

    private void RefreshVisibleItems(string? selectedCode)
    {
        string search = SearchText?.Trim() ?? string.Empty;
        IEnumerable<QuotationItemRowViewModel> rows = _allRows.Where(row => search.Length == 0 ||
            Strings.Culture.CompareInfo.IndexOf($"{row.ItemCode} {row.Name} {row.Specification}", search,
                System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0);

        rows = SelectedFilterIndex switch
        {
            1 => rows.Where(row => row.IsPending),
            2 => rows.Where(row => row.WinnerCode == "A"),
            3 => rows.Where(row => row.WinnerCode == "B"),
            4 => rows.Where(row => row.WinnerCode == "C"),
            _ => rows
        };
        rows = SelectedSortIndex switch
        {
            1 => rows.OrderByDescending(row => row.TotalValue).ThenBy(row => row.ItemCode, StringComparer.Ordinal),
            2 => rows.OrderBy(row => row.WinnerCode is null).ThenBy(row => row.WinnerCode, StringComparer.Ordinal)
                .ThenBy(row => row.ItemCode, StringComparer.Ordinal),
            _ => rows
        };

        QuotationItemRowViewModel[] visible = rows.ToArray();
        SelectedItem = null;
        VisibleItems.Clear();
        foreach (QuotationItemRowViewModel row in visible)
        {
            VisibleItems.Add(row);
        }

        SelectedItem = visible.FirstOrDefault(row => row.ItemCode == selectedCode);
        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(HasNoResults));
        NotifySelection();
    }

    private void RefreshOffers()
    {
        Offers.Clear();
        if (SelectedItem is not { } selected)
        {
            return;
        }

        foreach (OfferEvaluation evaluation in selected.Analysis.Comparison.Offers)
        {
            Offers.Add(new OfferRowViewModel(evaluation, Strings, () =>
            {
                _selectedSupplier = evaluation.Offer.SupplierCode;
                NotifyDetails();
                IsDetailsOpen = true;
            }, selected.Unit, selected.Analysis.HasTie));
        }
    }

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(IsItemDetailsOpen));
        OnPropertyChanged(nameof(ItemPosition));
        OnPropertyChanged(nameof(SelectedItemCost));
        OnPropertyChanged(nameof(SelectedItemWinner));
        OnPropertyChanged(nameof(CanGoToPrevious));
        OnPropertyChanged(nameof(CanGoToNext));
        OnPropertyChanged(nameof(HasQuantityError));
        OnPropertyChanged(nameof(CanApplyQuantity));
        ApplyRequestedQuantityCommand.NotifyCanExecuteChanged();
        PreviousItemCommand.NotifyCanExecuteChanged();
        NextItemCommand.NotifyCanExecuteChanged();
        NotifyDetails();
    }

    private void NotifyDetails()
    {
        OnPropertyChanged(nameof(DetailsTitle));
        OnPropertyChanged(nameof(DetailsText));
    }

    private void ApplyAppearance()
    {
        if (Avalonia.Application.Current is { } application)
        {
            application.RequestedThemeVariant = (AppearanceMode)SelectedAppearanceIndex switch
            {
                AppearanceMode.Light => ThemeVariant.Light,
                AppearanceMode.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
    }

    private void SavePreferences() => HasPreferenceError = !_preferenceStore.TrySave(
        _preferenceStore.Load() with { CultureName = Strings.Culture.Name, Appearance = (AppearanceMode)SelectedAppearanceIndex });
}

