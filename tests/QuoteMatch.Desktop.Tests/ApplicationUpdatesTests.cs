using Avalonia.Controls;
using QuoteMatch.Application.Updating;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.Preferences;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Infrastructure.Persistence;
using QuoteMatch.Testing;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task AutomaticDownload_ShowsVersionAndNotesAndWaitsForTheInstallAction()
    {
        await session.Session.Dispatch(async () =>
        {
            var backend = new SyntheticUpdater();
            using var model = new ApplicationUpdatesViewModel(new Localizer(), backend, _ => Task.FromResult(true));
            int restarts = 0; model.RestartRequested += () => restarts++;
            await model.InitializeAsync();
            Assert.Equal(ApplicationUpdateState.Ready, model.State); Assert.Equal(1, backend.Downloads);
            Assert.Equal("0.1.1", model.AvailableVersion); Assert.Equal("Synthetic changes", model.NotesText);
            Assert.Equal(100, model.Progress); Assert.Equal(0, backend.Restarts); Assert.Equal(0, restarts);
            var window = new ApplicationUpdatesWindow { DataContext = model }; window.Show();
            try
            {
                Assert.Equal("Synthetic changes", window.FindControl<TextBlock>("UpdateReleaseNotes")!.Text);
                Assert.True(window.FindControl<Button>("InstallApplicationUpdate")!.IsVisible);
                Assert.Contains("0.1.0", model.VersionLabel);
                await model.InstallCommand.ExecuteAsync(null);
                Assert.Equal(1, backend.Restarts); Assert.Equal(1, restarts); Assert.False(model.CanInstall);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UninstalledSourceBuild_DoesNotUseTheNetwork()
    {
        await session.Session.Dispatch(async () =>
        {
            var backend = new SyntheticUpdater { IsInstalled = false };
            using var model = new ApplicationUpdatesViewModel(new Localizer(), backend, _ => Task.FromResult(true));
            await model.InitializeAsync();
            Assert.Equal(ApplicationUpdateState.Unavailable, model.State); Assert.Equal(0, backend.Checks); Assert.False(model.CanCheck);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FailedChecksKeepAnAlreadyDownloadedUpdateAndBlockedRestartDoesNotApplyIt()
    {
        await session.Session.Dispatch(async () =>
        {
            var backend = new SyntheticUpdater { PendingUpdate = new("0.1.1", "Saved notes"), FailCheck = true };
            bool canRestart = false;
            using var model = new ApplicationUpdatesViewModel(new Localizer(), backend, _ => Task.FromResult(canRestart));
            model.RestartRequested += () => { };
            await model.InitializeAsync(); Assert.Equal(0, backend.Checks);
            await model.CheckCommand.ExecuteAsync(null);
            Assert.Equal(ApplicationUpdateState.Ready, model.State); Assert.Equal("Saved notes", model.NotesText); Assert.NotEmpty(model.Status);
            await model.InstallCommand.ExecuteAsync(null);
            Assert.Equal(0, backend.Restarts); Assert.Equal(ApplicationUpdateState.Ready, model.State);
            Assert.Equal(model.Strings["ApplicationUpdateRestartBlocked"], model.Status);
            backend.FailRestart = true; canRestart = true;
            await model.InstallCommand.ExecuteAsync(null);
            Assert.Equal(ApplicationUpdateState.Ready, model.State); Assert.Equal(model.Strings["ApplicationUpdateFailed"], model.Status);
            model.Strings.SetCulture("en-US"); model.RefreshLabels();
            Assert.Equal("Update ready to install", model.StateLabel);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrClosingDuringDownloadNeverInstalls(bool close)
    {
        await session.Session.Dispatch(async () =>
        {
            var backend = new SyntheticUpdater { WaitForCancellation = true };
            using var model = new ApplicationUpdatesViewModel(new Localizer(), backend, _ => Task.FromResult(true));
            Task operation = model.InitializeAsync(); await backend.DownloadStarted.Task;
            Assert.Equal(ApplicationUpdateState.Downloading, model.State); Assert.False(model.CanInstall);
            if (close) model.Dispose(); else model.CancelCommand.Execute(null);
            await operation;
            Assert.Equal(0, backend.Restarts); Assert.False(model.IsReady); Assert.False(model.IsBusy);
            Assert.Equal(!close, model.CanCheck);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WorkspaceFooterOpensUpdatesAndAnUnfinishedQuotationBlocksRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.Update.Desktop", Guid.NewGuid().ToString("N"));
        try
        {
            await session.Session.Dispatch(async () =>
            {
                var store = new SqliteImportStore(directory); await store.InitializeAsync();
                var backend = new SyntheticUpdater { PendingUpdate = new("0.1.1", "Saved notes") };
                using var model = new WorkspaceViewModel(new PreferenceStore(Path.Combine(directory, "preferences.json")), store, backend);
                var window = new WorkspaceWindow { DataContext = model, InitializeOnOpen = false }; window.Show();
                try
                {
                    Assert.Equal("Versão 0.1.0", window.FindControl<Button>("ApplicationVersionButton")!.Content);
                    Assert.True(window.FindControl<TextBlock>("ApplicationUpdateNotice")!.IsVisible);
                    await model.NewQuotationCommand.ExecuteAsync(null);
                    await model.Updates.InstallCommand.ExecuteAsync(null);
                    Assert.Equal(0, backend.Restarts); Assert.True(window.IsVisible);
                    Assert.Equal(model.Strings["ApplicationUpdateRestartBlocked"], model.Updates.Status);
                }
                finally { window.Close(); }
                return true;
            }, TestContext.Current.CancellationToken);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartIsBlockedByPendingOrUnknownOrdersAndAllowedAfterReconciliation(bool unknown)
    {
        await session.Session.Dispatch(async () =>
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using var fixture = await DocumentCombinationFixture.CreateAsync(token, new CombinationDocument("Synthetic supplier", 10, 5));
            var preview = await fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, token);
            var selection = PurchaseSynchronization.Select(preview, preview.Differences.Select(change => change.Id).ToArray(), false);
            var plan = await fixture.Writes.PreviewAsync(selection, token, date: new(2026, 11, 3));
            await fixture.Store.BeginSynchronizationAsync(plan, token);
            plan = plan with { Allocation = await fixture.ReserveAsync(plan, token) };
            await fixture.Store.SaveSynchronizationAllocationAsync(plan, token);
            if (unknown) await fixture.Store.CompleteSynchronizationAsync(new(plan.OperationId, OrderOperationState.Unknown, null), token);
            var backend = new SyntheticUpdater { PendingUpdate = new("0.1.1", "Saved notes") };
            using var model = new WorkspaceViewModel(new PreferenceStore(Path.Combine(fixture.DirectoryPath, "preferences.json")), fixture.Store, backend);
            model.CanRestartPresentation = () => true;
            model.Updates.RestartRequested += () => { };
            await model.Updates.InstallCommand.ExecuteAsync(null);
            Assert.Equal(0, backend.Restarts); Assert.Equal(ApplicationUpdateState.Ready, model.Updates.State);
            await fixture.Store.CompleteSynchronizationAsync(new(plan.OperationId, OrderOperationState.Committed, plan.TargetId), token);
            await model.Updates.InstallCommand.ExecuteAsync(null);
            Assert.Equal(1, backend.Restarts); Assert.False(model.IsInteractionEnabled);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnsavedSettingsAndTemplateChangesBlockRestartUntilSavedOrDiscarded()
    {
        string directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.Update.Desktop", Guid.NewGuid().ToString("N"));
        try
        {
            await session.Session.Dispatch(async () =>
            {
                var store = new SqliteImportStore(directory); await store.InitializeAsync();
                var backend = new SyntheticUpdater { PendingUpdate = new("0.1.1", "Saved notes") };
                using var model = new WorkspaceViewModel(new PreferenceStore(Path.Combine(directory, "preferences.json")), store, backend);
                await model.InitializeAsync(); model.CanRestartPresentation = () => true; model.Updates.RestartRequested += () => { };
                Assert.False(model.Settings.HasUnsavedChanges); Assert.False(model.Templates.Editor.HasUnsavedChanges);
                model.Settings.OperationIndex = 1;
                Assert.False(model.Settings.HasUnsavedChanges);
                model.Settings.Server = "Synthetic server";
                await model.Updates.InstallCommand.ExecuteAsync(null); Assert.Equal(0, backend.Restarts);
                await model.Settings.SaveCommand.ExecuteAsync(null); Assert.False(model.Settings.HasUnsavedChanges);
                model.Templates.Editor.Name = "Unsaved synthetic template";
                await model.Updates.InstallCommand.ExecuteAsync(null); Assert.Equal(0, backend.Restarts);
                model.Templates.Editor.Load(new());
                await model.Updates.InstallCommand.ExecuteAsync(null); Assert.Equal(1, backend.Restarts);
                return true;
            }, TestContext.Current.CancellationToken);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class SyntheticUpdater : IApplicationUpdater
    {
        public string CurrentVersion => "0.1.0";
        public bool IsInstalled { get; init; } = true;
        public ApplicationUpdate? PendingUpdate { get; set; }
        public bool FailCheck { get; init; }
        public bool FailRestart { get; set; }
        public bool WaitForCancellation { get; init; }
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public int Restarts { get; private set; }
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ApplicationUpdate?> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++;
            if (FailCheck) throw new ApplicationUpdateException();
            return Task.FromResult<ApplicationUpdate?>(new("0.1.1", "Synthetic changes"));
        }
        public async Task DownloadAsync(ApplicationUpdate update, IProgress<int> progress, CancellationToken cancellationToken)
        {
            Downloads++; DownloadStarted.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            progress.Report(100); PendingUpdate = update;
        }
        public void PrepareRestart(ApplicationUpdate update)
        {
            if (FailRestart) throw new ApplicationUpdateException();
            Assert.Equal(PendingUpdate, update); Restarts++;
        }
    }
}
