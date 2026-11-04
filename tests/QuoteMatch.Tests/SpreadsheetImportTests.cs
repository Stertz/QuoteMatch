using System.Text.Json;
using QuoteMatch.Application.Importing;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Pdf;
using QuoteMatch.Infrastructure.Persistence;
using QuoteMatch.Infrastructure.Spreadsheets;
using QuoteMatch.Testing;

namespace QuoteMatch.Tests;

public sealed class SpreadsheetImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.Spreadsheet.Tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static PdfTemplate Template => new()
    {
        Name = "Synthetic spreadsheet",
        SupplierName = "Synthetic supplier",
        DefaultUnit = "un",
        SourceFormat = ImportSourceFormat.Excel,
        Spreadsheet = new() { SheetName = "Synthetic", TotalColumn = "E", ExpectedHeaders = new Dictionary<string, string> { ["A"] = "Supplier code", ["C"] = "Quantity" } }
    };
    private static object?[][] Rows => [["Supplier code", "Description", "Quantity", "Unit price", "Total"], ["000.01-U", "Synthetic product", 2m, 12.345m, 24.69m]];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualExcelReaders_PreserveCodesNumbersAndRowEvidence(bool binary)
    {
        byte[] bytes = binary ? SyntheticWorkbook.Xls(Rows) : SyntheticWorkbook.Xlsx(Rows, cachedFormula: true);
        var workbook = new ExcelWorkbookReader().Read(bytes, "pt-BR", Token);
        var template = Template with { Spreadsheet = Template.Spreadsheet with { SheetName = workbook.Sheets[0].Name } };
        var (_, extraction) = SpreadsheetExtraction.Extract(workbook, template, Token);
        Assert.Empty(extraction.Issues);
        CapturedItem item = Assert.Single(extraction.Items);
        Assert.Empty(item.Issues); Assert.Equal("000.01-U", item.ProductCode); Assert.Equal(2m, item.Quantity);
        Assert.Equal(12.345m, item.UnitPrice); Assert.Equal(24.69m, item.ReportedTotal);
        Assert.Equal(2, item.RecordNumber); Assert.Equal(workbook.Sheets[0].Name, item.SourceSheet);
        Assert.Equal("000.01-U", item.OriginalFields["code"]); Assert.Equal("12.345", item.OriginalFields["unitPriceNumeric"]);
        Assert.Contains("Synthetic product", item.SourceText); Assert.False(item.IsTotalCalculated);
    }

    [Fact]
    public void NumericCodes_PreserveFormattedLeadingZerosAndPunctuation_WhilePricesUseUnderlyingPrecision()
    {
        object?[][] rows = [["Supplier code", "Description", "Quantity", "Price", "Total"], [123m, "Synthetic", 3m, .123456789012345m, .370370367037035m]];
        var workbook = new ExcelWorkbookReader().Read(SyntheticWorkbook.Xlsx(rows, codeFormat: "000\".\"00"), "pt-BR", Token);
        var (_, extraction) = SpreadsheetExtraction.Extract(workbook, Template, Token);
        var item = Assert.Single(extraction.Items);
        Assert.Equal("001.23", item.ProductCode); Assert.Equal(.123456789012345m, item.UnitPrice);
        Assert.Equal(.370370367037035m, item.ReportedTotal);
    }

    [Theory]
    [InlineData("pt-BR", "1.234,5", "2,3456")]
    [InlineData("en-US", "1,234.5", "2.3456")]
    public void TextNumbers_UseTemplateCulture_AndOptionalTotalsAreExplicitlyDerived(string culture, string quantity, string price)
    {
        var workbook = new ExcelWorkbookReader().Read(SyntheticWorkbook.Xlsx([["Supplier code", "Description", "Quantity", "Unit price"], ["00-1", "Synthetic", quantity, price]]), culture, Token);
        var template = Template with { CultureName = culture, Spreadsheet = Template.Spreadsheet with { TotalColumn = "" } };
        CapturedItem item = Assert.Single(SpreadsheetExtraction.Extract(workbook, template, Token).Extraction.Items);
        Assert.Equal(1234.5m, item.Quantity); Assert.Equal(2.3456m, item.UnitPrice); Assert.Equal(2895.64320m, item.ReportedTotal);
        Assert.True(item.IsTotalCalculated); Assert.Equal(quantity, item.OriginalFields["quantity"]);
    }

    [Fact]
    public void MissingRowsAndFields_NeverInheritValues_AndShiftedHeadersBlockConfirmation()
    {
        var rows = Rows.Concat(new object?[][] { [null, null, null, null, null], ["002", "Unpriced", 1m, null, null], [null, "Unidentified", null, 3m, null], ["Footer", null, null, null, 99m] }).ToArray();
        var workbook = new ExcelWorkbookReader().Read(SyntheticWorkbook.Xlsx(rows), "pt-BR", Token);
        var extraction = SpreadsheetExtraction.Extract(workbook, Template, Token).Extraction;
        Assert.Equal(4, extraction.Items.Count); Assert.Null(extraction.Items[1].UnitPrice); Assert.Null(extraction.Items[1].ReportedTotal);
        Assert.Null(extraction.Items[2].Quantity); Assert.Equal("", extraction.Items[2].ProductCode);
        Assert.Contains(extraction.Items[1].Issues, issue => issue.IsBlocking && issue.Field == "unitPrice");
        Assert.Equal(4, extraction.Items[1].RecordNumber);
        var bounded = SpreadsheetExtraction.Extract(workbook, Template with { Spreadsheet = Template.Spreadsheet with { LastDataRow = 4 } }, Token).Extraction;
        Assert.Equal(2, bounded.Items.Count);
        var shifted = SpreadsheetExtraction.Extract(workbook, Template with { Spreadsheet = Template.Spreadsheet with { ExpectedHeaders = new Dictionary<string, string> { ["A"] = "Other heading" } } }, Token).Extraction;
        Assert.Contains(shifted.Issues, issue => issue.Code == ImportIssueCode.SpreadsheetHeaderMismatch && issue.IsBlocking);
        var empty = SpreadsheetExtraction.Extract(workbook, Template with { Spreadsheet = Template.Spreadsheet with { FirstDataRow = 20 } }, Token).Extraction;
        Assert.Contains(empty.Issues, issue => issue.Code == ImportIssueCode.NoRecords && issue.IsBlocking);
    }

    [Fact]
    public void MappingValidation_RejectsDuplicateOrInvalidColumnsAndRowRanges_WithoutRequiringPdfRegex()
    {
        TemplateExtraction.Validate(Template with { RecordPattern = "(", RecordStartPattern = "(" });
        foreach (var mapping in new[] { Template.Spreadsheet with { CodeColumn = "C" }, Template.Spreadsheet with { QuantityColumn = "A1" }, Template.Spreadsheet with { UnitPriceColumn = "XFD" }, Template.Spreadsheet with { HeaderRow = 3 }, Template.Spreadsheet with { LastDataRow = 1 } })
            Assert.Equal(ImportErrorCode.InvalidTemplate, Assert.Throws<ImportException>(() => TemplateExtraction.Validate(Template with { Spreadsheet = mapping })).Code);
        var old = JsonSerializer.Deserialize<PdfTemplate>("{\"Name\":\"Legacy\"}")!;
        Assert.Equal(ImportSourceFormat.Pdf, old.SourceFormat);
    }

    [Fact]
    public async Task WorkbookReview_RoundTripsOriginalFileAndVersionedMapping_ThenUsesLastPurchaseAndPreparesANewOrder()
    {
        Directory.CreateDirectory(_directory);
        byte[] bytes = SyntheticWorkbook.Xlsx(Rows);
        string path = Path.Combine(_directory, "synthetic.xlsx"); await File.WriteAllBytesAsync(path, bytes, Token);
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        Quotation quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic Excel study", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0), Token);
        PdfTemplate template = await store.SaveTemplateAsync(Template, Token);
        PdfImportService service = new(new PdfPigTextExtractor(), store, new ExcelWorkbookReader());
        ImportPreview preview = await service.PreviewAsync(path, template, Token);
        CapturedItem capture = Assert.Single(preview.Extraction.Items);
        ReviewedItem review = new(capture.Id, capture.ProductCode, capture.Description, capture.Quantity, capture.UnitPrice, capture.ReportedTotal, capture.Unit, false, "");
        SavedProposal saved = await service.ConfirmAsync(preview, [review], Token, quotation.Id);
        var restarted = new SqliteImportStore(_directory);
        Assert.Equal(bytes, await restarted.GetOriginalPdfAsync(saved.Id, Token));
        SavedProposal loaded = (await restarted.GetProposalAsync(saved.Id, Token))!;
        Assert.Equal("000.01-U", loaded.Review[0].ProductCode); Assert.Equal(template.Version, loaded.Template.Version);
        Assert.Equal("Synthetic", loaded.Extraction.Items[0].SourceSheet);
        await store.SaveTemplateAsync(template with { Spreadsheet = template.Spreadsheet with { UnitPriceColumn = "F" } }, Token);
        Assert.Equal("D", (await restarted.GetProposalAsync(saved.Id, Token))!.Template.Spreadsheet.UnitPriceColumn);
        AnalysisInput input = await store.GetAnalysisInputAsync(quotation.Id, Token);
        ErpProductCandidate product = new("ERP-001", "Synthetic product", "Synthetic family", false, "brand-1", "Synthetic brand", "000.01-U", 1, "un", new(2026, 1, 1), 10m, "un", "un", "synthetic-catalog", DateTime.UtcNow)
        { Brand = new("synthetic-catalog", "brand-1", "Synthetic brand") };
        string key = ErpProductIdentity.GroupKey(product);
        AnalysisState state = await store.SaveAnalysisAsync(input.State with
        {
            Links = [new(saved.Id, capture.Id, key, product.Name, "un", 1m, product, DateTime.UtcNow)],
            Demands = [new(key, "un", 2m)]
        }, Token);
        AnalyzedGroup group = Assert.Single(ImportedQuotationAnalysis.Calculate([loaded], state));
        Assert.Equal(12.345m, Assert.Single(group.Offers).BaseUnitPrice); Assert.Equal(10m, group.Offers[0].Link!.Product!.LastPurchaseUnitPrice);
        var purchase = await store.PreparePurchaseAsync(new(quotation.Id, input.Quotation.Revision, state.Version, 0, [new(saved.Id, capture.Id)]), Token);
        var proposal = Assert.Single(PurchaseSynchronization.Proposals(purchase));
        BuyingSupplierSnapshot supplier = new(product.Brand!, new("synthetic-catalog", "buying-supplier", "Synthetic buying supplier"), "history", "revision", "branch", new(2026, 1, 1), DateTime.UtcNow);
        var creation = PurchaseSynchronization.Compare(proposal, supplier, "branch", null);
        Assert.Null(creation.Target);
        var selection = PurchaseSynchronization.Select(creation, creation.Differences.Select(change => change.Id).ToArray(), false);
        Assert.Equal(12.345m, Assert.Single(selection.Lines).UnitPrice); Assert.Equal(24.69m, selection.ItemSubtotal);
        Assert.Empty(await store.GetSynchronizationOperationsAsync(quotation.Id, Token));
    }

    [Fact]
    public async Task InvalidFilesCancellationAndMismatchedFormats_ReturnTypedErrors()
    {
        Directory.CreateDirectory(_directory); var store = new SqliteImportStore(_directory);
        var service = new PdfImportService(new PdfPigTextExtractor(), store, new ExcelWorkbookReader());
        string path = Path.Combine(_directory, "invalid.xls"); await File.WriteAllTextAsync(path, "not a workbook", Token);
        Assert.Equal(ImportErrorCode.InvalidSpreadsheet, (await Assert.ThrowsAsync<ImportException>(() => service.PreviewAsync(path, Template, Token))).Code);
        Assert.Equal(ImportErrorCode.SourceFormatMismatch, (await Assert.ThrowsAsync<ImportException>(() => service.PreviewAsync(path, Template with { SourceFormat = ImportSourceFormat.Pdf }, Token))).Code);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PreviewAsync(path, Template, canceled.Token));
    }

    [Fact]
    public void ItemLimitAndMissingWorksheet_BlockExtractionInsteadOfReturningAPartialSuccess()
    {
        object?[][] rows = new[] { Rows[0] }.Concat(Enumerable.Repeat(Rows[1], 5001)).ToArray();
        var workbook = new ExcelWorkbookReader().Read(SyntheticWorkbook.Xlsx(rows), "pt-BR", Token);
        Assert.Equal(ImportErrorCode.ExtractionLimit, Assert.Throws<ImportException>(() => SpreadsheetExtraction.Extract(workbook, Template, Token)).Code);
        Assert.Equal(ImportErrorCode.InvalidSpreadsheet, Assert.Throws<ImportException>(() => SpreadsheetExtraction.Extract(workbook,
            Template with { Spreadsheet = Template.Spreadsheet with { SheetName = "Missing" } }, Token)).Code);
    }

    [Fact]
    public void ExpandedWorkbookLimit_IsCheckedBeforeTheReaderExpandsCompressedContent()
    {
        using MemoryStream stream = new();
        using (System.IO.Compression.ZipArchive zip = new(stream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var entry = zip.CreateEntry("oversized.xml").Open();
            byte[] zeros = new byte[1024 * 1024];
            for (int index = 0; index < 65; index++) entry.Write(zeros);
        }
        Assert.Equal(ImportErrorCode.ExtractionLimit, Assert.Throws<ImportException>(() => new ExcelWorkbookReader().Read(stream.ToArray(), "pt-BR", Token)).Code);
    }

    [Fact]
    public async Task HeaderMismatchMissingTotalsAndChangedSource_CannotBeConfirmedAsAValidProposal()
    {
        Directory.CreateDirectory(_directory); string path = Path.Combine(_directory, "synthetic.xlsx");
        await File.WriteAllBytesAsync(path, SyntheticWorkbook.Xlsx(Rows), Token);
        var store = new SqliteImportStore(_directory); var service = new PdfImportService(new PdfPigTextExtractor(), store, new ExcelWorkbookReader());
        var mismatch = await service.PreviewAsync(path, Template with { Spreadsheet = Template.Spreadsheet with { ExpectedHeaders = new Dictionary<string, string> { ["A"] = "Different" } } }, Token);
        var capture = mismatch.Extraction.Items[0];
        ReviewedItem review = new(capture.Id, capture.ProductCode, capture.Description, capture.Quantity, capture.UnitPrice, capture.ReportedTotal, capture.Unit, false, "");
        Assert.Equal(ImportErrorCode.InvalidReview, (await Assert.ThrowsAsync<ImportException>(() => service.ConfirmAsync(mismatch, [review], Token))).Code);
        var valid = await service.PreviewAsync(path, Template, Token);
        review = review with { CaptureId = valid.Extraction.Items[0].Id, UnitPrice = null };
        Assert.Equal(ImportErrorCode.InvalidReview, (await Assert.ThrowsAsync<ImportException>(() => service.ConfirmAsync(valid, [review], Token))).Code);
        review = review with { UnitPrice = 12.345m, ReportedTotal = null };
        Assert.Equal(ImportErrorCode.InvalidReview, (await Assert.ThrowsAsync<ImportException>(() => service.ConfirmAsync(valid, [review], Token))).Code);
        review = review with { ReportedTotal = 24.69m };
        valid.OriginalPdf[0] ^= 1;
        Assert.Equal(ImportErrorCode.SourceChanged, (await Assert.ThrowsAsync<ImportException>(() => service.ConfirmAsync(valid, [review], Token))).Code);
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
