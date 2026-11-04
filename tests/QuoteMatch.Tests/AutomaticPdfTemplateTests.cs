using System.Security.Cryptography;
using QuoteMatch.Application.Importing;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Infrastructure.Pdf;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Tests;

public sealed partial class PdfImportTests
{
    private async Task<(PdfImportService Service, PdfImportSource Source, CountingTemplateExtractor Extractor, string Path)> DetectionSourceAsync()
    {
        Directory.CreateDirectory(_directory);
        string path = System.IO.Path.Combine(_directory, "synthetic.pdf");
        await File.WriteAllBytesAsync(path, BuildPdf(), TestContext.Current.CancellationToken);
        var extractor = new CountingTemplateExtractor();
        var service = new PdfImportService(extractor, new SqliteImportStore(_directory));
        return (service, await service.ReadPdfAsync(path, TestContext.Current.CancellationToken), extractor, path);
    }

    [Fact]
    public async Task Detection_ReusesOnePdfReadForCompetingLayoutsAndStopsAtTheFirstCapturableItem()
    {
        var (service, source, extractor, _) = await DetectionSourceAsync();
        var correct = Template with { Name = "Matching layout" };
        var result = await service.SuggestAsync(source,
            [Template with { IdentificationPattern = "Other company" }, Template with { RecordStartPattern = "^ITEM" }, correct,
                Template with { IdentificationPattern = "(" }], TestContext.Current.CancellationToken);
        Assert.Equal(correct, result.Preview!.Template); Assert.Equal(2, result.Preview.Extraction.Items.Count);
        Assert.Equal(0, result.SkippedTemplates); Assert.False(result.LimitReached);
        Assert.Equal(1, extractor.Calls);
        Assert.Empty(result.Preview.Extraction.Issues);
    }

    [Fact]
    public async Task Detection_PreservesCallerPriorityWhenSeveralTemplatesFindAnItem()
    {
        var (service, source, extractor, _) = await DetectionSourceAsync();
        var first = Template with { Name = "Preferred layout" };
        var second = Template with { Name = "Other matching layout" };
        Assert.Equal(first, (await service.SuggestAsync(source, [first, second], TestContext.Current.CancellationToken)).Preview!.Template);
        Assert.Equal(second, (await service.SuggestAsync(source, [second, first], TestContext.Current.CancellationToken)).Preview!.Template);
        Assert.Equal(1, extractor.Calls);
    }

    [Fact]
    public async Task Detection_RequiresAnIdentifierAndAnItemAndSkipsSpreadsheetTemplates()
    {
        var (service, source, extractor, _) = await DetectionSourceAsync();
        var disabled = await service.SuggestAsync(source, [Template with { IdentificationPattern = "" }, Template with { SourceFormat = ImportSourceFormat.Excel }], TestContext.Current.CancellationToken);
        Assert.Null(disabled.Preview); Assert.Equal(0, extractor.Calls);
        var companyOnly = await service.SuggestAsync(source, [Template with { RecordStartPattern = "^ITEM" }], TestContext.Current.CancellationToken);
        Assert.Null(companyOnly.Preview); Assert.False(companyOnly.LimitReached); Assert.Equal(1, extractor.Calls);
    }

    [Fact]
    public async Task Detection_SkipsInvalidExpressionsAndKeepsPartialExtractionVisibleForReview()
    {
        var (service, source, extractor, _) = await DetectionSourceAsync();
        var result = await service.SuggestAsync(source,
            [Template with { IdentificationPattern = "(" }, Template with { RecordPattern = @"^(?<code>001) (?<description>Copper wire) (?<quantity>[\d.,]+) (?<unitPrice>[\d.,]+) (?<total>[\d.,]+)$" }], TestContext.Current.CancellationToken);
        Assert.Equal(1, result.SkippedTemplates); Assert.Equal(2, result.Preview!.Extraction.Items.Count);
        Assert.Contains(result.Preview.Extraction.Items[1].Issues, issue => issue.Code == ImportIssueCode.CaptureFailed && issue.IsBlocking);
        Assert.Equal(1, extractor.Calls);
        Assert.Equal(ImportErrorCode.InvalidReview, (await Assert.ThrowsAsync<ImportException>(() => service.ConfirmAsync(result.Preview,
            result.Preview.Extraction.Items.Select(Review).ToArray(), TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task ManualOverride_ReusesTextPerStrategyAndInvalidatesTheCacheWhenFileBytesChange()
    {
        var (service, source, extractor, path) = await DetectionSourceAsync();
        var match = await service.SuggestAsync(source, [Template with { RecordStartPattern = "^ITEM" }, Template with { TextStrategy = PdfTextStrategy.ContentOrder }], TestContext.Current.CancellationToken);
        Assert.NotNull(match.Preview); Assert.Equal(2, extractor.Calls);
        await service.PreviewAsync(source, Template, TestContext.Current.CancellationToken); Assert.Equal(2, extractor.Calls);
        Assert.Same(source, await service.ReadPdfAsync(path, TestContext.Current.CancellationToken, source));
        byte[] changed = [.. await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken), (byte)'\n'];
        await File.WriteAllBytesAsync(path, changed, TestContext.Current.CancellationToken);
        var next = await service.ReadPdfAsync(path, TestContext.Current.CancellationToken, source);
        Assert.NotSame(source, next); Assert.NotEqual(source.SourceSha256, next.SourceSha256);
        var preview = await service.PreviewAsync(next, Template, TestContext.Current.CancellationToken);
        Assert.Equal(3, extractor.Calls); Assert.Equal(changed, preview.OriginalPdf);
    }

    [Fact]
    public async Task PreviewMutation_DoesNotCorruptTheReusableOriginalSource()
    {
        var (service, source, extractor, _) = await DetectionSourceAsync();
        var first = await service.PreviewAsync(source, Template, TestContext.Current.CancellationToken);
        first.OriginalPdf[^1] ^= 1;
        var next = await service.PreviewAsync(source, Template, TestContext.Current.CancellationToken);
        Assert.Equal(source.SourceSha256, Convert.ToHexString(SHA256.HashData(next.OriginalPdf)));
        Assert.NotEqual(first.OriginalPdf, next.OriginalPdf); Assert.Equal(1, extractor.Calls);
    }

    [Fact]
    public async Task Detection_ObservesCancellationBeforeReadingAnyTemplate()
    {
        var (service, source, extractor, _) = await DetectionSourceAsync();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SuggestAsync(source, [Template], canceled.Token));
        Assert.Equal(0, extractor.Calls);
    }

    [Fact]
    public async Task Detection_ContainsAPathologicalIdentifierAndContinuesWithAnotherTemplate()
    {
        var (_, source, _, _) = await DetectionSourceAsync();
        var document = Document("Example supplier\n" + new string('a', 20000) + "!\n001 Wire 1 2,00 2,00");
        var service = new PdfImportService(new FixedTemplateExtractor(document), new SqliteImportStore(_directory));
        var valid = Template with { ExpectedCountPattern = "", DocumentTotalPattern = "" };
        var suggestion = await service.SuggestAsync(source, [valid with { IdentificationPattern = "(a+)+$" }, valid], TestContext.Current.CancellationToken);
        Assert.Equal(1, suggestion.SkippedTemplates); Assert.Equal("001", Assert.Single(suggestion.Preview!.Extraction.Items).ProductCode);
    }

    [Fact]
    public async Task Detection_ReturnsAManualFallbackWhenItsWorkIsCanceledInternally()
    {
        var (_, source, _, _) = await DetectionSourceAsync();
        var service = new PdfImportService(new InterruptedTemplateExtractor(), new SqliteImportStore(_directory));
        var result = await service.SuggestAsync(source, [Template], TestContext.Current.CancellationToken);
        Assert.Null(result.Preview); Assert.True(result.LimitReached);
    }

    private sealed class CountingTemplateExtractor : IPdfTextExtractor
    {
        public int Calls { get; private set; }
        public ExtractedPdf Extract(byte[] bytes, PdfTextStrategy strategy, CancellationToken token)
        { Calls++; return new PdfPigTextExtractor().Extract(bytes, strategy, token); }
    }
    private sealed class FixedTemplateExtractor(ExtractedPdf document) : IPdfTextExtractor
    {
        public ExtractedPdf Extract(byte[] bytes, PdfTextStrategy strategy, CancellationToken token) => document;
    }
    private sealed class InterruptedTemplateExtractor : IPdfTextExtractor
    {
        public ExtractedPdf Extract(byte[] bytes, PdfTextStrategy strategy, CancellationToken token) => throw new OperationCanceledException();
    }
}
