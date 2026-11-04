using System.Text;
using System.Xml;
using System.Xml.Linq;
using QuoteMatch.Application.Importing;
using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Infrastructure.Spreadsheets;

public sealed partial class ExcelWorkbookReader
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Calc = "urn:org:documentfoundation:names:experimental:calc:xmlns:calcext:1.0";

    private static SpreadsheetDocument ReadOpenDocument(Stream stream, Action checkBudget)
    {
        checkBudget();
        using XmlReader reader = XmlReader.Create(stream, new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024
        });
        XDocument document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        checkBudget();
        XElement? spreadsheet = document.Root?.Element(Office + "body")?.Element(Office + "spreadsheet");
        if (document.Root?.Name.Namespace != Office || spreadsheet is null)
            throw new ImportException(ImportErrorCode.InvalidSpreadsheet, "The file does not contain an OpenDocument spreadsheet.");
        List<SpreadsheetSheet> sheets = [];
        int cells = 0, characters = 0;
        foreach (XElement table in spreadsheet.Elements(Table + "table"))
        {
            checkBudget();
            string name = (string?)table.Attribute(Table + "name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || sheets.Any(sheet => sheet.Name == name))
                throw new ImportException(ImportErrorCode.InvalidSpreadsheet, "Worksheet names must be present and unique.");
            if (sheets.Count >= 50) Limit();
            List<SpreadsheetRow> rows = [];
            long rowNumber = 1;
            foreach (XElement sourceRow in table.Descendants(Table + "table-row").Where(row => row.Ancestors(Table + "table").First() == table))
            {
                checkBudget();
                int rowRepeat = Repeat(sourceRow, Table + "number-rows-repeated");
                List<SpreadsheetCell> values = [];
                long column = 0;
                foreach (XElement cell in sourceRow.Elements().Where(element => element.Name == Table + "table-cell" || element.Name == Table + "covered-table-cell"))
                {
                    checkBudget();
                    int repeat = Repeat(cell, Table + "number-columns-repeated");
                    SpreadsheetCell value = ReadCell(cell);
                    bool populated = value.Text.Length > 0 || value.NumericText is not null || value.IsError;
                    if (populated)
                    {
                        if (column + repeat > MaximumColumns) Limit();
                        while (values.Count < column) values.Add(new(string.Empty));
                        for (int index = 0; index < repeat; index++) values.Add(value);
                    }
                    column += repeat;
                }
                if (values.Count > 0)
                {
                    if (rowNumber + rowRepeat - 1 > MaximumRows) Limit();
                    long addedCharacters = (long)values.Sum(value => value.Text.Length + (value.NumericText?.Length ?? 0)) * rowRepeat;
                    if (characters + addedCharacters > MaximumCharacters || (long)cells + values.Count * (long)rowRepeat > MaximumCells) Limit();
                    characters += (int)addedCharacters;
                    cells += values.Count * rowRepeat;
                    for (int index = 0; index < rowRepeat; index++)
                    {
                        checkBudget();
                        rows.Add(new((int)rowNumber + index, values.ToArray()));
                    }
                }
                rowNumber += rowRepeat;
            }
            int width = rows.Count == 0 ? 0 : rows.Max(row => row.Cells.Count);
            int padding = rows.Sum(row => width - row.Cells.Count);
            if ((long)cells + padding > MaximumCells) Limit();
            cells += padding;
            sheets.Add(new(name, rows.Select(row => row with
            {
                Cells = row.Cells.Concat(Enumerable.Repeat(new SpreadsheetCell(string.Empty), width - row.Cells.Count)).ToArray()
            }).ToArray()));
        }
        if (sheets.Count == 0) throw new ImportException(ImportErrorCode.InvalidSpreadsheet, "The OpenDocument spreadsheet has no worksheets.");
        return new(sheets, "QuoteMatch.OpenDocument/1");
    }

    private static SpreadsheetCell ReadCell(XElement cell)
    {
        if (cell.Name == Table + "covered-table-cell") return new(string.Empty);
        string? type = (string?)cell.Attribute(Office + "value-type");
        string? numeric = type is "float" or "currency" or "percentage" ? (string?)cell.Attribute(Office + "value") : null;
        string? cachedString = (string?)cell.Attribute(Office + "string-value");
        bool formula = cell.Attribute(Table + "formula") is not null;
        string text = string.Join('\n', cell.Elements(Text + "p").Select(ReadParagraph));
        if (text.Length == 0) text = cachedString ?? numeric ?? string.Empty;
        bool error = (string?)cell.Attribute(Calc + "value-type") == "error"
            || type is "boolean" or "date" or "time" or "error"
            || type is "float" or "currency" or "percentage" && numeric is null
            || formula && numeric is null && cachedString is null && (type != "string" || text.Length == 0);
        return new(text, numeric, error);
    }

    private static string ReadParagraph(XElement paragraph)
    {
        StringBuilder text = new();
        Append(paragraph);
        return text.ToString();
        void Append(XNode node)
        {
            if (node is XText characters) text.Append(characters.Value);
            else if (node is XElement element)
            {
                if (element.Name == Text + "s")
                {
                    int count = Repeat(element, Text + "c");
                    if ((long)text.Length + count > MaximumCharacters) Limit();
                    text.Append(' ', count);
                }
                else if (element.Name == Text + "tab") text.Append('\t');
                else if (element.Name == Text + "line-break") text.Append('\n');
                else if (element.Name.Namespace == Text) foreach (var child in element.Nodes()) Append(child);
            }
            if (text.Length > MaximumCharacters) Limit();
        }
    }

    private static int Repeat(XElement element, XName attribute)
    {
        string? value = (string?)element.Attribute(attribute);
        if (value is null) return 1;
        if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int count) && count > 0) return count;
        throw new ImportException(ImportErrorCode.InvalidSpreadsheet, "An OpenDocument repetition count is invalid.");
    }

    private static void Limit() => throw new ImportException(ImportErrorCode.ExtractionLimit, "The OpenDocument spreadsheet exceeds extraction limits.");
}
