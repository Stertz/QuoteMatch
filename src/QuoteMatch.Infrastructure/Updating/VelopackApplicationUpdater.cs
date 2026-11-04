using System.Reflection;
using QuoteMatch.Application.Updating;
using Velopack;
using Velopack.Sources;

namespace QuoteMatch.Infrastructure.Updating;

public sealed class VelopackApplicationUpdater : IApplicationUpdater
{
    public static string RepositoryUrl => typeof(VelopackApplicationUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "UpdateRepositoryUrl").Value!;
    private readonly UpdateManager _manager;
    private UpdateInfo? _available;

    public VelopackApplicationUpdater() : this(new UpdateManager(new GithubSource(RepositoryUrl, null, prerelease: false))) { }

    public VelopackApplicationUpdater(UpdateManager manager) => _manager = manager;

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ??
        typeof(VelopackApplicationUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public bool IsInstalled => _manager.IsInstalled;
    public ApplicationUpdate? PendingUpdate => _manager.UpdatePendingRestart is { } pending && IsApplicationAsset(pending) ? Describe(pending) : null;

    public async Task<ApplicationUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsInstalled) return null;
            UpdateInfo? available = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (available is not null && (!IsApplicationAsset(available.TargetFullRelease) || available.IsDowngrade)) throw new ApplicationUpdateException();
            _available = available;
            return available is null ? null : Describe(available.TargetFullRelease);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { throw new ApplicationUpdateException(); }
    }

    public async Task DownloadAsync(ApplicationUpdate update, IProgress<int> progress, CancellationToken cancellationToken)
    {
        try
        {
            if (_available is null || Describe(_available.TargetFullRelease) != update) throw new ApplicationUpdateException();
            await _manager.DownloadUpdatesAsync(_available, progress.Report, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (PendingUpdate?.Version != update.Version) throw new ApplicationUpdateException();
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { throw new ApplicationUpdateException(); }
    }

    public void PrepareRestart(ApplicationUpdate update)
    {
        try
        {
            if (_manager.UpdatePendingRestart is not { } pending || !IsApplicationAsset(pending) || Describe(pending).Version != update.Version) throw new ApplicationUpdateException();
            _manager.WaitExitThenApplyUpdates(pending, restart: true);
        }
        catch (Exception) { throw new ApplicationUpdateException(); }
    }

    private bool IsApplicationAsset(VelopackAsset asset) => asset.PackageId == _manager.AppId && asset.Type == VelopackAssetType.Full;
    private static ApplicationUpdate Describe(VelopackAsset asset) => new(asset.Version.ToString(),
        (asset.NotesMarkdown ?? string.Empty) is { Length: > 32000 } notes ? notes[..32000] : asset.NotesMarkdown ?? string.Empty);
}
