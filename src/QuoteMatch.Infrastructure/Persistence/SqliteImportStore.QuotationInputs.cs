using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Infrastructure.Persistence;

public sealed partial class SqliteImportStore
{
    public Task<QuotationInputs> GetQuotationInputsAsync(Guid quotationId, int? version = null, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await using ImportDbContext context = CreateContext();
        if (!await context.Quotations.AnyAsync(row => row.Id == quotationId, cancellationToken)) throw new QuotationException(QuotationError.Conflict);
        return await ReadQuotationInputsAsync(context, quotationId, version, cancellationToken);
    }, cancellationToken);

    private static async Task<QuotationInputs> ReadQuotationInputsAsync(ImportDbContext context, Guid quotationId, int? version, CancellationToken token)
    {
        if (version == 0) return QuotationInputs.Legacy(quotationId);
        var query = context.QuotationInputs.AsNoTracking().Where(row => row.QuotationId == quotationId);
        if (version is not null) query = query.Where(row => row.Version == version);
        string? json = await query.OrderByDescending(row => row.Version).Select(row => row.DataJson).FirstOrDefaultAsync(token);
        if (json is null && version is not null) throw new QuotationException(QuotationError.Conflict);
        return json is null ? QuotationInputs.Legacy(quotationId) : JsonSerializer.Deserialize<QuotationInputs>(json, JsonOptions)!;
    }

    public Task<QuotationInputs> SaveQuotationInputsAsync(QuotationInputs inputs, int expectedQuotationRevision, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        QuotationInputRules.Validate(inputs);
        await using ImportDbContext context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        QuotationRow? quotation = await context.Quotations.SingleOrDefaultAsync(row => row.Id == inputs.QuotationId, cancellationToken);
        if (quotation is null || quotation.Revision != expectedQuotationRevision) throw new QuotationException(QuotationError.Conflict);
        if (quotation.State != QuotationState.Draft) throw new QuotationException(QuotationError.ReadOnly);
        int latest = await context.QuotationInputs.Where(row => row.QuotationId == inputs.QuotationId)
            .Select(row => (int?)row.Version).MaxAsync(cancellationToken) ?? 0;
        if (inputs.Version != latest) throw new QuotationException(QuotationError.Conflict);
        var saved = inputs with { Version = latest + 1, CreatedAtUtc = DateTime.UtcNow };
        quotation.Revision++; quotation.UpdatedAtUtc = saved.CreatedAtUtc;
        context.QuotationInputs.Add(new() { QuotationId = saved.QuotationId, Version = saved.Version, DataJson = JsonSerializer.Serialize(saved, JsonOptions) });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }, cancellationToken);
}
