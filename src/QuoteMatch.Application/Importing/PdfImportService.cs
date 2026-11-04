using System.Security.Cryptography;

using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Application.Importing;

public sealed class PdfImportService(IPdfTextExtractor extractor, IProposalRepository proposals, ISpreadsheetReader? spreadsheets = null)
{
    public async Task<SpreadsheetDocument> InspectSpreadsheetAsync(string filePath, string cultureName, CancellationToken cancellationToken = default,
        SpreadsheetTextOptions? textOptions = null)
    {
        byte[] bytes = await ReadSpreadsheetAsync(filePath, cancellationToken);
        return await Task.Run(() => ReadWorkbook(bytes, cultureName, cancellationToken, Path.GetExtension(filePath), textOptions), cancellationToken);
    }

    public async Task<ImportPreview> PreviewAsync(string filePath, PdfTemplate template, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TemplateExtraction.Validate(template);
        if (template.SourceFormat == ImportSourceFormat.Excel)
        {
            byte[] workbookBytes = await ReadSpreadsheetAsync(filePath, cancellationToken);
            return await Task.Run(() =>
            {
                SpreadsheetDocument workbook = ReadWorkbook(workbookBytes, template.CultureName, cancellationToken, Path.GetExtension(filePath), template.Spreadsheet.TextOptions);
                var (document, extraction) = SpreadsheetExtraction.Extract(workbook, template, cancellationToken);
                return new ImportPreview(Guid.CreateVersion7(), Path.GetFileName(filePath), workbookBytes,
                    Convert.ToHexString(SHA256.HashData(workbookBytes)), template, document, extraction);
            }, cancellationToken);
        }
        if (SpreadsheetFormats.Supports(Path.GetExtension(filePath)))
            throw new ImportException(ImportErrorCode.SourceFormatMismatch, "Select a spreadsheet mapping for this file.");
        var source = await ReadPdfAsync(filePath, cancellationToken);
        return await PreviewAsync(source, template, cancellationToken);
    }

    public async Task<PdfImportSource> ReadPdfAsync(string filePath, CancellationToken cancellationToken = default, PdfImportSource? previous = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ImportException(ImportErrorCode.InvalidPdf, "Select a PDF file before extracting items.");
        }
        FileInfo file = new(filePath);
        if (file.Length > 32 * 1024 * 1024)
        {
            throw new ImportException(ImportErrorCode.FileTooLarge, "The PDF exceeds the 32 MB import limit.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        if (bytes.Length > 32 * 1024 * 1024 || bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
        {
            throw new ImportException(ImportErrorCode.InvalidPdf, "The selected file is not a supported PDF.");
        }

        string fileName = Path.GetFileName(filePath);
        if (previous is not null && previous.FileName == fileName && previous.SourceSha256 == Convert.ToHexString(SHA256.HashData(bytes))) return previous;
        return new(fileName, bytes);
    }

    public Task<ImportPreview> PreviewAsync(PdfImportSource source, PdfTemplate template, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        TemplateExtraction.Validate(template);
        if (template.SourceFormat != ImportSourceFormat.Pdf) throw new ImportException(ImportErrorCode.SourceFormatMismatch, "Select a PDF template for this source.");
        ExtractedPdf document = source.GetDocument(extractor, template.TextStrategy, cancellationToken);
        ExtractionResult extraction = TemplateExtraction.Extract(document, template, cancellationToken);
        return new ImportPreview(Guid.CreateVersion7(), source.FileName, source.Bytes.ToArray(), source.SourceSha256, template, document, extraction);
    }, cancellationToken);

    public async Task<PdfTemplateSuggestion> SuggestAsync(PdfImportSource source, IReadOnlyList<PdfTemplate> templates, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        int skipped = 0;
        var match = await Task.Run(() =>
        {
            foreach (var template in templates.Where(template => template.SourceFormat == ImportSourceFormat.Pdf && !string.IsNullOrWhiteSpace(template.IdentificationPattern)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget.IsCancellationRequested) return (Template: (PdfTemplate?)null, Limited: true);
                try
                {
                    TemplateExtraction.Validate(template);
                    var document = source.GetDocument(extractor, template.TextStrategy, budget.Token);
                    if (TemplateExtraction.HasMatchingItem(document, template, budget.Token))
                    {
                        budget.Token.ThrowIfCancellationRequested();
                        return (Template: (PdfTemplate?)template, Limited: false);
                    }
                }
                catch (ImportException exception) when (exception.Code is ImportErrorCode.InvalidTemplate or ImportErrorCode.RegexTimeout) { skipped++; }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (Template: (PdfTemplate?)null, Limited: true); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return (Template: (PdfTemplate?)null, Limited: false);
        }, cancellationToken);
        return new(match.Template is null ? null : await PreviewAsync(source, match.Template, cancellationToken), skipped, match.Limited);
    }

    private SpreadsheetDocument ReadWorkbook(byte[] bytes, string cultureName, CancellationToken cancellationToken, string extension, SpreadsheetTextOptions? options) =>
        (spreadsheets ?? throw new ImportException(ImportErrorCode.InvalidSpreadsheet, "No spreadsheet reader is configured."))
        .Read(bytes, cultureName, cancellationToken, extension, options);

    private static async Task<byte[]> ReadSpreadsheetAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ImportException(ImportErrorCode.InvalidSpreadsheet, "Select a workbook first.");
        if (!SpreadsheetFormats.Supports(Path.GetExtension(path)))
            throw new ImportException(ImportErrorCode.SourceFormatMismatch, "Select a supported spreadsheet or delimited text file for this mapping.");
        if (new FileInfo(path).Length > 32 * 1024 * 1024)
            throw new ImportException(ImportErrorCode.FileTooLarge, "The workbook exceeds the 32 MB limit.");
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length > 32 * 1024 * 1024)
            throw new ImportException(ImportErrorCode.FileTooLarge, "The workbook exceeds the 32 MB limit.");
        return bytes;
    }

    public async Task<SavedProposal> ConfirmAsync(ImportPreview preview, IReadOnlyList<ReviewedItem> review,
        CancellationToken cancellationToken = default, Guid? quotationId = null)
    {
        if (preview.Extraction.Issues.Any(issue => issue.IsBlocking) || review.Count == 0
            || review.Count != preview.Extraction.Items.Count || review.Select(item => item.CaptureId).Distinct().Count() != review.Count
            || !review.Select(item => item.CaptureId).Order().SequenceEqual(preview.Extraction.Items.Select(item => item.Id).Order())
            || review.All(item => item.IsExcluded))
        {
            throw new ImportException(ImportErrorCode.InvalidReview, "The import review is incomplete or does not match the captured document.");
        }

        foreach (ReviewedItem item in review)
        {
            if (item.IsExcluded ? string.IsNullOrWhiteSpace(item.ExclusionReason) :
                string.IsNullOrWhiteSpace(item.ProductCode) || string.IsNullOrWhiteSpace(item.Description)
                || string.IsNullOrWhiteSpace(item.Unit) || item.Quantity is not > 0 || item.UnitPrice is not > 0
                || item.ReportedTotal is null or < 0 || !item.Financials.IsValid
                || Enum.GetValues<FinancialField>().Any(field =>
                    preview.Extraction.Items.Single(capture => capture.Id == item.CaptureId).Issues.Any(issue => issue.IsBlocking && issue.Field == FinancialAmounts.CaptureName(field))
                    && item.Financials.Get(field) is null))
            {
                throw new ImportException(ImportErrorCode.InvalidReview, "Every record needs valid reviewed fields or an explicit exclusion reason.");
            }
        }

        try
        {
            foreach (ReviewedItem item in review.Where(item => !item.IsExcluded))
            {
                _ = item.Quantity!.Value * item.UnitPrice!.Value;
            }
            _ = review.Where(item => !item.IsExcluded).Sum(item => item.ReportedTotal!.Value);
        }
        catch (OverflowException exception)
        {
            throw new ImportException(ImportErrorCode.InvalidReview, "The reviewed financial values exceed supported limits.", exception);
        }

        if (!Convert.ToHexString(SHA256.HashData(preview.OriginalPdf)).Equals(preview.SourceSha256, StringComparison.Ordinal))
        {
            throw new ImportException(ImportErrorCode.SourceChanged, "The source document changed after extraction.");
        }

        SavedProposal proposal = new(preview.Id, DateTime.UtcNow, preview.FileName, preview.SourceSha256,
            preview.Template, preview.Document, preview.Extraction, review)
        { QuotationId = quotationId };
        await proposals.SaveProposalAsync(proposal, preview.OriginalPdf, cancellationToken);
        return proposal;
    }
}




