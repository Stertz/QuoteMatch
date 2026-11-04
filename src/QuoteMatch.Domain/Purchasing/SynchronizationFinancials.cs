using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Domain.Purchasing;

public enum SynchronizationPriceSource { SelectedChanges = 0, KeepExisting = 1, LastPurchaseOrder = 2, Zero = 3 }
public enum SynchronizationFinancialSource { ScriptDefault = 0, Document = 1, LastPurchaseOrder = 2, Zero = 3, KeepExisting = 4 }
public sealed record SynchronizationFinancialPolicy
{
    public SynchronizationPriceSource Prices { get; init; }
    public SynchronizationFinancialSource Freight { get; init; }
    public SynchronizationFinancialSource Discount { get; init; }
    public SynchronizationFinancialSource OtherCharges { get; init; }
    public SynchronizationFinancialSource Taxes { get; init; }
    public bool IsExplicit => Prices != SynchronizationPriceSource.SelectedChanges || new[] { Freight, Discount, OtherCharges, Taxes }.Any(source => source != SynchronizationFinancialSource.ScriptDefault);
}
public enum SynchronizationFinancialStatus { Ready = 0, MissingDocumentValues = 1, MissingHistory = 2, UnsupportedPolicy = 3 }
public sealed record SynchronizationFinancialLine(int Position, string? Control, string ProductCode, string Unit, string BaseUnit,
    decimal Quantity, decimal Factor, decimal UnitPrice, decimal ItemTotal, FinancialAmounts Values);
public sealed record SynchronizationFinancialSourceOrder(string CatalogIdentity, string OrderId, string Revision, DateOnly Date);
public sealed record SynchronizationFinancialPreview(SynchronizationFinancialStatus Status, string Revision, bool HasChanges,
    FinancialAmounts Header, decimal? OrderTotal, IReadOnlyList<SynchronizationFinancialLine> Lines, IReadOnlyList<SynchronizationFinancialSourceOrder> Sources);

public static class SynchronizationFinancialRules
{
    public static void ValidatePolicy(SynchronizationWritePlan plan)
    {
        var policy = plan.FinancialPolicy;
        if (!Enum.IsDefined(policy.Prices) || new[] { policy.Freight, policy.Discount, policy.OtherCharges, policy.Taxes }.Any(source => !Enum.IsDefined(source)))
            throw new OrderException(OrderError.FinancialPolicyUnsupported);
        if (!plan.RequiresFinancialPreview) return;
        if (!plan.Profile.SupportsFinancialPolicies) throw new OrderException(OrderError.FinancialPolicyUnsupported);
        if (plan.Selection.Preview.Target is { } target && (target.Financials is null || target.OrderTotal is null || target.Lines.Any(line => !line.IsCancelled && line.Financials is null)))
            throw new OrderException(OrderError.FinancialPolicyUnsupported);
        if (plan.Kind == OrderWriteKind.Create && (policy.Prices == SynchronizationPriceSource.KeepExisting
            || new[] { policy.Freight, policy.Discount, policy.OtherCharges, policy.Taxes }.Contains(SynchronizationFinancialSource.KeepExisting)))
            throw new OrderException(OrderError.FinancialPolicyUnsupported);
        if (policy.Prices == SynchronizationPriceSource.KeepExisting && plan.Selection.Lines.Any(line => line.Control is null))
            throw new OrderException(OrderError.FinancialDataMissing);
        var preview = plan.Selection.Preview;
        var sourceLines = preview.CreationDocuments?.Sources.Select(source => source.Line) ?? preview.Proposal.Lines;
        bool Has(FinancialField field) => sourceLines.Any(line => line.Offer.Item.Financials.Get(field).HasValue)
            || preview.FinancialSources.Any(source => source.Values.Get(field).HasValue);
        if (policy.Freight == SynchronizationFinancialSource.Document && !Has(FinancialField.Freight)
            || policy.Discount == SynchronizationFinancialSource.Document && !Has(FinancialField.Discount)
            || policy.OtherCharges == SynchronizationFinancialSource.Document && !Has(FinancialField.OtherCharges)
            || policy.Taxes == SynchronizationFinancialSource.Document && !Enum.GetValues<FinancialField>().Where(field => field >= FinancialField.IpiAmount).Any(Has))
            throw new OrderException(OrderError.FinancialDataMissing);
    }

    public static void ValidateWrittenOrder(SynchronizationWritePlan plan, PurchaseOrderSnapshot actual)
    {
        var expected = plan.FinancialPreview ?? throw new OrderException(OrderError.FinancialDataMissing);
        bool Matches(FinancialAmounts? current, FinancialAmounts approved) => current is not null && Enum.GetValues<FinancialField>().All(key => approved.Get(key) is null || current.Get(key) == approved.Get(key));
        if (actual.Header.Id != plan.TargetId || actual.Header.SupplierId != plan.Selection.SupplierId || actual.Header.Branch != plan.Selection.Preview.Branch || actual.CatalogIdentity != plan.Selection.Preview.Proposal.Brand!.CatalogIdentity
            || actual.OrderTotal != expected.OrderTotal || !Matches(actual.Financials, expected.Header)) throw new OrderException(OrderError.InvalidResult);
        var active = actual.Lines.Where(line => !line.IsCancelled).ToArray();
        if (active.Length != expected.Lines.Count) throw new OrderException(OrderError.InvalidResult);
        HashSet<string> used = [];
        foreach (var line in expected.Lines)
        {
            string? control = line.Control ?? (plan.Allocation is { } allocation ? allocation.LineControls[line.Position] : null);
            var found = active.FirstOrDefault(current => !used.Contains(current.Id) && (control is null || current.Id == control)
                && current.ProductCode == line.ProductCode && current.Unit == line.Unit && current.BaseUnit == line.BaseUnit && current.Quantity == line.Quantity
                && current.BaseUnitsPerUnit == line.Factor && current.UnitPrice == line.UnitPrice && current.Total == line.ItemTotal && Matches(current.Financials, line.Values));
            if (found is null) throw new OrderException(OrderError.InvalidResult);
            used.Add(found.Id);
        }
    }

    public static void ValidatePreview(SynchronizationWritePlan plan, SynchronizationFinancialPreview preview)
    {
        if (preview.Status == SynchronizationFinancialStatus.MissingDocumentValues) throw new OrderException(OrderError.FinancialDataMissing);
        if (preview.Status == SynchronizationFinancialStatus.MissingHistory) throw new OrderException(OrderError.FinancialHistoryMissing);
        if (preview.Status == SynchronizationFinancialStatus.UnsupportedPolicy) throw new OrderException(OrderError.FinancialPolicyUnsupported);
        if (preview.Status != SynchronizationFinancialStatus.Ready || string.IsNullOrWhiteSpace(preview.Revision) || preview.Header is null || !preview.Header.IsValid
            || preview.OrderTotal is null or < 0 || preview.Lines is null || preview.Sources is null || preview.Sources.Count > 1000
            || preview.Lines.Count != plan.Selection.Lines.Count || preview.Lines.Any(line => line is null) || preview.Lines.Select(line => line.Position).Distinct().Count() != preview.Lines.Count)
            throw new OrderException(OrderError.InvalidResult);
        foreach (var line in preview.Lines)
        {
            if (line.Position < 0 || line.Position >= plan.Selection.Lines.Count) throw new OrderException(OrderError.InvalidResult);
            var expected = plan.Selection.Lines[line.Position];
            if (line.Control != expected.Control || line.ProductCode != expected.ProductCode || line.Unit != expected.Unit || line.BaseUnit != expected.BaseUnit
                || line.Quantity != expected.Quantity || line.Factor != expected.Factor || line.UnitPrice < 0 || line.ItemTotal < 0 || line.Values is null || !line.Values.IsValid)
                throw new OrderException(OrderError.InvalidResult);
            if (plan.FinancialPolicy.Prices == SynchronizationPriceSource.SelectedChanges && line.UnitPrice != expected.UnitPrice
                || plan.FinancialPolicy.Prices == SynchronizationPriceSource.Zero && line.UnitPrice != 0)
                throw new OrderException(OrderError.InvalidResult);
            var previous = plan.Selection.Preview.Target?.Lines.SingleOrDefault(item => item.Id == line.Control);
            if (plan.FinancialPolicy.Prices == SynchronizationPriceSource.KeepExisting && previous is not null && previous.Unit == line.Unit
                && previous.BaseUnitsPerUnit == line.Factor && previous.UnitPrice != line.UnitPrice) throw new OrderException(OrderError.InvalidResult);
        }
        if (preview.Sources.Any(source => source is null || source.CatalogIdentity != plan.Selection.Preview.Proposal.Brand!.CatalogIdentity || string.IsNullOrWhiteSpace(source.OrderId) || string.IsNullOrWhiteSpace(source.Revision))
            || preview.Sources.Select(source => source.OrderId).Distinct().Count() != preview.Sources.Count)
            throw new OrderException(OrderError.InvalidResult);
        var policy = plan.FinancialPolicy;
        bool needsHistory = policy.Prices == SynchronizationPriceSource.LastPurchaseOrder
            || new[] { policy.Freight, policy.Discount, policy.OtherCharges, policy.Taxes }.Contains(SynchronizationFinancialSource.LastPurchaseOrder);
        if (needsHistory && preview.Sources.Count == 0) throw new OrderException(OrderError.FinancialHistoryMissing);
        void ValidateZero(SynchronizationFinancialSource source, params FinancialField[] fields)
        {
            if (source != SynchronizationFinancialSource.Zero) return;
            var values = fields.SelectMany(key => preview.Lines.Select(line => line.Values.Get(key)).Prepend(preview.Header.Get(key))).Where(value => value.HasValue).ToArray();
            if (values.Length == 0 || values.Any(value => value != 0)) throw new OrderException(OrderError.InvalidResult);
        }
        ValidateZero(policy.Freight, FinancialField.Freight); ValidateZero(policy.Discount, FinancialField.Discount); ValidateZero(policy.OtherCharges, FinancialField.OtherCharges);
        ValidateZero(policy.Taxes, FinancialField.IpiAmount, FinancialField.IpiRate, FinancialField.IcmsAmount, FinancialField.IcmsRate, FinancialField.SubstitutionTaxAmount, FinancialField.SubstitutionTaxRate);
        if (plan.Selection.SelectedChanges.Count == 0 && !preview.HasChanges) throw new OrderException(OrderError.NoChanges);
        try { _ = preview.Lines.Sum(line => line.ItemTotal); }
        catch (OverflowException) { throw new OrderException(OrderError.InvalidResult); }
    }
}
