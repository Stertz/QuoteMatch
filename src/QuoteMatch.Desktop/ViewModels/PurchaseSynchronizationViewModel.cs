using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuoteMatch.Application.Integration;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.Preferences;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Desktop.ViewModels;

public sealed record BrandPurchaseRow(BrandPurchaseProposal Proposal, string Label);
public sealed record SynchronizationSourceRow(PurchaseLine Line, string Label);
public sealed record SynchronizationTargetRow(PurchaseOrderLine Line, string Label);
public sealed record SynchronizationPairRow(SynchronizationPair Pair, string Label);
public sealed record CreatedPurchaseOrderRow(string Number, string Code, string Branch, string Label);
public partial class SupplierDocumentChoiceRow(SupplierDocumentGroup group, bool hasDifferentPrices, Localizer strings) : ObservableObject
{
    public SupplierDocumentGroup Group { get; } = group;
    public Localizer Strings => strings;
    public string Question => strings.Format("SyncCombineDocumentsQuestion", Group.SupplierName, Group.Documents.Count);
    public string Documents => string.Join(Environment.NewLine, Group.Documents.Select(document => document.FileName));
    [ObservableProperty] private int _quantityChoiceIndex = -1;
    [ObservableProperty] private int _priceChoiceIndex = -1;
    public bool NeedsPriceChoice => QuantityChoiceIndex == 1 && hasDifferentPrices;
    public bool IsAnswered => QuantityChoiceIndex is 0 or 1 && (!NeedsPriceChoice || PriceChoiceIndex is >= 0 and <= 2);
    public SupplierDocumentChoice Build() => new(Group.SupplierKey, QuantityChoiceIndex == 1 ? DocumentQuantityChoice.Combine
        : QuantityChoiceIndex == 0 ? DocumentQuantityChoice.KeepSelected : DocumentQuantityChoice.Pending,
        NeedsPriceChoice && PriceChoiceIndex >= 0 ? (DocumentPriceChoice)(PriceChoiceIndex + 1) : DocumentPriceChoice.NotRequired);
    partial void OnQuantityChoiceIndexChanged(int value) { PriceChoiceIndex = -1; OnPropertyChanged(nameof(NeedsPriceChoice)); OnPropertyChanged(nameof(IsAnswered)); }
    partial void OnPriceChoiceIndexChanged(int value) => OnPropertyChanged(nameof(IsAnswered));
}
public partial class SynchronizationDifferenceRow(SynchronizationDifference difference, string label) : ObservableObject
{
    public SynchronizationDifference Difference { get; } = difference;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _canSelect = true;
}
public partial class SynchronizationFinancialPolicyRow(string key, Localizer strings) : ObservableObject
{
    public string Key { get; } = key;
    public string Label => strings["FinancialPolicy" + Key];
    public Localizer Strings => strings;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SourceHelp))] private int _sourceIndex;
    public string SourceHelp => strings["FinancialSource" + ((SynchronizationFinancialSource)SourceIndex) + "Help"];
    [ObservableProperty] private bool _canKeepExisting;
}
public partial class PurchaseSynchronizationViewModel(Localizer strings, IPurchasePreparationRepository repository, IPurchaseOrderReader orders,
    IErpBuyingSupplierLookup suppliers, IPurchaseSynchronizationWriter? writer = null, ISynchronizationOperationRepository? operations = null,
    PreferenceStore? preferences = null) : ObservableObject
{
    private readonly PurchaseSynchronizationService _service = new(repository, orders, suppliers);
    private readonly SynchronizationWriteService? _writes = writer is not null && operations is not null
        ? new(operations, new(repository, orders, suppliers), writer) : null;
    private SynchronizationPreview? _preview;
    private SynchronizationWritePlan? _writePlan;
    private SynchronizationOperation? _pendingOperation;
    private AnalysisInput? _documentInput;
    private readonly Dictionary<OrderWriteKind, string> _configurationErrors = [];
    public SynchronizationSelection? Selection { get; private set; }
    [ObservableProperty] private BrandPurchaseRow? _selectedBrand;
    [ObservableProperty] private OrderSearchRow? _selectedTarget;
    [ObservableProperty] private string _branch = string.Empty;
    [ObservableProperty] private string _number = string.Empty;
    [ObservableProperty] private int _kindIndex;
    [ObservableProperty] private bool _removalConfirmed;
    [ObservableProperty] private DateTimeOffset? _newOrderDate = DateTimeOffset.Now;
    [ObservableProperty] private bool _updatePrices = true;
    [ObservableProperty] private int _priceSourceIndex;
    private int _lastUpdatingPriceSource;
    [ObservableProperty] private string _financialSummary = string.Empty;
    [ObservableProperty] private string _financialDetails = string.Empty;
    private IReadOnlyList<SynchronizationFinancialPolicyRow>? _financialPolicies;
    public IReadOnlyList<SynchronizationFinancialPolicyRow> FinancialPolicies => _financialPolicies ??= CreateFinancialPolicies();
    private IReadOnlyList<SynchronizationFinancialPolicyRow> CreateFinancialPolicies()
    {
        var rows = new[] { "Freight", "Discount", "OtherCharges", "Taxes" }.Select(key => new SynchronizationFinancialPolicyRow(key, strings) { CanKeepExisting = IsUpdate }).ToArray();
        foreach (var row in rows) row.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(SynchronizationFinancialPolicyRow.SourceIndex)) FinancialPolicyChanged(); };
        return rows;
    }
    public SynchronizationFinancialPolicy FinancialPolicy => new()
    {
        Prices = IsUpdate && !UpdatePrices ? SynchronizationPriceSource.KeepExisting : (SynchronizationPriceSource)PriceSourceIndex,
        Freight = (SynchronizationFinancialSource)FinancialPolicies[0].SourceIndex,
        Discount = (SynchronizationFinancialSource)FinancialPolicies[1].SourceIndex,
        OtherCharges = (SynchronizationFinancialSource)FinancialPolicies[2].SourceIndex,
        Taxes = (SynchronizationFinancialSource)FinancialPolicies[3].SourceIndex
    };
    public bool CanChoosePriceSource => !IsUpdate || UpdatePrices;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _previewSummary = string.Empty;
    [ObservableProperty] private string _resultSummary = string.Empty;
    [ObservableProperty] private SynchronizationSourceRow? _selectedPairSource;
    [ObservableProperty] private SynchronizationTargetRow? _selectedPairTarget;
    [ObservableProperty] private SynchronizationPairRow? _selectedPair;
    public Localizer Strings => strings;
    public bool IsUpdate => KindIndex == 1;
    public string PreviewActionLabel => strings[IsUpdate ? "SyncPreview" : "SyncCreatePreview"];
    public string WriteActionLabel => strings[IsUpdate ? "SyncUpdateOrder" : "SyncCreateOrder"];
    public string WriteConfigurationStatus => writer is null ? strings["OrderErrorWritesNotConfigured"]
        : _configurationErrors.GetValueOrDefault(IsUpdate ? OrderWriteKind.Update : OrderWriteKind.Create, string.Empty);
    public bool CanConfirm => _writePlan is not null && !IsBusy && _pendingOperation is null;
    public bool CanReconcile => _writes is not null && _pendingOperation is not null && !IsBusy;
    public bool HasPendingOperation => _pendingOperation is not null;
    public string WriteDetails => _writePlan is null ? strings["SyncSafeValidationRequired"] : strings.Format("SyncWriteDetails", _writePlan.Profile.OperatorCode?.ToString(strings.Culture) ?? "—", _writePlan.TargetId ?? strings["SyncNewOrderNumber"]);
    public bool HasPreview => _preview is not null;
    public bool HasRemoval => Differences.Any(row => row.Difference.Kind == SynchronizationChangeKind.Remove);
    public bool RequiresRemovalConfirmation => Differences.Any(row => row.IsSelected && row.Difference.Kind == SynchronizationChangeKind.Remove);
    public bool HasSelection => Selection is not null;
    public bool ShowDocumentChoices => !IsUpdate && DocumentChoices.Count > 0;
    public bool DocumentChoicesAnswered => IsUpdate || DocumentChoices.All(row => row.IsAnswered);
    public ObservableCollection<SupplierDocumentChoiceRow> DocumentChoices { get; } = [];
    public bool CanPreview => !IsBusy && SelectedBrand?.Proposal.Brand is not null && !string.IsNullOrWhiteSpace(Branch)
        && (IsUpdate ? SelectedTarget is not null : NewOrderDate is not null) && DocumentChoicesAnswered;
    public string PreviewBlockedReason => IsBusy ? strings["ImportWorking"] : SelectedBrand is null ? strings["SyncPrepareFirst"]
        : SelectedBrand.Proposal.Brand is null ? strings["SyncErrorMissingBrand"] : string.IsNullOrWhiteSpace(Branch) ? strings["SyncBranchRequired"]
        : IsUpdate && SelectedTarget is null ? strings["SyncTargetRequired"]
        : !IsUpdate && NewOrderDate is null ? strings["SyncOrderDateRequired"] : !DocumentChoicesAnswered ? strings["SyncDocumentChoicesRequired"] : string.Empty;
    public ObservableCollection<BrandPurchaseRow> Brands { get; } = [];
    public ObservableCollection<OrderSearchRow> Targets { get; } = [];
    public ObservableCollection<SynchronizationDifferenceRow> Differences { get; } = [];
    public ObservableCollection<PurchaseSavedLineRow> ResultLines { get; } = [];
    public ObservableCollection<SynchronizationSourceRow> PairSources { get; } = [];
    public ObservableCollection<SynchronizationTargetRow> PairTargets { get; } = [];
    public ObservableCollection<SynchronizationPairRow> Pairs { get; } = [];
    public ObservableCollection<CreatedPurchaseOrderRow> CreatedOrders { get; } = [];
    public bool HasCreatedOrders => CreatedOrders.Count > 0;
    partial void OnSelectedBrandChanged(BrandPurchaseRow? value)
    {
        ClearPairings(); PairSources.Clear();
        foreach (var line in value?.Proposal.Lines ?? []) PairSources.Add(new(line, line.Offer.Link?.Product?.ProductCode + " · " + line.GroupName));
        SelectedPairSource = PairSources.FirstOrDefault(); RefreshDocumentChoices(); Invalidate();
    }
    private void RefreshDocumentChoices()
    {
        DocumentChoices.Clear();
        if (!IsUpdate && _documentInput is { } input && SelectedBrand is { } brand)
        {
            var priceConflicts = CreationDocumentRules.PriceConflictSuppliers(input, brand.Proposal);
            foreach (var group in CreationDocumentRules.Groups(input, brand.Proposal))
            {
                var row = new SupplierDocumentChoiceRow(group, priceConflicts.Contains(group.SupplierKey), strings);
                row.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName is nameof(SupplierDocumentChoiceRow.QuantityChoiceIndex) or nameof(SupplierDocumentChoiceRow.PriceChoiceIndex))
                    {
                        Invalidate(); OnPropertyChanged(nameof(DocumentChoicesAnswered));
                    }
                };
                DocumentChoices.Add(row);
            }
        }
        OnPropertyChanged(nameof(ShowDocumentChoices)); OnPropertyChanged(nameof(DocumentChoicesAnswered));
    }
    partial void OnSelectedTargetChanged(OrderSearchRow? value) { ClearPairings(); Invalidate(); }
    partial void OnBranchChanged(string value) { ClearPairings(); Invalidate(); }
    partial void OnKindIndexChanged(int value)
    {
        UpdatePrices = true; if (!IsUpdate && PriceSourceIndex == (int)SynchronizationPriceSource.KeepExisting) PriceSourceIndex = 0;
        foreach (var row in FinancialPolicies) { row.CanKeepExisting = IsUpdate; if (!IsUpdate && row.SourceIndex == (int)SynchronizationFinancialSource.KeepExisting) row.SourceIndex = 0; }
        RefreshDocumentChoices(); Invalidate(); OnPropertyChanged(nameof(IsUpdate)); OnPropertyChanged(nameof(CanChoosePriceSource));
        OnPropertyChanged(nameof(PreviewActionLabel)); OnPropertyChanged(nameof(WriteActionLabel)); OnPropertyChanged(nameof(WriteConfigurationStatus));
    }
    partial void OnUpdatePricesChanged(bool value)
    {
        if (IsUpdate && !value) { if (PriceSourceIndex != 1) _lastUpdatingPriceSource = PriceSourceIndex; PriceSourceIndex = 1; }
        else if (PriceSourceIndex == 1) PriceSourceIndex = _lastUpdatingPriceSource;
        FinancialPolicyChanged(); OnPropertyChanged(nameof(CanChoosePriceSource));
    }
    partial void OnPriceSourceIndexChanged(int value)
    {
        if (value == 1 && IsUpdate && UpdatePrices) UpdatePrices = false;
        if (value != 1) _lastUpdatingPriceSource = value;
        FinancialPolicyChanged();
    }
    private void FinancialPolicyChanged()
    {
        ClearSelection();
        foreach (var row in Differences.Where(row => row.Difference.Kind == SynchronizationChangeKind.Price))
        {
            row.CanSelect = FinancialPolicy.Prices == SynchronizationPriceSource.SelectedChanges;
            if (!row.CanSelect) row.IsSelected = false;
        }
        if (HasPreview && Differences.Count == 0) Status = strings[FinancialPolicy.IsExplicit ? "FinancialReviewPolicy" : "SyncNoDifferences"];
    }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanPreview)); OnPropertyChanged(nameof(PreviewBlockedReason)); RefreshWriteState(); }
    partial void OnNewOrderDateChanged(DateTimeOffset? value) { ClearSelection(); OnPropertyChanged(nameof(CanPreview)); OnPropertyChanged(nameof(PreviewBlockedReason)); }
    private void RefreshWriteState() { OnPropertyChanged(nameof(CanConfirm)); ConfirmWriteCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanReconcile)); OnPropertyChanged(nameof(HasPendingOperation)); OnPropertyChanged(nameof(WriteDetails)); }
    private string ErpErrorMessage(ErpException exception) => exception.Code == ErpError.QueryMissing && exception.Operation is { } operation
        ? strings.Format("SyncMissingQuery", strings["QueryName" + operation], operation) : strings["ErpError" + exception.Code];
    private async Task ReloadOperationsAsync(Guid quotationId, CancellationToken token)
    {
        var saved = operations is null ? [] : await operations.GetSynchronizationOperationsAsync(quotationId, token);
        _pendingOperation = saved.FirstOrDefault(operation => operation.State is OrderOperationState.Pending or OrderOperationState.Unknown);
        CreatedOrders.Clear();
        foreach (var operation in saved.Where(operation => operation.State == OrderOperationState.Committed
            && operation.Plan.Kind == OrderWriteKind.Create && !string.IsNullOrWhiteSpace(operation.OrderId)).OrderByDescending(operation => operation.CreatedAtUtc))
        {
            string number = operation.Plan.Allocation?.Number ?? strings["NotAvailable"];
            string code = operation.OrderId!;
            string branch = operation.Plan.Selection.Preview.Branch;
            CreatedOrders.Add(new(number, code, branch, strings.Format("SyncCreatedOrderDetails", number, code, branch)));
        }
        OnPropertyChanged(nameof(HasCreatedOrders)); RefreshWriteState();
    }
    private void RememberBranch()
    {
        if (preferences is null) return;
        var saved = preferences.Load();
        if (saved.LastPurchaseBranch != Branch.Trim() && !preferences.TrySave(saved with { LastPurchaseBranch = Branch.Trim() }))
            Status += " " + strings["PreferenceError"];
    }
    public Task LoadAsync(Guid quotationId) => RunAsync(async token =>
    {
        Invalidate(); Brands.Clear(); Targets.Clear(); SelectedBrand = null; SelectedTarget = null; KindIndex = 0; RemovalConfirmed = false;
        UpdatePrices = true; PriceSourceIndex = 0; foreach (var row in FinancialPolicies) row.SourceIndex = 0;
        await ReloadOperationsAsync(quotationId, token);
        var input = await repository.GetPurchaseInputAsync(quotationId, token);
        _documentInput = input.Analysis;
        string? lastBranch = preferences?.Load().LastPurchaseBranch;
        Branch = !string.IsNullOrWhiteSpace(lastBranch) ? lastBranch.Trim()
            : input.Analysis.Inputs.Orders.FirstOrDefault()?.Header.Branch ?? CreatedOrders.FirstOrDefault()?.Branch ?? string.Empty;
        if (input.Preparation is null || input.Preparation.Lines.Count == 0) { Status = strings["SyncPrepareFirst"]; return; }
        foreach (var proposal in PurchaseSynchronization.Proposals(input.Preparation))
            Brands.Add(new(proposal, proposal.Brand?.Name ?? strings["SyncBrandUnknown"] + " · " + proposal.Lines[0].Offer.Item.ProductCode));
        SelectedBrand = Brands.FirstOrDefault();
        _configurationErrors.Clear();
        if (writer is not null)
            foreach (var kind in new[] { OrderWriteKind.Create, OrderWriteKind.Update })
                try { _ = await writer.GetProfileAsync(kind, token); }
                catch (OrderException exception) { _configurationErrors[kind] = strings["OrderError" + exception.Code]; }
                catch (ErpException exception) { _configurationErrors[kind] = ErpErrorMessage(exception); }
        OnPropertyChanged(nameof(WriteConfigurationStatus));
    });

    public async Task PrepareOrderPreviewAsync()
    {
        if (!CanPreview) { Status = PreviewBlockedReason; return; }
        var selected = !IsUpdate && HasPreview ? Differences.Where(row => row.IsSelected).Select(row => row.Difference.Id).ToHashSet() : null;
        await PreviewCommand.ExecuteAsync(null);
        if (IsUpdate || !HasPreview) return;
        foreach (var difference in Differences) difference.IsSelected = difference.CanSelect && (selected is null || selected.Contains(difference.Difference.Id));
        await ReviewSelectionCommand.ExecuteAsync(null);
    }
    [RelayCommand]
    private Task SearchTargetsAsync() => RunAsync(async token =>
    {
        var rows = await orders.SearchAsync(new(Number: Number, Branch: Branch), token);
        Targets.Clear(); foreach (var row in rows) Targets.Add(new(row, strings.Format("OrderResultLabel", row.Number, row.SupplierName, row.Id, row.Branch)));
        SelectedTarget = null;
    });
    private void ClearPairings() { Pairs.Clear(); PairTargets.Clear(); SelectedPairTarget = null; SelectedPair = null; }
    [RelayCommand]
    private Task LoadPairingLinesAsync() => RunAsync(async token =>
    {
        if (SelectedTarget is null) return;
        var order = await orders.ReadAsync(SelectedTarget.Header.Id, token);
        token.ThrowIfCancellationRequested();
        if (SelectedBrand?.Proposal.Brand?.CatalogIdentity != order.CatalogIdentity || order.Header.Branch != Branch.Trim())
            throw new SynchronizationException(SynchronizationError.Conflict);
        ClearPairings(); Invalidate();
        foreach (var line in order.Lines.Where(line => !line.IsCancelled)) PairTargets.Add(new(line, strings.Format("SyncPairTarget", line.Id, line.ProductCode, line.Description)));
        SelectedPairTarget = PairTargets.FirstOrDefault();
    });
    [RelayCommand]
    private void AddPair()
    {
        if (SelectedPairSource is null || SelectedPairTarget is null) return;
        foreach (var old in Pairs.Where(row => row.Pair.LineId == SelectedPairTarget.Line.Id || row.Pair.GroupKey == SelectedPairSource.Line.GroupKey).ToArray()) Pairs.Remove(old);
        Pairs.Add(new(new(SelectedPairTarget.Line.Id, SelectedPairSource.Line.GroupKey), SelectedPairTarget.Label + " → " + SelectedPairSource.Label));
        Invalidate();
    }
    [RelayCommand] private void RemovePair() { if (SelectedPair is not null) Pairs.Remove(SelectedPair); SelectedPair = null; Invalidate(); }
    [RelayCommand]
    private Task PreviewAsync() => RunAsync(async token =>
    {
        if (SelectedBrand is null || string.IsNullOrWhiteSpace(Branch) || IsUpdate && SelectedTarget is null || !DocumentChoicesAnswered) return;
        Invalidate();
        _preview = await _service.PreviewAsync(SelectedBrand.Proposal, Branch.Trim(), IsUpdate ? SelectedTarget!.Header.Id : null, IsUpdate ? Pairs.Select(row => row.Pair).ToArray() : null, token,
            IsUpdate ? null : DocumentChoices.Select(row => row.Build()).ToArray());
        PreviewSummary = !IsUpdate ? strings.Format("SyncCreatePreviewSummary", _preview.Supplier.Supplier.Name, Branch)
            : strings.Format("SyncPreviewSummary", _preview.Target?.Header.Id ?? strings["SyncNewOrder"], Branch,
            _preview.Target?.Header.Status ?? strings["SyncStatusPending"], _preview.Target?.Header.SupplierName ?? "—", _preview.Supplier.Supplier.Name,
            _preview.Supplier.Supplier.Id, _preview.Supplier.SourceOrderId);
        foreach (var difference in _preview.Differences)
        {
            var row = new SynchronizationDifferenceRow(difference, Label(difference)) { CanSelect = difference.Kind != SynchronizationChangeKind.Price || FinancialPolicy.Prices == SynchronizationPriceSource.SelectedChanges };
            row.PropertyChanged += (_, _) => { ClearSelection(); OnPropertyChanged(nameof(RequiresRemovalConfirmation)); if (!RequiresRemovalConfirmation) RemovalConfirmed = false; }; Differences.Add(row);
        }
        Status = Differences.Count == 0 ? strings[FinancialPolicy.IsExplicit ? "FinancialReviewPolicy" : "SyncNoDifferences"] : strings["SyncChooseChanges"];
        OnPropertyChanged(nameof(HasPreview)); OnPropertyChanged(nameof(HasRemoval));
    });
    private string Label(SynchronizationDifference change)
    {
        if (change.Kind == SynchronizationChangeKind.Supplier) return strings.Format("SyncSupplierChange", _preview!.Target!.Header.SupplierId, _preview.Supplier.Supplier.Id);
        if (change.Kind == SynchronizationChangeKind.Remove) return strings.Format("SyncRemoveLine", change.Target!.Id, change.Target.Description, strings.Quantity(change.Target.Quantity, change.Target.Unit));
        var source = change.Source!; var item = source.Offer.Item;
        if (_preview!.CreationDocuments is not null)
        {
            var lines = CreationDocumentRules.Lines(_preview, [source.GroupKey]);
            return string.Join(Environment.NewLine, lines.Select(line => strings.Format("SyncCombinedLine", line.ProductCode, line.Description,
                strings.Quantity(line.Quantity, line.Unit), strings.Money(line.UnitPrice, 4), strings.Money(line.Total))));
        }
        return strings.Format("SyncLineChange", strings["SyncKind" + change.Kind], change.Target?.Id ?? "—", change.Target?.ProductCode ?? "—", source.Offer.Link!.Product!.ProductCode,
            change.Target is null ? "—" : strings.Quantity(change.Target.Quantity, change.Target.Unit), strings.Quantity(item.Quantity!.Value, item.Unit),
            change.Target is null ? "—" : strings.Money(change.Target.UnitPrice, 4), strings.Money(item.UnitPrice!.Value, 4),
            source.RequestedQuantity is decimal requested ? strings.Quantity(requested, source.BaseUnit) : strings["DemandNotInformed"]);
    }
    [RelayCommand]
    private Task ReviewSelectionAsync() => RunAsync(async token =>
    {
        if (_preview is null) return;
        ClearSelection();
        await _service.ValidatePreviewAsync(_preview, token);
        Selection = PurchaseSynchronization.Select(_preview, Differences.Where(row => row.IsSelected && row.CanSelect).Select(row => row.Difference.Id).ToArray(), RemovalConfirmed, FinancialPolicy.IsExplicit);
        foreach (var line in Selection.Lines) ResultLines.Add(new(line.Description, line.ProductCode, strings.Quantity(line.Quantity, line.Unit), strings.Money(line.Total)) { Price = strings.Money(line.UnitPrice, 4) });
        ResultSummary = !IsUpdate ? strings.Format("SyncCreateResultSummary", Selection.Lines.Count, strings.Money(Selection.ItemSubtotal))
            : strings.Format("SyncResultSummary", Selection.SupplierId, Selection.Preview.Proposal.Brand!.Name, strings.Money(Selection.ItemSubtotal), Selection.RemovedControls.Count);
        if (_preview.CreationDocuments is { } documents)
            ResultSummary += Environment.NewLine + strings.Format("SyncCombinedDocumentsSummary", documents.Sources.Select(source => source.Line.Offer.ProposalId).Distinct().Count());
        Status = strings["SyncSafeValidationRequired"]; OnPropertyChanged(nameof(HasSelection));
        if (_writes is not null)
        {
            _writePlan = await _writes.PreviewAsync(Selection, token, date: NewOrderDate is { } date ? DateOnly.FromDateTime(date.Date) : null, financialPolicy: FinancialPolicy);
            if (_writePlan.FinancialPreview is { } financial)
            {
                ResultLines.Clear();
                foreach (var line in financial.Lines.OrderBy(line => line.Position))
                {
                    var original = Selection.Lines[line.Position];
                    ResultLines.Add(new(original.Description, original.ProductCode, strings.Quantity(line.Quantity, original.Unit), strings.Money(line.ItemTotal))
                    { Price = strings.Money(line.UnitPrice, 4) });
                }
                FinancialSummary = strings.Format("FinancialConfirmedTotal", strings.Money(financial.OrderTotal!.Value));
                ResultSummary = !IsUpdate ? strings.Format("SyncCreateResultSummary", financial.Lines.Count, strings.Money(financial.Lines.Sum(line => line.ItemTotal)))
                    : strings.Format("SyncResultSummary", Selection.SupplierId, Selection.Preview.Proposal.Brand!.Name, strings.Money(financial.Lines.Sum(line => line.ItemTotal)), Selection.RemovedControls.Count);
                List<string> details = [];
                foreach (var key in Enum.GetValues<FinancialField>())
                    if (financial.Header.Get(key) is { } value) details.Add(strings["FinancialField" + key] + ": " + value.ToString("G29", strings.Culture));
                foreach (var line in financial.Lines)
                {
                    details.Add(strings.Format("FinancialResolvedLine", line.ProductCode, line.UnitPrice.ToString("G29", strings.Culture), line.ItemTotal.ToString("G29", strings.Culture)));
                    foreach (var key in Enum.GetValues<FinancialField>())
                        if (line.Values.Get(key) is { } value) details.Add(line.ProductCode + " · " + strings["FinancialField" + key] + ": " + value.ToString("G29", strings.Culture));
                }
                details.AddRange(financial.Sources.Select(source => strings.Format("FinancialHistorySource", source.OrderId, source.Date.ToString("d", strings.Culture))));
                FinancialDetails = string.Join(Environment.NewLine, details);
            }
            Status = strings["SyncReadyToConfirm"]; RefreshWriteState();
        }
        RememberBranch();
    });
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private Task ConfirmWriteAsync() => RunAsync(async token =>
    {
        if (_writes is null || _writePlan is null || _pendingOperation is not null) throw new OrderException(OrderError.ConfirmationRequired);
        var plan = _writePlan;
        try
        {
            var receipt = await _writes.ConfirmAsync(plan, true, RemovalConfirmed, token);
            Status = strings["OrderState" + receipt.State];
        }
        finally
        {
            ClearSelection();
            await ReloadOperationsAsync(plan.Selection.Preview.Proposal.QuotationId, CancellationToken.None);
        }
    });
    [RelayCommand]
    private Task ReconcileAsync() => RunAsync(async token =>
    {
        if (_writes is null || _pendingOperation is null) return;
        var pending = _pendingOperation;
        var receipt = await _writes.ReconcileAsync(pending, token);
        Status = strings["OrderState" + receipt.State];
        await ReloadOperationsAsync(pending.Plan.Selection.Preview.Proposal.QuotationId, token);
    });
    partial void OnRemovalConfirmedChanged(bool value) => ClearSelection();
    private void ClearSelection() { Selection = null; _writePlan = null; ResultLines.Clear(); ResultSummary = string.Empty; FinancialSummary = string.Empty; FinancialDetails = string.Empty; OnPropertyChanged(nameof(HasSelection)); RefreshWriteState(); }
    private void Invalidate() { _preview = null; Differences.Clear(); RemovalConfirmed = false; ClearSelection(); PreviewSummary = string.Empty; OnPropertyChanged(nameof(CanPreview)); OnPropertyChanged(nameof(PreviewBlockedReason)); OnPropertyChanged(nameof(HasPreview)); OnPropertyChanged(nameof(HasRemoval)); OnPropertyChanged(nameof(RequiresRemovalConfirmation)); }
    private CancellationTokenSource? _operation;
    [RelayCommand] private void Cancel() => _operation?.Cancel();
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return; using CancellationTokenSource operation = new(); _operation = operation; IsBusy = true; Status = strings["ImportWorking"];
        try { await action(operation.Token); if (Status == strings["ImportWorking"]) Status = string.Empty; }
        catch (OperationCanceledException) { Status = strings[HasPendingOperation ? "OrderStateUnknown" : "ImportCanceled"]; }
        catch (SynchronizationException exception) { Status = strings["SyncError" + exception.Code]; }
        catch (ErpException exception) { Status = HasPendingOperation ? strings["OrderStateUnknown"] : ErpErrorMessage(exception); }
        catch (OrderException exception) { Status = strings["OrderError" + exception.Code]; }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or Microsoft.EntityFrameworkCore.DbUpdateException) { Status = strings[HasPendingOperation ? "OrderStateUnknown" : "ImportStorageError"]; }
        finally { _operation = null; IsBusy = false; }
    }
}
