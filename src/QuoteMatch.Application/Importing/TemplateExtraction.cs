using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Application.Importing;

public static class TemplateExtraction
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private const RegexOptions Options = RegexOptions.Multiline | RegexOptions.CultureInvariant;

    public static void Validate(PdfTemplate template)
    {
        if (!Enum.IsDefined(template.SourceFormat) || !Enum.IsDefined(template.CodeKind) || template.Spreadsheet?.TextOptions is null
            || !Enum.IsDefined(template.Spreadsheet.TextOptions.Separator) || !Enum.IsDefined(template.Spreadsheet.TextOptions.Encoding))
            throw new ImportException(ImportErrorCode.InvalidTemplate, "The source format is invalid.");
        if (template.SourceFormat == ImportSourceFormat.Excel)
        {
            SpreadsheetExtraction.Validate(template);
            return;
        }
        if (string.IsNullOrWhiteSpace(template.Name) || string.IsNullOrWhiteSpace(template.SupplierName)
            || string.IsNullOrWhiteSpace(template.DefaultUnit) || string.IsNullOrWhiteSpace(template.RecordStartPattern)
            || string.IsNullOrWhiteSpace(template.RecordPattern) || template.AbsoluteTolerance < 0
            || new[] { template.IdentificationPattern, template.TableEndPattern, template.ExpectedCountPattern, template.DocumentTotalPattern, template.DocumentFinancialPattern }.Any(pattern => pattern is null)
            || template.RelativeTolerance is < 0 or > 0.1m || !Enum.IsDefined(template.TextStrategy)
            || template.CultureName is not ("pt-BR" or "en-US"))
        {
            throw new ImportException(ImportErrorCode.InvalidTemplate, "Template metadata is invalid.");
        }

        try
        {
            Regex capture = Create(template.RecordPattern, singleline: true);
            string[] groups = capture.GetGroupNames();
            foreach (string group in new[] { "code", "description", "quantity", "unitPrice", "total" })
            {
                if (!groups.Contains(group, StringComparer.Ordinal))
                {
                    throw new ImportException(ImportErrorCode.InvalidTemplate, $"The template requires a named capture group: {group}.");
                }
            }

            foreach (string pattern in new[] { template.IdentificationPattern, template.RecordStartPattern, template.TableEndPattern })
            {
                if (pattern.Length > 0)
                {
                    Regex regex = Create(pattern);
                    if (regex.IsMatch(string.Empty))
                    {
                        throw new ImportException(ImportErrorCode.InvalidTemplate, "Boundary and identification expressions must not match empty input.");
                    }
                }
            }

            ValidateOptionalGroup(template.ExpectedCountPattern, "count");
            ValidateOptionalGroup(template.DocumentTotalPattern, "total");
            if (template.DocumentFinancialPattern.Length > 0)
            {
                var financial = Create(template.DocumentFinancialPattern);
                if (financial.IsMatch(string.Empty) || !Enum.GetValues<FinancialField>().Any(field => financial.GetGroupNames().Contains(FinancialAmounts.CaptureName(field), StringComparer.Ordinal)))
                    throw new ImportException(ImportErrorCode.InvalidTemplate, "The financial expression requires a supported named group and must not match empty input.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new ImportException(ImportErrorCode.InvalidTemplate, "A template regular expression is invalid.", exception);
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new ImportException(ImportErrorCode.RegexTimeout, "Template validation exceeded its execution budget.", exception);
        }
    }

    public static ExtractionResult Extract(ExtractedPdf document, PdfTemplate template, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(template);
        List<CapturedItem> items = [];
        List<ImportIssue> documentIssues = [];
        string allText = string.Join('\n', document.Pages.Select(page => page.Text));
        if (allText.Length > 2_000_000 || document.Pages.Count > 200)
        {
            throw new ImportException(ImportErrorCode.ExtractionLimit, "The document exceeds extraction limits.");
        }

        if (string.IsNullOrWhiteSpace(allText))
        {
            return new ExtractionResult([], [new(ImportIssueCode.NoText, true)], null, null);
        }

        Stopwatch budget = Stopwatch.StartNew();
        try
        {
            if (template.IdentificationPattern.Length > 0 && !Create(template.IdentificationPattern).IsMatch(allText))
            {
                documentIssues.Add(new(ImportIssueCode.WrongTemplate, true));
            }

            Regex captureRegex = Create(template.RecordPattern, singleline: true);
            foreach (var record in Records(document, template, CheckBudget))
            {
                string block = record.Text;
                Match match = captureRegex.Match(block);
                Dictionary<string, string> raw = captureRegex.GetGroupNames()
                    .Where(name => !int.TryParse(name, out _))
                    .ToDictionary(name => name, name => match.Groups[name].Value);
                List<ImportIssue> issues = [];
                if (!match.Success || match.Index != 0 || match.Length != block.Length)
                {
                    issues.Add(new(ImportIssueCode.CaptureFailed, true));
                }

                string code = raw.GetValueOrDefault("code", string.Empty).Trim();
                string description = Normalize(raw.GetValueOrDefault("description", string.Empty));
                decimal? quantity = Parse("quantity");
                decimal? price = Parse("unitPrice");
                decimal? total = Parse("total");
                string unit = raw.GetValueOrDefault("unit", string.Empty).Trim();
                if (unit.Length == 0)
                {
                    unit = template.DefaultUnit;
                }

                if (code.Length == 0 || description.Length == 0)
                {
                    issues.Add(new(ImportIssueCode.MissingField, true));
                }

                if (quantity is <= 0 || price is <= 0 || total is < 0)
                {
                    issues.Add(new(ImportIssueCode.NonPositiveValue, true));
                }

                if (quantity is { } q && price is { } p && total is { } t && !WithinTolerance(q * p, t, template))
                {
                    issues.Add(new(ImportIssueCode.ItemTotalMismatch, false));
                }

                FinancialAmounts financials = new();
                foreach (var field in Enum.GetValues<FinancialField>())
                {
                    string name = FinancialAmounts.CaptureName(field);
                    if (!raw.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value)) continue;
                    decimal? parsed = Parse(name);
                    financials = financials.With(field, parsed);
                    if (parsed < 0 || FinancialAmounts.IsRate(field) && parsed > 100) issues.Add(new(ImportIssueCode.InvalidFinancialValue, true, name));
                }
                items.Add(new(Guid.CreateVersion7(), record.PageNumber, items.Count + 1, block, raw, code, description,
                    quantity, price, total, unit, issues)
                { Financials = financials });

                decimal? Parse(string field)
                {
                    if (TryParseNumber(raw.GetValueOrDefault(field, string.Empty), template, out decimal value))
                    {
                        return value;
                    }

                    issues.Add(new(ImportIssueCode.InvalidNumber, true, field));
                    return null;
                }
            }

            if (items.Count == 0)
            {
                documentIssues.Add(new(ImportIssueCode.NoRecords, true));
            }

            int? expectedCount = null;
            if (template.ExpectedCountPattern.Length > 0)
            {
                Match count = Create(template.ExpectedCountPattern).Match(allText);
                if (!count.Success || !int.TryParse(count.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int expected)
                    || expected != items.Count)
                {
                    documentIssues.Add(new(ImportIssueCode.ItemCountMismatch, true));
                }
                else
                {
                    expectedCount = expected;
                }
            }

            decimal? documentTotal = null;
            if (template.DocumentTotalPattern.Length > 0)
            {
                Match[] totals = Create(template.DocumentTotalPattern).Matches(allText).Cast<Match>().ToArray();
                List<decimal> values = [];
                foreach (Match total in totals)
                {
                    if (TryParseNumber(total.Groups["total"].Value, template, out decimal value))
                    {
                        values.Add(value);
                    }
                }

                if (values.Count == 0)
                {
                    documentIssues.Add(new(ImportIssueCode.MissingField, true, "documentTotal"));
                }
                else
                {
                    documentTotal = values[0];
                    if (values.Any(value => value != values[0]))
                    {
                        documentIssues.Add(new(ImportIssueCode.ConflictingDocumentTotals, true));
                    }

                    if (items.All(item => item.ReportedTotal.HasValue)
                        && !WithinTolerance(items.Sum(item => item.ReportedTotal!.Value), documentTotal.Value, template))
                    {
                        documentIssues.Add(new(ImportIssueCode.DocumentTotalMismatch, false));
                    }
                }
            }

            FinancialAmounts documentFinancials = new();
            List<FinancialCaptureEvidence> evidence = [];
            if (template.DocumentFinancialPattern.Length > 0)
            {
                var financialRegex = Create(template.DocumentFinancialPattern);
                foreach (var page in document.Pages)
                    foreach (Match match in financialRegex.Matches(page.Text))
                    {
                        CheckBudget();
                        foreach (var field in Enum.GetValues<FinancialField>())
                        {
                            string name = FinancialAmounts.CaptureName(field);
                            var group = match.Groups[name];
                            if (!group.Success || string.IsNullOrWhiteSpace(group.Value)) continue;
                            decimal? value = TryParseNumber(group.Value, template, out decimal parsed) ? parsed : null;
                            evidence.Add(new(field, group.Value, value, page.Number, match.Value));
                            if (evidence.Count > 10_000) throw new ImportException(ImportErrorCode.ExtractionLimit, "Financial capture exceeded its evidence budget.");
                            if (value is null) documentIssues.Add(new(ImportIssueCode.InvalidNumber, true, name));
                            else if (value < 0 || FinancialAmounts.IsRate(field) && value > 100) documentIssues.Add(new(ImportIssueCode.InvalidFinancialValue, true, name));
                        }
                    }
                foreach (var field in Enum.GetValues<FinancialField>())
                {
                    var values = evidence.Where(item => item.Field == field && item.Value.HasValue).Select(item => item.Value!.Value).Distinct().ToArray();
                    if (values.Length > 1) documentIssues.Add(new(ImportIssueCode.ConflictingFinancialValues, true, FinancialAmounts.CaptureName(field)));
                    else if (values.Length == 1) documentFinancials = documentFinancials.With(field, values[0]);
                }
                if (evidence.Count == 0) documentIssues.Add(new(ImportIssueCode.MissingField, true, "documentFinancials"));
            }
            _ = items.Sum(item => item.ReportedTotal ?? 0m);
            CheckBudget();
            return new ExtractionResult(items, documentIssues, expectedCount, documentTotal) { DocumentFinancials = new(documentFinancials, evidence) };
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new ImportException(ImportErrorCode.RegexTimeout, "A template expression exceeded its execution budget.", exception);
        }
        catch (OverflowException exception)
        {
            throw new ImportException(ImportErrorCode.ExtractionLimit, "A captured financial value exceeds supported limits.", exception);
        }

        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.Elapsed > TimeSpan.FromSeconds(20) || items.Count > 5000)
            {
                throw new ImportException(ImportErrorCode.ExtractionLimit, "Template extraction exceeded its work budget.");
            }
        }
    }

    internal static bool HasMatchingItem(ExtractedPdf document, PdfTemplate template, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string text = string.Join('\n', document.Pages.Select(page => page.Text));
        if (text.Length > 2_000_000 || document.Pages.Count > 200) throw new ImportException(ImportErrorCode.ExtractionLimit, "The document exceeds extraction limits.");
        try
        {
            if (!Create(template.IdentificationPattern).IsMatch(text)) return false;
            var capture = Create(template.RecordPattern, singleline: true);
            foreach (var record in Records(document, template, token.ThrowIfCancellationRequested))
            {
                var match = capture.Match(record.Text);
                if (match.Success && match.Index == 0 && match.Length == record.Text.Length
                    && !string.IsNullOrWhiteSpace(match.Groups["code"].Value) && !string.IsNullOrWhiteSpace(match.Groups["description"].Value)
                    && TryParseNumber(match.Groups["quantity"].Value, template, out decimal quantity) && quantity > 0
                    && TryParseNumber(match.Groups["unitPrice"].Value, template, out decimal price) && price > 0
                    && TryParseNumber(match.Groups["total"].Value, template, out decimal total) && total >= 0) return true;
            }
            return false;
        }
        catch (RegexMatchTimeoutException exception) { throw new ImportException(ImportErrorCode.RegexTimeout, "Template identification exceeded its execution budget.", exception); }
    }

    private static IEnumerable<(int PageNumber, string Text)> Records(ExtractedPdf document, PdfTemplate template, Action checkBudget)
    {
        var startRegex = Create(template.RecordStartPattern);
        var endRegex = template.TableEndPattern.Length > 0 ? Create(template.TableEndPattern) : null;
        foreach (var page in document.Pages)
        {
            checkBudget();
            var start = startRegex.Match(page.Text);
            while (start.Success)
            {
                checkBudget();
                var next = start.NextMatch();
                int end = next.Success ? next.Index : page.Text.Length;
                var footer = endRegex?.Match(page.Text, start.Index);
                if (footer is { Success: true } && footer.Index < end) end = footer.Index;
                string block = page.Text[start.Index..end].Trim();
                if (block.Length > 0) yield return (page.Number, block);
                start = next;
            }
        }
    }

    public static bool WithinTolerance(decimal calculated, decimal reported, PdfTemplate template) =>
        Math.Abs(calculated - reported) <= Math.Max(template.AbsoluteTolerance, Math.Abs(reported) * template.RelativeTolerance);

    private static bool TryParseNumber(string text, PdfTemplate template, out decimal value)
    {
        if (template.RemoveGroupingSpaces)
        {
            text = text.Replace(" ", string.Empty).Replace("\u00a0", string.Empty).Replace("\u202f", string.Empty);
        }

        return decimal.TryParse(text.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,
            CultureInfo.GetCultureInfo(template.CultureName), out value);
    }

    private static Regex Create(string pattern, bool singleline = false) => new(pattern,
        Options | (singleline ? RegexOptions.Singleline : RegexOptions.None), RegexTimeout);

    private static void ValidateOptionalGroup(string pattern, string group)
    {
        if (pattern.Length > 0 && !Create(pattern).GetGroupNames().Contains(group, StringComparer.Ordinal))
        {
            throw new ImportException(ImportErrorCode.InvalidTemplate, $"The expression requires a named capture group: {group}.");
        }
    }

    private static string Normalize(string text) => Regex.Replace(text.Trim(), @"\s+", " ", RegexOptions.CultureInvariant, RegexTimeout);
}



