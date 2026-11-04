using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Infrastructure.Persistence;

public sealed partial class SqliteImportStore : IOrderWorkspaceRepository
{
    public async Task<OrderReconciliation?> GetReconciliationAsync(Guid quotationId, Guid proposalId, CancellationToken token)
    {
        await using var context = CreateContext();
        var json = await context.OrderReconciliations.AsNoTracking().Where(row => row.QuotationId == quotationId && row.ProposalId == proposalId)
            .OrderByDescending(row => row.Version).Select(row => row.DataJson).FirstOrDefaultAsync(token);
        return json is null ? null : JsonSerializer.Deserialize<OrderReconciliation>(json, JsonOptions);
    }

    public Task<OrderReconciliation> SaveReconciliationAsync(OrderReconciliation reconciliation, CancellationToken token) => Task.Run(async () =>
    {
        _ = PurchaseOrderRules.Compare(reconciliation.Reference, reconciliation.Orders, reconciliation.ManualPairs);
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(token);
        var reference = reconciliation.Reference;
        var savedReference = await context.Purchases.AsNoTracking().Where(row => row.QuotationId == reference.QuotationId
            && row.Version == reference.PreparationVersion).Select(row => row.DataJson).SingleOrDefaultAsync(token);
        if (savedReference is null || JsonSerializer.Serialize(PurchaseOrderReference.From(JsonSerializer.Deserialize<PurchasePreparation>(savedReference, JsonOptions)!, reference.ProposalId), JsonOptions)
            != JsonSerializer.Serialize(reference, JsonOptions)) throw new OrderException(OrderError.InvalidReference);
        int latest = await context.OrderReconciliations.Where(row => row.QuotationId == reference.QuotationId && row.ProposalId == reference.ProposalId)
            .Select(row => (int?)row.Version).MaxAsync(token) ?? 0;
        if (latest != reconciliation.Version) throw new OrderException(OrderError.Conflict);
        var saved = reconciliation with { Id = Guid.CreateVersion7(), Version = latest + 1, CreatedAtUtc = DateTime.UtcNow };
        context.OrderReconciliations.Add(new()
        {
            QuotationId = reference.QuotationId,
            ProposalId = reference.ProposalId,
            Version = saved.Version,
            DataJson = JsonSerializer.Serialize(saved, JsonOptions)
        });
        await context.SaveChangesAsync(token); await transaction.CommitAsync(token); return saved;
    }, token);

    public async Task<IReadOnlyList<OrderOperation>> GetOrderOperationsAsync(Guid quotationId, CancellationToken token)
    {
        await using var context = CreateContext();
        var rows = await context.OrderOperations.AsNoTracking().Where(row => row.QuotationId == quotationId).OrderBy(row => row.CreatedAtUtc).ToListAsync(token);
        return rows.Select(row => new OrderOperation(JsonSerializer.Deserialize<OrderWritePlan>(row.DataJson, JsonOptions)!, row.State, row.CreatedAtUtc, row.OrderId)).ToArray();
    }

    public Task BeginOrderOperationAsync(OrderWritePlan plan, CancellationToken token) => Task.Run(async () =>
    {
        OrderChangeRules.Validate(plan);
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(token);
        if (await context.OrderOperations.AnyAsync(row => row.Id == plan.OperationId
            || row.QuotationId == plan.Reference.QuotationId && (row.State == OrderOperationState.Pending || row.State == OrderOperationState.Unknown), token))
            throw new OrderException(OrderError.PendingOperation);
        if (await context.SynchronizationOperations.AnyAsync(row => row.Id == plan.OperationId || (row.State == OrderOperationState.Pending || row.State == OrderOperationState.Unknown)
            && (row.QuotationId == plan.Reference.QuotationId || plan.ExpectedOrder != null && row.CatalogIdentity == plan.Reference.CatalogIdentity && row.TargetId == plan.ExpectedOrder.Header.Id), token))
            throw new OrderException(OrderError.PendingOperation);
        // A preview may use a historical preparation, but its exact saved reference must exist.
        var preparationJson = await context.Purchases.AsNoTracking().Where(row => row.QuotationId == plan.Reference.QuotationId
            && row.Version == plan.Reference.PreparationVersion).Select(row => row.DataJson).SingleOrDefaultAsync(token);
        if (preparationJson is null || JsonSerializer.Serialize(PurchaseOrderReference.From(JsonSerializer.Deserialize<PurchasePreparation>(preparationJson, JsonOptions)!, plan.Reference.ProposalId), JsonOptions)
            != JsonSerializer.Serialize(plan.Reference, JsonOptions)) throw new OrderException(OrderError.InvalidReference);
        var preparation = JsonSerializer.Deserialize<PurchasePreparation>(preparationJson, JsonOptions)!;
        int latest = await context.Purchases.Where(row => row.QuotationId == plan.Reference.QuotationId).MaxAsync(row => row.Version, token);
        var current = await ReadAnalysisInputAsync(context, plan.Reference.QuotationId, token);
        if (latest != preparation.Version || current.Quotation.Revision != preparation.InputsRevision || current.State.Version != preparation.AnalysisVersion)
            throw new OrderException(OrderError.Conflict);
        context.OrderOperations.Add(new()
        {
            Id = plan.OperationId,
            QuotationId = plan.Reference.QuotationId,
            State = OrderOperationState.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            DataJson = JsonSerializer.Serialize(plan, JsonOptions)
        });
        await context.SaveChangesAsync(token); await transaction.CommitAsync(token);
    }, token);

    public async Task CompleteOrderOperationAsync(OrderWriteReceipt receipt, CancellationToken token)
    {
        await using var context = CreateContext();
        var row = await context.OrderOperations.SingleOrDefaultAsync(row => row.Id == receipt.OperationId, token);
        if (!Enum.IsDefined(receipt.State) || row is null || row.State is not (OrderOperationState.Pending or OrderOperationState.Unknown)
            || receipt.State == OrderOperationState.Pending || receipt.State == OrderOperationState.Committed && string.IsNullOrWhiteSpace(receipt.OrderId))
            throw new OrderException(OrderError.InvalidChange);
        row.State = receipt.State; row.OrderId = receipt.OrderId;
        await context.SaveChangesAsync(token);
    }
}
