using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;

using QuoteMatch.Application.Importing;
using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Infrastructure.Persistence;

public sealed partial class SqliteImportStore(string directory) : ITemplateRepository, IProposalRepository
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => InitializeCoreAsync(cancellationToken), cancellationToken);

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        await using ImportDbContext context = CreateContext();
        if ((await context.Database.GetPendingMigrationsAsync(cancellationToken)).Any()
            && (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).Any())
        {
            string backups = Path.Combine(directory, "backups"); Directory.CreateDirectory(backups);
            string backup = Path.Combine(backups, $"imports-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
            await context.Database.OpenConnectionAsync(cancellationToken);
            await using Microsoft.Data.Sqlite.SqliteConnection destination = new(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = backup }.ToString());
            await destination.OpenAsync(cancellationToken);
            ((Microsoft.Data.Sqlite.SqliteConnection)context.Database.GetDbConnection()).BackupDatabase(destination);
            await context.Database.CloseConnectionAsync();
        }
        await context.Database.MigrateAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PdfTemplate>> GetLatestTemplatesAsync(CancellationToken cancellationToken = default)
    {
        await using ImportDbContext context = CreateContext();
        List<TemplateVersionRow> rows = await context.Templates.AsNoTracking().ToListAsync(cancellationToken);
        return rows.GroupBy(row => row.Id).Select(group => group.MaxBy(row => row.Version)!)
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Select(row => JsonSerializer.Deserialize<PdfTemplate>(row.DataJson, JsonOptions)!).ToArray();
    }

    public async Task<PdfTemplate> SaveTemplateAsync(PdfTemplate template, CancellationToken cancellationToken = default)
    {
        TemplateExtraction.Validate(template);
        await using ImportDbContext context = CreateContext();
        Guid id = template.Id == Guid.Empty ? Guid.CreateVersion7() : template.Id;
        int latest = await context.Templates.Where(row => row.Id == id).Select(row => (int?)row.Version).MaxAsync(cancellationToken) ?? 0;
        if (template.Version != latest)
        {
            throw new ImportException(ImportErrorCode.StorageConflict, "The template was changed by another operation. Reload it before saving.");
        }

        PdfTemplate saved = template with { Id = id, Version = latest + 1 };
        context.Templates.Add(new TemplateVersionRow { Id = id, Version = saved.Version, Name = saved.Name, DataJson = JsonSerializer.Serialize(saved, JsonOptions) });
        await context.SaveChangesAsync(cancellationToken);
        return saved;
    }

    public Task SaveProposalAsync(SavedProposal proposal, byte[] originalPdf, CancellationToken cancellationToken = default) =>
        Task.Run(() => SaveProposalCoreAsync(proposal, originalPdf, cancellationToken), cancellationToken);

    private async Task SaveProposalCoreAsync(SavedProposal proposal, byte[] originalPdf, CancellationToken cancellationToken)
    {
        await using ImportDbContext context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (proposal.QuotationId is Guid quotationId)
        {
            QuotationRow? quotation = await context.Quotations.SingleOrDefaultAsync(row => row.Id == quotationId, cancellationToken);
            if (quotation is null || quotation.State != Domain.Quotations.QuotationState.Draft)
                throw new Domain.Quotations.QuotationException(Domain.Quotations.QuotationError.ReadOnly);
            quotation.UpdatedAtUtc = proposal.CreatedAtUtc; quotation.Revision++;
        }
        if (await context.Proposals.AnyAsync(row => row.Id == proposal.Id, cancellationToken))
        {
            throw new ImportException(ImportErrorCode.StorageConflict, "This preview has already been confirmed.");
        }

        context.Proposals.Add(new ProposalRow
        {
            Id = proposal.Id,
            QuotationId = proposal.QuotationId,
            CreatedAtUtc = proposal.CreatedAtUtc,
            FileName = proposal.FileName,
            SupplierName = proposal.Template.SupplierName,
            TemplateName = proposal.Template.Name,
            ItemCount = proposal.IncludedItemCount,
            ReportedItemTotalText = proposal.ReportedItemTotal.ToString("G29", CultureInfo.InvariantCulture),
            SourceSha256 = proposal.SourceSha256,
            DataJson = JsonSerializer.Serialize(proposal, JsonOptions),
            OriginalPdf = originalPdf
        });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProposalSummary>> GetProposalsAsync(CancellationToken cancellationToken = default)
    {
        await using ImportDbContext context = CreateContext();
        var rows = await context.Proposals.AsNoTracking().OrderByDescending(row => row.CreatedAtUtc)
            .Select(row => new { row.Id, row.CreatedAtUtc, row.FileName, row.SupplierName, row.TemplateName, row.ItemCount, row.ReportedItemTotalText })
            .ToListAsync(cancellationToken);
        return rows.Select(row => new ProposalSummary(row.Id, row.CreatedAtUtc, row.FileName, row.SupplierName,
            row.TemplateName, row.ItemCount, decimal.Parse(row.ReportedItemTotalText, CultureInfo.InvariantCulture))).ToArray();
    }

    public async Task<SavedProposal?> GetProposalAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using ImportDbContext context = CreateContext();
        var row = await context.Proposals.AsNoTracking().Where(row => row.Id == id).Select(row => new { row.DataJson, row.QuotationId }).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : JsonSerializer.Deserialize<SavedProposal>(row.DataJson, JsonOptions)! with { QuotationId = row.QuotationId };
    }

    public async Task<byte[]?> GetOriginalPdfAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using ImportDbContext context = CreateContext();
        return await context.Proposals.AsNoTracking().Where(row => row.Id == id).Select(row => row.OriginalPdf).SingleOrDefaultAsync(cancellationToken);
    }

    private ImportDbContext CreateContext() => new(new DbContextOptionsBuilder<ImportDbContext>()
        .UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "imports.db") }.ToString()).Options);
}






