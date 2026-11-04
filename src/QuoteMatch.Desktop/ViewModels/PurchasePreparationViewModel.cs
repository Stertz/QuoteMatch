using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Desktop.ViewModels;

public sealed record PurchaseGroupRow(AnalyzedGroup Group, string Choice, string Demand)
{
    public string Name => Group.Offers.Select(offer => offer.Link?.Product?.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? Group.Name;
}
public sealed record PurchaseOfferRow(AnalyzedOffer Offer, string Quantity, string Normalized, string Total, string Detail, bool IsEligible,
    bool IsChosen, string ChoiceLabel)
{
    public string Supplier => Offer.SupplierName;
    public string Description => Offer.Item.Description;
    public string Code => Offer.Item.ProductCode;
}
public sealed record PurchaseSavedLineRow(string Name, string Code, string Quantity, string Total)
{
    public string Price { get; init; } = string.Empty;
}
public sealed record PurchaseSupplierRow(string Name, string Detail, string Subtotal, IReadOnlyList<PurchaseSavedLineRow> Lines);
public sealed record PurchasePackageRow(Guid ProposalId, string Name, string Total, string Comparison, string PriceCounts,
    string Availability, bool CanSelect, string UnavailableReason = "");

public partial class PurchasePreparationViewModel(Localizer strings, IPurchasePreparationRepository repository, PurchaseSynchronizationViewModel? synchronization = null,
    QuotationAnalysisViewModel? analysis = null) : ObservableObject
{
    public QuotationAnalysisViewModel? Analysis => analysis;
    public async Task<bool> RefreshAfterReviewAsync()
    {
        if (_input is null) return false;
        var choices = CurrentChoices();
        await LoadAsync(_input.Analysis.Quotation.Id);
        if (!IsOutdated) return true;
        await RunAsync(async token => await SaveAsync(choices, SelectedGroup?.Group.Key, token));
        return !IsOutdated;
    }
    public bool CanSynchronize => synchronization is not null && _input?.Preparation?.Lines.Count > 0 && !IsOutdated && !IsBusy;
    public async Task<PurchaseSynchronizationViewModel?> CreateSynchronizationAsync()
    {
        if (synchronization is null || _input is null) return null;
        await synchronization.LoadAsync(_input.Analysis.Quotation.Id); return synchronization;
    }
    private PurchaseInput? _input;
    private IReadOnlyList<AnalyzedGroup> _groups = [];
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private PurchaseGroupRow? _selectedGroup;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChoose)), NotifyPropertyChangedFor(nameof(ChooseUnavailableReason))] private PurchaseOfferRow? _selectedOffer;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isShowingSummary;
    public Localizer Strings => strings;
    public ObservableCollection<PurchaseGroupRow> Groups { get; } = [];
    public ObservableCollection<PurchaseOfferRow> Offers { get; } = [];
    public ObservableCollection<PurchaseSupplierRow> Suppliers { get; } = [];
    public ObservableCollection<PurchasePackageRow> Packages { get; } = [];
    public bool HasSavedChoices => Suppliers.Count > 0;
    public bool HasPackages => Packages.Count > 0;
    public bool CanEdit => _input?.Analysis.Quotation.State == QuotationState.Draft && !IsBusy;
    public bool HasSelection => SelectedGroup is not null;
    public bool CanChoose => CanEdit && SelectedOffer?.IsEligible == true;
    public string ChooseUnavailableReason => IsBusy ? strings["PurchaseBlockedBusy"]
        : _input?.Analysis.Quotation.State == QuotationState.Finalized ? strings["PurchaseBlockedFinalized"]
        : SelectedGroup is null || SelectedOffer is null ? strings["PurchaseBlockedNoSelection"]
        : OfferBlockReason(SelectedGroup.Group, SelectedOffer.Offer);
    public bool CanRemove => CanEdit && !IsOutdated && _input?.Preparation?.Lines.Any(line => line.GroupKey == SelectedGroup?.Group.Key) == true;
    public bool CanSuggest => CanEdit && _groups.Any(group => group.Winner is not null);
    public bool IsOutdated => _input?.Preparation is { } saved && (saved.InputsRevision != _input.Analysis.Quotation.Revision || saved.AnalysisVersion != _input.Analysis.State.Version || saved.QuotationInputsVersion != _input.Analysis.Inputs.Version);
    public string Summary => strings.Format("PurchaseSummary", _input?.Preparation?.Lines.Count ?? 0,
        _input?.Preparation?.PendingGroupCount ?? _groups.Count);
    public string ItemSubtotal => strings.Money(_input?.Preparation?.ItemSubtotal ?? 0m);
    public string SavedReference => _input?.Preparation is { } saved
        ? strings.Format("PurchaseSavedReference", saved.Version, saved.AnalysisVersion, saved.CreatedAtUtc.ToLocalTime().ToString("g", strings.Culture)) : strings["PurchaseNotSaved"];
    public string PreparationHelp => _input?.Preparation is null ? strings["PurchaseRulesHelp"] : strings["PurchaseRulesHelp"] + Environment.NewLine + SavedReference;
    public string ChooseLabel => strings[IsOutdated ? "PurchaseStartNewWithOffer" : "PurchaseChooseOffer"];
    public Task LoadAsync(Guid quotationId) => RunAsync(async token =>
    {
        _input = await repository.GetPurchaseInputAsync(quotationId, token); Rebuild();
        if (_input.Analysis.Quotation.State != QuotationState.Draft) return;
        if (_input.Preparation is { } previous && IsOutdated)
        {
            var choices = PurchasePreparationRules.RetainEligibleSelections(_groups, previous);
            await SaveAsync(choices, SelectedGroup?.Group.Key, token);
            if (choices.Count < previous.Lines.Count) Status = strings.Format("PurchaseChoicesNeedReview", previous.Lines.Count - choices.Count);
        }
        else if (_input.Preparation is null)
        {
            var choices = PurchasePreparationRules.SelectSingleSupplierOffers(_groups);
            if (choices.Count > 0) await SaveAsync(choices, SelectedGroup?.Group.Key, token);
        }
    });
    public void Clear() { _input = null; _groups = []; Groups.Clear(); Offers.Clear(); Suppliers.Clear(); Packages.Clear(); SelectedGroup = null; NotifyState(); }
    public void RefreshLabels() { if (_input is not null) Rebuild(); }
    partial void OnSearchTextChanged(string value) => Filter();
    partial void OnIsBusyChanged(bool value) => NotifyState();
    partial void OnSelectedGroupChanged(PurchaseGroupRow? value)
    {
        IsShowingSummary = false; Offers.Clear();
        PurchaseLine? choice = IsOutdated ? null : _input?.Preparation?.Lines.FirstOrDefault(line => line.GroupKey == value?.Group.Key);
        bool IsChosen(AnalyzedOffer offer) => offer.ProposalId == choice?.Offer.ProposalId && offer.Item.CaptureId == choice?.Offer.Item.CaptureId;
        foreach (AnalyzedOffer offer in (value?.Group.Offers ?? []).OrderByDescending(IsChosen))
        {
            AnalyzedGroup group = value!.Group;
            bool eligible = PurchasePreparationRules.IsEligible(group, offer);
            string detail = !eligible ? OfferBlockReason(group, offer) : group.RequestedQuantity is null ? strings["PurchaseDemandOptional"]
                : offer.BaseQuantity > group.RequestedQuantity ? strings.Format("OfferExcess", strings.Quantity(offer.BaseQuantity!.Value - group.RequestedQuantity.Value, group.BaseUnit)) : strings["DemandCovered"];
            string normalized = offer.BaseQuantity is decimal quantity && offer.BaseUnitPrice is decimal price
                ? strings.Format("NormalizedOffer", strings.Quantity(quantity, group.BaseUnit), strings.Money(price, 4), group.BaseUnit) : strings["NotComparable"];
            Offers.Add(new(offer, strings.Quantity(offer.Item.Quantity!.Value, offer.Item.Unit), normalized,
                strings.Money(offer.Item.ReportedTotal!.Value), detail, eligible, IsChosen(offer), strings["PurchaseChosenOffer"]));
        }
        SelectedOffer = Offers.FirstOrDefault(row => row.Offer.ProposalId == choice?.Offer.ProposalId && row.Offer.Item.CaptureId == choice?.Offer.Item.CaptureId) ?? Offers.FirstOrDefault();
        NotifyState();
    }
    private void Rebuild(string? selectKey = null)
    {
        if (_input is null) return;
        bool showSummary = IsShowingSummary;
        _groups = ImportedQuotationAnalysis.Calculate(_input.Analysis.Proposals, _input.Analysis.State);
        Filter();
        SelectedGroup = Groups.FirstOrDefault(row => row.Group.Key == selectKey) ?? Groups.FirstOrDefault();
        Suppliers.Clear();
        foreach (var supplier in (_input.Preparation?.Lines ?? []).GroupBy(line => line.Offer.ProposalId))
            Suppliers.Add(new(supplier.First().Offer.SupplierName, strings.Format("PurchaseSupplierDetail", supplier.Count(), supplier.First().SourceFileName),
                strings.Money(supplier.Sum(line => line.Offer.Item.ReportedTotal!.Value)), supplier.Select(line => new PurchaseSavedLineRow(line.GroupName,
                    line.Offer.Item.ProductCode, strings.Quantity(line.Offer.BaseQuantity!.Value, line.BaseUnit), strings.Money(line.Offer.Item.ReportedTotal!.Value))).ToArray()));
        Packages.Clear();
        foreach (var total in SupplierQuotationComparison.Calculate(_input.Analysis.Proposals, _groups))
        {
            int covered = _groups.Count(group => group.Offers.Count(offer => offer.ProposalId == total.ProposalId && PurchasePreparationRules.IsEligible(group, offer)) == 1);
            bool complete = _groups.Count > 0 && covered == _groups.Count;
            string comparison = total.PremiumPercent is null ? strings["PackageComparison" + total.PendingReason] : total.PremiumPercent == 0 ? strings["PackageReference"]
                : strings.Format("PackagePremium", "+" + total.PremiumPercent.Value.ToString("N2", strings.Culture) + "%");
            if (total.CommonTotal is decimal comparable && total.PendingGroups > 0)
                comparison = strings.Format("PackagePartialComparison", total.CommonGroups, strings.Money(comparable), comparison);
            Packages.Add(new(total.ProposalId, total.Supplier, total.QuotedTotal is decimal price ? strings.Money(price) : strings["NotAvailable"],
                comparison,
                strings.Format("SupplierPriceCounts", total.IncludedItems, total.LowestPriceItems, total.TiedLowestPriceItems),
                complete ? _groups.Any(group => group.RequestedQuantity is null) ? strings["PurchaseDemandOptional"] : strings.Format("PackageCoversDemand", covered)
                    : strings.Format("PackageIncomplete", covered, _groups.Count),
                complete && _input.Analysis.Quotation.State == QuotationState.Draft,
                _input.Analysis.Quotation.State == QuotationState.Finalized ? strings["PurchaseBlockedFinalized"]
                    : complete ? "" : PackageBlockReason(total.ProposalId)));
        }
        IsShowingSummary = showSummary;
        NotifyState();
    }
    private void Filter()
    {
        string? old = SelectedGroup?.Group.Key; Groups.Clear();
        foreach (AnalyzedGroup group in _groups.Where(group => strings.Culture.CompareInfo.IndexOf(group.Name + " " + string.Join(" ", group.Offers.Select(offer => offer.Item.ProductCode)),
            SearchText, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0))
        {
            PurchaseLine? selected = IsOutdated ? null : _input?.Preparation?.Lines.FirstOrDefault(line => line.GroupKey == group.Key);
            Groups.Add(new(group, selected?.Offer.SupplierName ?? strings["PurchaseUnselected"],
                group.RequestedQuantity is decimal demand ? strings.Quantity(demand, group.BaseUnit) : strings["PurchaseNeedsDemand"]));
        }
        SelectedGroup = Groups.FirstOrDefault(row => row.Group.Key == old) ?? Groups.FirstOrDefault();
    }
    private void NotifyState()
    {
        foreach (string name in new[] { nameof(CanEdit), nameof(CanChoose), nameof(ChooseUnavailableReason), nameof(CanRemove), nameof(CanSuggest), nameof(HasSelection), nameof(IsOutdated), nameof(Summary), nameof(ItemSubtotal), nameof(SavedReference), nameof(PreparationHelp), nameof(ChooseLabel), nameof(HasSavedChoices), nameof(HasPackages), nameof(CanSynchronize) }) OnPropertyChanged(name);
    }
    private string OfferBlockReason(AnalyzedGroup group, AnalyzedOffer offer)
    {
        if (PurchasePreparationRules.IsEligible(group, offer)) return "";
        string key = !group.IsLinked || offer.Link is null ? "IdentityRequired" : group.HasUnitConflict ? "UnitConflict" : group.HasVariantConflict ? "VariantConflict"
            : offer.Link.Product?.IsInactive == true ? "InactiveProduct"
            : offer.BaseQuantity < group.RequestedQuantity ? "InsufficientQuantity" : "InvalidOffer";
        return strings["PurchaseBlocked" + key];
    }
    private string PackageBlockReason(Guid proposalId)
    {
        foreach (var group in _groups)
        {
            var offers = group.Offers.Where(offer => offer.ProposalId == proposalId).ToArray();
            if (offers.Length == 0) return strings["PurchaseBlockedIncompletePackage"];
            var eligible = offers.Where(offer => PurchasePreparationRules.IsEligible(group, offer)).ToArray();
            if (eligible.Length > 1) return strings["PurchaseBlockedAmbiguousPackage"];
            if (eligible.Length == 0) return OfferBlockReason(group, offers[0]);
        }
        return strings["PurchaseBlockedIncompletePackage"];
    }
    [RelayCommand]
    private Task ChooseSupplierPackageAsync(PurchasePackageRow package) => RunAsync(async token =>
    {
        if (_input is null || !package.CanSelect) return;
        var choices = PurchasePreparationRules.SelectSupplierPackage(_groups, package.ProposalId);
        await SaveAsync(choices, SelectedGroup?.Group.Key, token);
        IsShowingSummary = true;
    });
    private IReadOnlyList<PurchaseSelection> CurrentChoices() => IsOutdated ? [] : (_input?.Preparation?.Lines ?? []).Select(line => new PurchaseSelection(line.Offer.ProposalId, line.Offer.Item.CaptureId)).ToArray();
    [RelayCommand]
    private Task ChooseOfferAsync() => RunAsync(async token =>
    {
        if (_input is null || SelectedGroup is null || SelectedOffer?.IsEligible != true) return;
        var offer = SelectedOffer.Offer;
        var choices = CurrentChoices().Where(choice => !SelectedGroup.Group.Offers.Any(row => row.ProposalId == choice.ProposalId && row.Item.CaptureId == choice.CaptureId))
            .Append(new PurchaseSelection(offer.ProposalId, offer.Item.CaptureId)).ToArray();
        await SaveAsync(choices, SelectedGroup.Group.Key, token);
    });
    [RelayCommand]
    private Task RemoveChoiceAsync() => RunAsync(async token =>
    {
        if (SelectedGroup is null || IsOutdated) return;
        var choices = CurrentChoices().Where(choice => !SelectedGroup.Group.Offers.Any(row => row.ProposalId == choice.ProposalId && row.Item.CaptureId == choice.CaptureId)).ToArray();
        await SaveAsync(choices, SelectedGroup.Group.Key, token);
    });
    [RelayCommand]
    private Task UseSuggestedAsync() => RunAsync(async token =>
    {
        var choices = _groups.Where(group => group.Winner is not null).Select(group => new PurchaseSelection(group.Winner!.ProposalId, group.Winner.Item.CaptureId)).ToArray();
        await SaveAsync(choices, SelectedGroup?.Group.Key, token);
    });
    private async Task SaveAsync(IReadOnlyList<PurchaseSelection> choices, string? key, CancellationToken token)
    {
        if (_input is null) return;
        var input = _input.Analysis;
        var saved = await repository.PreparePurchaseAsync(new(input.Quotation.Id, input.Quotation.Revision, input.State.Version, _input.Preparation?.Version ?? 0, choices), token);
        _input = _input with { Preparation = saved }; Rebuild(key); Status = strings["PurchaseSaved"];
    }
    [RelayCommand] private void ShowSummary() => IsShowingSummary = true;
    [RelayCommand] private void ShowOffers() => IsShowingSummary = false;
    [RelayCommand] private Task RefreshAsync() => _input is null ? Task.CompletedTask : LoadAsync(_input.Analysis.Quotation.Id);
    private CancellationTokenSource? _operation;
    [RelayCommand] private void Cancel() => _operation?.Cancel();
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return; using CancellationTokenSource operation = new(); _operation = operation; IsBusy = true; Status = strings["ImportWorking"];
        try { await action(operation.Token); if (Status == strings["ImportWorking"]) Status = string.Empty; }
        catch (OperationCanceledException) { Status = strings["ImportCanceled"]; }
        catch (PurchaseException exception) { Status = strings["PurchaseError" + exception.Code]; }
        catch (AnalysisException exception) { Status = strings["AnalysisError" + exception.Code]; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or Microsoft.EntityFrameworkCore.DbUpdateException) { Status = strings["ImportStorageError"]; }
        finally { _operation = null; IsBusy = false; }
    }
}
