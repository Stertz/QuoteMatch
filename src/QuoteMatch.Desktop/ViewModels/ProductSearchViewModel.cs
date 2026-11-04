using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuoteMatch.Application.Integration;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Desktop.ViewModels;

public sealed record ProductSearchRow(ErpProductCandidate Product, string State)
{
    public string Code => Product.ProductCode;
    public string Name => Product.Name;
    public string BaseUnit => Product.BaseUnit ?? "—";
}

public partial class ProductSearchViewModel(Localizer strings, IErpProductSearch? search, TimeProvider? timeProvider = null) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _request;
    private bool _disposed;
    private bool _isConfirming;
    public bool CanEditQuery => !_isConfirming;
    public Localizer Strings => strings;
    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _includeInactive;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSelect))] private ProductSearchRow? _selectedProduct;
    public bool CanSelect => !IsBusy && !_isConfirming && SelectedProduct?.Product is { IsInactive: false, BaseUnit.Length: > 0 };
    public ObservableCollection<ProductSearchRow> Results { get; } = [];
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSelect));
    partial void OnModeIndexChanged(int value) => QueueSearch();
    partial void OnSearchTextChanged(string value) => QueueSearch();
    partial void OnIncludeInactiveChanged(bool value) => QueueSearch();

    private void QueueSearch()
    {
        CancelCurrent();
        Results.Clear(); SelectedProduct = null;
        Status = string.Empty;
        if (_disposed || string.IsNullOrWhiteSpace(SearchText)) return;
        if (ModeIndex != 0 && SearchText.Trim().Length < 3) { Status = Strings["ProductSearchMinimum"]; return; }
        _ = RunSearchAsync(true, CancellationToken.None);
    }

    private void CancelCurrent()
    {
        var previous = _request;
        _request = null;
        previous?.Cancel();
        IsBusy = false;
    }

    public void Dispose()
    {
        _disposed = true;
        CancelCurrent();
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    private void CancelSearch() { if (_isConfirming) return; CancelCurrent(); Status = Strings["ImportCanceled"]; }

    public async Task<ErpProductCandidate?> ConfirmSelectionAsync(CancellationToken token)
    {
        if (!CanSelect || search is null) return null;
        var selection = SelectedProduct!.Product;
        string code = selection.ProductCode;
        CancelCurrent();
        _isConfirming = true; OnPropertyChanged(nameof(CanEditQuery));
        IsBusy = true;
        try
        {
            var products = await search.FindByProductCodeAsync(code, token);
            var product = products.FirstOrDefault(product => product.CatalogIdentity == selection.CatalogIdentity && product.ProductCode == code && !product.IsInactive && !string.IsNullOrWhiteSpace(product.BaseUnit));
            if (product is null) { Status = Strings["ProductSelectionUnavailable"]; return null; }
            return product;
        }
        catch (OperationCanceledException) { Status = Strings["ImportCanceled"]; return null; }
        catch (ErpException exception) { Status = Strings["ErpError" + exception.Code]; return null; }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException) { Status = Strings["ImportStorageError"]; return null; }
        finally { _isConfirming = false; IsBusy = false; OnPropertyChanged(nameof(CanEditQuery)); }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SearchAsync(CancellationToken token) => RunSearchAsync(false, token);

    private async Task RunSearchAsync(bool debounce, CancellationToken token)
    {
        if (_disposed || _isConfirming) return;
        CancelCurrent();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        _request = request;
        string text = SearchText;
        int mode = ModeIndex;
        bool includeInactive = IncludeInactive;
        bool Current() => !_disposed && ReferenceEquals(_request, request) && !request.IsCancellationRequested;
        Results.Clear(); SelectedProduct = null;
        try
        {
            if (debounce) await Task.Delay(TimeSpan.FromMilliseconds(350), timeProvider ?? TimeProvider.System, request.Token);
            if (!Current()) return;
            IsBusy = true; Status = Strings["ImportWorking"];
            if (search is null) throw new ErpException(ErpError.NotConfigured);
            var products = mode == 0 ? await search.FindByProductCodeAsync(text, request.Token) : await search.SearchByNameAsync(text, request.Token);
            if (!Current()) return;
            foreach (var product in products.Where(product => includeInactive || !product.IsInactive).DistinctBy(product => (product.CatalogIdentity, product.ProductCode)))
                Results.Add(new(product, Strings[product.IsInactive ? "InactiveProduct" : "ActiveProduct"]));
            Status = Strings.Format(products.Count >= 100 ? "ProductSearchLimited" : "ProductSearchCount", Results.Count);
        }
        catch (OperationCanceledException) { if (ReferenceEquals(_request, request)) Status = Strings["ImportCanceled"]; }
        catch (ErpException exception) { if (Current()) Status = Strings["ErpError" + exception.Code]; }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException) { if (Current()) Status = Strings["ImportStorageError"]; }
        finally { if (ReferenceEquals(_request, request)) { _request = null; IsBusy = false; } }
    }
}
