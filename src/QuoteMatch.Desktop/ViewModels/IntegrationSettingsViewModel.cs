using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Infrastructure.Erp;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.ViewModels;

public partial class IntegrationSettingsViewModel(Localizer strings, SqliteImportStore store, ErpGateway gateway) : ObservableObject
{
    private static readonly ErpOperation[] SupportedOperations = [ErpOperation.FindProduct, ErpOperation.GetEquivalentProducts, ErpOperation.FindProductById, ErpOperation.SearchProducts,
        ErpOperation.SearchPurchaseOrders, ErpOperation.ReadPurchaseOrder, ErpOperation.FindBuyingSupplier, ErpOperation.PreviewPurchaseFinancials,
        ErpOperation.ReservePurchaseSynchronization, ErpOperation.CreatePurchaseSynchronization, ErpOperation.UpdatePurchaseSynchronization, ErpOperation.ReconcilePurchaseSynchronization,
        ErpOperation.CreatePurchaseOrder, ErpOperation.UpdatePurchaseOrder, ErpOperation.ReconcilePurchaseOperation];
    private readonly Dictionary<ErpOperation, string> _queries = [];
    private string? _savedSnapshot;
    public bool HasUnsavedChanges => SessionPassword.Length > 0 || WritePassword.Length > 0 || _savedSnapshot is not null && Snapshot(Build()) != _savedSnapshot;
    private static string Snapshot(ErpConfiguration configuration) => JsonSerializer.Serialize(configuration with
    {
        Queries = configuration.Queries.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value)
    });
    [ObservableProperty] private bool _trustServerCertificate;
    [ObservableProperty] private bool _encrypt = true;
    [ObservableProperty] private int _connectionTimeoutSeconds = 30;
    [ObservableProperty] private string _server = string.Empty;
    [ObservableProperty] private string _database = string.Empty;
    [ObservableProperty] private int _authenticationIndex;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _sessionPassword = string.Empty;
    [ObservableProperty] private string _writeUserName = string.Empty;
    [ObservableProperty] private string _writePassword = string.Empty;
    [ObservableProperty] private bool _useReadConnectionForWrites;
    [ObservableProperty] private int? _writeOperatorCode;
    [ObservableProperty] private bool _allowProductReadScripts;
    [ObservableProperty] private int _operationIndex;
    [ObservableProperty] private string _queryText = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _diagnosticDetail = string.Empty;
    public bool HasDiagnosticDetail => !string.IsNullOrWhiteSpace(DiagnosticDetail);
    partial void OnDiagnosticDetailChanged(string value) => OnPropertyChanged(nameof(HasDiagnosticDetail));
    [ObservableProperty] private bool _isBusy;
    public Localizer Strings => strings;
    public bool IsSqlLogin => AuthenticationIndex == 1;
    private ErpOperation SelectedOperation => SupportedOperations[OperationIndex];
    public string QueryContract => Strings["QueryContract" + SelectedOperation];
    private IReadOnlyList<QueryOperationOption>? _operationOptions;
    public IReadOnlyList<QueryOperationOption> OperationOptions => _operationOptions ??= SupportedOperations.Select(operation => new QueryOperationOption(operation, strings)).ToArray();
    public bool IsProductReadOperation => SelectedOperation is ErpOperation.FindProduct or ErpOperation.FindProductById or ErpOperation.GetEquivalentProducts or ErpOperation.SearchProducts;
    partial void OnAuthenticationIndexChanged(int value) => OnPropertyChanged(nameof(IsSqlLogin));
    partial void OnOperationIndexChanging(int value) => _queries[SelectedOperation] = QueryText;
    partial void OnOperationIndexChanged(int value)
    {
        QueryText = _queries.GetValueOrDefault(SelectedOperation, string.Empty);
        OnPropertyChanged(nameof(QueryContract));
        OnPropertyChanged(nameof(IsProductReadOperation));
    }
    public async Task LoadQueryFileAsync(string path)
    {
        try { QueryText = await File.ReadAllTextAsync(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Status = Strings["ImportStorageError"]; }
    }
    public void RefreshLabels() { foreach (var option in OperationOptions) option.RefreshLabels(); OnPropertyChanged(nameof(QueryContract)); }
    public async Task LoadAsync(CancellationToken token = default)
    {
        ErpConfiguration config = await store.GetErpConfigurationAsync(token);
        TrustServerCertificate = config.TrustServerCertificate; Server = config.Server; Database = config.Database; AuthenticationIndex = (int)config.Authentication; UserName = config.UserName;
        Encrypt = config.Encrypt; ConnectionTimeoutSeconds = config.ConnectionTimeoutSeconds > 0 ? config.ConnectionTimeoutSeconds : 30;
        WriteUserName = config.WriteUserName;
        UseReadConnectionForWrites = config.UseReadConnectionForWrites;
        WriteOperatorCode = config.WriteOperatorCode;
        AllowProductReadScripts = config.ProductReadMode == ProductReadMode.Script;
        _queries.Clear(); foreach (var pair in config.Queries) _queries[pair.Key] = pair.Value;
        QueryText = _queries.GetValueOrDefault(SelectedOperation, string.Empty);
        _savedSnapshot = Snapshot(Build());
    }
    private ErpConfiguration Build()
    {
        _queries[SelectedOperation] = QueryText;
        return new()
        {
            TrustServerCertificate = TrustServerCertificate,
            Encrypt = Encrypt,
            ConnectionTimeoutSeconds = ConnectionTimeoutSeconds,
            Server = Server.Trim(),
            Database = Database.Trim(),
            Authentication = (ErpAuthentication)AuthenticationIndex,
            UserName = UserName.Trim(),
            WriteUserName = WriteUserName.Trim(),
            UseReadConnectionForWrites = UseReadConnectionForWrites,
            WriteOperatorCode = WriteOperatorCode,
            ProductReadMode = AllowProductReadScripts ? ProductReadMode.Script : ProductReadMode.SingleQuery,
            Queries = new(_queries)
        };
    }
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy) return; IsBusy = true; DiagnosticDetail = string.Empty;
        try
        {
            ErpConfiguration config = Build();
            if (config.Authentication == ErpAuthentication.SqlLogin && SessionPassword.Length > 0) await gateway.SavePasswordAsync(config, SessionPassword, CancellationToken.None);
            if (!config.UseReadConnectionForWrites && config.WriteUserName.Length > 0 && WritePassword.Length > 0) await gateway.SavePasswordAsync(PurchaseOrderScripts.WriteConfiguration(config), WritePassword, CancellationToken.None);
            await store.SaveErpConfigurationAsync(config); SessionPassword = string.Empty; WritePassword = string.Empty; _savedSnapshot = Snapshot(config); Status = Strings["SettingsSaved"];
        }
        catch (ErpException exception) { Status = Strings["ErpError" + exception.Code]; }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { Status = Strings["ImportStorageError"]; }
        finally { IsBusy = false; }
    }
    [RelayCommand(IncludeCancelCommand = true)]
    private Task TestConnectionAsync(CancellationToken token) => TestCoreAsync(false, token);
    [RelayCommand(IncludeCancelCommand = true)]
    private Task TestSavedConnectionAsync(CancellationToken token) => TestCoreAsync(true, token);
    [RelayCommand] private void CancelConnectionTest() { TestConnectionCommand.Cancel(); TestSavedConnectionCommand.Cancel(); }
    private async Task TestCoreAsync(bool saved, CancellationToken token)
    {
        if (IsBusy) return; IsBusy = true; Status = Strings["TestingConnection"]; DiagnosticDetail = string.Empty;
        try
        {
            var result = saved ? await gateway.TestSavedDetailedAsync(token) : await gateway.TestDetailedAsync(Build(), SessionPassword, token);
            Status = Strings[result.Connected ? saved ? "SavedConnectionSucceeded" : "ConnectionSucceeded" : "ConnectionFailed"];
            if (result.Failure is { } failure) DiagnosticDetail = Strings.Format("ConnectionDiagnostic", Strings["ErpStage" + failure.Stage],
                failure.SqlErrorNumber?.ToString(Strings.Culture) ?? "—", failure.NativeErrorCode?.ToString(Strings.Culture) ?? "—");
        }
        catch (ErpException exception) { Status = Strings["ErpError" + exception.Code]; }
        catch (OperationCanceledException) { Status = Strings["ImportCanceled"]; }
        finally { IsBusy = false; }
    }
}




