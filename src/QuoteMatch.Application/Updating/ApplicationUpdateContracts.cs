namespace QuoteMatch.Application.Updating;

public sealed record ApplicationUpdate(string Version, string ReleaseNotes);

public interface IApplicationUpdater
{
    string CurrentVersion { get; }
    bool IsInstalled { get; }
    ApplicationUpdate? PendingUpdate { get; }
    Task<ApplicationUpdate?> CheckAsync(CancellationToken cancellationToken);
    Task DownloadAsync(ApplicationUpdate update, IProgress<int> progress, CancellationToken cancellationToken);
    void PrepareRestart(ApplicationUpdate update);
}

public sealed class ApplicationUpdateException() : Exception("The application update could not be completed.");
