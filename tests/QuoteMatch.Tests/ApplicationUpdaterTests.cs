using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using QuoteMatch.Application.Updating;
using QuoteMatch.Infrastructure.Updating;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace QuoteMatch.Tests;

public sealed class ApplicationUpdaterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.Update.Tests", Guid.NewGuid().ToString("N"));
    private const string PackageId = "io.github.stertz.quotematch";
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FileFeed_DownloadsAndRestoresTheVerifiedUpdateOrRejectsCorruption(bool corrupt, bool legacy)
    {
        string remote = Path.Combine(_directory, "remote"), local = Path.Combine(_directory, "packages");
        Directory.CreateDirectory(remote); Directory.CreateDirectory(local);
        string package = Path.Combine(remote, "synthetic-full.nupkg");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("synthetic.nuspec").Open());
            writer.Write($"<package><metadata><id>{PackageId}</id><version>0.1.1</version><authors>Synthetic</authors><description>Synthetic update</description><releaseNotes>New synthetic feature</releaseNotes></metadata></package>");
        }
        byte[] bytes = await File.ReadAllBytesAsync(package, Token);
        var asset = new
        {
            PackageId,
            Version = "0.1.1",
            Type = "Full",
            FileName = "synthetic-full.nupkg",
            Size = bytes.Length,
            SHA256 = Convert.ToHexString(SHA256.HashData(bytes)),
            SHA1 = Convert.ToHexString(SHA1.HashData(bytes)),
            NotesMarkdown = "New synthetic feature"
        };
        await File.WriteAllTextAsync(Path.Combine(remote, "releases.win-x64-stable.json"), JsonSerializer.Serialize(new { Assets = new[] { asset } }), Token);
        File.Copy(Path.Combine(remote, "releases.win-x64-stable.json"), Path.Combine(remote, "releases.win-x64-preview.json"));
        string currentVersion = legacy ? "0.1.0-alpha.2" : "0.1.0";
        string channel = legacy ? "win-x64-preview" : "win-x64-stable";
        var locator = new TestVelopackLocator(PackageId, currentVersion, local, _directory, _directory, Path.Combine(_directory, "synthetic-updater"), channel);
        var updater = new VelopackApplicationUpdater(new UpdateManager(new SimpleFileSource(new DirectoryInfo(remote)), locator: locator));
        Assert.True(updater.IsInstalled); Assert.Equal(currentVersion, updater.CurrentVersion); Assert.Null(updater.PendingUpdate);
        var update = await updater.CheckAsync(Token);
        Assert.Equal(new ApplicationUpdate("0.1.1", "New synthetic feature"), update);
        if (corrupt) await File.WriteAllTextAsync(package, "Corrupted synthetic package", Token);
        if (corrupt)
        {
            var failure = await Assert.ThrowsAsync<ApplicationUpdateException>(() => updater.DownloadAsync(update!, new Progress<int>(), Token));
            Assert.Null(failure.InnerException); Assert.Null(updater.PendingUpdate);
        }
        else
        {
            await updater.DownloadAsync(update!, new Progress<int>(), Token);
            Assert.Equal("0.1.1", updater.PendingUpdate?.Version);
            Assert.Equal("New synthetic feature", updater.PendingUpdate?.ReleaseNotes);
            var restarted = new VelopackApplicationUpdater(new UpdateManager(new SimpleFileSource(new DirectoryInfo(remote)), locator: locator));
            Assert.Equal(updater.PendingUpdate, restarted.PendingUpdate);
            await Assert.ThrowsAsync<ApplicationUpdateException>(() => restarted.DownloadAsync(update!, new Progress<int>(), Token));
            Assert.Throws<ApplicationUpdateException>(() => restarted.PrepareRestart(update! with { Version = "9.0.0" }));
        }
    }

    [Fact]
    public async Task AFeedForAnotherApplicationCannotAuthorizeADownload()
    {
        Directory.CreateDirectory(_directory);
        var asset = new { PackageId = "synthetic.other.application", Version = "0.1.1", Type = "Full", FileName = "other-full.nupkg", Size = 1, SHA256 = new string('0', 64), NotesMarkdown = "Synthetic other application" };
        await File.WriteAllTextAsync(Path.Combine(_directory, "releases.win-x64-stable.json"), JsonSerializer.Serialize(new { Assets = new[] { asset } }), Token);
        var locator = new TestVelopackLocator(PackageId, "0.1.0", _directory, _directory, _directory, Path.Combine(_directory, "synthetic-updater"), "win-x64-stable");
        var updater = new VelopackApplicationUpdater(new UpdateManager(new SimpleFileSource(new DirectoryInfo(_directory)), locator: locator));
        await Assert.ThrowsAsync<ApplicationUpdateException>(() => updater.CheckAsync(Token));
        Assert.Null(updater.PendingUpdate);
    }

    [Fact]
    public async Task AnUnrelatedChannelDoesNotOfferTheOtherArchitecture()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "releases.osx-arm64-stable.json"), "{\"Assets\":[]}", Token);
        var locator = new TestVelopackLocator(PackageId, "0.1.0", _directory, _directory, _directory, Path.Combine(_directory, "synthetic-updater"), "win-x64-stable");
        var updater = new VelopackApplicationUpdater(new UpdateManager(new SimpleFileSource(new DirectoryInfo(_directory)), locator: locator));
        Assert.Null(await updater.CheckAsync(Token));
        Assert.Null(updater.PendingUpdate);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
