using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Infrastructure.Persistence;

public sealed partial class SqliteImportStore
{
    public Task<Quotation> SaveQuotationWorkspaceAsync(Quotation quotation, QuotationInputs inputs, bool replaceManualDemand, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        QuotationInputRules.Validate(inputs);
        if (inputs.QuotationId != quotation.Id || quotation.State != QuotationState.Draft) throw new QuotationException(QuotationError.InvalidInputs);
        await using ImportDbContext context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        QuotationRow? row = await context.Quotations.SingleOrDefaultAsync(row => row.Id == quotation.Id, cancellationToken);
        QuotationRules.ValidateSave(row is null ? null : ToQuotation(row), quotation, false);
        if (row?.State == QuotationState.Finalized) throw new QuotationException(QuotationError.ReadOnly);
        var previous = await ReadQuotationInputsAsync(context, quotation.Id, null, cancellationToken);
        if (inputs.Version != previous.Version) throw new QuotationException(QuotationError.Conflict);
        DateTime now = DateTime.UtcNow;
        if (row is null)
        {
            row = new() { Id = quotation.Id, CreatedAtUtc = quotation.CreatedAtUtc };
            context.Quotations.Add(row);
        }
        row.Name = quotation.Name.Trim(); row.Notes = quotation.Notes.Trim(); row.UpdatedAtUtc = now; row.Revision++;
        var saved = inputs with { Version = previous.Version + 1, CreatedAtUtc = now };
        context.QuotationInputs.Add(new() { QuotationId = saved.QuotationId, Version = saved.Version, DataJson = JsonSerializer.Serialize(saved, JsonOptions) });
        await context.SaveChangesAsync(cancellationToken);
        var input = await ReadAnalysisInputAsync(context, quotation.Id, cancellationToken);
        var state = OrderInputRules.Apply(input, replaceManualDemand) with { Version = input.State.Version + 1, CreatedAtUtc = now };
        context.Analyses.Add(new() { QuotationId = quotation.Id, Version = state.Version, InputsRevision = row.Revision, CreatedAtUtc = now, DataJson = JsonSerializer.Serialize(state, JsonOptions) });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToQuotation(row);
    }, cancellationToken);
}
