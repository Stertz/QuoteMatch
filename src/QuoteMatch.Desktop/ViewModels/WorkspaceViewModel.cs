using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuoteMatch.Application.Importing;
using QuoteMatch.Application.Quotations;
using QuoteMatch.Desktop.Preferences;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Erp;
using QuoteMatch.Infrastructure.Pdf;
using QuoteMatch.Infrastructure.Persistence;
using QuoteMatch.Infrastructure.Spreadsheets;

namespace QuoteMatch.Desktop.ViewModels;

public sealed record QuotationListRow(QuotationSummary Summary, string StateLabel, string Detail)
{
    public string Name => Summary.Quotation.Name;
}
public sealed record ProposalListRow(ProposalSummary Summary, string Total, string Detail, string Quantities = "")
{
    public string Supplier => Summary.SupplierName;
    public string Marker => Supplier.Length > 0 ? Supplier[..1].ToUpperInvariant() : "?";
    public int ItemCount => Summary.ItemCount;
}
public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly QuoteMatch.Application.Analysis.SavedProductMappingService _automaticMappings;
    private readonly SqliteImportStore _store;
    private List<QuotationSummary> _allQuotations = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSupplierOverview))] private int _quotationTabIndex;
    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private bool _isLegacyChecking;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private Quotation? _currentQuotation;
    [ObservableProperty] private string _quotationName = string.Empty;
    [ObservableProperty] private string _quotationNotes = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSupplierOverview))] private bool _isEditingDetails;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _confirmationTitle = string.Empty;
    [ObservableProperty] private string _confirmationText = string.Empty;
    [ObservableProperty] private int _confirmationKind;
    [ObservableProperty] private ProposalListRow? _selectedProposal;
    [ObservableProperty] private QuotationListRow? _selectedQuotation;

    public WorkspaceViewModel(PreferenceStore preferences, SqliteImportStore store, QuoteMatch.Application.Updating.IApplicationUpdater? updater = null)
    {
        Appearance = new(preferences, []); _store = store;
        Updates = new(Strings, updater, CanRestartForUpdateAsync);
        Updates.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ApplicationUpdatesViewModel.IsRestarting)) { OnPropertyChanged(nameof(IsInteractionEnabled)); OnPropertyChanged(nameof(CanNavigate)); } };
        Templates = new(Strings, store, new PdfImportService(new PdfPigTextExtractor(), store, new ExcelWorkbookReader()));
        ProtectedErpCredentials credentials = new(store.DataDirectory);
        ErpGateway gateway = new(store, credentials);
        _automaticMappings = new(store, store, gateway);
        var orderReader = new ErpPurchaseOrderReader(store, credentials);
        Synchronization = new(Strings, store, orderReader, orderReader, new ErpPurchaseSynchronizationWriter(store, credentials), store, preferences);
        OrderInputs = new(Strings, store, orderReader);
        Orders = new(Strings, store, store, store, orderReader, new ErpPurchaseOrderWriter(store, credentials));
        Settings = new(Strings, store, gateway); Analysis = new(Strings, store, gateway, store, preferences, OrderInputs, Orders); Purchase = new(Strings, store, Synchronization, Analysis);
        Analysis.QuotationUpdated += quotation =>
        {
            if (CurrentQuotation?.Id == quotation.Id && !IsEditingDetails) CurrentQuotation = quotation;
            _allQuotations = _allQuotations.Select(summary => summary.Quotation.Id == quotation.Id ? summary with { Quotation = quotation } : summary).ToList();
            RefreshLists();
        };
        OrderInputs.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(QuotationOrderInputsViewModel.IsBusy)) OnPropertyChanged(nameof(CanSaveQuotation)); };
        Orders.PropertyChanged += (_, args) => { if (args.PropertyName is nameof(PurchaseOrdersViewModel.IsBusy) or nameof(PurchaseOrdersViewModel.IsPreviewing)) { OnPropertyChanged(nameof(CanNavigate)); OnPropertyChanged(nameof(CanImport)); } };
        Analysis.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(QuotationAnalysisViewModel.IsBusy)) { OnPropertyChanged(nameof(CanNavigate)); OnPropertyChanged(nameof(CanImport)); } };
        Purchase.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(PurchasePreparationViewModel.IsBusy)) { OnPropertyChanged(nameof(CanNavigate)); OnPropertyChanged(nameof(CanImport)); } };
        Appearance.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MainWindowViewModel.SelectedLanguageIndex)) RefreshLabels(); };
    }
    public QuotationAnalysisViewModel Analysis { get; }
    public PurchasePreparationViewModel Purchase { get; }
    public PurchaseOrdersViewModel Orders { get; }
    public PurchaseSynchronizationViewModel Synchronization { get; }
    public QuotationOrderInputsViewModel OrderInputs { get; }
    public bool CanSaveQuotation => !IsBusy && !OrderInputs.IsBusy;
    public MainWindowViewModel Appearance { get; }
    public Localization.Localizer Strings => Appearance.Strings;
    public ImportWorkspaceViewModel Templates { get; }
    public IntegrationSettingsViewModel Settings { get; }
    public ObservableCollection<QuotationListRow> Quotations { get; } = [];
    public ObservableCollection<QuotationListRow> RecentQuotations { get; } = [];
    public ObservableCollection<ProposalListRow> Proposals { get; } = [];
    public bool IsHome => PageIndex == 0;
    public bool IsQuotations => PageIndex == 1;
    public bool IsTemplates => PageIndex == 2;
    public bool IsSettings => PageIndex == 3;
    public bool IsOrderTools => IsQuotations && IsLegacyChecking;
    public bool IsQuotationArea => IsQuotations && !IsLegacyChecking;
    public bool IsQuotationOpen => CurrentQuotation is not null && IsQuotationArea;
    public bool IsQuotationList => IsQuotationArea && !IsQuotationOpen;
    public string WorkspaceHeading => Strings["NavQuotations"];
    public bool IsSupplierOverview => !IsEditingDetails && QuotationTabIndex == 0;
    public bool IsFinalized => CurrentQuotation?.State == QuotationState.Finalized;
    public bool CanEditQuotation => CurrentQuotation?.State == QuotationState.Draft && !IsEditingDetails;
    public bool CanNavigate => !Updates.IsRestarting && !IsEditingDetails && !OrderInputs.IsBusy && !Analysis.IsBusy && !Purchase.IsBusy && !Orders.IsBusy && !Orders.IsPreviewing && !IsBusy;
    public bool CanImport => CanEditQuotation && CurrentQuotation?.Revision > 0 && !Analysis.IsBusy && !Purchase.IsBusy && !Orders.IsBusy;
    public bool HasConfirmation => ConfirmationKind != 0;
    public bool HasNoRecentQuotations => RecentQuotations.Count == 0;
    public bool CanOpenQuotation => SelectedQuotation is not null;
    public bool CanOpenProposal => SelectedProposal is not null;
    public string QuotationEmptyText => Strings[_allQuotations.Count == 0 ? "NoQuotationsHelp" : "NoMatchingQuotationsHelp"];
    partial void OnSelectedQuotationChanged(QuotationListRow? value) => OnPropertyChanged(nameof(CanOpenQuotation));
    partial void OnSelectedProposalChanged(ProposalListRow? value) => OnPropertyChanged(nameof(CanOpenProposal));
    public bool HasNoQuotations => Quotations.Count == 0;
    public bool HasNoProposals => Proposals.Count == 0;
    public string QuotationHeading => CurrentQuotation?.Revision == 0 ? Strings["NewQuotation"] : CurrentQuotation?.Name ?? string.Empty;
    public string CurrentState => Strings[IsFinalized ? "QuotationFinalized" : "QuotationDraft"];
    partial void OnPageIndexChanged(int value) { CurrentQuotation = null; IsLegacyChecking = false; ConfirmationKind = 0; NotifyNavigation(); }
    partial void OnIsLegacyCheckingChanged(bool value) => NotifyNavigation();
    partial void OnSearchTextChanged(string value) => RefreshLists();
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanNavigate)); OnPropertyChanged(nameof(CanSaveQuotation)); OnPropertyChanged(nameof(IsInteractionEnabled)); }
    partial void OnIsEditingDetailsChanged(bool value) { OnPropertyChanged(nameof(CanNavigate)); OnPropertyChanged(nameof(CanEditQuotation)); OnPropertyChanged(nameof(CanImport)); }
    partial void OnConfirmationKindChanged(int value) => OnPropertyChanged(nameof(HasConfirmation));
    partial void OnCurrentQuotationChanged(Quotation? value)
    {
        QuotationName = value?.Name ?? string.Empty; QuotationNotes = value?.Notes ?? string.Empty;
        NotifyNavigation();
    }
    private void NotifyNavigation()
    {
        foreach (string name in new[] { nameof(IsHome), nameof(IsQuotations), nameof(IsOrderTools), nameof(IsQuotationArea), nameof(WorkspaceHeading), nameof(IsTemplates), nameof(IsSettings), nameof(IsQuotationOpen), nameof(IsQuotationList), nameof(IsFinalized), nameof(CanEditQuotation), nameof(CanImport), nameof(CurrentState), nameof(QuotationHeading) }) OnPropertyChanged(name);
    }
    public Task InitializeAsync() => RunAsync(async () =>
    {
        await _store.InitializeAsync(); await ReloadAsync(); await Templates.InitializeAsync(); await Settings.LoadAsync();
    });
    private async Task ReloadAsync()
    {
        _allQuotations = (await _store.GetQuotationsAsync()).ToList();
        if (CurrentQuotation?.Revision > 0 && !IsEditingDetails) CurrentQuotation = _allQuotations.SingleOrDefault(row => row.Quotation.Id == CurrentQuotation.Id)?.Quotation;
        RefreshLists();
        Proposals.Clear(); SelectedProposal = null;
        if (CurrentQuotation?.Revision > 0) { await Analysis.LoadAsync(CurrentQuotation.Id); await Purchase.LoadAsync(CurrentQuotation.Id); }
        else { Analysis.Clear(); Purchase.Clear(); }
        if (CurrentQuotation?.Revision > 0)
            foreach (ProposalSummary proposal in await _store.GetQuotationProposalsAsync(CurrentQuotation.Id))
                Proposals.Add(new(proposal, Strings.Money(proposal.ReportedItemTotal), Strings.Format("ProposalDetail", proposal.ItemCount, proposal.CreatedAtUtc.ToLocalTime().ToString("d", Strings.Culture)), Analysis.ProposalQuantities(proposal.Id)));
        OnPropertyChanged(nameof(HasNoProposals));
    }
    private void RefreshLists()
    {
        QuotationListRow Row(QuotationSummary summary) => new(summary, Strings[summary.Quotation.State == QuotationState.Finalized ? "QuotationFinalized" : "QuotationDraft"],
            Strings.Format("QuotationListDetail", summary.ProposalCount, summary.Quotation.UpdatedAtUtc.ToLocalTime().ToString("d", Strings.Culture)));
        Quotations.Clear(); RecentQuotations.Clear();
        foreach (QuotationSummary summary in _allQuotations.Where(summary => Strings.Culture.CompareInfo.IndexOf(summary.Quotation.Name, SearchText, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0)) Quotations.Add(Row(summary));
        foreach (QuotationSummary summary in _allQuotations.Take(3)) RecentQuotations.Add(Row(summary));
        OnPropertyChanged(nameof(HasNoQuotations)); OnPropertyChanged(nameof(HasNoRecentQuotations)); OnPropertyChanged(nameof(QuotationEmptyText));
    }
    private void RefreshLabels() { Templates.Strings.SetCulture(Strings.Culture.Name); Settings.RefreshLabels(); Analysis.RefreshLabels(); Purchase.RefreshLabels(); Orders.RefreshLabels(); Updates.RefreshLabels(); RefreshLists(); NotifyNavigation(); OnPropertyChanged(nameof(Strings)); }
    [RelayCommand] private void GoToQuotations() => PageIndex = 1;
    [RelayCommand] private Task GoToCheckingAsync() { GoToQuotations(); return Task.CompletedTask; }
    [RelayCommand] private async Task OpenOrderToolsAsync() { PageIndex = 1; IsLegacyChecking = true; await Orders.RefreshReferencesCommand.ExecuteAsync(null); }
    [RelayCommand] private void CloseOrderTools() => IsLegacyChecking = false;
    [RelayCommand] private void GoHome() { IsEditingDetails = false; PageIndex = 0; }
    [RelayCommand] private void BackToQuotations() { IsEditingDetails = false; CurrentQuotation = null; ConfirmationKind = 0; }
    [RelayCommand]
    private async Task NewQuotationAsync()
    {
        await OrderInputs.LoadAsync(null);
        QuotationTabIndex = 0; PageIndex = 1; IsLegacyChecking = false; Proposals.Clear(); CurrentQuotation = new(Guid.CreateVersion7(), string.Empty, string.Empty, DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0);
        IsEditingDetails = true; Status = string.Empty; OnPropertyChanged(nameof(HasNoProposals));
    }
    [RelayCommand]
    private Task OpenQuotationAsync(QuotationListRow? row) => RunAsync(async () =>
    {
        row ??= SelectedQuotation; if (row is null) return;
        QuotationTabIndex = 0; PageIndex = 1; IsLegacyChecking = false; CurrentQuotation = row.Summary.Quotation; IsEditingDetails = false; await ReloadAsync();
    });
    [RelayCommand] private void ShowAnalysis() => QuotationTabIndex = 1;
    [RelayCommand]
    private Task EditQuotationAsync() => RunAsync(async () =>
    {
        if (!CanEditQuotation) return;
        var input = await _store.GetAnalysisInputAsync(CurrentQuotation!.Id);
        CurrentQuotation = input.Quotation;
        await OrderInputs.LoadAsync(CurrentQuotation.Id, input.State.Demands.Any(demand => demand.Origin == Domain.Analysis.DemandOrigin.Manual));
        IsEditingDetails = true;
    });
    [RelayCommand]
    private void CancelEdit()
    {
        if (OrderInputs.IsBusy) { OrderInputs.CancelCommand.Execute(null); return; }
        IsEditingDetails = false;
        if (CurrentQuotation?.Revision == 0) CurrentQuotation = null;
        else { QuotationName = CurrentQuotation?.Name ?? string.Empty; QuotationNotes = CurrentQuotation?.Notes ?? string.Empty; }
    }
    [RelayCommand]
    private Task SaveQuotationAsync() => RunAsync(async () =>
    {
        if (CurrentQuotation is null) return;
        if (OrderInputs.IsBusy) return;
        var candidate = CurrentQuotation with { Name = QuotationName, Notes = QuotationNotes, UpdatedAtUtc = DateTime.UtcNow };
        CurrentQuotation = CurrentQuotation.Revision == 0 || OrderInputs.HasChanges || OrderInputs.ReplaceManualDemand
            ? await _store.SaveQuotationWorkspaceAsync(candidate, OrderInputs.Build(candidate.Id), OrderInputs.ReplaceManualDemand)
            : await _store.SaveQuotationAsync(candidate);
        IsEditingDetails = false; await ReloadAsync(); Status = Strings["QuotationSaved"];
    });
    [RelayCommand]
    private void RequestDelete()
    {
        if (CurrentQuotation?.Revision is not > 0) return;
        ConfirmationTitle = Strings["DeleteQuotation"]; ConfirmationText = Strings.Format("DeleteQuotationHelp", CurrentQuotation.Name, Proposals.Count); ConfirmationKind = 1;
    }
    [RelayCommand]
    private void RequestFinalize()
    {
        if (!CanImport) return;
        ConfirmationTitle = Strings["FinalizeQuotation"]; ConfirmationText = Strings["FinalizeQuotationHelp"]; ConfirmationKind = 2;
    }
    [RelayCommand] private void CancelConfirmation() => ConfirmationKind = 0;
    [RelayCommand]
    private Task ConfirmActionAsync() => RunAsync(async () =>
    {
        if (CurrentQuotation is null) return;
        if (ConfirmationKind == 1) { await _store.DeleteQuotationAsync(CurrentQuotation.Id, CurrentQuotation.Revision); CurrentQuotation = null; }
        else if (ConfirmationKind == 2) CurrentQuotation = await _store.SaveQuotationAsync(CurrentQuotation with { State = QuotationState.Finalized, UpdatedAtUtc = DateTime.UtcNow });
        ConfirmationKind = 0; await ReloadAsync();
    });
    [RelayCommand]
    private Task ReopenQuotationAsync() => RunAsync(async () =>
    {
        if (CurrentQuotation?.State != QuotationState.Finalized) return;
        CurrentQuotation = await _store.SaveQuotationAsync(CurrentQuotation with { State = QuotationState.Draft, UpdatedAtUtc = DateTime.UtcNow }); await ReloadAsync();
    });
    public ImportWorkspaceViewModel CreateImport()
    {
        if (!CanImport) throw new QuotationException(QuotationError.ReadOnly);
        return new(Strings, _store, new PdfImportService(new PdfPigTextExtractor(), _store, new ExcelWorkbookReader()), CurrentQuotation!.Id, CurrentQuotation.Name, _automaticMappings);
    }
    public ImportWorkspaceViewModel CreateProposalReader() => new(Strings, _store, new PdfImportService(new PdfPigTextExtractor(), _store, new ExcelWorkbookReader()), CurrentQuotation?.Id, CurrentQuotation?.Name ?? string.Empty);
    public Task RefreshAsync() => RunAsync(ReloadAsync);
    public Task RefreshPurchaseAsync() => CurrentQuotation?.Revision > 0 ? Purchase.LoadAsync(CurrentQuotation.Id) : Task.CompletedTask;
    public async Task OpenPurchasePreparationAsync(Guid quotationId)
    {
        var summary = _allQuotations.SingleOrDefault(row => row.Quotation.Id == quotationId);
        if (summary is null) return;
        await OpenQuotationAsync(new(summary, "", "")); QuotationTabIndex = 2;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return; IsBusy = true; Status = string.Empty;
        try { await action(); }
        catch (QuotationException exception) { Status = Strings["QuotationError" + exception.Code]; }
        catch (Domain.Analysis.AnalysisException exception) { Status = Strings["AnalysisError" + exception.Code]; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { Status = Strings["ImportStorageError"]; }
        finally { IsBusy = false; }
    }
    public void Dispose() { Updates.Dispose(); Templates.Dispose(); OrderInputs.CancelCommand.Execute(null); Synchronization.CancelCommand.Execute(null); Analysis.CancelCommand.Execute(null); Purchase.CancelCommand.Execute(null); Orders.CancelCommand.Execute(null); Settings.CancelConnectionTestCommand.Execute(null); Settings.SessionPassword = string.Empty; Settings.WritePassword = string.Empty; }
}







