using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Domain.Integration;

public static class ErpProductIdentity
{
    public static bool MatchesSupplierCode(ErpProductCandidate product, string sourceCode) =>
        string.Equals(product.SupplierProductCode.Trim(), sourceCode.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string GroupKey(ErpProductCandidate product, ProductGroupingVersion version = ProductGroupingVersion.ProductCodeFallback)
        => GroupKey(product.CatalogIdentity, product.ProductCode, product.Name, product.SimilarityCode, product.BaseUnit, version);

    public static string GroupKey(string catalogIdentity, string productCode, string name, string? similarityCode, string? baseUnit, ProductGroupingVersion version)
    {
        if (!Enum.IsDefined(version)) throw new ArgumentOutOfRangeException(nameof(version));
        if (version == ProductGroupingVersion.ProductNameFallback && string.IsNullOrWhiteSpace(similarityCode))
            return $"erp:{catalogIdentity}:name:{Uri.EscapeDataString(name.Trim().ToUpperInvariant())}:unit:{Uri.EscapeDataString(baseUnit?.Trim().ToUpperInvariant() ?? string.Empty)}";
        string identity = version == ProductGroupingVersion.ExactProductCode || string.IsNullOrWhiteSpace(similarityCode) ? "product:" + productCode.Trim() : similarityCode.Trim();
        return $"erp:{catalogIdentity}:{identity.ToUpperInvariant()}";
    }

    public static string GroupName(ErpProductCandidate product) =>
        string.IsNullOrWhiteSpace(product.SimilarityCode) ? product.Name : product.SimilarityCode.Trim();
}
