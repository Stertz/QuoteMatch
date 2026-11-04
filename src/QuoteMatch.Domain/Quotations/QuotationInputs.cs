using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Domain.Quotations;

public enum QuotationPriceReference { LastPurchase = 0, LinkedOrder = 1 }
public enum ProductGroupingVersion { ProductCodeFallback = 1, ProductNameFallback = 2, ExactProductCode = 3 }

public sealed record QuotationInputs(Guid QuotationId, int Version, DateTime CreatedAtUtc,
    QuotationPriceReference PriceReference, ProductGroupingVersion GroupingVersion,
    IReadOnlyList<PurchaseOrderSnapshot> Orders)
{
    public IReadOnlyList<BuyingSupplierSnapshot> BuyingSuppliers { get; init; } = [];

    public static QuotationInputs Legacy(Guid quotationId) => new(quotationId, 0, DateTime.UnixEpoch,
        QuotationPriceReference.LastPurchase, ProductGroupingVersion.ProductCodeFallback, []);
}

public static class QuotationInputRules
{
    public static void Validate(QuotationInputs inputs)
    {
        if (inputs.QuotationId == Guid.Empty || inputs.Version < 0 || !Enum.IsDefined(inputs.PriceReference)
            || !Enum.IsDefined(inputs.GroupingVersion)
            || inputs.PriceReference == QuotationPriceReference.LinkedOrder && inputs.Orders.Count == 0
            || inputs.Orders.Select(order => (order.CatalogIdentity, order.Header.Id)).Distinct().Count() != inputs.Orders.Count
            || inputs.Orders.Select(order => order.CatalogIdentity).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new QuotationException(QuotationError.InvalidInputs);
        foreach (var order in inputs.Orders)
        {
            try { PurchaseOrderRules.Validate(order); }
            catch (OrderException) { throw new QuotationException(QuotationError.InvalidInputs); }
            if (order.FetchedAtUtc.Kind != DateTimeKind.Utc) throw new QuotationException(QuotationError.InvalidInputs);
            foreach (var line in order.Lines)
                if (line.Brand is { } brand && !ValidBrand(brand, order.CatalogIdentity))
                    throw new QuotationException(QuotationError.InvalidInputs);
        }
        if (inputs.BuyingSuppliers.Select(snapshot => (snapshot.Brand.CatalogIdentity, snapshot.Brand.Id))
            .Distinct().Count() != inputs.BuyingSuppliers.Count) throw new QuotationException(QuotationError.InvalidInputs);
        foreach (var snapshot in inputs.BuyingSuppliers)
            if (!ValidBrand(snapshot.Brand, snapshot.Supplier.CatalogIdentity) || string.IsNullOrWhiteSpace(snapshot.Supplier.Id)
                || string.IsNullOrWhiteSpace(snapshot.Supplier.Name) || string.IsNullOrWhiteSpace(snapshot.SourceOrderId)
                || string.IsNullOrWhiteSpace(snapshot.SourceOrderRevision) || string.IsNullOrWhiteSpace(snapshot.Branch)
                || snapshot.FetchedAtUtc.Kind != DateTimeKind.Utc
                || inputs.Orders.Any(order => order.CatalogIdentity != snapshot.Supplier.CatalogIdentity))
                throw new QuotationException(QuotationError.InvalidInputs);
    }

    private static bool ValidBrand(ErpBrandIdentity brand, string catalogIdentity) =>
        !string.IsNullOrWhiteSpace(brand.CatalogIdentity) && brand.CatalogIdentity == catalogIdentity
        && !string.IsNullOrWhiteSpace(brand.Id) && !string.IsNullOrWhiteSpace(brand.Name);
}
