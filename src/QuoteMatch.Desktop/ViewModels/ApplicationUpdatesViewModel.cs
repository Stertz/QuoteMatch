using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuoteMatch.Application.Updating;
using QuoteMatch.Desktop.Localization;

namespace QuoteMatch.Desktop.ViewModels;

public enum ApplicationUpdateState { Unavailable, Idle, Checking, Downloading, Ready, Failed, Restarting }

public partial class ApplicationUpdatesViewModel : ObservableObject, IDisposable
{
    private readonly IApplicationUpdater? _updater;
    private readonly Func<CancellationToken, Task<bool>> _canRestart;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private ApplicationUpdate? _update;
    private bool _disposed;
    [ObservableProperty] private ApplicationUpdateState _state;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _availableVersion = string.Empty;
    [ObservableProperty] private string _releaseNotes = string.Empty;

    public ApplicationUpdatesViewModel(Localizer strings, IApplicationUpdater? updater, Func<CancellationToken, Task<bool>> canRestart)
    {
        Strings = strings; _updater = updater; _canRestart = canRestart;
        CurrentVersion = updater?.CurrentVersion ?? typeof(ApplicationUpdatesViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        State = updater?.IsInstalled == true ? ApplicationUpdateState.Idle : ApplicationUpdateState.Unavailable;
        if (updater?.PendingUpdate is { } pending) SetReady(pending);
    }

    public Localizer Strings { get; }
    public string CurrentVersion { get; }
    public string VersionLabel => Strings.Format("ApplicationVersion", CurrentVersion);
    public bool IsBusy => State is ApplicationUpdateState.Checking or ApplicationUpdateState.Downloading or ApplicationUpdateState.Restarting;
    public bool IsDownloading => State == ApplicationUpdateState.Downloading;
    public bool IsRestarting => State == ApplicationUpdateState.Restarting;
    public bool ShowNotice => IsBusy || IsReady;
    public bool IsReady => State == ApplicationUpdateState.Ready;
    public bool CanCheck => !_disposed && State != ApplicationUpdateState.Unavailable && !IsBusy;
    public bool CanInstall => !_disposed && IsReady;
    public string StateLabel => Strings["ApplicationUpdate" + State];
    public string NotesText => string.IsNullOrWhiteSpace(ReleaseNotes) ? Strings["ApplicationUpdateNoNotes"] : ReleaseNotes;
    public event Action? RestartRequested;

    partial void OnStateChanged(ApplicationUpdateState value)
    {
        foreach (string property in new[] { nameof(IsBusy), nameof(IsDownloading), nameof(IsRestarting), nameof(IsReady), nameof(ShowNotice), nameof(CanCheck), nameof(CanInstall), nameof(StateLabel) }) OnPropertyChanged(property);
        CheckCommand.NotifyCanExecuteChanged(); InstallCommand.NotifyCanExecuteChanged();
    }
    partial void OnReleaseNotesChanged(string value) => OnPropertyChanged(nameof(NotesText));

    public void RefreshLabels()
    {
        foreach (string property in new[] { nameof(Strings), nameof(VersionLabel), nameof(StateLabel), nameof(NotesText) }) OnPropertyChanged(property);
        Status = string.Empty;
    }

    public Task InitializeAsync() => IsReady || !CanCheck ? Task.CompletedTask : CheckCommand.ExecuteAsync(null);

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        if (!CanCheck || _updater is null) return;
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(15)); _operation = operation;
        State = ApplicationUpdateState.Checking; Status = string.Empty;
        try
        {
            ApplicationUpdate? update = await _updater.CheckAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (update is null)
            {
                if (_updater.PendingUpdate is { } pending) SetReady(pending);
                else State = ApplicationUpdateState.Idle;
                return;
            }
            AvailableVersion = update.Version; ReleaseNotes = update.ReleaseNotes;
            State = ApplicationUpdateState.Downloading; Progress = 0;
            var progress = new Progress<int>(value =>
            {
                if (!_disposed && _operation == operation && State == ApplicationUpdateState.Downloading) Progress = Math.Clamp(value, 0, 100);
            });
            await _updater.DownloadAsync(update, progress, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            SetReady(update);
        }
        catch (OperationCanceledException) { RestoreState(ApplicationUpdateState.Idle); }
        catch (ApplicationUpdateException) { RestoreState(); Status = Strings["ApplicationUpdateFailed"]; }
        finally { _operation = null; }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (!CanInstall || _updater is null || _update is null || RestartRequested is null) return;
        State = ApplicationUpdateState.Restarting; Status = string.Empty;
        try
        {
            if (!await _canRestart(_lifetime.Token)) { State = ApplicationUpdateState.Ready; Status = Strings["ApplicationUpdateRestartBlocked"]; return; }
            _lifetime.Token.ThrowIfCancellationRequested();
            _updater.PrepareRestart(_update);
            RestartRequested.Invoke();
        }
        catch (OperationCanceledException) { State = ApplicationUpdateState.Ready; }
        catch (ApplicationUpdateException) { State = ApplicationUpdateState.Ready; Status = Strings["ApplicationUpdateFailed"]; }
    }

    [RelayCommand] private void Cancel() => _operation?.Cancel();
    private void SetReady(ApplicationUpdate update)
    {
        _update = update; AvailableVersion = update.Version; ReleaseNotes = update.ReleaseNotes; Progress = 100; State = ApplicationUpdateState.Ready;
    }
    private void RestoreState(ApplicationUpdateState fallback = ApplicationUpdateState.Failed)
    {
        if (_updater?.PendingUpdate is { } pending) SetReady(pending);
        else State = fallback;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _lifetime.Dispose();
    }
}
