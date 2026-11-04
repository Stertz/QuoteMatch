namespace QuoteMatch.Domain.Integration;

public sealed record ErpBrandIdentity(string CatalogIdentity, string Id, string Name);
public sealed record ErpBuyingSupplierIdentity(string CatalogIdentity, string Id, string Name);
public sealed record BuyingSupplierSnapshot(ErpBrandIdentity Brand, ErpBuyingSupplierIdentity Supplier,
    string SourceOrderId, string SourceOrderRevision, string Branch, DateOnly OrderDate, DateTime FetchedAtUtc);
