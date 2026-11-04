using System.Text;

using QuoteMatch.Application.Importing;
using QuoteMatch.Domain.Importing;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace QuoteMatch.Infrastructure.Pdf;

public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    public ExtractedPdf Extract(byte[] bytes, PdfTextStrategy strategy, CancellationToken cancellationToken)
    {
        try
        {
            using PdfDocument document = PdfDocument.Open(bytes);
            if (document.NumberOfPages > 200)
            {
                throw new ImportException(ImportErrorCode.ExtractionLimit, "The document has too many pages.");
            }

            List<ExtractedPage> pages = [];
            int totalLength = 0;
            foreach (Page page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (page.Letters.Count > 200_000)
                {
                    throw new ImportException(ImportErrorCode.ExtractionLimit, "A page exceeds text extraction limits.");
                }

                string text = strategy switch
                {
                    PdfTextStrategy.ContentOrder => ContentOrderTextExtractor.GetText(page),
                    PdfTextStrategy.PositionedLines => BuildText(page.Letters
                        .OrderByDescending(letter => Math.Round(letter.StartBaseLine.Y / 2d))
                        .ThenBy(letter => letter.StartBaseLine.X), cancellationToken),
                    _ => BuildText(page.Letters, cancellationToken)
                };
                totalLength += text.Length;
                if (totalLength > 2_000_000)
                {
                    throw new ImportException(ImportErrorCode.ExtractionLimit, "The document exceeds text extraction limits.");
                }

                pages.Add(new(page.Number, text));
            }

            return new ExtractedPdf(pages, $"PdfPig/{typeof(PdfDocument).Assembly.GetName().Version};QuoteMatchText/1");
        }
        catch (Exception exception) when (exception is not (ImportException or OperationCanceledException))
        {
            throw new ImportException(ImportErrorCode.InvalidPdf, "PDF extraction failed. The file may be damaged or encrypted.", exception);
        }
    }

    private static string BuildText(IEnumerable<Letter> letters, CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        Letter? previous = null;
        int count = 0;
        foreach (Letter letter in letters)
        {
            if (++count % 256 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (previous is not null)
            {
                double verticalGap = Math.Abs(letter.StartBaseLine.Y - previous.StartBaseLine.Y);
                double horizontalGap = letter.StartBaseLine.X - previous.EndBaseLine.X;
                if (verticalGap > Math.Max(1.5d, previous.FontSize * 0.35d)
                    || horizontalGap < -Math.Max(1d, previous.FontSize * 0.3d))
                {
                    builder.AppendLine();
                }
                else if (horizontalGap > Math.Max(0.5d, previous.FontSize * 0.12d)
                         && builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
                {
                    builder.Append(' ');
                }
            }

            builder.Append(letter.Value);
            previous = letter;
        }

        return builder.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
    }
}
