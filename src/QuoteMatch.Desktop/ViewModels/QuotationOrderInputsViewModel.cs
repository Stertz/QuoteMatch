using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Application.Quotations;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Desktop.ViewModels;

public sealed record OrderSearchRow(PurchaseOrderHeader Header, string Label);

public partial class QuotationOrderInputsViewModel(Localizer strings, IQuotationRepository repository, IPurchaseOrderReader reader) : ObservableObject
{
    private QuotationInputs _saved = QuotationInputs.Legacy(Guid.Empty);
    [ObservableProperty] private string _number = string.Empty;
    [ObservableProperty] private string _supplier = string.Empty;
    [ObservableProperty] private int _priceReferenceIndex;
    [ObservableProperty] private bool _replaceManualDemand;
    [ObservableProperty] private bool _hasManualDemand;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private OrderSearchRow? _selectedResult;
    [ObservableProperty] private LinkedOrderRow? _selectedOrder;
    public Localizer Strings => strings;
    public ObservableCollection<OrderSearchRow> Results { get; } = [];
    public ObservableCollection<LinkedOrderRow> Orders { get; } = [];
    public bool HasOrders => Orders.Count > 0;
    public bool HasChanges => _saved.PriceReference != (QuotationPriceReference)PriceReferenceIndex
        || !_saved.Orders.SequenceEqual(Orders.Select(row => row.Snapshot));
    partial void OnPriceReferenceIndexChanged(int value) => OnPropertyChanged(nameof(HasChanges));
    partial void OnReplaceManualDemandChanged(bool value) => OnPropertyChanged(nameof(HasChanges));
    public QuotationInputs Build(Guid quotationId) => _saved with
    {
        QuotationId = quotationId,
        PriceReference = (QuotationPriceReference)PriceReferenceIndex,
        GroupingVersion = HasOrders || _saved.QuotationId == Guid.Empty ? ProductGroupingVersion.ProductNameFallback : _saved.GroupingVersion,
        Orders = Orders.Select(row => row.Snapshot).ToArray()
    };
    public async Task LoadAsync(Guid? quotationId, bool hasManualDemand = false)
    {
        _saved = quotationId is { } id ? await repository.GetQuotationInputsAsync(id) : QuotationInputs.Legacy(Guid.Empty);
        PriceReferenceIndex = (int)_saved.PriceReference; HasManualDemand = hasManualDemand; ReplaceManualDemand = false;
        Orders.Clear(); Results.Clear(); SelectedOrder = null; SelectedResult = null; Status = string.Empty;
        foreach (var snapshot in _saved.Orders) Orders.Add(Row(snapshot));
        OnPropertyChanged(nameof(HasOrders));
    }
    private LinkedOrderRow Row(PurchaseOrderSnapshot order) => new(order, strings.Format("LinkedOrderLabel", order.Header.Number, order.Header.SupplierName, order.Header.Id, order.FetchedAtUtc.ToLocalTime().ToString("g", strings.Culture)));
    [RelayCommand(IncludeCancelCommand = true)]
    private Task SearchAsync(CancellationToken token) => RunAsync(async () =>
    {
        var rows = await reader.SearchAsync(new(Number, Supplier), token);
        token.ThrowIfCancellationRequested();
        Results.Clear(); foreach (var row in rows) Results.Add(new(row, strings.Format("OrderResultLabel", row.Number, row.SupplierName, row.Id, row.Branch)));
        SelectedResult = Results.FirstOrDefault(); Status = strings.Format("OrderSearchCount", rows.Count);
    });
    [RelayCommand(IncludeCancelCommand = true)]
    private Task LinkAsync(CancellationToken token) => RunAsync(async () =>
    {
        if (SelectedResult is null) return;
        var snapshot = await reader.ReadAsync(SelectedResult.Header.Id, token);
        token.ThrowIfCancellationRequested();
        if (Orders.Any(row => row.Snapshot.CatalogIdentity != snapshot.CatalogIdentity)) throw new QuotationException(QuotationError.InvalidInputs);
        var old = Orders.FirstOrDefault(row => row.Snapshot.CatalogIdentity == snapshot.CatalogIdentity && row.Snapshot.Header.Id == snapshot.Header.Id);
        if (old is not null) Orders.Remove(old);
        Orders.Add(Row(snapshot)); SelectedOrder = Orders.Last(); OnPropertyChanged(nameof(HasOrders)); OnPropertyChanged(nameof(HasChanges));
    });
    [RelayCommand(IncludeCancelCommand = true)]
    private Task RefreshAsync(CancellationToken token) => RunAsync(async () =>
    {
        if (SelectedOrder is not { } selected) return;
        var snapshot = await reader.ReadAsync(selected.Snapshot.Header.Id, token);
        token.ThrowIfCancellationRequested();
        if (snapshot.CatalogIdentity != selected.Snapshot.CatalogIdentity) throw new QuotationException(QuotationError.InvalidInputs);
        int index = Orders.IndexOf(selected); Orders[index] = Row(snapshot); SelectedOrder = Orders[index]; OnPropertyChanged(nameof(HasChanges));
    });
    [RelayCommand] private void Remove() { if (SelectedOrder is not null) Orders.Remove(SelectedOrder); SelectedOrder = Orders.FirstOrDefault(); OnPropertyChanged(nameof(HasOrders)); OnPropertyChanged(nameof(HasChanges)); }
    [RelayCommand] private void Cancel() { SearchCommand.Cancel(); LinkCommand.Cancel(); RefreshCommand.Cancel(); }
    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return; IsBusy = true; Status = strings["ImportWorking"];
        try { await action(); if (Status == strings["ImportWorking"]) Status = string.Empty; }
        catch (OperationCanceledException) { Status = strings["ImportCanceled"]; }
        catch (ErpException exception) { Status = strings["ErpError" + exception.Code]; }
        catch (QuotationException exception) { Status = strings["QuotationError" + exception.Code]; }
        catch (OrderException exception) { Status = strings["OrderError" + exception.Code]; }
        finally { IsBusy = false; }
    }
}
