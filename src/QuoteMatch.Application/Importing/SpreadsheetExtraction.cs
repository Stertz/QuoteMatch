using System.Globalization;
using System.Text.Json;
using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Application.Importing;

public static class SpreadsheetExtraction
{
    public static int ColumnIndex(string column)
    {
        if (string.IsNullOrWhiteSpace(column) || column.Length > 3) return -1;
        int result = 0;
        foreach (char letter in column.ToUpperInvariant())
        {
            if (letter is < 'A' or > 'Z') return -1;
            result = result * 26 + letter - 'A' + 1;
        }
        return result is > 0 and <= 256 ? result - 1 : -1;
    }

    public static string ColumnName(int index)
    {
        string name = string.Empty;
        for (int value = index + 1; value > 0; value = (value - 1) / 26)
            name = (char)('A' + (value - 1) % 26) + name;
        return name;
    }

    public static void Validate(PdfTemplate template)
    {
        SpreadsheetMapping map = template.Spreadsheet;
        if (map is null || map.TextOptions is null || !Enum.IsDefined(map.TextOptions.Separator) || !Enum.IsDefined(map.TextOptions.Encoding)
            || string.IsNullOrWhiteSpace(template.Name) || string.IsNullOrWhiteSpace(template.SupplierName)
            || string.IsNullOrWhiteSpace(template.DefaultUnit) || template.CultureName is not ("pt-BR" or "en-US")
            || template.AbsoluteTolerance < 0 || template.RelativeTolerance is < 0 or > .1m
            || string.IsNullOrWhiteSpace(map.SheetName) || map.HeaderRow is < 1 or > 10000
            || map.FirstDataRow <= map.HeaderRow || map.FirstDataRow > 10000
            || map.LastDataRow is int last && (last < map.FirstDataRow || last > 10000)
            || new[] { map.CodeColumn, map.DescriptionColumn, map.QuantityColumn, map.UnitPriceColumn }.Any(column => ColumnIndex(column) < 0)
            || new[] { map.TotalColumn, map.UnitColumn }.Any(column => column is null || column.Length > 0 && ColumnIndex(column) < 0)
            || map.ExpectedHeaders is null || map.ExpectedHeaders.Any(pair => ColumnIndex(pair.Key) < 0 || pair.Value is null))
            throw new ImportException(ImportErrorCode.InvalidTemplate, "The spreadsheet mapping is invalid.");
        string[] columns = new[] { map.CodeColumn, map.DescriptionColumn, map.QuantityColumn, map.UnitPriceColumn, map.TotalColumn, map.UnitColumn }
            .Where(column => column.Length > 0).Select(column => column.ToUpperInvariant()).ToArray();
        if (columns.Distinct(StringComparer.Ordinal).Count() != columns.Length)
            throw new ImportException(ImportErrorCode.InvalidTemplate, "Each spreadsheet field requires a different column.");
    }

    public static (ExtractedPdf Document, ExtractionResult Extraction) Extract(SpreadsheetDocument workbook, PdfTemplate template,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(template);
        SpreadsheetMapping map = template.Spreadsheet;
        SpreadsheetSheet? sheet = workbook.Sheets.SingleOrDefault(sheet => sheet.Name == map.SheetName);
        if (sheet is null)
            throw new ImportException(ImportErrorCode.InvalidSpreadsheet, "The mapped worksheet was not found.");
        SpreadsheetRow? header = sheet.Rows.SingleOrDefault(row => row.Number == map.HeaderRow);
        List<ImportIssue> issues = [];
        if (header is null || map.ExpectedHeaders.Any(pair => Cell(header, pair.Key).Text != pair.Value))
            issues.Add(new(ImportIssueCode.SpreadsheetHeaderMismatch, true));
        int maximumColumn = new[] { map.CodeColumn, map.DescriptionColumn, map.QuantityColumn, map.UnitPriceColumn, map.TotalColumn, map.UnitColumn }.Max(ColumnIndex);
        if (header is not null && maximumColumn >= header.Cells.Count)
            issues.Add(new(ImportIssueCode.SpreadsheetHeaderMismatch, true));
        CultureInfo culture = CultureInfo.GetCultureInfo(template.CultureName);
        List<CapturedItem> items = [];
        foreach (SpreadsheetRow row in sheet.Rows.Where(row => row.Number >= map.FirstDataRow && (map.LastDataRow is null || row.Number <= map.LastDataRow)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Cells.All(cell => string.IsNullOrWhiteSpace(cell.Text) && !cell.IsError)) continue;
            if (items.Count >= 5000) throw new ImportException(ImportErrorCode.ExtractionLimit, "The spreadsheet exceeds the item limit.");
            Dictionary<string, string> originals = new(StringComparer.Ordinal);
            List<ImportIssue> itemIssues = [];
            string Text(string field, string column)
            {
                SpreadsheetCell cell = Cell(row, column);
                originals[field] = cell.Text;
                if (cell.NumericText is not null) originals[field + "Numeric"] = cell.NumericText;
                if (cell.IsError) itemIssues.Add(new(ImportIssueCode.CaptureFailed, true, field));
                return cell.IsError ? string.Empty : cell.Text;
            }
            decimal? Number(string field, string column)
            {
                string text = Text(field, column);
                SpreadsheetCell cell = Cell(row, column);
                decimal? number = cell.IsError ? null : decimal.TryParse(cell.NumericText ?? text,
                    cell.NumericText is null ? NumberStyles.Number : NumberStyles.Float,
                    cell.NumericText is null ? culture : CultureInfo.InvariantCulture, out decimal value) ? value : null;
                if (number is null) itemIssues.Add(new(string.IsNullOrWhiteSpace(text) ? ImportIssueCode.MissingField : ImportIssueCode.InvalidNumber, true, field));
                else if (number <= 0 && field != "total" || number < 0) itemIssues.Add(new(ImportIssueCode.NonPositiveValue, true, field));
                return number;
            }
            string code = Text("code", map.CodeColumn);
            string description = Text("description", map.DescriptionColumn);
            if (string.IsNullOrWhiteSpace(code)) itemIssues.Add(new(ImportIssueCode.MissingField, true, "code"));
            if (string.IsNullOrWhiteSpace(description)) itemIssues.Add(new(ImportIssueCode.MissingField, true, "description"));
            decimal? quantity = Number("quantity", map.QuantityColumn);
            decimal? price = Number("unitPrice", map.UnitPriceColumn);
            decimal? calculated = null;
            try { calculated = quantity * price; }
            catch (OverflowException) { itemIssues.Add(new(ImportIssueCode.InvalidNumber, true, "total")); }
            bool derived = string.IsNullOrEmpty(map.TotalColumn);
            decimal? total = derived ? calculated : Number("total", map.TotalColumn);
            string unit = map.UnitColumn.Length == 0 ? template.DefaultUnit : Text("unit", map.UnitColumn);
            if (string.IsNullOrWhiteSpace(unit)) itemIssues.Add(new(ImportIssueCode.MissingField, true, "unit"));
            if (calculated is decimal expected && total is decimal actual && !TemplateExtraction.WithinTolerance(expected, actual, template))
                itemIssues.Add(new(ImportIssueCode.ItemTotalMismatch, false));
            items.Add(new(Guid.CreateVersion7(), 1, row.Number, JsonSerializer.Serialize(row.Cells.Select(cell => cell.Text)),
                originals, code, description, quantity, price, total, unit, itemIssues)
            { SourceSheet = sheet.Name, IsTotalCalculated = derived });
        }
        if (items.Count == 0) issues.Add(new(ImportIssueCode.NoRecords, true));
        try { _ = items.Sum(item => item.ReportedTotal ?? 0m); }
        catch (OverflowException exception) { throw new ImportException(ImportErrorCode.ExtractionLimit, "The spreadsheet total exceeds supported limits.", exception); }
        ExtractedPdf document = new([new(1, string.Join('\n', sheet.Rows.Select(row => row.Number + ": " + JsonSerializer.Serialize(row.Cells.Select(cell => cell.Text)))))], workbook.ReaderVersion);
        return (document, new(items, issues, null, null));
    }

    private static SpreadsheetCell Cell(SpreadsheetRow row, string column)
    {
        int index = ColumnIndex(column);
        return index >= 0 && index < row.Cells.Count ? row.Cells[index] : new(string.Empty);
    }
}
