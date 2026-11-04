using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Application.Purchasing;

public interface IPurchaseSynchronizationWriter
{
    Task<SynchronizationWriteProfile> GetProfileAsync(OrderWriteKind kind, CancellationToken token);
    Task<SynchronizationAllocation> ReserveAsync(SynchronizationWritePlan plan, CancellationToken token);
    Task<OrderWriteReceipt> ExecuteAsync(SynchronizationWritePlan plan, CancellationToken token);
    Task<OrderWriteReceipt> ReconcileAsync(SynchronizationOperation operation, CancellationToken token);
    Task<SynchronizationFinancialPreview> PreviewFinancialsAsync(SynchronizationWritePlan plan, CancellationToken token) => throw new OrderException(OrderError.FinancialPolicyUnsupported);
}

public interface ISynchronizationOperationRepository
{
    Task<IReadOnlyList<SynchronizationOperation>> GetSynchronizationOperationsAsync(Guid quotationId, CancellationToken token);
    Task BeginSynchronizationAsync(SynchronizationWritePlan plan, CancellationToken token);
    Task SaveSynchronizationAllocationAsync(SynchronizationWritePlan plan, CancellationToken token);
    Task CompleteSynchronizationAsync(OrderWriteReceipt receipt, CancellationToken token);
}

public sealed class SynchronizationWriteService(ISynchronizationOperationRepository repository,
    PurchaseSynchronizationService previews, IPurchaseSynchronizationWriter writer)
{
    public async Task<SynchronizationWritePlan> PreviewAsync(SynchronizationSelection selection, CancellationToken token, string? status = null, DateOnly? date = null, SynchronizationFinancialPolicy? financialPolicy = null)
    {
        var kind = selection.Preview.Target is null ? OrderWriteKind.Create : OrderWriteKind.Update;
        var plan = new SynchronizationWritePlan(Guid.CreateVersion7(), selection, await writer.GetProfileAsync(kind, token))
        { Status = status, Date = date, FinancialPolicy = financialPolicy ?? new() };
        SynchronizationWriteRules.Validate(plan);
        await previews.ValidatePreviewAsync(selection.Preview, token);
        if (plan.RequiresFinancialPreview)
        {
            plan = plan with { FinancialPreview = await writer.PreviewFinancialsAsync(plan, token) };
            SynchronizationWriteRules.Validate(plan, true);
        }
        return plan;
    }

    public async Task<OrderWriteReceipt> ConfirmAsync(SynchronizationWritePlan plan, bool confirmed, bool removalConfirmed, CancellationToken token)
    {
        if (!confirmed || plan.Selection.RemovedControls.Count > 0 && !removalConfirmed)
            throw new OrderException(OrderError.ConfirmationRequired);
        SynchronizationWriteRules.Validate(plan, true);
        await previews.ValidatePreviewAsync(plan.Selection.Preview, token);
        await repository.BeginSynchronizationAsync(plan, token);
        OrderWriteReceipt receipt;
        try
        {
            if (plan.Kind == OrderWriteKind.Create)
            {
                var allocation = await writer.ReserveAsync(plan, token);
                SynchronizationWriteRules.ValidateAllocation(plan, allocation);
                plan = plan with { Allocation = allocation };
                await repository.SaveSynchronizationAllocationAsync(plan, token);
            }
            receipt = await writer.ExecuteAsync(plan, token);
            if (!IsReceipt(plan, receipt) || receipt.State != OrderOperationState.Committed)
                receipt = new(plan.OperationId, OrderOperationState.Unknown, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await repository.CompleteSynchronizationAsync(new(plan.OperationId, OrderOperationState.Unknown, null), CancellationToken.None);
            throw;
        }
        await repository.CompleteSynchronizationAsync(receipt, CancellationToken.None);
        return receipt;
    }

    public async Task<OrderWriteReceipt> ReconcileAsync(SynchronizationOperation operation, CancellationToken token)
    {
        if (operation.State is not (OrderOperationState.Pending or OrderOperationState.Unknown)) throw new OrderException(OrderError.InvalidChange);
        var receipt = await writer.ReconcileAsync(operation, token);
        // An absent marker cannot establish that a delayed or disconnected write will never commit.
        if (!IsReceipt(operation.Plan, receipt) || receipt.State is not (OrderOperationState.Committed or OrderOperationState.Unknown))
            throw new OrderException(OrderError.InvalidResult);
        await repository.CompleteSynchronizationAsync(receipt, token);
        return receipt;
    }

    private static bool IsReceipt(SynchronizationWritePlan plan, OrderWriteReceipt receipt) => receipt.OperationId == plan.OperationId
        && (receipt.State != OrderOperationState.Committed || !string.IsNullOrWhiteSpace(plan.TargetId) && receipt.OrderId == plan.TargetId
            && (!plan.RequiresFinancialPreview || receipt.FinancialRevision == plan.FinancialPreview?.Revision));
}
