using System.Text.Json;
using QuoteMatch.Application.Importing;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Tests;

public sealed partial class PdfImportTests
{
    [Fact]
    public void OptionalFinancialCaptures_PreserveScopeCulturePrecisionAndMissingValuesAcrossPages()
    {
        var template = Template with
        {
            ExpectedCountPattern = "",
            DocumentTotalPattern = "",
            TableEndPattern = "^(Freight|IPI)",
            RecordPattern = @"^(?<code>\S+)\s+(?<description>.+?)\s+(?<quantity>[\d.,]+)\s+(?<unitPrice>[\d.,]+)\s+(?<total>[\d.,]+)(?:\s+IPI\s+(?<ipiRate>[\d.,]+)\s+(?<ipiAmount>[\d.,]+))?\s*$",
            DocumentFinancialPattern = @"^Freight (?<freight>[\d.,]+)$|^IPI (?<ipiAmount>[\d.,]+)$"
        };
        var source = Document("Example supplier\n001 Synthetic wire 2 10,00 20,00 IPI 5,5 1,10\nFreight 12,1234567890123",
            "002 Other wire 1 5,00 5,00\nFreight 12,1234567890123\nIPI 1,10");
        var extracted = TemplateExtraction.Extract(source, template, TestContext.Current.CancellationToken);
        Assert.Empty(extracted.Issues);
        Assert.Equal(5.5m, extracted.Items[0].Financials.IpiRate); Assert.Equal(1.10m, extracted.Items[0].Financials.IpiAmount);
        Assert.Null(extracted.Items[1].Financials.IpiRate); Assert.Null(extracted.Items[1].Financials.IpiAmount);
        Assert.Null(extracted.Items[0].Financials.Freight);
        Assert.Equal("5,5", extracted.Items[0].OriginalFields["ipiRate"]);
        Assert.Equal(12.1234567890123m, extracted.DocumentFinancials.Values.Freight);
        Assert.Equal(1.10m, extracted.DocumentFinancials.Values.IpiAmount);
        Assert.Null(extracted.DocumentFinancials.Values.Discount);
        Assert.Equal(3, extracted.DocumentFinancials.Evidence.Count);
        Assert.Equal([1, 2, 2], extracted.DocumentFinancials.Evidence.Select(capture => capture.PageNumber));
        Assert.Equal("12,1234567890123", extracted.DocumentFinancials.Evidence[0].OriginalText);
        Assert.Equal("Freight 12,1234567890123", extracted.DocumentFinancials.Evidence[0].SourceText);
    }

    [Fact]
    public void DocumentFinancialConflicts_AreBlockingAndNeverChooseTheFirstValueOrDefaultToZero()
    {
        var template = Template with { ExpectedCountPattern = "", DocumentTotalPattern = "", TableEndPattern = "^Freight", DocumentFinancialPattern = @"^Freight (?<freight>[\d.,]+)$" };
        var extracted = TemplateExtraction.Extract(Document("Example supplier\n001 Wire 1 2,00 2,00\nFreight 10,00", "Freight 20,00"), template, TestContext.Current.CancellationToken);
        Assert.Null(extracted.DocumentFinancials.Values.Freight);
        Assert.Equal(2, extracted.DocumentFinancials.Evidence.Count);
        Assert.Contains(extracted.Issues, issue => issue.Code == ImportIssueCode.ConflictingFinancialValues && issue.IsBlocking && issue.Field == "freight");
        var missing = TemplateExtraction.Extract(Document("Example supplier\n001 Wire 1 2,00 2,00"), template, TestContext.Current.CancellationToken);
        Assert.Contains(missing.Issues, issue => issue.Code == ImportIssueCode.MissingField && issue.IsBlocking && issue.Field == "documentFinancials");
        Assert.Null(missing.DocumentFinancials.Values.Freight);
    }

    [Theory]
    [InlineData(@"(?<unknown>\d+)")]
    [InlineData(@"(?<freight>\d*)")]
    [InlineData(@"(?<freight>[)")]
    public void DocumentFinancialMapping_RejectsUnsupportedEmptyOrMalformedExpressions(string pattern)
    {
        var exception = Assert.Throws<ImportException>(() => TemplateExtraction.Validate(Template with { DocumentFinancialPattern = pattern }));
        Assert.Equal(ImportErrorCode.InvalidTemplate, exception.Code);
    }

    [Fact]
    public async Task SavedFinancialCaptures_RestartWithReviewedItemValuesAndOriginalEvidenceUnchanged()
    {
        var template = Template with { ExpectedCountPattern = "", DocumentTotalPattern = "", TableEndPattern = "^Freight", DocumentFinancialPattern = @"^Freight (?<freight>[\d.,]+)$" };
        var extraction = TemplateExtraction.Extract(Document("Example supplier\n001 Wire 1 2,00 2,00\nFreight 3,25"), template, TestContext.Current.CancellationToken);
        var reviewed = Review(extraction.Items[0]) with { Financials = new() { IpiRate = 5.123456789m, IpiAmount = 0m } };
        SqliteImportStore store = new(_directory); await store.InitializeAsync(TestContext.Current.CancellationToken);
        SavedProposal saved = new(Guid.NewGuid(), DateTime.UtcNow, "synthetic-financial.pdf", "synthetic", template, Document("Synthetic"), extraction, [reviewed]);
        await store.SaveProposalAsync(saved, [], TestContext.Current.CancellationToken);
        var loaded = (await new SqliteImportStore(_directory).GetProposalAsync(saved.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(5.123456789m, loaded.Review[0].Financials.IpiRate); Assert.Equal(0m, loaded.Review[0].Financials.IpiAmount);
        Assert.Null(loaded.Review[0].Financials.IcmsRate);
        Assert.Equal(3.25m, loaded.Extraction.DocumentFinancials.Values.Freight);
        Assert.Equal(extraction.DocumentFinancials.Evidence, loaded.Extraction.DocumentFinancials.Evidence);
        var legacy = JsonSerializer.Deserialize<ReviewedItem>("""{"CaptureId":"00000000-0000-0000-0000-000000000001","ProductCode":"001","Description":"Synthetic","Quantity":1,"UnitPrice":2,"ReportedTotal":2,"Unit":"M","IsExcluded":false,"ExclusionReason":""}""", SqliteImportStore.JsonOptions)!;
        Assert.Null(legacy.Financials.Freight); Assert.Null(legacy.Financials.IpiAmount); Assert.True(legacy.Financials.IsValid);
    }

    [Fact]
    public void FinancialNumbers_UseTheTemplateCultureAndRejectInvalidRates()
    {
        var template = Template with { CultureName = "en-US", ExpectedCountPattern = "", DocumentTotalPattern = "", TableEndPattern = "^Freight", DocumentFinancialPattern = @"^Freight (?<freight>[\d.,]+)$|^IPI (?<ipiRate>[\d.,]+)$" };
        var result = TemplateExtraction.Extract(Document("Example supplier\n001 Wire 1 2.00 2.00\nFreight 1,234.56789\nIPI 101"), template, TestContext.Current.CancellationToken);
        Assert.Equal(1234.56789m, result.DocumentFinancials.Values.Freight);
        Assert.Contains(result.Issues, issue => issue.Code == ImportIssueCode.InvalidFinancialValue && issue.IsBlocking && issue.Field == "ipiRate");
        Assert.False(result.DocumentFinancials.Values.IsValid);
    }
}
