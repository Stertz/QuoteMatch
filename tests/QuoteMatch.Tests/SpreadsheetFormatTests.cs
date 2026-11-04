using System.Text;
using QuoteMatch.Application.Importing;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Infrastructure.Pdf;
using QuoteMatch.Infrastructure.Persistence;
using QuoteMatch.Infrastructure.Spreadsheets;
using QuoteMatch.Testing;

namespace QuoteMatch.Tests;

public sealed class SpreadsheetFormatTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.Format.Tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Headers = """
        <table:table-row><table:table-cell office:value-type="string"><text:p>Code</text:p></table:table-cell><table:table-cell office:value-type="string"><text:p>Description</text:p></table:table-cell><table:table-cell office:value-type="string"><text:p>Quantity</text:p></table:table-cell><table:table-cell office:value-type="string"><text:p>Price</text:p></table:table-cell><table:table-cell office:value-type="string"><text:p>Total</text:p></table:table-cell></table:table-row>
        """;
    private const string Item = """
        <table:table-row><table:table-cell office:value-type="string"><text:p>000.01-U</text:p></table:table-cell><table:table-cell office:value-type="string"><text:p>Synthetic café</text:p></table:table-cell><table:table-cell office:value-type="float" office:value="2.5"><text:p>2,5</text:p></table:table-cell><table:table-cell office:value-type="currency" office:value="3.125" office:currency="BRL"><text:p>3,13</text:p></table:table-cell><table:table-cell table:formula="of:=[.C2]*[.D2]" office:value-type="float" office:value="7.8125"><text:p>7,81</text:p></table:table-cell></table:table-row>
        """;

    private static PdfTemplate Template(string sheet = "Offers") => new()
    {
        Name = "Synthetic formats",
        SupplierName = "Synthetic supplier",
        DefaultUnit = "un",
        SourceFormat = ImportSourceFormat.Excel,
        CodeKind = ProductCodeKind.Internal,
        Spreadsheet = new() { SheetName = sheet, TotalColumn = "E", ExpectedHeaders = new Dictionary<string, string> { ["A"] = "Code", ["C"] = "Quantity" } }
    };

    [Theory]
    [InlineData(".csv")]
    [InlineData(".tsv")]
    [InlineData(".ods")]
    [InlineData(".fods")]
    [InlineData(".xlsm")]
    [InlineData(".xlsb")]
    public async Task MainFormats_UseSharedReviewAndPersistExactOriginalsAndTemplateOptions(string extension)
    {
        object?[][] rows = [["Code", "Description", "Quantity", "Price", "Total"], ["000.01-U", "Synthetic café", 2.5m, 3.125m, 7.8125m]];
        byte[] bytes = extension switch
        {
            ".csv" => new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("Code;Description;Quantity;Price;Total\r\n000.01-U;Synthetic café;2,5;3,125;7,8125\r\n")).ToArray(),
            ".tsv" => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Code\tDescription\tQuantity\tPrice\tTotal\r\n000.01-U\tSynthetic café\t2,5\t3,125\t7,8125\r\n")).ToArray(),
            ".ods" or ".fods" => SyntheticWorkbook.OpenDocument("<table:table table:name=\"Offers\">" + Headers + Item + "</table:table>", extension == ".fods"),
            ".xlsm" => SyntheticWorkbook.Xlsx(rows, macroEnabled: true),
            _ => SyntheticWorkbook.Xlsb(rows)
        };
        Directory.CreateDirectory(_directory); string path = Path.Combine(_directory, "synthetic" + extension);
        await File.WriteAllBytesAsync(path, bytes, Token);
        var store = new SqliteImportStore(_directory); await store.InitializeAsync(Token);
        var template = Template(extension is ".csv" or ".tsv" ? "Data" : extension is ".xlsm" or ".xlsb" ? "Synthetic" : "Offers");
        template = await store.SaveTemplateAsync(template, Token);
        var service = new PdfImportService(new PdfPigTextExtractor(), store, new ExcelWorkbookReader());
        var preview = await service.PreviewAsync(path, template, Token);
        Assert.Empty(preview.Extraction.Issues);
        var item = Assert.Single(preview.Extraction.Items);
        Assert.Empty(item.Issues); Assert.Equal("000.01-U", item.ProductCode); Assert.Equal("Synthetic café", item.Description);
        Assert.Equal(2.5m, item.Quantity); Assert.Equal(3.125m, item.UnitPrice); Assert.Equal(7.8125m, item.ReportedTotal);
        Assert.Equal(2, item.RecordNumber); Assert.Equal(template.Spreadsheet.SheetName, item.SourceSheet);
        var saved = await service.ConfirmAsync(preview, [new(item.Id, item.ProductCode, item.Description, item.Quantity, item.UnitPrice, item.ReportedTotal, item.Unit, false, "")], Token);
        var restarted = new SqliteImportStore(_directory);
        Assert.Equal(bytes, await restarted.GetOriginalPdfAsync(saved.Id, Token));
        var reopened = (await restarted.GetProposalAsync(saved.Id, Token))!;
        Assert.Equal(template, reopened.Template); Assert.Equal(ProductCodeKind.Internal, reopened.Template.CodeKind);
        Assert.Equal(7.8125m, reopened.Review.Single().ReportedTotal); Assert.Equal("synthetic" + extension, reopened.FileName);
    }

    [Theory]
    [InlineData(";", DelimitedSeparator.Automatic)]
    [InlineData(",", DelimitedSeparator.Comma)]
    [InlineData("\t", DelimitedSeparator.Tab)]
    [InlineData("|", DelimitedSeparator.Pipe)]
    public void DelimitedText_PreservesQuotesEmbeddedNewlinesWhitespaceAndTextCodes(string separator, DelimitedSeparator selection)
    {
        string description = " Synthetic café; \"quoted\"\r\nsecond line ";
        string csv = string.Join(separator, new[] { "Code", "Description", "Quantity", "Price" }) + "\r\n"
            + string.Join(separator, new[] { "000.01-U", "\"" + description.Replace("\"", "\"\"") + "\"", "\"1.234,5\"", "\"2,3456\"" }) + "\r\n";
        var workbook = new ExcelWorkbookReader().Read(Encoding.UTF8.GetBytes(csv), "pt-BR", Token, ".csv", new() { Separator = selection });
        var (_, extraction) = SpreadsheetExtraction.Extract(workbook, Template("Data") with { Spreadsheet = Template("Data").Spreadsheet with { TotalColumn = "" } }, Token);
        Assert.Empty(extraction.Issues); var item = Assert.Single(extraction.Items);
        Assert.Equal(description, item.Description); Assert.Equal("000.01-U", item.ProductCode);
        Assert.Equal(1234.5m, item.Quantity); Assert.Equal(2.3456m, item.UnitPrice); Assert.Equal(2895.6432m, item.ReportedTotal);
        Assert.Equal(description, item.OriginalFields["description"]); Assert.True(item.IsTotalCalculated);
    }

    [Theory]
    [InlineData(DelimitedEncoding.Automatic)]
    [InlineData(DelimitedEncoding.Windows1252)]
    [InlineData(DelimitedEncoding.Utf8)]
    [InlineData(DelimitedEncoding.Utf16LittleEndian)]
    [InlineData(DelimitedEncoding.Utf16BigEndian)]
    public void DelimitedEncoding_UsesTheSavedChoiceOrAutomaticDetection(DelimitedEncoding encoding)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding sourceEncoding = encoding switch
        {
            DelimitedEncoding.Utf8 => new UTF8Encoding(false, true),
            DelimitedEncoding.Utf16LittleEndian => new UnicodeEncoding(false, false, true),
            DelimitedEncoding.Utf16BigEndian => new UnicodeEncoding(true, false, true),
            _ => Encoding.GetEncoding(1252)
        };
        byte[] bytes = sourceEncoding.GetBytes("Code;Description;Quantity;Price\r\n000.01-U;Synthetic café;2;1,25\r\n");
        var read = new ExcelWorkbookReader().Read(bytes, "pt-BR", Token, ".csv", new() { Separator = DelimitedSeparator.Semicolon, Encoding = encoding });
        Assert.Equal("Synthetic café", read.Sheets[0].Rows[1].Cells[1].Text);
        Assert.Contains(sourceEncoding.WebName, read.ReaderVersion);
        if (sourceEncoding.CodePage == 1252)
            Assert.Equal(ImportErrorCode.InvalidSpreadsheet, Assert.Throws<ImportException>(() => new ExcelWorkbookReader().Read(bytes, "pt-BR", Token, ".csv", new() { Encoding = DelimitedEncoding.Utf8 })).Code);
    }

    [Fact]
    public void OpenDocument_RepeatedAndCoveredCellsPreserveRowsWithoutExpandingTrailingEmptySheetAreas()
    {
        string repeated = Item.Replace("<table:table-row>", "<table:table-row table:number-rows-repeated=\"2\">");
        string blanks = "<table:table-row table:number-rows-repeated=\"2\"><table:table-cell table:number-columns-repeated=\"16384\"/></table:table-row>";
        string trailing = "<table:table-row table:number-rows-repeated=\"1048576\"><table:table-cell table:number-columns-repeated=\"16384\"/></table:table-row>";
        string missing = Item.Replace("<table:table-cell office:value-type=\"float\" office:value=\"2.5\"><text:p>2,5</text:p></table:table-cell>", "<table:covered-table-cell/>");
        byte[] bytes = SyntheticWorkbook.OpenDocument("<table:table table:name=\"Offers\"><table:table-header-rows>" + Headers + "</table:table-header-rows>" + blanks + "<table:table-row-group>" + repeated + missing + "</table:table-row-group>" + trailing + "</table:table>");
        var workbook = new ExcelWorkbookReader().Read(bytes, "pt-BR", Token, ".ods");
        var extraction = SpreadsheetExtraction.Extract(workbook, Template(), Token).Extraction;
        Assert.Equal([4, 5, 6], extraction.Items.Select(item => item.RecordNumber));
        Assert.Equal(2.5m, extraction.Items[0].Quantity); Assert.Equal(2.5m, extraction.Items[1].Quantity); Assert.Null(extraction.Items[2].Quantity);
        Assert.Contains(extraction.Items[2].Issues, issue => issue.IsBlocking && issue.Field == "quantity");
        Assert.Equal(4, workbook.Sheets[0].Rows.Count);
    }

    [Fact]
    public void OpenDocument_RetainsFormattedCodesTextMarkupAndMissingOrErrorFormulaCachesForReview()
    {
        string row = Item.Replace("office:value-type=\"string\"><text:p>000.01-U</text:p>", "office:value-type=\"float\" office:value=\"123\"><text:p>001.23</text:p>")
            .Replace("Synthetic café", "Synthetic<text:s text:c=\"2\"/><text:span>café</text:span><text:line-break/>next<text:tab/>part")
            .Replace("office:value-type=\"currency\" office:value=\"3.125\" office:currency=\"BRL\"><text:p>3,13</text:p>", "office:value-type=\"string\" calcext:value-type=\"error\" table:formula=\"of:=1/0\"><text:p>#DIV/0!</text:p>");
        var workbook = new ExcelWorkbookReader().Read(SyntheticWorkbook.OpenDocument("<table:table table:name=\"Offers\">" + Headers + row + "</table:table>"), "pt-BR", Token, ".ods");
        var capture = Assert.Single(SpreadsheetExtraction.Extract(workbook, Template(), Token).Extraction.Items);
        Assert.Equal("001.23", capture.ProductCode); Assert.Equal("123", capture.OriginalFields["codeNumeric"]);
        Assert.Equal("Synthetic  café\nnext\tpart", capture.Description); Assert.Null(capture.UnitPrice);
        Assert.Equal("#DIV/0!", capture.OriginalFields["unitPrice"]); Assert.Contains(capture.Issues, issue => issue.IsBlocking && issue.Field == "unitPrice");
        string uncached = Item.Replace("office:value-type=\"currency\" office:value=\"3.125\" office:currency=\"BRL\"><text:p>3,13</text:p>", "table:formula=\"of:=1+1\">");
        var missingCache = new ExcelWorkbookReader().Read(SyntheticWorkbook.OpenDocument("<table:table table:name=\"Offers\">" + Headers + uncached + "</table:table>"), "pt-BR", Token, ".ods");
        Assert.Null(SpreadsheetExtraction.Extract(missingCache, Template(), Token).Extraction.Items[0].UnitPrice);
    }

    [Theory]
    [InlineData("10001", "1", ImportErrorCode.ExtractionLimit)]
    [InlineData("1", "257", ImportErrorCode.ExtractionLimit)]
    [InlineData("0", "1", ImportErrorCode.InvalidSpreadsheet)]
    public void OpenDocument_RejectsInvalidOrExcessivePopulatedRepetitions(string rows, string columns, ImportErrorCode expected)
    {
        string xml = "<table:table table:name=\"Offers\"><table:table-row table:number-rows-repeated=\"" + rows + "\"><table:table-cell table:number-columns-repeated=\"" + columns + "\" office:value-type=\"string\"><text:p>Synthetic</text:p></table:table-cell></table:table-row></table:table>";
        Assert.Equal(expected, Assert.Throws<ImportException>(() => new ExcelWorkbookReader().Read(SyntheticWorkbook.OpenDocument(xml), "pt-BR", Token, ".ods")).Code);
    }

    [Fact]
    public void OpenDocument_RejectsDtdAndInvalidPackages_AndWrongCsvDelimiterBlocksReview()
    {
        byte[] dtd = Encoding.UTF8.GetBytes("<!DOCTYPE document [<!ENTITY value 'Synthetic'>]><document>&value;</document>");
        Assert.Equal(ImportErrorCode.InvalidSpreadsheet, Assert.Throws<ImportException>(() => new ExcelWorkbookReader().Read(dtd, "pt-BR", Token, ".fods")).Code);
        Assert.Equal(ImportErrorCode.InvalidSpreadsheet, Assert.Throws<ImportException>(() => new ExcelWorkbookReader().Read(Encoding.UTF8.GetBytes("not a package"), "pt-BR", Token, ".ods")).Code);
        var workbook = new ExcelWorkbookReader().Read(Encoding.UTF8.GetBytes("Code;Description;Quantity;Price\r\n001;Synthetic;2;3\r\n"), "pt-BR", Token, ".csv", new() { Separator = DelimitedSeparator.Comma });
        Assert.Contains(SpreadsheetExtraction.Extract(workbook, Template("Data"), Token).Extraction.Issues, issue => issue.IsBlocking && issue.Code == ImportIssueCode.SpreadsheetHeaderMismatch);
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
