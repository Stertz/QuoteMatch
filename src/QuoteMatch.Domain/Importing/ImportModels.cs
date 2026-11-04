namespace QuoteMatch.Domain.Importing;

public enum ImportIssueCode
{
    NoText,
    NoRecords,
    WrongTemplate,
    CaptureFailed,
    InvalidNumber,
    MissingField,
    NonPositiveValue,
    ItemTotalMismatch,
    ItemCountMismatch,
    DocumentTotalMismatch,
    ConflictingDocumentTotals,
    ConflictingFinancialValues,
    InvalidFinancialValue,
    SpreadsheetHeaderMismatch
}

public sealed record ImportIssue(ImportIssueCode Code, bool IsBlocking, string? Field = null);
public sealed record ExtractedPage(int Number, string Text);
public sealed record ExtractedPdf(IReadOnlyList<ExtractedPage> Pages, string ExtractorVersion);

public sealed record CapturedItem(
    Guid Id,
    int PageNumber,
    int RecordNumber,
    string SourceText,
    IReadOnlyDictionary<string, string> OriginalFields,
    string ProductCode,
    string Description,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? ReportedTotal,
    string Unit,
    IReadOnlyList<ImportIssue> Issues)
{
    public string? SourceSheet { get; init; }
    public bool IsTotalCalculated { get; init; }
    public FinancialAmounts Financials { get; init; } = new();
}

public sealed record ExtractionResult(
    IReadOnlyList<CapturedItem> Items,
    IReadOnlyList<ImportIssue> Issues,
    int? ExpectedItemCount,
    decimal? ReportedDocumentTotal)
{
    public DocumentFinancialCapture DocumentFinancials { get; init; } = new(new(), []);
}

public sealed record ReviewedItem(
    Guid CaptureId,
    string ProductCode,
    string Description,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? ReportedTotal,
    string Unit,
    bool IsExcluded,
    string ExclusionReason)
{
    public string Color { get; init; } = string.Empty;
    public FinancialAmounts Financials { get; init; } = new();
}

public sealed record SavedProposal(
    Guid Id,
    DateTime CreatedAtUtc,
    string FileName,
    string SourceSha256,
    PdfTemplate Template,
    ExtractedPdf Document,
    ExtractionResult Extraction,
    IReadOnlyList<ReviewedItem> Review)
{
    public Guid? QuotationId { get; init; }
    public int IncludedItemCount => Review.Count(item => !item.IsExcluded);
    public decimal ReportedItemTotal => Review.Where(item => !item.IsExcluded).Sum(item => item.ReportedTotal ?? 0m);
}

public sealed record ProposalSummary(Guid Id, DateTime CreatedAtUtc, string FileName, string SupplierName,
    string TemplateName, int ItemCount, decimal ReportedItemTotal);

