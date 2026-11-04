using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Domain.Integration;

public sealed record SupplierProductReference(string Supplier, string Code, string Description, string Color, string Unit)
{
    public ProductCodeKind CodeKind { get; init; }
    public string LookupKey => (CodeKind == ProductCodeKind.Internal ? "internal:" : string.Empty) + Code.Trim();
    public static SupplierProductReference From(SavedProposal proposal, ReviewedItem item) =>
        new(proposal.Template.SupplierName, item.ProductCode, item.Description, item.Color, item.Unit) { CodeKind = proposal.Template.CodeKind };

    public string Key => string.Concat(new[] { Supplier, Code, Description, Color, Unit }.Select(value =>
    {
        string normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        return normalized.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + normalized;
    })) + (CodeKind == ProductCodeKind.Internal ? ":internal" : string.Empty);
    public string Display => string.Join(" · ", new[] { Code, Description, Color, Unit }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public enum ProductMappingState { Active, Disabled }
public sealed record SupplierProductMapping(Guid Id, int Version, SupplierProductReference Source,
    ErpProductCandidate Product, string BaseUnit, decimal ConversionFactor, ProductMappingState State,
    DateTime CreatedAtUtc, DateTime UpdatedAtUtc)
{
    public void Validate()
    {
        if (Id == Guid.Empty || Version < 0 || !Enum.IsDefined(State) || !Enum.IsDefined(Source.CodeKind)
            || new[] { Source.Supplier, Source.Code, Source.Description, Source.Unit, Product.ProductCode, Product.CatalogIdentity, BaseUnit }.Any(string.IsNullOrWhiteSpace)
            || Product.IsInactive || ConversionFactor <= 0 || string.IsNullOrWhiteSpace(Product.BaseUnit)
            || !string.Equals(BaseUnit.Trim(), Product.BaseUnit.Trim(), StringComparison.OrdinalIgnoreCase)
            || Source.CodeKind == ProductCodeKind.Internal && !string.Equals(Source.Code.Trim(), Product.ProductCode.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ProductMappingException(ProductMappingError.Invalid);
    }
}
public enum ProductMappingError { Invalid, Conflict, ConflictingConversion }
public sealed class ProductMappingException(ProductMappingError code) : Exception($"Product mapping operation failed: {code}.")
{
    public ProductMappingError Code { get; } = code;
}
