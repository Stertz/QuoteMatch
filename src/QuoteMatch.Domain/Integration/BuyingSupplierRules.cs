namespace QuoteMatch.Domain.Integration;

public static class BuyingSupplierRules
{
    public static BuyingSupplierSnapshot Validate(BuyingSupplierSnapshot snapshot, ErpBrandIdentity brand, string branch)
    {
        if (snapshot.Supplier is null || snapshot.Brand != brand || snapshot.Supplier.CatalogIdentity != brand.CatalogIdentity
            || string.IsNullOrWhiteSpace(snapshot.Supplier.Id) || string.IsNullOrWhiteSpace(snapshot.Supplier.Name)
            || snapshot.Branch != branch || string.IsNullOrWhiteSpace(branch) || string.IsNullOrWhiteSpace(snapshot.SourceOrderId)
            || string.IsNullOrWhiteSpace(snapshot.SourceOrderRevision) || snapshot.FetchedAtUtc.Kind != DateTimeKind.Utc)
            throw new ErpException(ErpError.InvalidResult);
        return snapshot;
    }
}
