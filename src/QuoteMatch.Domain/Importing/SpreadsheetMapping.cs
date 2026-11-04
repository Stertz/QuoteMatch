namespace QuoteMatch.Domain.Importing;

public enum DelimitedSeparator { Automatic, Semicolon, Comma, Tab, Pipe }
public enum DelimitedEncoding { Automatic, Utf8, Windows1252, Utf16LittleEndian, Utf16BigEndian }
public sealed record SpreadsheetTextOptions
{
    public DelimitedSeparator Separator { get; init; }
    public DelimitedEncoding Encoding { get; init; }
}

public sealed record SpreadsheetMapping
{
    public SpreadsheetTextOptions TextOptions { get; init; } = new();
    public string SheetName { get; init; } = string.Empty;
    public int HeaderRow { get; init; } = 1;
    public int FirstDataRow { get; init; } = 2;
    public int? LastDataRow { get; init; }
    public string CodeColumn { get; init; } = "A";
    public string DescriptionColumn { get; init; } = "B";
    public string QuantityColumn { get; init; } = "C";
    public string UnitPriceColumn { get; init; } = "D";
    public string TotalColumn { get; init; } = string.Empty;
    public string UnitColumn { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> ExpectedHeaders { get; init; } = new Dictionary<string, string>();

    public bool Equals(SpreadsheetMapping? other) => other is not null
        && TextOptions == other.TextOptions && SheetName == other.SheetName && HeaderRow == other.HeaderRow && FirstDataRow == other.FirstDataRow && LastDataRow == other.LastDataRow
        && CodeColumn == other.CodeColumn && DescriptionColumn == other.DescriptionColumn && QuantityColumn == other.QuantityColumn
        && UnitPriceColumn == other.UnitPriceColumn && TotalColumn == other.TotalColumn && UnitColumn == other.UnitColumn
        && (ReferenceEquals(ExpectedHeaders, other.ExpectedHeaders) || ExpectedHeaders is not null && other.ExpectedHeaders is not null
            && ExpectedHeaders.Count == other.ExpectedHeaders.Count && ExpectedHeaders.All(pair => other.ExpectedHeaders.TryGetValue(pair.Key, out string? value) && pair.Value == value));

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(TextOptions); hash.Add(SheetName); hash.Add(HeaderRow); hash.Add(FirstDataRow); hash.Add(LastDataRow);
        hash.Add(CodeColumn); hash.Add(DescriptionColumn); hash.Add(QuantityColumn); hash.Add(UnitPriceColumn); hash.Add(TotalColumn); hash.Add(UnitColumn);
        if (ExpectedHeaders is not null) foreach (var pair in ExpectedHeaders.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { hash.Add(pair.Key); hash.Add(pair.Value); }
        return hash.ToHashCode();
    }
}

public sealed record SpreadsheetCell(string Text, string? NumericText = null, bool IsError = false);
public sealed record SpreadsheetRow(int Number, IReadOnlyList<SpreadsheetCell> Cells);
public sealed record SpreadsheetSheet(string Name, IReadOnlyList<SpreadsheetRow> Rows);
public sealed record SpreadsheetDocument(IReadOnlyList<SpreadsheetSheet> Sheets, string ReaderVersion);
