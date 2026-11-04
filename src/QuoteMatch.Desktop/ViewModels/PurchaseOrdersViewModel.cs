using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Application.Quotations;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Desktop.ViewModels;

public sealed record OrderReferenceChoice(PurchaseOrderReference Reference, string Label);
public sealed record OrderHeaderRow(PurchaseOrderHeader Header, string Label);
public sealed record LinkedOrderRow(PurchaseOrderSnapshot Snapshot, string Label);
public sealed record OrderComparisonRow(OrderComparisonLine Comparison, string Product, string Quoted, string Covered,
    string Remaining, string QuotedPrice, string Contributions);
public sealed record OrderExcessRow(string Label);
public sealed record OrderChangeRow(OrderLineChange Change, string Label);
public sealed record OrderOperationRow(OrderOperation Operation, string Label);

public partial class PurchaseOrdersViewModel(Localizer strings, IQuotationRepository quotations,
    IPurchasePreparationRepository preparations, IOrderWorkspaceRepository repository,
    IPurchaseOrderReader reader, IPurchaseOrderWriter writer) : ObservableObject
{
    private readonly PurchaseOrderService _service = new(repository, writer);
    private OrderReconciliation? _saved;
    private PurchaseOrderReference? _reference;
    private OrderWritePlan? _preview;
    private readonly List<OrderManualPair> _pairs = [];
    [ObservableProperty] private OrderReferenceChoice? _selectedReference;
    [ObservableProperty] private OrderHeaderRow? _selectedSearchResult;
    [ObservableProperty] private LinkedOrderRow? _selectedOrder;
    [ObservableProperty] private PurchaseOrderLine? _selectedLine;
    [ObservableProperty] private OrderComparisonRow? _selectedComparison;
    [ObservableProperty] private OrderChangeRow? _selectedChange;
    [ObservableProperty] private OrderOperationRow? _selectedOperation;
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _supplier = "";
    [ObservableProperty] private string _branch = "";
    [ObservableProperty] private string _orderStatus = "";
    [ObservableProperty] private DateTimeOffset? _fromDate;
    [ObservableProperty] private DateTimeOffset? _toDate;
    [ObservableProperty] private DateTimeOffset? _orderDate = DateTimeOffset.Now.Date;
    [ObservableProperty] private bool _changeQuantity;
    [ObservableProperty] private bool _changePrice;
    [ObservableProperty] private bool _cancelLine;
    [ObservableProperty] private bool _cancellationConfirmed;
    [ObservableProperty] private bool _shortagesAccepted;
    [ObservableProperty] private decimal _quantity = 1;
    [ObservableProperty] private decimal _unitPrice;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    public Localizer Strings => strings;
    public ObservableCollection<OrderReferenceChoice> References { get; } = [];
    public ObservableCollection<PurchaseReferenceCandidate> ReferenceCandidates { get; } = [];
    public ObservableCollection<OrderHeaderRow> SearchResults { get; } = [];
    public ObservableCollection<LinkedOrderRow> Orders { get; } = [];
    public ObservableCollection<PurchaseOrderLine> Lines { get; } = [];
    public ObservableCollection<OrderComparisonRow> Comparison { get; } = [];
    public ObservableCollection<OrderExcessRow> ExcessLines { get; } = [];
    public ObservableCollection<OrderChangeRow> Changes { get; } = [];
    public ObservableCollection<OrderOperationRow> Operations { get; } = [];
    public bool HasReference => _reference is not null;
    public bool CanLink => CanWork && HasReference && SelectedSearchResult is not null;
    public string LinkUnavailableReason => HasReference ? "" : strings["OrdersLinkNeedsReference"];
    public bool IsPreviewing => _preview is not null;
    public bool CanWork => !IsBusy && !IsPreviewing;
    public bool HasCancellations => _preview?.Changes.Any(change => change.Cancel) == true;
    public string ComparisonHelp => strings["OrdersComparisonHelp"] + (SelectedComparison?.Comparison.Reference is not { } line ? "" : Environment.NewLine
        + strings.Format("OrdersComparisonInputs", line.ProductCode, strings.Quantity(line.Quantity, line.Unit), line.BaseUnitsPerUnit.ToString("G29", strings.Culture),
            line.UnitPrice.ToString("G29", strings.Culture), strings.Money(line.Total), line.RequestedQuantity is decimal demand ? strings.Quantity(demand, line.BaseUnit) : strings["DemandNotInformed"]));
    public string SelectedLineDetail => SelectedLine is not { } line ? "" : strings.Format("OrdersLineDetail", line.Unit, line.BaseUnit,
        line.BaseUnitsPerUnit.ToString("G29", strings.Culture), strings.Quantity(line.ReceivedQuantity, line.Unit),
        strings[line.IsCancelled ? "OrdersLineCancelled" : line.ReceivedQuantity > 0 || SelectedOrder?.Snapshot.Header.CanEdit != true ? "OrdersLineReadOnly" : "OrdersLineEditable"]);
    public string ReferenceLabel => _reference is null ? strings["OrdersSelectReference"] : strings.Format("OrdersReference", _reference.SupplierName, _reference.PreparationVersion);
    public string Excess => strings.Format("OrdersExcess", ExcessLines.Count);
    public string PreviewLabel => _preview is null ? "" : strings.Format("OrdersPreviewSummary", strings["Orders" + _preview.Kind], _preview.Reference.SupplierName,
        _preview.ExpectedOrder?.Header.Number ?? "—", _preview.Branch, _preview.Status, _preview.Date.ToString("d", strings.Culture), _preview.Changes.Count);
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanWork)); OnPropertyChanged(nameof(CanLink)); }
    partial void OnSelectedSearchResultChanged(OrderHeaderRow? value) => OnPropertyChanged(nameof(CanLink));
    partial void OnSelectedComparisonChanged(OrderComparisonRow? value) => OnPropertyChanged(nameof(ComparisonHelp));
    partial void OnSelectedReferenceChanged(OrderReferenceChoice? value)
    {
        _reference = null; _saved = null; _pairs.Clear(); Orders.Clear(); Lines.Clear(); Comparison.Clear(); ExcessLines.Clear(); Changes.Clear(); Operations.Clear(); SelectedOrder = null;
        NotifyState();
    }
    partial void OnSelectedOrderChanged(LinkedOrderRow? value)
    {
        Changes.Clear(); Lines.Clear();
        foreach (var line in value?.Snapshot.Lines ?? []) Lines.Add(line);
        SelectedLine = Lines.FirstOrDefault();
        if (value is null) return;
        Branch = value.Snapshot.Header.Branch; OrderStatus = value.Snapshot.Header.Status;
        OrderDate = new DateTimeOffset(value.Snapshot.Header.Date.ToDateTime(TimeOnly.MinValue));
    }
    partial void OnSelectedLineChanged(PurchaseOrderLine? value)
    {
        ChangePrice = false; ChangeQuantity = false; CancelLine = false;
        Quantity = value?.Quantity ?? 1; UnitPrice = value?.UnitPrice ?? 0;
        OnPropertyChanged(nameof(SelectedLineDetail));
    }
    [RelayCommand(IncludeCancelCommand = true)]
    private Task RefreshReferencesAsync(CancellationToken token) => RunAsync(async () =>
    {
        var choices = new List<OrderReferenceChoice>();
        var candidates = new List<PurchaseReferenceCandidate>();
        foreach (var quotation in await quotations.GetQuotationsAsync(token))
        {
            var input = await preparations.GetPurchaseInputAsync(quotation.Quotation.Id, token);
            if (input.Preparation is not { Lines.Count: > 0 } preparation)
            {
                candidates.Add(new(quotation.Quotation.Id, quotation.Quotation.Name,
                    strings["OrdersReferenceNeedsPreparation"], null)); continue;
            }
            foreach (var proposal in preparation.Lines.Select(line => line.Offer.ProposalId).Distinct())
            {
                try
                {
                    var reference = PurchaseOrderReference.From(preparation, proposal);
                    choices.Add(new(reference, quotation.Quotation.Name + " · " + reference.SupplierName + " · v" + reference.PreparationVersion));
                    candidates.Add(new(quotation.Quotation.Id, quotation.Quotation.Name + " · " + reference.SupplierName,
                        strings.Format("OrdersReferenceReady", reference.PreparationVersion), reference));
                }
                catch (OrderException)
                {
                    candidates.Add(new(quotation.Quotation.Id, quotation.Quotation.Name + " · " + preparation.Lines.First(line => line.Offer.ProposalId == proposal).Offer.SupplierName,
                        strings["OrdersReferenceNeedsErp"], null));
                }
            }
        }
        References.Clear(); foreach (var choice in choices) References.Add(choice);
        ReferenceCandidates.Clear(); foreach (var candidate in candidates) ReferenceCandidates.Add(candidate);
        SelectedReference ??= References.FirstOrDefault();
        if (References.Count == 0) Status = strings["OrdersNoPreparedPurchases"];
    }, token);

    [RelayCommand(IncludeCancelCommand = true)]
    private Task LoadReferenceAsync(CancellationToken token) => RunAsync(async () =>
    {
        if (SelectedReference is null) return;
        var latest = SelectedReference.Reference;
        _saved = await repository.GetReconciliationAsync(latest.QuotationId, latest.ProposalId, token);
        _reference = _saved?.Reference ?? latest;
        _pairs.Clear(); _pairs.AddRange(_saved?.ManualPairs ?? []);
        Orders.Clear(); foreach (var order in _saved?.Orders ?? []) Orders.Add(Row(order));
        ShortagesAccepted = _saved?.ShortagesAccepted ?? false;
        SelectedOrder = Orders.FirstOrDefault(); Rebuild(); await ReloadOperationsAsync(token);
    }, token);

    [RelayCommand]
    private void UseLatestReference()
    {
        if (!CanWork || SelectedReference is null) return;
        _reference = SelectedReference.Reference; _pairs.Clear(); ShortagesAccepted = false; Changes.Clear(); Rebuild();
    }
    [RelayCommand(IncludeCancelCommand = true)]
    private Task SearchAsync(CancellationToken token) => RunAsync(async () =>
    {
        SearchResults.Clear();
        foreach (var header in await reader.SearchAsync(new(Number, Supplier, Branch, OrderStatus, Date(FromDate), Date(ToDate)), token))
            SearchResults.Add(new(header, header.Number + " · " + header.SupplierName + " · " + header.Branch + " · " + header.Status + " · " + header.Id));
        Status = strings.Format(SearchResults.Count == 1000 ? "OrdersSearchLimited" : "OrdersSearchCount", SearchResults.Count);
    }, token);

    [RelayCommand(IncludeCancelCommand = true)]
    private Task LinkAsync(CancellationToken token) => RunAsync(async () =>
    {
        if (_reference is null || SelectedSearchResult is null) return;
        var order = await reader.ReadAsync(SelectedSearchResult.Header.Id, token);
        _ = PurchaseOrderRules.Compare(_reference, [.. Orders.Select(row => row.Snapshot), order]);
        Orders.Add(Row(order)); ShortagesAccepted = false; SelectedOrder = Orders.Last(); Rebuild();
    }, token);
    [RelayCommand] private void Unlink() { if (CanWork && SelectedOrder is { } row) { Orders.Remove(row); _pairs.RemoveAll(pair => pair.OrderId == row.Snapshot.Header.Id); SelectedOrder = Orders.FirstOrDefault(); ShortagesAccepted = false; Rebuild(); } }
    [RelayCommand]
    private void MoveUp()
    {
        if (!CanWork || SelectedOrder is null) return;
        var selected = SelectedOrder;
        int index = Orders.IndexOf(selected); if (index > 0) { Orders.Move(index, index - 1); SelectedOrder = selected; Changes.Clear(); Rebuild(); }
    }
    [RelayCommand(IncludeCancelCommand = true)]
    private Task RefreshOrdersAsync(CancellationToken token) => RunAsync(async () =>
    {
        var refreshed = new List<PurchaseOrderSnapshot>();
        foreach (var order in Orders) refreshed.Add(await reader.ReadAsync(order.Snapshot.Header.Id, token));
        foreach (var operation in Operations.Where(row => row.Operation.State == OrderOperationState.Committed
            && row.Operation.Plan.Reference.ProposalId == _reference?.ProposalId && !refreshed.Any(order => order.Header.Id == row.Operation.OrderId)))
            refreshed.Add(await reader.ReadAsync(operation.Operation.OrderId!, token));
        if (_reference is not null) _ = PurchaseOrderRules.Compare(_reference, refreshed, _pairs);
        Orders.Clear(); foreach (var order in refreshed) Orders.Add(Row(order)); SelectedOrder = Orders.FirstOrDefault();
        Changes.Clear(); ShortagesAccepted = false; Rebuild();
    }, token);
    [RelayCommand(IncludeCancelCommand = true)]
    private Task SaveAsync(CancellationToken token) => RunAsync(async () =>
    {
        if (_reference is null) return;
        _saved = await repository.SaveReconciliationAsync(new(_saved?.Id ?? Guid.Empty, _saved?.Version ?? 0,
            _saved?.CreatedAtUtc ?? DateTime.UtcNow, _reference, Orders.Select(row => row.Snapshot).ToArray(), ShortagesAccepted)
        { ManualPairs = _pairs.ToArray() }, token);
        Status = strings["OrdersSaved"];
    }, token);

    [RelayCommand]
    private void PairSelectedLine()
    {
        if (!CanWork || _reference is null || SelectedOrder is null || SelectedLine is null || SelectedComparison is null) return;
        var pair = new OrderManualPair(SelectedOrder.Snapshot.Header.Id, SelectedLine.Id, SelectedComparison.Comparison.Reference.Id);
        var pairs = _pairs.Where(item => item.OrderId != pair.OrderId || item.LineId != pair.LineId).Append(pair).ToArray();
        try { _ = PurchaseOrderRules.Compare(_reference, Orders.Select(row => row.Snapshot).ToArray(), pairs); }
        catch (OrderException exception) { Status = strings["OrderError" + exception.Code]; return; }
        _pairs.Clear(); _pairs.AddRange(pairs); ShortagesAccepted = false; Changes.Clear(); Rebuild();
    }
    [RelayCommand] private void ClearManualPairs() { if (CanWork) { _pairs.Clear(); Changes.Clear(); Rebuild(); } }

    [RelayCommand]
    private void AddChange()
    {
        if (!CanWork || SelectedLine is not { } line) return;
        if (line.ReceivedQuantity > 0 || line.IsCancelled || SelectedOrder?.Snapshot.Header.CanEdit != true) { Status = strings["OrderErrorNotEditable"]; return; }
        if (!ChangeQuantity && !ChangePrice && !CancelLine) return;
        var change = new OrderLineChange(line.Id, null, CancelLine || !ChangeQuantity ? null : Quantity, CancelLine || !ChangePrice ? null : UnitPrice, CancelLine);
        var old = Changes.FirstOrDefault(row => row.Change.LineId == line.Id); if (old is not null) Changes.Remove(old);
        Changes.Add(new(change, ChangeLabel(change, line.Description)));
    }
    [RelayCommand]
    private void IncludeMissing()
    {
        if (!CanWork || SelectedComparison?.Comparison is not { RemainingQuantity: > 0 } comparison || SelectedOrder is null) return;
        var line = comparison.Reference;
        var change = new OrderLineChange(null, line.Id, comparison.RemainingQuantity / line.BaseUnitsPerUnit, line.UnitPrice, false);
        var old = Changes.FirstOrDefault(row => row.Change.ReferenceLineId == line.Id); if (old is not null) Changes.Remove(old);
        Changes.Add(new(change, ChangeLabel(change, line.Description)));
    }
    [RelayCommand] private void RemoveChange() { if (CanWork && SelectedChange is not null) Changes.Remove(SelectedChange); }
    [RelayCommand(IncludeCancelCommand = true)]
    private Task PreviewCreateAsync(CancellationToken token) => RunAsync(async () =>
    {
        if (_reference is null) return;
        EnsureKnownOrdersLinked();
        var remaining = PurchaseOrderRules.Compare(_reference, Orders.Select(row => row.Snapshot).ToArray(), _pairs).Lines.Where(line => line.RemainingQuantity > 0);
        var changes = remaining.Select(row => new OrderLineChange(null, row.Reference.Id, row.RemainingQuantity / row.Reference.BaseUnitsPerUnit, row.Reference.UnitPrice, false)).ToArray();
        _preview = await _service.PreviewAsync(OrderWriteKind.Create, _reference, null, Branch, OrderStatus,
            Date(OrderDate) ?? throw new OrderException(OrderError.InvalidChange), changes, token, Orders.Select(row => row.Snapshot).ToArray(), _pairs);
        Changes.Clear(); foreach (var change in changes) Changes.Add(new(change, ChangeLabel(change, _reference.Lines.Single(line => line.Id == change.ReferenceLineId).Description)));
        CancellationConfirmed = false; NotifyState();
    }, token);
    [RelayCommand(IncludeCancelCommand = true)]
    private Task PreviewUpdateAsync(CancellationToken token) => RunAsync(async () =>
    {
        if (_reference is null || SelectedOrder is null) return;
        EnsureKnownOrdersLinked();
        var order = SelectedOrder.Snapshot;
        _preview = await _service.PreviewAsync(OrderWriteKind.Update, _reference, order, order.Header.Branch, order.Header.Status, order.Header.Date,
            Changes.Select(row => row.Change).ToArray(), token, Orders.Select(row => row.Snapshot).ToArray(), _pairs);
        CancellationConfirmed = false; NotifyState();
    }, token);
    [RelayCommand] private void DismissPreview() { if (IsBusy) return; _preview = null; Changes.Clear(); NotifyState(); }
    [RelayCommand(IncludeCancelCommand = true)]
    private Task ConfirmAsync(CancellationToken token) => RunAsync(async () =>
    {
        if (_preview is null) return;
        if (HasCancellations && !CancellationConfirmed) throw new OrderException(OrderError.ConfirmationRequired);
        try
        {
            var receipt = await _service.ConfirmAsync(_preview, true, CancellationConfirmed, token);
            Status = strings["OrderState" + receipt.State];
            if (receipt.State == OrderOperationState.Committed) await LoadCommittedOrderAsync(receipt, token);
        }
        finally
        {
            _preview = null; Changes.Clear(); NotifyState(); await ReloadOperationsAsync(CancellationToken.None);
        }
    }, token, true);
    [RelayCommand(IncludeCancelCommand = true)]
    private Task ReconcileAsync(CancellationToken token) => RunAsync(async () =>
    {
        if (SelectedOperation is null) return;
        var receipt = await _service.ReconcileAsync(SelectedOperation.Operation, token);
        Status = strings["OrderState" + receipt.State];
        if (receipt.State == OrderOperationState.Committed) await LoadCommittedOrderAsync(receipt, token);
        await ReloadOperationsAsync(token);
    }, token);
    [RelayCommand]
    private void Cancel()
    {
        RefreshReferencesCommand.Cancel(); LoadReferenceCommand.Cancel(); SearchCommand.Cancel(); LinkCommand.Cancel(); RefreshOrdersCommand.Cancel();
        SaveCommand.Cancel(); PreviewCreateCommand.Cancel(); PreviewUpdateCommand.Cancel(); ConfirmCommand.Cancel(); ReconcileCommand.Cancel();
    }
    public void RefreshLabels() { Rebuild(); OnPropertyChanged(nameof(Strings)); }
    private async Task ReloadOperationsAsync(CancellationToken token)
    {
        Operations.Clear(); if (_reference is null) return;
        foreach (var operation in await repository.GetOrderOperationsAsync(_reference.QuotationId, token))
            Operations.Add(new(operation, strings["OrderState" + operation.State] + " · " + operation.Plan.OperationId + " · " + (operation.OrderId ?? "—")));
        SelectedOperation = Operations.LastOrDefault();
    }
    private void Rebuild()
    {
        Comparison.Clear(); ExcessLines.Clear();
        if (_reference is not null)
        {
            var comparison = PurchaseOrderRules.Compare(_reference, Orders.Select(row => row.Snapshot).ToArray(), _pairs);
            foreach (var row in comparison.Lines)
                Comparison.Add(new(row, row.Reference.ProductCode + " · " + row.Reference.Description,
                    strings.Quantity(row.QuotedQuantity, row.Reference.BaseUnit), strings.Quantity(row.CoveredQuantity, row.Reference.BaseUnit),
                    strings.Quantity(row.RemainingQuantity, row.Reference.BaseUnit), strings.Format("OrdersReferencePrice", strings.Money(row.Reference.UnitPrice / row.Reference.BaseUnitsPerUnit, 4), strings.Money(row.Reference.Total)), string.Join(Environment.NewLine, row.Contributions.Select(item =>
                        strings.Format("OrdersContribution", item.OrderId, item.LineId, strings.Quantity(item.Quantity, row.Reference.BaseUnit), strings.Money(item.BaseUnitPrice, 4),
                            item.PriceDifferencePercent?.ToString("+0.##;-0.##;0", strings.Culture) + (item.PriceDifferencePercent is null ? "—" : "%"),
                            strings.Money(item.ReportedLineTotal), strings.Quantity(item.TotalBaseQuantity, row.Reference.BaseUnit))))));
            foreach (var excess in comparison.Excess) ExcessLines.Add(new(excess.OrderId + " · " + excess.Line.Id + " · " + excess.Line.ProductCode + " · " + strings.Quantity(excess.Quantity, excess.Line.BaseUnit)));
        }
        SelectedComparison = Comparison.FirstOrDefault(); NotifyState();
    }
    private void EnsureKnownOrdersLinked()
    {
        if (Operations.Any(row => row.Operation.State is OrderOperationState.Pending or OrderOperationState.Unknown)) throw new OrderException(OrderError.PendingOperation);
        if (Operations.Any(row => row.Operation.State == OrderOperationState.Committed && row.Operation.Plan.Reference.ProposalId == _reference?.ProposalId
            && !Orders.Any(order => order.Snapshot.Header.Id == row.Operation.OrderId))) throw new OrderException(OrderError.RefreshRequired);
    }
    private async Task LoadCommittedOrderAsync(OrderWriteReceipt receipt, CancellationToken token)
    {
        try
        {
            var order = await reader.ReadAsync(receipt.OrderId!, token);
            if (_reference is null) return;
            var updated = Orders.Where(row => row.Snapshot.Header.Id != order.Header.Id).Select(row => row.Snapshot).Append(order).ToArray();
            _ = PurchaseOrderRules.Compare(_reference, updated, _pairs);
            var old = Orders.FirstOrDefault(row => row.Snapshot.Header.Id == order.Header.Id);
            if (old is not null) Orders[Orders.IndexOf(old)] = Row(order); else Orders.Add(Row(order));
            SelectedOrder = Orders.First(row => row.Snapshot.Header.Id == order.Header.Id); Rebuild();
        }
        catch (Exception exception) when (exception is ErpException or OrderException or OperationCanceledException)
        { Status = strings["OrderStateCommitted"] + " · " + strings["OrderErrorRefreshRequired"]; }
    }
    private string ChangeLabel(OrderLineChange change, string description)
    {
        var existing = SelectedOrder?.Snapshot.Lines.SingleOrDefault(line => line.Id == change.LineId);
        var reference = _reference?.Lines.SingleOrDefault(line => line.Id == change.ReferenceLineId);
        string unit = existing?.Unit ?? reference?.Unit ?? "";
        string quantityBefore = existing is null ? "" : existing.Quantity.ToString("G29", strings.Culture) + " → ";
        string priceBefore = existing is null ? "" : existing.UnitPrice.ToString("G29", strings.Culture) + " → ";
        return strings.Format("OrdersChange", (change.LineId ?? change.ReferenceLineId) + " · " + description,
            change.Cancel ? strings["OrdersCancelLine"] : string.Join(" · ", new[] {
                change.Quantity is decimal quantity ? strings["OrdersChangeQuantity"] + ": " + quantityBefore + strings.Quantity(quantity, unit) : "",
                change.UnitPrice is decimal price ? strings["OrdersChangePrice"] + ": " + priceBefore + price.ToString("G29", strings.Culture) + "/" + unit : "" }.Where(value => value.Length > 0)));
    }
    private static DateOnly? Date(DateTimeOffset? value) => value is { } date ? DateOnly.FromDateTime(date.DateTime) : null;
    private static LinkedOrderRow Row(PurchaseOrderSnapshot order) => new(order, order.Header.Number + " · " + order.Header.Branch + " · " + order.Header.Status + " · " + order.Header.Id);
    private void NotifyState()
    {
        foreach (var name in new[] { nameof(HasReference), nameof(CanLink), nameof(LinkUnavailableReason), nameof(IsPreviewing), nameof(CanWork), nameof(HasCancellations), nameof(ReferenceLabel), nameof(PreviewLabel), nameof(Excess) }) OnPropertyChanged(name);
    }
    private async Task RunAsync(Func<Task> action, CancellationToken token, bool confirmation = false)
    {
        if (IsBusy || IsPreviewing && !confirmation) return;
        IsBusy = true; Status = "";
        try { token.ThrowIfCancellationRequested(); await action(); }
        catch (OrderException exception) { Status = strings["OrderError" + exception.Code]; }
        catch (ErpException exception) { Status = strings["ErpError" + exception.Code]; }
        catch (OperationCanceledException) { Status = strings["ImportCanceled"]; }
        catch (OverflowException) { Status = strings["PurchaseErrorNumericLimit"]; }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { Status = strings["ImportStorageError"]; }
        finally { IsBusy = false; }
    }
}
