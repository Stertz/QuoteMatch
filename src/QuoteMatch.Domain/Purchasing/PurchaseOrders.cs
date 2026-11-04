using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Domain.Purchasing;

public sealed record PurchaseOrderHeader(string Id, string Number, string SupplierId, string SupplierName,
    string Branch, string Status, DateOnly Date, bool CanEdit);
public sealed record PurchaseOrderLine(string Id, string ProductCode, string Description, string Unit,
    string BaseUnit, decimal Quantity, decimal BaseUnitsPerUnit, decimal UnitPrice, decimal Total,
    decimal ReceivedQuantity, bool IsCancelled)
{
    public string? SimilarityCode { get; init; }
    public ErpBrandIdentity? Brand { get; init; }
    public FinancialAmounts? Financials { get; init; }
}
public sealed record PurchaseOrderSnapshot(string CatalogIdentity, PurchaseOrderHeader Header,
    string Revision, DateTime FetchedAtUtc, IReadOnlyList<PurchaseOrderLine> Lines)
{
    public FinancialAmounts? Financials { get; init; }
    public decimal? OrderTotal { get; init; }
}
public sealed record PurchaseOrderSearch(string Number = "", string Supplier = "", string Branch = "", string Status = "",
    DateOnly? From = null, DateOnly? To = null);
public sealed record PurchaseReferenceLine(string Id, string ProductCode, string Description, string Unit,
    string BaseUnit, decimal Quantity, decimal BaseUnitsPerUnit, decimal UnitPrice, decimal Total, decimal? RequestedQuantity);
public sealed record PurchaseOrderReference(Guid QuotationId, int PreparationVersion, Guid ProposalId,
    string CatalogIdentity, string SupplierId, string SupplierName, IReadOnlyList<PurchaseReferenceLine> Lines)
{
    public static PurchaseOrderReference From(PurchasePreparation preparation, Guid proposalId)
    {
        var lines = preparation.Lines.Where(line => line.Offer.ProposalId == proposalId).ToArray();
        if (lines.Length == 0 || lines.Any(line => line.Offer.Link?.Product is null)) throw new OrderException(OrderError.InvalidReference);
        var product = lines[0].Offer.Link!.Product!;
        if (string.IsNullOrWhiteSpace(product.SupplierId) || string.IsNullOrWhiteSpace(product.CatalogIdentity)
            || lines.Any(line => line.Offer.Link!.Product!.CatalogIdentity != product.CatalogIdentity
                || line.Offer.Link.Product.SupplierId != product.SupplierId)) throw new OrderException(OrderError.InvalidReference);
        return new(preparation.QuotationId, preparation.Version, proposalId, product.CatalogIdentity, product.SupplierId,
            lines[0].Offer.SupplierName, lines.Select(line => new PurchaseReferenceLine(line.GroupKey,
                line.Offer.Link!.Product!.ProductCode, line.GroupName, line.Offer.Item.Unit, line.BaseUnit,
                line.Offer.Item.Quantity!.Value, line.Offer.Link.BaseUnitsPerQuotedUnit, line.Offer.Item.UnitPrice!.Value,
                line.Offer.Item.ReportedTotal!.Value, line.RequestedQuantity)).ToArray());
    }
}

public sealed record OrderContribution(string OrderId, string LineId, decimal Quantity, decimal BaseUnitPrice, decimal? PriceDifferencePercent,
    decimal ReportedLineTotal, decimal TotalBaseQuantity);
public sealed record OrderComparisonLine(PurchaseReferenceLine Reference, decimal QuotedQuantity, decimal CoveredQuantity,
    decimal RemainingQuantity, IReadOnlyList<OrderContribution> Contributions);
public sealed record OrderExcess(string OrderId, PurchaseOrderLine Line, decimal Quantity);
public sealed record OrderComparison(IReadOnlyList<OrderComparisonLine> Lines, IReadOnlyList<OrderExcess> Excess);
public sealed record OrderManualPair(string OrderId, string LineId, string ReferenceLineId);
public sealed record OrderReconciliation(Guid Id, int Version, DateTime CreatedAtUtc, PurchaseOrderReference Reference,
    IReadOnlyList<PurchaseOrderSnapshot> Orders, bool ShortagesAccepted)
{
    public IReadOnlyList<OrderManualPair> ManualPairs { get; init; } = [];
}

public enum OrderError { InvalidReference, InvalidResult, DuplicateOrder, InvalidChange, Conflict, NotEditable, ConfirmationRequired, PendingOperation, WritesNotConfigured, RefreshRequired, FinancialPolicyUnsupported, FinancialDataMissing, FinancialHistoryMissing, NoChanges }
public sealed class OrderException(OrderError code) : Exception($"Purchase order operation failed: {code}.")
{
    public OrderError Code { get; } = code;
}

public static class PurchaseOrderRules
{
    public static void Validate(PurchaseOrderSnapshot order)
    {
        if (order.Financials is { IsValid: false } || order.OrderTotal is < 0 || order.Lines.Any(line => line.Financials is { IsValid: false })) throw new OrderException(OrderError.InvalidResult);
        if (string.IsNullOrWhiteSpace(order.CatalogIdentity) || string.IsNullOrWhiteSpace(order.Header.Id)
            || string.IsNullOrWhiteSpace(order.Header.SupplierId) || string.IsNullOrWhiteSpace(order.Revision)
            || order.Lines.Select(line => line.Id).Distinct(StringComparer.Ordinal).Count() != order.Lines.Count
            || order.Lines.Any(line => string.IsNullOrWhiteSpace(line.Id) || string.IsNullOrWhiteSpace(line.ProductCode)
                || string.IsNullOrWhiteSpace(line.BaseUnit) || string.IsNullOrWhiteSpace(line.Unit)
                || line.Quantity < 0 || line.BaseUnitsPerUnit <= 0 || line.UnitPrice < 0 || line.Total < 0 || line.ReceivedQuantity < 0))
            throw new OrderException(OrderError.InvalidResult);
        if (order.Lines.Any(line => line.Brand is { } brand && (brand.CatalogIdentity != order.CatalogIdentity
            || string.IsNullOrWhiteSpace(brand.Id) || string.IsNullOrWhiteSpace(brand.Name)))) throw new OrderException(OrderError.InvalidResult);
    }

    public static OrderComparison Compare(PurchaseOrderReference reference, IReadOnlyList<PurchaseOrderSnapshot> orders, IReadOnlyList<OrderManualPair>? manualPairs = null)
    {
        if (reference.QuotationId == Guid.Empty || reference.ProposalId == Guid.Empty || reference.PreparationVersion <= 0
            || string.IsNullOrWhiteSpace(reference.CatalogIdentity) || string.IsNullOrWhiteSpace(reference.SupplierId)
            || reference.Lines.Count == 0 || reference.Lines.Select(line => line.Id).Distinct().Count() != reference.Lines.Count
            || reference.Lines.Any(line => line.Quantity <= 0 || line.BaseUnitsPerUnit <= 0 || line.UnitPrice < 0
                || line.Total < 0 || line.RequestedQuantity is <= 0 || string.IsNullOrWhiteSpace(line.Id)
                || string.IsNullOrWhiteSpace(line.ProductCode) || string.IsNullOrWhiteSpace(line.Unit) || string.IsNullOrWhiteSpace(line.BaseUnit)))
            throw new OrderException(OrderError.InvalidReference);
        if (orders.Select(order => order.Header.Id).Distinct(StringComparer.Ordinal).Count() != orders.Count)
            throw new OrderException(OrderError.DuplicateOrder);
        foreach (var order in orders)
        {
            Validate(order);
            if (order.CatalogIdentity != reference.CatalogIdentity || order.Header.SupplierId != reference.SupplierId)
                throw new OrderException(OrderError.InvalidReference);
        }
        var available = orders.SelectMany(order => order.Lines.Where(line => !line.IsCancelled)
            .Select(line => (Order: order, Line: line))).ToArray();
        Dictionary<(string, string), string> pairs = [];
        foreach (var pair in manualPairs ?? [])
        {
            var item = available.SingleOrDefault(item => item.Order.Header.Id == pair.OrderId && item.Line.Id == pair.LineId);
            var referenceLine = reference.Lines.SingleOrDefault(line => line.Id == pair.ReferenceLineId);
            if (item.Line is null || referenceLine is null || !string.Equals(item.Line.BaseUnit, referenceLine.BaseUnit, StringComparison.OrdinalIgnoreCase)
                || !pairs.TryAdd((pair.OrderId, pair.LineId), pair.ReferenceLineId)) throw new OrderException(OrderError.InvalidReference);
        }
        var remaining = available.ToDictionary(item => (item.Order.Header.Id, item.Line.Id), item => item.Line.Quantity * item.Line.BaseUnitsPerUnit);
        List<OrderComparisonLine> result = [];
        foreach (var line in reference.Lines)
        {
            decimal quoted = line.Quantity * line.BaseUnitsPerUnit, needed = quoted;
            List<OrderContribution> contributions = [];
            foreach (var item in available.Where(item => (pairs.TryGetValue((item.Order.Header.Id, item.Line.Id), out var pairedId)
                ? pairedId == line.Id : item.Line.ProductCode == line.ProductCode)
                && string.Equals(item.Line.BaseUnit, line.BaseUnit, StringComparison.OrdinalIgnoreCase)))
            {
                var key = (item.Order.Header.Id, item.Line.Id);
                decimal quantity = Math.Min(needed, remaining[key]);
                if (quantity <= 0) continue;
                decimal price = item.Line.UnitPrice / item.Line.BaseUnitsPerUnit;
                decimal referencePrice = line.UnitPrice / line.BaseUnitsPerUnit;
                contributions.Add(new(item.Order.Header.Id, item.Line.Id, quantity, price, referencePrice > 0 ? (price - referencePrice) / referencePrice * 100m : null,
                    item.Line.Total, item.Line.Quantity * item.Line.BaseUnitsPerUnit));
                remaining[key] -= quantity; needed -= quantity;
            }
            result.Add(new(line, quoted, quoted - needed, needed, contributions));
        }
        return new(result, available.Where(item => remaining[(item.Order.Header.Id, item.Line.Id)] > 0)
            .Select(item => new OrderExcess(item.Order.Header.Id, item.Line, remaining[(item.Order.Header.Id, item.Line.Id)])).ToArray());
    }
}

public enum OrderWriteKind { Create, Update }
public enum OrderOperationState { Pending, Committed, NotApplied, Unknown }
public sealed record OrderLineChange(string? LineId, string? ReferenceLineId, decimal? Quantity, decimal? UnitPrice, bool Cancel);
public sealed record OrderWritePlan(Guid OperationId, OrderWriteKind Kind, PurchaseOrderReference Reference,
    PurchaseOrderSnapshot? ExpectedOrder, string Branch, string Status, DateOnly Date, string ScriptIdentity,
    IReadOnlyList<OrderLineChange> Changes)
{
    public IReadOnlyList<PurchaseOrderSnapshot> CoverageOrders { get; init; } = [];
    public IReadOnlyList<OrderManualPair> CoveragePairs { get; init; } = [];
}
public sealed record OrderWriteReceipt(Guid OperationId, OrderOperationState State, string? OrderId)
{
    public string? FinancialRevision { get; init; }
}
public sealed record OrderOperation(OrderWritePlan Plan, OrderOperationState State, DateTime CreatedAtUtc, string? OrderId);

public static class OrderChangeRules
{
    public static void Validate(OrderWritePlan plan)
    {
        _ = PurchaseOrderRules.Compare(plan.Reference, plan.CoverageOrders, plan.CoveragePairs);
        if (!Enum.IsDefined(plan.Kind) || plan.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(plan.ScriptIdentity) || string.IsNullOrWhiteSpace(plan.Branch)
            || string.IsNullOrWhiteSpace(plan.Status) || plan.Changes.Count == 0) throw new OrderException(OrderError.InvalidChange);
        if (plan.Kind == OrderWriteKind.Create && plan.ExpectedOrder is not null || plan.Kind == OrderWriteKind.Update && plan.ExpectedOrder is null)
            throw new OrderException(OrderError.InvalidChange);
        if (plan.ExpectedOrder is { } order)
        {
            PurchaseOrderRules.Validate(order);
            if (!order.Header.CanEdit || order.Header.SupplierId != plan.Reference.SupplierId || order.CatalogIdentity != plan.Reference.CatalogIdentity)
                throw new OrderException(OrderError.NotEditable);
        }
        HashSet<string> lines = [], references = [];
        foreach (var change in plan.Changes)
        {
            if (change.LineId is { } id)
            {
                var line = plan.ExpectedOrder?.Lines.SingleOrDefault(line => line.Id == id);
                if (line is null || !lines.Add(id) || line.IsCancelled || line.ReceivedQuantity > 0) throw new OrderException(OrderError.NotEditable);
                if (change.ReferenceLineId is not null || change.Quantity is null && change.UnitPrice is null && !change.Cancel
                    || change.Cancel && (change.Quantity is not null || change.UnitPrice is not null)) throw new OrderException(OrderError.InvalidChange);
            }
            else if (change.ReferenceLineId is not { } referenceId || !references.Add(referenceId)
                || !plan.Reference.Lines.Any(line => line.Id == referenceId) || change.Cancel || change.Quantity is null || change.UnitPrice is null)
                throw new OrderException(OrderError.InvalidChange);
            if (change.Quantity is <= 0 || change.UnitPrice is < 0) throw new OrderException(OrderError.InvalidChange);
        }
    }
}
