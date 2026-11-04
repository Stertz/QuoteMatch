using System.Security.Cryptography;
using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Application.Importing;

public sealed class PdfImportSource
{
    private readonly Dictionary<PdfTextStrategy, ExtractedPdf> _documents = [];
    internal PdfImportSource(string fileName, byte[] bytes)
    {
        FileName = fileName; Bytes = bytes; SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes));
    }
    public string FileName { get; }
    public string SourceSha256 { get; }
    internal byte[] Bytes { get; }
    internal ExtractedPdf GetDocument(IPdfTextExtractor extractor, PdfTextStrategy strategy, CancellationToken token)
    {
        lock (_documents)
        {
            token.ThrowIfCancellationRequested();
            if (_documents.TryGetValue(strategy, out var document)) return document;
            document = extractor.Extract(Bytes, strategy, token);
            token.ThrowIfCancellationRequested();
            _documents.Add(strategy, document); return document;
        }
    }
}

public sealed record PdfTemplateSuggestion(ImportPreview? Preview, int SkippedTemplates, bool LimitReached);
