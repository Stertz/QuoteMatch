using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuoteMatch.Application.Analysis;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Desktop.ViewModels;

public sealed record DemandDocumentChoice(Guid ProposalId, string Label);

public partial class ProductReviewViewModel : ObservableObject, IDisposable
{
    private readonly QuotationAnalysisViewModel _analysis;
    private readonly AnalysisInput _input;
    private bool _changingDemand;
    private bool _disposed;
    public ProductReviewViewModel(QuotationAnalysisViewModel analysis, AnalysisInput input, IReadOnlyList<ProductReviewRow> rows, Localizer strings, bool rememberMappings)
    {
        _analysis = analysis; _input = input; Rows = rows; Strings = strings; RememberMappings = rememberMappings;
        foreach (var proposal in input.Proposals.Where(proposal => proposal.IncludedItemCount > 0))
            Documents.Add(new(proposal.Id, proposal.Template.SupplierName + " · " + proposal.FileName));
        SelectedDocument = Documents.FirstOrDefault();
        foreach (var row in Rows) row.PropertyChanged += RowChanged;
        if (OrderInputs is not null) OrderInputs.PropertyChanged += ReferencesChanged;
        RefreshVisibleRows();
    }
    public Localizer Strings { get; }
    public IReadOnlyList<ProductReviewRow> Rows { get; }
    public ObservableCollection<ProductReviewRow> VisibleRows { get; } = [];
    public ObservableCollection<DemandDocumentChoice> Documents { get; } = [];
    public QuotationOrderInputsViewModel? OrderInputs => _analysis.OrderInputs;
    public PurchaseOrdersViewModel? OrderHistory => _analysis.OrderHistory;
    public bool HasReferenceTools => OrderInputs is not null;
    public bool HasHistoryTools => OrderHistory is not null;
    public bool HasReferenceOrders => OrderInputs?.HasOrders == true;
    public bool CanEdit => _analysis.CanEdit;
    public bool CanEditRows => CanEdit && !IsBusy;
    public bool CanRememberMappings => _analysis.CanRememberMapping;
    [ObservableProperty] private bool _rememberMappings;
    [ObservableProperty] private bool _onlyPending = true;
    [ObservableProperty] private bool _showDesiredQuantity;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsItemsTab))] private int _selectedTabIndex;
    public bool IsItemsTab => SelectedTabIndex == 0;
    [ObservableProperty] private DemandDocumentChoice? _selectedDocument;
    [ObservableProperty] private LinkedOrderRow? _selectedDemandOrder;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSave)), NotifyPropertyChangedFor(nameof(CanEditRows))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSave))] private bool _isSaved;
    [ObservableProperty] private string _status = string.Empty;
    public string Summary => Strings.Format("ReviewSelectionSummary", Rows.Count(row => row.IsSelected), Rows.Count(row => row.IsPending), Rows.Count);
    public bool HasVisibleRows => VisibleRows.Count > 0;
    public double GridMinimumWidth => ShowDesiredQuantity ? 1210 : 1060;
    public bool CanSave => CanEdit && !IsBusy && !IsSaved && OrderInputs?.IsBusy != true
        && (Rows.Any(row => row.IsSelected) || OrderInputs?.HasChanges == true || OrderInputs?.ReplaceManualDemand == true)
        && (OrderInputs?.PriceReferenceIndex != (int)QuotationPriceReference.LinkedOrder || HasReferenceOrders)
        && Rows.Where(row => row.IsSelected).All(row => !row.HasValidation);
    public async Task InitializeAsync()
    {
        if (OrderInputs is not null) await OrderInputs.LoadAsync(_input.Quotation.Id, _input.State.Demands.Any(demand => demand.Origin != DemandOrigin.LinkedOrder));
        if (OrderHistory is not null)
        {
            await OrderHistory.RefreshReferencesCommand.ExecuteAsync(null);
            OrderHistory.SelectedReference = OrderHistory.References.FirstOrDefault(row => row.Reference.QuotationId == _input.Quotation.Id);
            if (OrderHistory.SelectedReference is not null) await OrderHistory.LoadReferenceCommand.ExecuteAsync(null);
        }
        ReferencesChanged(this, new(nameof(QuotationOrderInputsViewModel.HasOrders)));
    }
    partial void OnOnlyPendingChanged(bool value) => RefreshVisibleRows();
    partial void OnShowDesiredQuantityChanged(bool value)
    {
        foreach (var row in Rows) row.ShowDesiredQuantity = value;
        OnPropertyChanged(nameof(GridMinimumWidth));
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not ProductReviewRow row) return;
        if (args.PropertyName == nameof(ProductReviewRow.Demand) && !_changingDemand)
        {
            _changingDemand = true;
            try
            {
                string? key = GroupKey(row);
                if (key is not null) foreach (var other in Rows.Where(other => !ReferenceEquals(other, row) && GroupKey(other) == key
                    && string.Equals(other.BaseUnit, row.BaseUnit, StringComparison.OrdinalIgnoreCase))) other.UseDemand(row.Demand, true);
            }
            finally { _changingDemand = false; }
        }
        if (args.PropertyName == nameof(ProductReviewRow.ConversionFactor) && !_changingDemand
            && Rows.Any(other => other.Demand?.DocumentSource is { } source && source.ProposalId == row.Proposal.Id && source.CaptureId == row.Item.CaptureId))
        {
            if (row.ConversionValidation.Length == 0) ApplyDocumentDemand(row);
        }
        if (args.PropertyName is nameof(ProductReviewRow.IsSelected) or nameof(ProductReviewRow.Validation) or nameof(ProductReviewRow.IsPending))
        {
            OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(Summary));
            if (OnlyPending && row.IsPending != VisibleRows.Contains(row)) RefreshVisibleRows();
        }
    }
    private void RefreshVisibleRows()
    {
        var rows = Rows.Where(row => !OnlyPending || row.IsPending).ToArray();
        var included = rows.ToHashSet();
        foreach (var row in VisibleRows.Where(row => !included.Contains(row)).ToArray()) VisibleRows.Remove(row);
        for (int index = 0; index < rows.Length; index++)
            if (index >= VisibleRows.Count) VisibleRows.Add(rows[index]);
            else if (VisibleRows[index] != rows[index]) { VisibleRows.Remove(rows[index]); VisibleRows.Insert(index, rows[index]); }
        OnPropertyChanged(nameof(HasVisibleRows)); OnPropertyChanged(nameof(Summary));
    }
    private void ReferencesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (OrderInputs is not null && (SelectedDemandOrder is null || !OrderInputs.Orders.Contains(SelectedDemandOrder))) SelectedDemandOrder = OrderInputs.Orders.FirstOrDefault();
        OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(HasReferenceOrders));
    }
    private QuotationInputs ReviewedInputs => OrderInputs?.Build(_input.Quotation.Id) ?? _input.Inputs;
    private string? GroupKey(ProductReviewRow row) => row.SelectedCandidate is not { } candidate ? null
        : ErpProductIdentity.GroupKey(candidate.Product, row.Proposal.Template.CodeKind == QuoteMatch.Domain.Importing.ProductCodeKind.Internal
            ? ProductGroupingVersion.ExactProductCode : ReviewedInputs.GroupingVersion);
    public ProductSearchViewModel CreateProductSearch(ProductReviewRow row)
    {
        var model = _analysis.CreateProductSearch();
        model.ModeIndex = row.Proposal.Template.CodeKind == QuoteMatch.Domain.Importing.ProductCodeKind.Internal ? 0 : 1;
        model.SearchText = model.ModeIndex == 0 ? row.Item.ProductCode : row.Item.Description;
        return model;
    }
    [RelayCommand] private void SelectAll() { foreach (var row in VisibleRows) row.IsSelected = row.SelectedCandidate is { Product.IsInactive: false }; }
    [RelayCommand] private void ClearSelection() { foreach (var row in Rows) row.IsSelected = false; }
    [RelayCommand]
    private void UseDocumentQuantities()
    {
        if (SelectedDocument is null || !CanEdit) return;
        var sources = Rows.Where(row => row.Proposal.Id == SelectedDocument.ProposalId && row.ConversionValidation.Length == 0).GroupBy(GroupKey).ToArray();
        if (sources.Any(group => group.Key is null || group.Count() != 1)) { Status = Strings["DemandDocumentAmbiguous"]; return; }
        foreach (var group in sources) ApplyDocumentDemand(group.Single());
        Status = Strings.Format("DemandQuantitiesApplied", sources.Length);
    }
    private void ApplyDocumentDemand(ProductReviewRow source)
    {
        string? key = GroupKey(source);
        if (key is null || source.SystemQuantity is not > 0 || source.ConversionFactor is not > 0) return;
        var demand = new GroupDemand(key, source.BaseUnit, source.SystemQuantity.Value)
        { Origin = DemandOrigin.Document, DocumentSource = new(source.Proposal.Id, source.Item.CaptureId, source.Proposal.SourceSha256, source.ConversionFactor.Value) };
        _changingDemand = true;
        try
        {
            foreach (var row in Rows.Where(row => GroupKey(row) == key && string.Equals(row.BaseUnit, source.BaseUnit, StringComparison.OrdinalIgnoreCase))) row.UseDemand(demand, true);
        }
        finally { _changingDemand = false; }
    }
    [RelayCommand]
    private void UseOrderQuantities()
    {
        if (SelectedDemandOrder is not { } selected || !CanEdit) return;
        try
        {
            var input = _input with { Inputs = ReviewedInputs };
            var confirmations = Rows.Where(row => row.ConversionValidation.Length == 0).Select(row => row.Confirmation()).ToArray();
            var state = ProductReviewRules.Apply(input, confirmations, DateTime.UtcNow, true, replaceManualDemand: true, allowEmpty: true);
            int changed = 0;
            foreach (var group in ImportedQuotationAnalysis.Calculate(input.Proposals, state))
            {
                var lines = OrderInputRules.MatchLines(group, input.Inputs).Where(line => line.Order.CatalogIdentity == selected.Snapshot.CatalogIdentity && line.Order.Header.Id == selected.Snapshot.Header.Id).ToArray();
                if (lines.Length == 0) continue;
                var demand = new GroupDemand(group.Key, group.BaseUnit, lines.Sum(line => line.Line.Quantity * line.Line.BaseUnitsPerUnit))
                { Origin = DemandOrigin.ReferenceOrder, Sources = lines.Select(line => new OrderDemandSource(line.Order.CatalogIdentity, line.Order.Header.Id, line.Order.Revision, line.Line.Id)).ToArray() };
                _changingDemand = true;
                try { foreach (var row in Rows.Where(row => GroupKey(row) == group.Key && row.BaseUnit.Equals(group.BaseUnit, StringComparison.OrdinalIgnoreCase))) row.UseDemand(demand, true); }
                finally { _changingDemand = false; }
                changed++;
            }
            Status = Strings.Format("DemandQuantitiesApplied", changed);
        }
        catch (AnalysisException exception) { Status = Strings["AnalysisError" + exception.Code]; }
        catch (OverflowException) { Status = Strings["BatchNumericLimit"]; }
    }
    private CancellationTokenSource? _operation;
    [RelayCommand] private void Cancel() => _operation?.Cancel();
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        using CancellationTokenSource operation = new(); _operation = operation; IsBusy = true; Status = Strings["ImportWorking"];
        try
        {
            var selected = Rows.Where(row => row.IsSelected).ToArray();
            var options = new ProductReviewOptions
            {
                AllowLinkedUpdates = true,
                Demands = selected.Where(row => row.HasDemandChange).Select(row => row.DemandChange()).ToArray(),
                Inputs = OrderInputs?.HasChanges == true ? ReviewedInputs : null,
                ReplaceManualDemand = OrderInputs?.ReplaceManualDemand == true
            };
            await _analysis.ConfirmProductReviewAsync(_input, selected.Select(row => row.Confirmation()).ToArray(), RememberMappings, operation.Token, options);
            IsSaved = true; Status = _analysis.Status;
        }
        catch (OperationCanceledException) { Status = Strings["ImportCanceled"]; }
        catch (AnalysisException exception) { Status = Strings["AnalysisError" + exception.Code]; }
        catch (ProductMappingException exception) { Status = Strings["MappingError" + exception.Code]; }
        catch (ErpException exception) { Status = Strings["ErpError" + exception.Code]; }
        catch (QuotationException exception) { Status = Strings["QuotationError" + exception.Code]; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { Status = Strings["ImportStorageError"]; }
        finally { _operation = null; IsBusy = false; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (OrderInputs is not null) OrderInputs.PropertyChanged -= ReferencesChanged;
        foreach (var row in Rows) row.PropertyChanged -= RowChanged;
        _analysis.SaveReviewDisplayPreferences(OnlyPending, ShowDesiredQuantity);
        GC.SuppressFinalize(this);
    }
}
