using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Infrastructure.Erp;

public static class ProductNameSearch
{
    public static string BuildPattern(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 3 || name.Length > 100)
            throw new ErpException(ErpError.InvalidQuery);
        return string.Join("%", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[")));
    }
}
