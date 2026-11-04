using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Infrastructure.Persistence;

public sealed partial class SqliteImportStore : ISynchronizationOperationRepository
{
    public async Task<IReadOnlyList<SynchronizationOperation>> GetSynchronizationOperationsAsync(Guid quotationId, CancellationToken token)
    {
        await using var context = CreateContext();
        var rows = await context.SynchronizationOperations.AsNoTracking().Where(row => row.QuotationId == quotationId).OrderBy(row => row.CreatedAtUtc).ToArrayAsync(token);
        return rows.Select(row => new SynchronizationOperation(JsonSerializer.Deserialize<SynchronizationWritePlan>(row.DataJson, JsonOptions)!, row.State, row.CreatedAtUtc, row.OrderId)).ToArray();
    }
    public Task BeginSynchronizationAsync(SynchronizationWritePlan plan, CancellationToken token) => Task.Run(async () =>
    {
        SynchronizationWriteRules.Validate(plan, true);
        var preview = plan.Selection.Preview;
        var proposal = preview.Proposal;
        string catalog = proposal.Brand!.CatalogIdentity;
        string? targetId = preview.Target?.Header.Id;
        if (plan.Allocation is not null) throw new OrderException(OrderError.InvalidChange);
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(token);
        if (await context.SynchronizationOperations.AnyAsync(row => row.Id == plan.OperationId
            || (row.State == OrderOperationState.Pending || row.State == OrderOperationState.Unknown)
                && (row.QuotationId == proposal.QuotationId || targetId != null && row.CatalogIdentity == catalog && row.TargetId == targetId), token))
            throw new OrderException(OrderError.PendingOperation);
        if (await context.OrderOperations.AnyAsync(row => row.Id == plan.OperationId, token)) throw new OrderException(OrderError.PendingOperation);
        var legacy = await context.OrderOperations.AsNoTracking().Where(row => row.State == OrderOperationState.Pending || row.State == OrderOperationState.Unknown).ToArrayAsync(token);
        if (legacy.Any(row => row.QuotationId == proposal.QuotationId || JsonSerializer.Deserialize<OrderWritePlan>(row.DataJson, JsonOptions) is { } old
            && targetId != null && old.Reference.CatalogIdentity == catalog && old.ExpectedOrder?.Header.Id == targetId)) throw new OrderException(OrderError.PendingOperation);
        var preparationJson = await context.Purchases.AsNoTracking().Where(row => row.QuotationId == proposal.QuotationId && row.Version == proposal.PreparationVersion)
            .Select(row => row.DataJson).SingleOrDefaultAsync(token);
        if (preparationJson is null) throw new OrderException(OrderError.InvalidReference);
        var preparation = JsonSerializer.Deserialize<PurchasePreparation>(preparationJson, JsonOptions)!;
        if (!PurchaseSynchronization.Proposals(preparation).Any(candidate => JsonSerializer.Serialize(candidate, JsonOptions) == JsonSerializer.Serialize(proposal, JsonOptions)))
            throw new OrderException(OrderError.InvalidReference);
        int latest = await context.Purchases.Where(row => row.QuotationId == proposal.QuotationId).MaxAsync(row => row.Version, token);
        var current = await ReadAnalysisInputAsync(context, proposal.QuotationId, token);
        if (latest != preparation.Version || current.Quotation.Revision != preparation.InputsRevision || current.State.Version != preparation.AnalysisVersion
            || current.Inputs.Version != preparation.QuotationInputsVersion) throw new OrderException(OrderError.Conflict);
        try
        {
            var expectedDocuments = targetId is null ? CreationDocumentRules.Create(current, proposal, preview.CreationDocuments?.Choices) : null;
            if (!CreationDocumentRules.Matches(expectedDocuments, preview.CreationDocuments)) throw new OrderException(OrderError.InvalidReference);
        }
        catch (SynchronizationException) { throw new OrderException(OrderError.InvalidReference); }
        context.SynchronizationOperations.Add(new()
        {
            Id = plan.OperationId,
            QuotationId = proposal.QuotationId,
            State = OrderOperationState.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            DataJson = JsonSerializer.Serialize(plan, JsonOptions),
            CatalogIdentity = catalog,
            TargetId = targetId ?? ""
        });
        await context.SaveChangesAsync(token); await transaction.CommitAsync(token);
    }, token);

    public async Task SaveSynchronizationAllocationAsync(SynchronizationWritePlan plan, CancellationToken token)
    {
        SynchronizationWriteRules.Validate(plan, true);
        if (plan.Allocation is null) throw new OrderException(OrderError.InvalidChange);
        await using var context = CreateContext();
        var row = await context.SynchronizationOperations.SingleOrDefaultAsync(row => row.Id == plan.OperationId, token);
        if (row is null || row.State != OrderOperationState.Pending || row.TargetId.Length != 0
            || row.DataJson != JsonSerializer.Serialize(plan with { Allocation = null }, JsonOptions)) throw new OrderException(OrderError.InvalidChange);
        row.TargetId = plan.Allocation.OrderId; row.DataJson = JsonSerializer.Serialize(plan, JsonOptions);
        await context.SaveChangesAsync(token);
    }

    public async Task CompleteSynchronizationAsync(OrderWriteReceipt receipt, CancellationToken token)
    {
        await using var context = CreateContext();
        var row = await context.SynchronizationOperations.SingleOrDefaultAsync(row => row.Id == receipt.OperationId, token);
        if (row is null || row.State is not (OrderOperationState.Pending or OrderOperationState.Unknown)
            || receipt.State is not (OrderOperationState.Committed or OrderOperationState.Unknown)
            || receipt.State == OrderOperationState.Committed && receipt.OrderId != row.TargetId) throw new OrderException(OrderError.InvalidResult);
        row.State = receipt.State; row.OrderId = receipt.OrderId;
        await context.SaveChangesAsync(token);
    }
}
