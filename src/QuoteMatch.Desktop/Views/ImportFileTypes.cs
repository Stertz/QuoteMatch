using Avalonia.Platform.Storage;
using QuoteMatch.Application.Importing;

namespace QuoteMatch.Desktop.Views;

internal static class ImportFileTypes
{
    public static FilePickerFileType Excel { get; } = new("Excel / LibreOffice / CSV") { Patterns = SpreadsheetFormats.Extensions.Select(extension => "*" + extension).ToArray() };
    public static FilePickerFileType Documents { get; } = new("PDF / Excel / LibreOffice / CSV") { Patterns = ["*.pdf", .. Excel.Patterns!] };
}
