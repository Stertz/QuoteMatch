namespace QuoteMatch.Domain.Importing;

public enum ImportSourceFormat
{
    Pdf,
    Excel
}

public enum ProductCodeKind
{
    Supplier,
    Internal
}

public enum PdfTextStrategy
{
    ContentStream,
    ContentOrder,
    PositionedLines
}

public sealed record PdfTemplate
{
    public Guid Id { get; init; }
    public int Version { get; init; }
    public string Name { get; init; } = string.Empty;
    public string SupplierName { get; init; } = string.Empty;
    public string CultureName { get; init; } = "pt-BR";
    public string DefaultUnit { get; init; } = "m";
    public PdfTextStrategy TextStrategy { get; init; }
    public string IdentificationPattern { get; init; } = string.Empty;
    public string RecordStartPattern { get; init; } = @"^\d{3}\s";
    public string RecordPattern { get; init; } = @"^(?<code>\S+)\s+(?<description>.+?)\s+(?<quantity>[\d.,]+)\s+(?<unitPrice>[\d.,]+)\s+(?<total>[\d.,]+)\s*$";
    public string TableEndPattern { get; init; } = string.Empty;
    public string ExpectedCountPattern { get; init; } = string.Empty;
    public string DocumentTotalPattern { get; init; } = string.Empty;
    public string DocumentFinancialPattern { get; init; } = string.Empty;
    public decimal AbsoluteTolerance { get; init; } = 0.01m;
    public decimal RelativeTolerance { get; init; } = 0.001m;
    public bool RemoveGroupingSpaces { get; init; } = true;
    public ImportSourceFormat SourceFormat { get; init; }
    public ProductCodeKind CodeKind { get; init; }
    public SpreadsheetMapping Spreadsheet { get; init; } = new();
}
