namespace QuoteMatch.Application.Importing;

public static class SpreadsheetFormats
{
    public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly(new[] { ".xls", ".xlsx", ".xlsm", ".xlsb", ".ods", ".fods", ".csv", ".tsv" });
    public static bool Supports(string extension) => Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
}
