using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using QuoteMatch.Application.Quotations;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Tests;

public sealed class QuotationPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.Quotation.Tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static Quotation NewQuotation(string name) => new(Guid.CreateVersion7(), name, "Notes", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0);
    private static SavedProposal Proposal(Guid quotationId) => new(Guid.CreateVersion7(), DateTime.UtcNow, "synthetic.pdf", "synthetic-hash",
        new PdfTemplate { Name = "Example", SupplierName = "Synthetic supplier" }, new([], "Synthetic/1"), new([], [], null, null), [])
    { QuotationId = quotationId };

    [Fact]
    public async Task QuotationLifecycle_IsPersistentScopedAndReadOnlyUntilExplicitlyReopened()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        Quotation first = await store.SaveQuotationAsync(NewQuotation("First"), Token);
        Quotation second = await store.SaveQuotationAsync(NewQuotation("Second"), Token);
        first = await store.SaveQuotationAsync(first with { Name = "Renamed", Notes = "Reviewed notes", CreatedAtUtc = DateTime.UnixEpoch, UpdatedAtUtc = DateTime.UtcNow }, Token);
        Assert.NotEqual(DateTime.UnixEpoch, first.CreatedAtUtc);
        Assert.Equal(2, first.Revision);
        Assert.Equal(QuotationError.Empty, (await Assert.ThrowsAsync<QuotationException>(() => store.SaveQuotationAsync(first with { State = QuotationState.Finalized }, Token))).Code);
        SavedProposal proposal = Proposal(first.Id);
        await store.SaveProposalAsync(proposal, [1, 2, 3], Token);
        Assert.Single(await store.GetQuotationProposalsAsync(first.Id, Token));
        Assert.Empty(await store.GetQuotationProposalsAsync(second.Id, Token));
        first = (await store.GetQuotationsAsync(Token)).Single(row => row.Quotation.Id == first.Id).Quotation;
        Quotation finalized = await store.SaveQuotationAsync(first with { State = QuotationState.Finalized }, Token);
        Assert.Equal(QuotationState.Finalized, finalized.State);
        Assert.Equal(QuotationError.ReadOnly, (await Assert.ThrowsAsync<QuotationException>(() => store.SaveQuotationAsync(finalized with { Name = "Forbidden" }, Token))).Code);
        Assert.Equal(QuotationError.ReadOnly, (await Assert.ThrowsAsync<QuotationException>(() => store.SaveProposalAsync(Proposal(first.Id), [9], Token))).Code);
        Assert.Equal(QuotationError.Conflict, (await Assert.ThrowsAsync<QuotationException>(() => store.SaveQuotationAsync(first, Token))).Code);
        SqliteImportStore restarted = new(_directory); await restarted.InitializeAsync(Token);
        Quotation loaded = (await restarted.GetQuotationsAsync(Token)).Single(row => row.Quotation.Id == first.Id).Quotation;
        Assert.Equal("Renamed", loaded.Name); Assert.Equal("Reviewed notes", loaded.Notes); Assert.Equal(QuotationState.Finalized, loaded.State);
        Quotation reopened = await restarted.SaveQuotationAsync(loaded with { State = QuotationState.Draft }, Token);
        await restarted.SaveProposalAsync(Proposal(reopened.Id), [4], Token);
        Assert.Equal(2, (await restarted.GetQuotationProposalsAsync(reopened.Id, Token)).Count);
        reopened = (await restarted.GetQuotationsAsync(Token)).Single(row => row.Quotation.Id == first.Id).Quotation;
        Assert.Equal(QuotationError.Conflict, (await Assert.ThrowsAsync<QuotationException>(() => restarted.DeleteQuotationAsync(first.Id, 1, Token))).Code);
        await restarted.DeleteQuotationAsync(first.Id, reopened.Revision, Token);
        Assert.Null(await restarted.GetProposalAsync(proposal.Id, Token));
        Assert.Null(await restarted.GetOriginalPdfAsync(proposal.Id, Token));
        Assert.Equal(second.Id, Assert.Single(await restarted.GetQuotationsAsync(Token)).Quotation.Id);
    }

    [Fact]
    public async Task PreviousSchema_RecoversUnscopedProposalsAndOriginalBytesWithoutChangingCaptureJson()
    {
        Directory.CreateDirectory(_directory);
        DbContextOptions<ImportDbContext> options = new DbContextOptionsBuilder<ImportDbContext>().UseSqlite($"Data Source={Path.Combine(_directory, "imports.db")}").Options;
        SavedProposal original = Proposal(Guid.NewGuid()) with { QuotationId = null };
        string json = JsonSerializer.Serialize(original, SqliteImportStore.JsonOptions);
        byte[] bytes = [10, 20, 30];
        await using (ImportDbContext context = new(options))
        {
            string initialId = context.GetService<IMigrationsAssembly>().Migrations.Keys.Single(id => id.EndsWith("InitialImportStorage", StringComparison.Ordinal));
            string initialName = context.GetService<IMigrationsIdGenerator>().GetName(initialId);
            await context.GetService<IMigrator>().MigrateAsync(initialName, Token);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Proposals (Id, CreatedAtUtc, FileName, SupplierName, TemplateName, ItemCount, ReportedItemTotalText, SourceSha256, DataJson, OriginalPdf) VALUES ({original.Id}, {original.CreatedAtUtc}, {original.FileName}, {original.Template.SupplierName}, {original.Template.Name}, {0}, {"0"}, {original.SourceSha256}, {json}, {bytes})", Token);
        }
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "backups"), "*.db"));
        QuotationSummary recovered = Assert.Single(await store.GetQuotationsAsync(Token));
        Assert.Equal(original.FileName, recovered.Quotation.Name); Assert.Equal(1, recovered.ProposalCount);
        SavedProposal loaded = (await store.GetProposalAsync(original.Id, Token))!;
        Assert.Equal(recovered.Quotation.Id, loaded.QuotationId);
        Assert.Equal(bytes, await store.GetOriginalPdfAsync(original.Id, Token));
        Assert.Equal(original.SourceSha256, loaded.SourceSha256); Assert.Equal(original.Template, loaded.Template);
        Assert.Single(await store.GetQuotationProposalsAsync(recovered.Quotation.Id, Token));
    }

    [Fact]
    public async Task ErpMetadataAndNamedQueries_PersistIndependentlyFromLocalQuotations()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        ErpConfiguration config = new()
        {
            Server = "synthetic-server",
            Database = "Example",
            Authentication = ErpAuthentication.SqlLogin,
            UserName = "reader",
            Queries = new() { [ErpOperation.FindProduct] = "SELECT @SupplierProductCode AS ProductCode", [ErpOperation.GetLastPurchase] = "SELECT @ProductCode AS ProductCode" }
        };
        await store.SaveErpConfigurationAsync(config, Token);
        ErpConfiguration loaded = await new SqliteImportStore(_directory).GetErpConfigurationAsync(Token);
        Assert.Equal(config.Server, loaded.Server); Assert.Equal(config.Database, loaded.Database); Assert.Equal(config.Authentication, loaded.Authentication);
        Assert.Equal(config.Queries[ErpOperation.FindProduct], loaded.Queries[ErpOperation.FindProduct]);
        Assert.Empty(await store.GetQuotationsAsync(Token));
        Assert.DoesNotContain("password", JsonSerializer.Serialize(loaded), StringComparison.OrdinalIgnoreCase);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}



