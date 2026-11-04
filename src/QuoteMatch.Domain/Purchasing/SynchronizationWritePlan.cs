namespace QuoteMatch.Domain.Purchasing;

public sealed record SynchronizationWriteProfile(string Identity, int? OperatorCode)
{
    public OrderWriteKind Kind { get; init; } = OrderWriteKind.Update;
    public bool SupportsFinancialPolicies { get; init; }
}
public sealed record SynchronizationAllocation(string OrderId, string Number, IReadOnlyList<string> LineControls);
public sealed record SynchronizationWritePlan(Guid OperationId, SynchronizationSelection Selection, SynchronizationWriteProfile Profile)
{
    public string? Status { get; init; }
    public DateOnly? Date { get; init; }
    public SynchronizationAllocation? Allocation { get; init; }
    public SynchronizationFinancialPolicy FinancialPolicy { get; init; } = new();
    public SynchronizationFinancialPreview? FinancialPreview { get; init; }
    public bool RequiresFinancialPreview => FinancialPolicy.IsExplicit || Profile.SupportsFinancialPolicies;
    public OrderWriteKind Kind => Selection.Preview.Target is null ? OrderWriteKind.Create : OrderWriteKind.Update;
    public string? TargetId => Selection.Preview.Target?.Header.Id ?? Allocation?.OrderId;
}
public sealed record SynchronizationOperation(SynchronizationWritePlan Plan, OrderOperationState State, DateTime CreatedAtUtc, string? OrderId);

public static class SynchronizationWriteRules
{
    public static void Validate(SynchronizationWritePlan plan, bool requireFinancialPreview = false)
    {
        if (plan is null || plan.Selection is null || plan.Profile is null || plan.FinancialPolicy is null || plan.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(plan.Profile.Identity) || plan.Profile.OperatorCode is <= 0)
            throw new OrderException(OrderError.InvalidChange);
        var selection = plan.Selection;
        SynchronizationFinancialRules.ValidatePolicy(plan);
        if (requireFinancialPreview && plan.RequiresFinancialPreview && plan.FinancialPreview is null) throw new OrderException(OrderError.FinancialDataMissing);
        if (plan.FinancialPreview is { } financial) SynchronizationFinancialRules.ValidatePreview(plan, financial);
        if (!plan.RequiresFinancialPreview && plan.FinancialPreview is not null) throw new OrderException(OrderError.InvalidChange);
        var preview = selection.Preview;
        if (plan.Profile.Kind != plan.Kind || plan.Kind == OrderWriteKind.Create && plan.Date is null
            || plan.Kind == OrderWriteKind.Update && plan.Allocation is not null) throw new OrderException(OrderError.InvalidChange);
        if (plan.Allocation is not null) ValidateAllocation(plan, plan.Allocation);
        var current = PurchaseSynchronization.Compare(preview.Proposal, preview.Supplier, preview.Branch, preview.Target, preview.Pairs, preview.CreationDocuments);
        var expected = PurchaseSynchronization.Select(current, selection.SelectedChanges, true, plan.FinancialPolicy.IsExplicit);
        if (selection.SupplierId != expected.SupplierId || !selection.Lines.SequenceEqual(expected.Lines)
            || !selection.RemovedControls.SequenceEqual(expected.RemovedControls)) throw new OrderException(OrderError.InvalidChange);
    }

    public static void ValidateAllocation(SynchronizationWritePlan plan, SynchronizationAllocation allocation)
    {
        if (plan.Kind != OrderWriteKind.Create || string.IsNullOrWhiteSpace(allocation.OrderId) || string.IsNullOrWhiteSpace(allocation.Number)
            || allocation.LineControls.Count != plan.Selection.Lines.Count || allocation.LineControls.Any(string.IsNullOrWhiteSpace)
            || allocation.LineControls.Append(allocation.OrderId).Distinct(StringComparer.Ordinal).Count() != allocation.LineControls.Count + 1)
            throw new OrderException(OrderError.InvalidResult);
    }
}
