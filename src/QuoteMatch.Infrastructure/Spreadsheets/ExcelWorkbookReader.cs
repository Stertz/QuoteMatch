using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using ExcelDataReader;
using ExcelNumberFormat;
using QuoteMatch.Application.Importing;
using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Infrastructure.Spreadsheets;

public sealed partial class ExcelWorkbookReader : ISpreadsheetReader
{
    private const int MaximumRows = 10000;
    private const int MaximumColumns = 256;
    private const int MaximumCells = 500000;
    private const int MaximumCharacters = 2000000;

    public SpreadsheetDocument Read(byte[] bytes, string cultureName, CancellationToken cancellationToken,
        string fileExtension = "", SpreadsheetTextOptions? textOptions = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > 32 * 1024 * 1024) throw new ImportException(ImportErrorCode.FileTooLarge, "The workbook exceeds the 32 MB limit.");
        Stopwatch budget = Stopwatch.StartNew();
        fileExtension = fileExtension.ToLowerInvariant();
        textOptions ??= new();
        if (!Enum.IsDefined(textOptions.Encoding) || !Enum.IsDefined(textOptions.Separator))
            throw new ImportException(ImportErrorCode.InvalidTemplate, "The delimited text settings are invalid.");
        try
        {
            using MemoryStream stream = new(bytes, false);
            if (bytes.AsSpan().StartsWith("PK"u8))
            {
                using ZipArchive zip = new(stream, ZipArchiveMode.Read, true);
                if (zip.Entries.Count > 2048 || zip.Entries.Sum(entry => entry.Length) > 64 * 1024 * 1024)
                    throw new ImportException(ImportErrorCode.ExtractionLimit, "The expanded workbook exceeds extraction limits.");
                if (zip.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != zip.Entries.Count)
                    throw new ImportException(ImportErrorCode.InvalidSpreadsheet, "The workbook contains duplicate package entries.");
                if (fileExtension == ".ods" || zip.GetEntry("mimetype") is { } mime && ReadMimeType(mime) == "application/vnd.oasis.opendocument.spreadsheet")
                {
                    if (zip.GetEntry("mimetype") is not { } type || ReadMimeType(type) != "application/vnd.oasis.opendocument.spreadsheet"
                        || zip.GetEntry("content.xml") is not { } content)
                        throw new ImportException(ImportErrorCode.InvalidSpreadsheet, "The OpenDocument package is incomplete.");
                    using var xml = content.Open();
                    return ReadOpenDocument(xml, CheckBudget);
                }
            }
            stream.Position = 0;
            if (fileExtension == ".fods") return ReadOpenDocument(stream, CheckBudget);
            if (fileExtension == ".ods") throw new ImportException(ImportErrorCode.InvalidSpreadsheet, "The file is not an OpenDocument package.");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            bool delimited = fileExtension is ".csv" or ".tsv";
            string encodingName = string.Empty;
            using MemoryStream? csv = delimited ? CreateDelimitedStream(bytes, textOptions.Encoding, out encodingName) : null;
            using IExcelDataReader reader = delimited ? ExcelReaderFactory.CreateCsvReader(csv!, new()
            {
                LeaveOpen = true,
                TrimWhiteSpace = false,
                AutodetectSeparators = Separators(textOptions.Separator, fileExtension)
            }) : ExcelReaderFactory.CreateReader(stream, new() { LeaveOpen = true });
            List<SpreadsheetSheet> sheets = [];
            CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
            int cells = 0, characters = 0;
            do
            {
                CheckBudget();
                if (sheets.Count >= 50 || reader.FieldCount > MaximumColumns || reader.RowCount > MaximumRows)
                    throw new ImportException(ImportErrorCode.ExtractionLimit, "The workbook dimensions exceed extraction limits.");
                List<SpreadsheetRow> rows = [];
                int rowNumber = 0;
                while (reader.Read())
                {
                    CheckBudget();
                    if (++rowNumber > MaximumRows) throw new ImportException(ImportErrorCode.ExtractionLimit, "The worksheet exceeds the row limit.");
                    List<SpreadsheetCell> values = [];
                    for (int column = 0; column < reader.FieldCount; column++)
                    {
                        if (++cells > MaximumCells) throw new ImportException(ImportErrorCode.ExtractionLimit, "The workbook exceeds the cell limit.");
                        object? value = reader.GetValue(column);
                        string? numeric = value is double number ? number.ToString("R", CultureInfo.InvariantCulture) : null;
                        string text = value switch
                        {
                            null => string.Empty,
                            string original => original,
                            double => new NumberFormat(reader.GetNumberFormatString(column) ?? "General").Format(value, culture),
                            _ => Convert.ToString(value, culture) ?? string.Empty
                        };
                        bool error = value is not null and not string and not double;
                        characters += text.Length + (numeric?.Length ?? 0);
                        if (characters > MaximumCharacters) throw new ImportException(ImportErrorCode.ExtractionLimit, "The workbook text exceeds extraction limits.");
                        values.Add(new(text, numeric, error));
                    }
                    rows.Add(new(rowNumber, values));
                }
                sheets.Add(new(delimited ? "Data" : reader.Name, rows));
            } while (reader.NextResult());
            return new(sheets, "ExcelDataReader/" + typeof(ExcelReaderFactory).Assembly.GetName().Version + (delimited ? "/CSV/" + encodingName : string.Empty));
        }
        catch (Exception exception) when (exception is ExcelDataReader.Exceptions.ExcelReaderException or InvalidDataException or ArgumentException or InvalidOperationException or System.Xml.XmlException or OverflowException)
        {
            throw new ImportException(ImportErrorCode.InvalidSpreadsheet, "The workbook is unsupported, damaged or encrypted.", exception);
        }

        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.Elapsed > TimeSpan.FromSeconds(15))
                throw new ImportException(ImportErrorCode.ExtractionLimit, "Workbook reading exceeded its processing budget.");
        }
    }

    private static string ReadMimeType(ZipArchiveEntry entry)
    {
        if (entry.Length > 256) return string.Empty;
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }

    private static char[] Separators(DelimitedSeparator separator, string extension) => separator switch
    {
        DelimitedSeparator.Semicolon => [';'],
        DelimitedSeparator.Comma => [','],
        DelimitedSeparator.Tab => ['\t'],
        DelimitedSeparator.Pipe => ['|'],
        _ => extension == ".tsv" ? ['\t'] : [';', ',', '\t', '|']
    };

    private static MemoryStream CreateDelimitedStream(byte[] bytes, DelimitedEncoding choice, out string encodingName)
    {
        Encoding encoding = choice switch
        {
            DelimitedEncoding.Utf8 => new UTF8Encoding(false, true),
            DelimitedEncoding.Windows1252 => Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            DelimitedEncoding.Utf16LittleEndian => new UnicodeEncoding(false, true, true),
            DelimitedEncoding.Utf16BigEndian => new UnicodeEncoding(true, true, true),
            _ => bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) ? new UnicodeEncoding(false, true, true)
                : bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }) ? new UnicodeEncoding(true, true, true) : new UTF8Encoding(false, true)
        };
        string text;
        try { text = encoding.GetString(bytes); }
        catch (DecoderFallbackException) when (choice == DelimitedEncoding.Automatic && encoding.CodePage == 65001 && !bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            encoding = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            text = encoding.GetString(bytes);
        }
        if (text.Length > MaximumCharacters) throw new ImportException(ImportErrorCode.ExtractionLimit, "The delimited text exceeds extraction limits.");
        if (text.Contains('\0')) throw new ImportException(ImportErrorCode.InvalidSpreadsheet, "The selected text encoding is incompatible with the file.");
        if (text.StartsWith('\uFEFF')) text = text[1..];
        encodingName = encoding.WebName;
        return new MemoryStream(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray(), false);
    }
}
