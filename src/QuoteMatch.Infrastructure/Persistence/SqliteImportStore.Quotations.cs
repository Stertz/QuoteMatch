using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuoteMatch.Application.Quotations;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Infrastructure.Persistence;

public sealed partial class SqliteImportStore : IQuotationRepository
{
    public async Task<IReadOnlyList<QuotationSummary>> GetQuotationsAsync(CancellationToken cancellationToken = default)
    {
        await using ImportDbContext context = CreateContext();
        var rows = await context.Quotations.AsNoTracking().OrderByDescending(row => row.UpdatedAtUtc)
            .Select(row => new { Row = row, Count = context.Proposals.Count(proposal => proposal.QuotationId == row.Id) }).ToListAsync(cancellationToken);
        return rows.Select(row => new QuotationSummary(ToQuotation(row.Row), row.Count)).ToArray();
    }
    public async Task<Quotation> SaveQuotationAsync(Quotation quotation, CancellationToken cancellationToken = default)
    {

        await using ImportDbContext context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        QuotationRow? row = await context.Quotations.SingleOrDefaultAsync(row => row.Id == quotation.Id, cancellationToken);
        bool hasProposals = quotation.State == QuotationState.Finalized && await context.Proposals.AnyAsync(proposal => proposal.QuotationId == quotation.Id, cancellationToken);
        QuotationRules.ValidateSave(row is null ? null : ToQuotation(row), quotation, hasProposals);
        if (row is null)
        {
            row = new QuotationRow { Id = quotation.Id, CreatedAtUtc = quotation.CreatedAtUtc };
            context.Quotations.Add(row);
        }
        row.Name = quotation.Name.Trim(); row.Notes = quotation.Notes.Trim(); row.State = quotation.State;
        row.UpdatedAtUtc = quotation.UpdatedAtUtc; row.Revision++;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToQuotation(row);
    }
    public Task DeleteQuotationAsync(Guid id, int expectedRevision, CancellationToken cancellationToken = default) =>
        Task.Run(() => DeleteQuotationCoreAsync(id, expectedRevision, cancellationToken), cancellationToken);

    private async Task DeleteQuotationCoreAsync(Guid id, int expectedRevision, CancellationToken cancellationToken)
    {
        await using ImportDbContext context = CreateContext();
        QuotationRow? row = await context.Quotations.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (row is null || row.Revision != expectedRevision) throw new QuotationException(QuotationError.Conflict);
        if (await context.OrderReconciliations.AnyAsync(history => history.QuotationId == id, cancellationToken)
            || await context.OrderOperations.AnyAsync(operation => operation.QuotationId == id, cancellationToken)
            || await context.SynchronizationOperations.AnyAsync(operation => operation.QuotationId == id, cancellationToken))
            throw new QuotationException(QuotationError.OrderHistory);
        context.Quotations.Remove(row);
        await context.SaveChangesAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<ProposalSummary>> GetQuotationProposalsAsync(Guid quotationId, CancellationToken cancellationToken = default)
    {
        await using ImportDbContext context = CreateContext();
        var rows = await context.Proposals.AsNoTracking().Where(row => row.QuotationId == quotationId).OrderBy(row => row.CreatedAtUtc)
            .Select(row => new { row.Id, row.CreatedAtUtc, row.FileName, row.SupplierName, row.TemplateName, row.ItemCount, row.ReportedItemTotalText }).ToListAsync(cancellationToken);
        return rows.Select(row => new ProposalSummary(row.Id, row.CreatedAtUtc, row.FileName, row.SupplierName, row.TemplateName,
            row.ItemCount, decimal.Parse(row.ReportedItemTotalText, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
    }
    public async Task<ErpConfiguration> GetErpConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await using ImportDbContext context = CreateContext();
        string? json = await context.Configuration.AsNoTracking().Where(row => row.Key == "erp").Select(row => row.DataJson).SingleOrDefaultAsync(cancellationToken);
        return json is null ? new() : JsonSerializer.Deserialize<ErpConfiguration>(json, JsonOptions) ?? new();
    }
    public async Task SaveErpConfigurationAsync(ErpConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await using ImportDbContext context = CreateContext();
        ConfigurationRow? row = await context.Configuration.SingleOrDefaultAsync(row => row.Key == "erp", cancellationToken);
        if (row is null) { row = new() { Key = "erp" }; context.Configuration.Add(row); }
        row.DataJson = JsonSerializer.Serialize(configuration, JsonOptions);
        await context.SaveChangesAsync(cancellationToken);
    }
    private static Quotation ToQuotation(QuotationRow row) => new(row.Id, row.Name, row.Notes, row.CreatedAtUtc, row.UpdatedAtUtc, row.State, row.Revision);
}



