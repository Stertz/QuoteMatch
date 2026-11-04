using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Application.Purchasing;

public interface IPurchaseOrderReader
{
    Task<IReadOnlyList<PurchaseOrderHeader>> SearchAsync(PurchaseOrderSearch search, CancellationToken token);
    Task<PurchaseOrderSnapshot> ReadAsync(string orderId, CancellationToken token);
}
public interface IPurchaseOrderWriter
{
    Task<string> GetScriptIdentityAsync(OrderWriteKind kind, CancellationToken token);
    Task<OrderWriteReceipt> ExecuteAsync(OrderWritePlan plan, CancellationToken token);
    Task<OrderWriteReceipt> ReconcileAsync(OrderOperation operation, CancellationToken token);
}
public interface IOrderWorkspaceRepository
{
    Task<OrderReconciliation?> GetReconciliationAsync(Guid quotationId, Guid proposalId, CancellationToken token);
    Task<OrderReconciliation> SaveReconciliationAsync(OrderReconciliation reconciliation, CancellationToken token);
    Task<IReadOnlyList<OrderOperation>> GetOrderOperationsAsync(Guid quotationId, CancellationToken token);
    Task BeginOrderOperationAsync(OrderWritePlan plan, CancellationToken token);
    Task CompleteOrderOperationAsync(OrderWriteReceipt receipt, CancellationToken token);
}

public sealed class PurchaseOrderService(IOrderWorkspaceRepository repository, IPurchaseOrderWriter writer)
{
    public async Task<OrderWritePlan> PreviewAsync(OrderWriteKind kind, PurchaseOrderReference reference,
        PurchaseOrderSnapshot? order, string branch, string status, DateOnly date,
        IReadOnlyList<OrderLineChange> changes, CancellationToken token, IReadOnlyList<PurchaseOrderSnapshot>? coverageOrders = null, IReadOnlyList<OrderManualPair>? coveragePairs = null)
    {
        var plan = new OrderWritePlan(Guid.CreateVersion7(), kind, reference, order, branch, status, date,
            await writer.GetScriptIdentityAsync(kind, token), changes.ToArray())
        { CoverageOrders = coverageOrders?.ToArray() ?? [], CoveragePairs = coveragePairs?.ToArray() ?? [] };
        OrderChangeRules.Validate(plan);
        return plan;
    }

    public async Task<OrderWriteReceipt> ConfirmAsync(OrderWritePlan plan, bool confirmed, bool cancellationConfirmed, CancellationToken token)
    {
        if (!confirmed || plan.Changes.Any(change => change.Cancel) && !cancellationConfirmed)
            throw new OrderException(OrderError.ConfirmationRequired);
        OrderChangeRules.Validate(plan);
        await repository.BeginOrderOperationAsync(plan, token);
        OrderWriteReceipt receipt;
        try
        {
            receipt = await writer.ExecuteAsync(plan, token);
            if (receipt.OperationId != plan.OperationId || receipt.State != OrderOperationState.Committed || string.IsNullOrWhiteSpace(receipt.OrderId)
                || plan.ExpectedOrder is { } expected && receipt.OrderId != expected.Header.Id)
                receipt = new(plan.OperationId, OrderOperationState.Unknown, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Once dispatch has begun, only reconciliation can establish the external outcome.
            await repository.CompleteOrderOperationAsync(new(plan.OperationId, OrderOperationState.Unknown, null), CancellationToken.None);
            throw;
        }
        await repository.CompleteOrderOperationAsync(receipt, CancellationToken.None);
        return receipt;
    }

    public async Task<OrderWriteReceipt> ReconcileAsync(OrderOperation operation, CancellationToken token)
    {
        if (operation.State is not (OrderOperationState.Pending or OrderOperationState.Unknown)) throw new OrderException(OrderError.InvalidChange);
        var receipt = await writer.ReconcileAsync(operation, token);
        if (!Enum.IsDefined(receipt.State) || receipt.OperationId != operation.Plan.OperationId || receipt.State == OrderOperationState.Pending
            || receipt.State == OrderOperationState.Committed && (string.IsNullOrWhiteSpace(receipt.OrderId)
                || operation.Plan.ExpectedOrder is { } expected && receipt.OrderId != expected.Header.Id)) throw new OrderException(OrderError.InvalidResult);
        await repository.CompleteOrderOperationAsync(receipt, token);
        return receipt;
    }
}
