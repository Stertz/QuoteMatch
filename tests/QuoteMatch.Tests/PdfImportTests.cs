using System.Security.Cryptography;
using System.Text.Json;
using QuoteMatch.Application.Importing;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Infrastructure.Pdf;
using QuoteMatch.Infrastructure.Persistence;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace QuoteMatch.Tests;

public sealed partial class PdfImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.Import.Tests", Guid.NewGuid().ToString("N"));
    private static PdfTemplate Template => new()
    {
        Name = "Synthetic table",
        SupplierName = "Example supplier",
        IdentificationPattern = "Example supplier",
        TableEndPattern = "^Total",
        DocumentTotalPattern = @"^Total (?<total>[\d.,]+)",
        ExpectedCountPattern = @"Count (?<count>\d+)"
    };
    private static ExtractedPdf Document(params string[] pages) => new(pages.Select((text, index) => new ExtractedPage(index + 1, text)).ToArray(), "Synthetic/1");
    private static ReviewedItem Review(CapturedItem item) => new(item.Id, item.ProductCode, item.Description, item.Quantity,
        item.UnitPrice, item.ReportedTotal, item.Unit, false, string.Empty);

    [Fact]
    public void MultiPageCapture_PreservesOriginalFieldsAndAssociatesValuesPerRecord()
    {
        ExtractionResult result = TemplateExtraction.Extract(Document("Example supplier\nCount 3\n001 Copper wire 1.500 2,1234 3.185,10",
            "002 Blue wire 2 4,5 9,00\n003 Red wire 3 1,20 3,60\nTotal 3.197,70"), Template, TestContext.Current.CancellationToken);
        Assert.Empty(result.Issues);
        Assert.Equal(3, result.ExpectedItemCount);
        Assert.Equal(3197.70m, result.ReportedDocumentTotal);
        Assert.Collection(result.Items,
            item => { Assert.Equal("001", item.ProductCode); Assert.Equal(1500m, item.Quantity); Assert.Equal(2.1234m, item.UnitPrice); Assert.Equal(3185.10m, item.ReportedTotal); Assert.Equal(1, item.PageNumber); Assert.Equal("1.500", item.OriginalFields["quantity"]); },
            item => { Assert.Equal("002", item.ProductCode); Assert.Equal(2m, item.Quantity); Assert.Equal(4.5m, item.UnitPrice); Assert.Equal(9m, item.ReportedTotal); Assert.Equal(2, item.PageNumber); },
            item => { Assert.Equal("003", item.ProductCode); Assert.Equal(3m, item.Quantity); Assert.Equal(1.2m, item.UnitPrice); Assert.Equal(3.6m, item.ReportedTotal); });
        Assert.All(result.Items, item => Assert.Empty(item.Issues));
    }

    [Fact]
    public void InvalidRecord_IsRetainedWithoutBorrowingFieldsFromPreviousItem()
    {
        ExtractionResult result = TemplateExtraction.Extract(Document("Example supplier\nCount 2\n001 Wire 3 2,00 6,00\n002 Incomplete record\nTotal 6,00"), Template, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items[1].Issues, issue => issue.Code == ImportIssueCode.CaptureFailed && issue.IsBlocking);
        Assert.Null(result.Items[1].Quantity);
        Assert.Null(result.Items[1].UnitPrice);
        Assert.Equal("002 Incomplete record", result.Items[1].SourceText);
    }

    [Theory]
    [InlineData("pt-BR", "1.234,50", "2,00", "2.469,00")]
    [InlineData("en-US", "1,234.50", "2.00", "2,469.00")]
    public void Culture_IsExplicitAndDecimalsAreExact(string culture, string quantity, string price, string total)
    {
        ExtractionResult result = TemplateExtraction.Extract(Document($"001 Wire {quantity} {price} {total}"),
            Template with { CultureName = culture, IdentificationPattern = "", DocumentTotalPattern = "", ExpectedCountPattern = "" }, TestContext.Current.CancellationToken);
        Assert.Equal(1234.50m, result.Items.Single().Quantity);
        Assert.Equal(2469m, result.Items.Single().ReportedTotal);
        Assert.Empty(result.Items.Single().Issues);
    }

    [Fact]
    public void WrongTemplate_MissingRecordsAndCountMismatchBlockConfirmation()
    {
        ExtractionResult result = TemplateExtraction.Extract(Document("Wrong supplier\nCount 5\nTotal 0,00"), Template, TestContext.Current.CancellationToken);
        Assert.Contains(result.Issues, issue => issue.Code == ImportIssueCode.WrongTemplate && issue.IsBlocking);
        Assert.Contains(result.Issues, issue => issue.Code == ImportIssueCode.NoRecords && issue.IsBlocking);
        Assert.Contains(result.Issues, issue => issue.Code == ImportIssueCode.ItemCountMismatch && issue.IsBlocking);
        Assert.Contains(TemplateExtraction.Extract(Document(" "), Template, TestContext.Current.CancellationToken).Issues, issue => issue.Code == ImportIssueCode.NoText);
    }

    [Fact]
    public void ConflictingTotalsBlock_WhileRoundingAndArithmeticDifferencesAreVisibleWarnings()
    {
        ExtractionResult result = TemplateExtraction.Extract(Document("Example supplier\nCount 1\n001 Wire 2 3,00 7,00\nTotal 8,00\nTotal 9,00"), Template, TestContext.Current.CancellationToken);
        Assert.Contains(result.Items[0].Issues, issue => issue.Code == ImportIssueCode.ItemTotalMismatch && !issue.IsBlocking);
        Assert.Contains(result.Issues, issue => issue.Code == ImportIssueCode.DocumentTotalMismatch && !issue.IsBlocking);
        Assert.Contains(result.Issues, issue => issue.Code == ImportIssueCode.ConflictingDocumentTotals && issue.IsBlocking);
    }

    [Theory]
    [InlineData("(")]
    [InlineData(".*")]
    public void InvalidCaptureSyntaxOrGroups_RejectTemplate(string pattern) =>
        Assert.Equal(ImportErrorCode.InvalidTemplate, Assert.Throws<ImportException>(() => TemplateExtraction.Validate(Template with { RecordPattern = pattern })).Code);

    [Fact]
    public void NullPatternFromJson_IsRejectedAsInvalidTemplate()
    {
        PdfTemplate template = JsonSerializer.Deserialize<PdfTemplate>("{\"Name\":\"Example\",\"SupplierName\":\"Example\",\"TableEndPattern\":null}")!;
        Assert.Equal(ImportErrorCode.InvalidTemplate, Assert.Throws<ImportException>(() => TemplateExtraction.Validate(template)).Code);
    }

    [Fact]
    public void Cancellation_IsObservedEvenForAnEmptyDocument()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => TemplateExtraction.Extract(Document(""), Template, source.Token));
    }

    [Fact]
    public void PathologicalRegex_IsStoppedByFiniteTimeout()
    {
        PdfTemplate template = Template with { IdentificationPattern = "(a+)+$", RecordStartPattern = "^item" };
        ImportException error = Assert.Throws<ImportException>(() => TemplateExtraction.Extract(Document(new string('a', 20000) + "!"), template, TestContext.Current.CancellationToken));
        Assert.Equal(ImportErrorCode.RegexTimeout, error.Code);
    }

    [Fact]
    public async Task RealPdfAndSqlite_RoundTripReviewOriginalBytesAndVersionSnapshotAfterRestart()
    {
        Directory.CreateDirectory(_directory);
        SqliteImportStore store = new(_directory);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        PdfTemplate first = await store.SaveTemplateAsync(Template, TestContext.Current.CancellationToken);
        byte[] bytes = BuildPdf();
        string path = Path.Combine(_directory, "synthetic.pdf");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        PdfImportService service = new(new PdfPigTextExtractor(), store);
        ImportPreview preview = await service.PreviewAsync(path, first, TestContext.Current.CancellationToken);
        Assert.Empty(preview.Extraction.Issues);
        Assert.Equal(2, preview.Extraction.Items.Count);
        Assert.Equal(3.1234m, preview.Extraction.Items[0].UnitPrice);
        ReviewedItem[] review = preview.Extraction.Items.Select(Review).ToArray();
        review[0] = review[0] with { Description = "Reviewed wire", UnitPrice = 3.1234567890123456789m };
        review[1] = review[1] with { IsExcluded = true, ExclusionReason = "Duplicate offer" };
        SavedProposal saved = await service.ConfirmAsync(preview, review, TestContext.Current.CancellationToken);
        Assert.Equal(1, saved.IncludedItemCount);
        File.Delete(path);
        PdfTemplate second = await store.SaveTemplateAsync(first with { Name = "Updated template" }, TestContext.Current.CancellationToken);
        Assert.Equal(2, second.Version);
        Assert.Equal(ImportErrorCode.StorageConflict, (await Assert.ThrowsAsync<ImportException>(() => store.SaveTemplateAsync(first, TestContext.Current.CancellationToken))).Code);
        SqliteImportStore reopened = new(_directory);
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        SavedProposal loaded = (await reopened.GetProposalAsync(saved.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(1, loaded.Template.Version);
        Assert.Equal("Synthetic table", loaded.Template.Name);
        Assert.Equal("Reviewed wire", loaded.Review[0].Description);
        Assert.Equal(3.1234567890123456789m, loaded.Review[0].UnitPrice);
        Assert.Equal("Copper wire", loaded.Extraction.Items[0].Description);
        Assert.Equal(3.1234m, loaded.Extraction.Items[0].UnitPrice);
        Assert.True(loaded.Review[1].IsExcluded);
        Assert.Equal(bytes, await reopened.GetOriginalPdfAsync(saved.Id, TestContext.Current.CancellationToken));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), loaded.SourceSha256);
        ProposalSummary summary = Assert.Single(await reopened.GetProposalsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(6.2468m, summary.ReportedItemTotal);
        Assert.Equal(1, summary.ItemCount);
        Assert.Equal(2, Assert.Single(await reopened.GetLatestTemplatesAsync(TestContext.Current.CancellationToken)).Version);
        Assert.Equal(ImportErrorCode.StorageConflict, (await Assert.ThrowsAsync<ImportException>(() => service.ConfirmAsync(preview, review, TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task Confirmation_RejectsInvalidValuesExclusionsAndChangedSource()
    {
        Directory.CreateDirectory(_directory);
        SqliteImportStore store = new(_directory);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        string path = Path.Combine(_directory, "synthetic.pdf");
        await File.WriteAllBytesAsync(path, BuildPdf(), TestContext.Current.CancellationToken);
        PdfImportService service = new(new PdfPigTextExtractor(), store);
        ImportPreview preview = await service.PreviewAsync(path, Template, TestContext.Current.CancellationToken);
        ReviewedItem[] valid = preview.Extraction.Items.Select(Review).ToArray();
        foreach (ReviewedItem invalid in new[] { valid[0] with { Quantity = null }, valid[0] with { UnitPrice = 0 }, valid[0] with { IsExcluded = true, ExclusionReason = "" } })
            Assert.Equal(ImportErrorCode.InvalidReview, (await Assert.ThrowsAsync<ImportException>(() => service.ConfirmAsync(preview, [invalid, valid[1]], TestContext.Current.CancellationToken))).Code);
        Assert.Equal(ImportErrorCode.InvalidReview, (await Assert.ThrowsAsync<ImportException>(() => service.ConfirmAsync(preview, [valid[0], valid[0]], TestContext.Current.CancellationToken))).Code);
        Assert.Equal(ImportErrorCode.InvalidReview, (await Assert.ThrowsAsync<ImportException>(() => service.ConfirmAsync(preview, [valid[0] with { ReportedTotal = decimal.MaxValue }, valid[1]], TestContext.Current.CancellationToken))).Code);
        preview.OriginalPdf[^1] ^= 1;
        Assert.Equal(ImportErrorCode.SourceChanged, (await Assert.ThrowsAsync<ImportException>(() => service.ConfirmAsync(preview, valid, TestContext.Current.CancellationToken))).Code);
        Assert.Empty(await store.GetProposalsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AggregateOverflow_IsRejectedEvenWithoutADocumentTotalRule()
    {
        PdfTemplate template = Template with { IdentificationPattern = "", ExpectedCountPattern = "", DocumentTotalPattern = "" };
        ImportException error = Assert.Throws<ImportException>(() => TemplateExtraction.Extract(Document(
            "001 Wire 1 1 79228162514264337593543950335\n002 Wire 1 1 79228162514264337593543950335"), template, TestContext.Current.CancellationToken));
        Assert.Equal(ImportErrorCode.ExtractionLimit, error.Code);
    }

    [Fact]
    public async Task NonPdfFile_IsRejectedBeforeExtraction()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "not-a-pdf.pdf");
        await File.WriteAllTextAsync(path, "This is not a PDF document.", TestContext.Current.CancellationToken);
        PdfImportService service = new(new PdfPigTextExtractor(), new SqliteImportStore(_directory));
        Assert.Equal(ImportErrorCode.InvalidPdf, (await Assert.ThrowsAsync<ImportException>(() => service.PreviewAsync(path, Template, TestContext.Current.CancellationToken))).Code);
    }

    private static byte[] BuildPdf()
    {
        PdfDocumentBuilder builder = new();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var first = builder.AddPage(PageSize.A4);
        first.AddText("Example supplier", 12, new PdfPoint(40, 800), font);
        first.AddText("Count 2", 12, new PdfPoint(40, 780), font);
        first.AddText("001 Copper wire 2 3,1234 6,2468", 12, new PdfPoint(40, 740), font);
        var second = builder.AddPage(PageSize.A4);
        second.AddText("002 Blue wire 3 2,00 6,00", 12, new PdfPoint(40, 740), font);
        second.AddText("Total 12,2468", 12, new PdfPoint(40, 700), font);
        return builder.Build();
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}



