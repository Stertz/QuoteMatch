using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Application.Importing;

public interface IPdfTextExtractor
{
    ExtractedPdf Extract(byte[] bytes, PdfTextStrategy strategy, CancellationToken cancellationToken);
}

public interface ISpreadsheetReader
{
    SpreadsheetDocument Read(byte[] bytes, string cultureName, CancellationToken cancellationToken,
        string fileExtension = "", SpreadsheetTextOptions? textOptions = null);
}

public interface ITemplateRepository
{
    Task<IReadOnlyList<PdfTemplate>> GetLatestTemplatesAsync(CancellationToken cancellationToken);
    Task<PdfTemplate> SaveTemplateAsync(PdfTemplate template, CancellationToken cancellationToken);
}

public interface IProposalRepository
{
    Task SaveProposalAsync(SavedProposal proposal, byte[] originalPdf, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProposalSummary>> GetProposalsAsync(CancellationToken cancellationToken);
    Task<SavedProposal?> GetProposalAsync(Guid id, CancellationToken cancellationToken);
}

public sealed record ImportPreview(Guid Id, string FileName, byte[] OriginalPdf, string SourceSha256,
    PdfTemplate Template, ExtractedPdf Document, ExtractionResult Extraction);

public enum ImportErrorCode
{
    InvalidTemplate,
    InvalidPdf,
    FileTooLarge,
    ExtractionLimit,
    RegexTimeout,
    InvalidReview,
    SourceChanged,
    StorageConflict,
    InvalidSpreadsheet,
    SourceFormatMismatch
}

public sealed class ImportException(ImportErrorCode code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ImportErrorCode Code { get; } = code;
}
