using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuoteMatch.Application.Importing;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.ViewModels;

public partial class ImportWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly QuoteMatch.Application.Analysis.SavedProductMappingService? _automaticMappings;
    private readonly SqliteImportStore _store;
    private readonly PdfImportService _service;
    private CancellationTokenSource? _operation;
    private ImportPreview? _preview;
    private PdfImportSource? _pdfSource;
    private int _fileVersion;
    private PdfTemplate? _reviewTemplate;
    public string ReviewedCodeLabel => Strings[_reviewTemplate?.CodeKind == ProductCodeKind.Internal ? "InternalCodeLabel" : "ImportCode"];
    private readonly Guid? _quotationId;
    private bool _reloadingTemplates;
    public string QuotationName { get; }
    public bool IsTemplateTest => _quotationId is null;
    [ObservableProperty] private bool _isConsulting;
    public string ScreenTitle => Strings[IsTemplateTest ? "TemplatePreviewTitle" : IsConsulting ? "SupplierProposalTitle" : "ImportProposal"];
    public bool ShowFileSelection => !IsTemplateTest && !IsConsulting;
    public bool ShowConfirmation => !IsTemplateTest && !IsConsulting;
    partial void OnIsConsultingChanged(bool value) { OnPropertyChanged(nameof(ShowFileSelection)); OnPropertyChanged(nameof(ShowConfirmation)); OnPropertyChanged(nameof(ScreenTitle)); }
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isEditable;
    [ObservableProperty] private bool _canSave;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private string _rawText = string.Empty;
    [ObservableProperty] private string _diagnostics = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _templateSuggestionStatus = string.Empty;
    [ObservableProperty] private PdfTemplate? _selectedTemplate;
    [ObservableProperty] private string _templateSearchText = string.Empty;
    public ObservableCollection<PdfTemplate> VisibleTemplates { get; } = [];
    public bool HasNoMatchingTemplates => VisibleTemplates.Count == 0;
    public string TemplateSearchSummary => Strings.Format("TemplateSearchSummary", VisibleTemplates.Count, Templates.Count);
    partial void OnTemplateSearchTextChanged(string value) => FilterTemplates();
    private void FilterTemplates()
    {
        string[] terms = TemplateSearchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matching = Templates.Where(template => terms.All(term => Strings.Culture.CompareInfo.IndexOf(template.Name + " " + template.SupplierName,
            term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)).ToArray();
        foreach (var template in VisibleTemplates.Where(template => !matching.Contains(template)).ToArray()) VisibleTemplates.Remove(template);
        for (int index = 0; index < matching.Length; index++)
            if (index >= VisibleTemplates.Count || VisibleTemplates[index] != matching[index])
            {
                VisibleTemplates.Remove(matching[index]); VisibleTemplates.Insert(index, matching[index]);
            }
        OnPropertyChanged(nameof(HasNoMatchingTemplates)); OnPropertyChanged(nameof(TemplateSearchSummary));
    }
    [ObservableProperty] private ImportItemViewModel? _selectedItem;
    [ObservableProperty] private ProposalSummary? _selectedProposal;

    public ImportWorkspaceViewModel(Localizer strings, SqliteImportStore store, PdfImportService service, Guid? quotationId = null, string quotationName = "",
        QuoteMatch.Application.Analysis.SavedProductMappingService? automaticMappings = null)
    {
        Strings = new Localizer(); Strings.SetCulture(strings.Culture.Name); _store = store; _service = service;
        _quotationId = quotationId; QuotationName = quotationName;
        _automaticMappings = automaticMappings;
        Templates.CollectionChanged += (_, _) => FilterTemplates();
    }
    public bool IsReviewEnabled => IsEditable && !IsBusy;
    public bool CanTestTemplate => !IsBusy && !string.IsNullOrWhiteSpace(FilePath);
    public string ReviewedSummary
    {
        get
        {
            try { return Strings.Format("ReviewedSummary", Items.Count(item => !item.IsExcluded), Strings.Money(Items.Where(item => !item.IsExcluded).Sum(item => item.ToReview().ReportedTotal ?? 0m))); }
            catch (OverflowException) { return Strings["ErrorExtractionLimit"]; }
        }
    }
    public string SelectedReviewWarning
    {
        get
        {
            if (SelectedItem is null || _reviewTemplate is null) return string.Empty;
            ReviewedItem item = SelectedItem.ToReview();
            if (item.IsExcluded) return string.Empty;
            if (!SelectedItem.Financials.IsValid) return Strings["FinancialInvalidValues"];
            if (item.Quantity is not > 0 || item.UnitPrice is not > 0 || item.ReportedTotal is null or < 0) return Strings["ErrorInvalidReview"];
            try { return TemplateExtraction.WithinTolerance(item.Quantity.Value * item.UnitPrice.Value, item.ReportedTotal.Value, _reviewTemplate) ? string.Empty : Strings["IssueItemTotalMismatch"]; }
            catch (OverflowException) { return Strings["ErrorExtractionLimit"]; }
        }
    }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(IsReviewEnabled)); OnPropertyChanged(nameof(CanTestTemplate)); }
    partial void OnIsEditableChanged(bool value) => OnPropertyChanged(nameof(IsReviewEnabled));
    partial void OnSelectedItemChanged(ImportItemViewModel? value) => OnPropertyChanged(nameof(SelectedReviewWarning));
    private void AddItem(CapturedItem capture, ReviewedItem? review = null)
    {
        ImportItemViewModel item = new(capture, Strings, review);
        item.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(ReviewedSummary)); OnPropertyChanged(nameof(SelectedReviewWarning)); };
        Items.Add(item);
    }
    public Localizer Strings { get; }
    public TemplateEditorViewModel Editor { get; } = new();
    public ObservableCollection<PdfTemplate> Templates { get; } = [];
    public ObservableCollection<ImportItemViewModel> Items { get; } = [];
    [ObservableProperty] private FinancialFieldsViewModel? _documentFinancials;
    [ObservableProperty] private string _financialEvidence = string.Empty;
    public ObservableCollection<ProposalSummary> Proposals { get; } = [];
    partial void OnSelectedTemplateChanged(PdfTemplate? value)
    {
        TemplateSuggestionStatus = string.Empty;
        if (!_reloadingTemplates && _preview is not null && (value?.Id != _preview.Template.Id || value?.Version != _preview.Template.Version)) InvalidatePreview();
        if (value is not null) Editor.Load(value);
    }
    partial void OnFilePathChanged(string value)
    {
        _fileVersion++; _pdfSource = null; TemplateSuggestionStatus = string.Empty;
        InvalidatePreview(); OnPropertyChanged(nameof(CanTestTemplate));
    }
    private void InvalidatePreview()
    {
        if (_preview is null) return;
        _preview = null; CanSave = false; IsEditable = false;
        Status = Strings["ImportPreviewChanged"];
    }
    private bool IsCurrentSource(int version, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (version == _fileVersion) return true;
        Status = Strings["ImportPreviewChanged"]; return false;
    }
    public Task InitializeAsync() => RunAsync(async token =>
    {
        await _store.InitializeAsync(token);
        await ReloadAsync(token);
        Status = string.Empty;
    });
    private async Task ReloadAsync(CancellationToken token, Guid? templateId = null)
    {
        Guid? selected = templateId ?? SelectedTemplate?.Id;
        _reloadingTemplates = true;
        try
        {
            Templates.Clear();
            foreach (PdfTemplate template in await _store.GetLatestTemplatesAsync(token)) Templates.Add(template);
            if (templateId is Guid savedId && !VisibleTemplates.Any(template => template.Id == savedId)) TemplateSearchText = string.Empty;
            SelectedTemplate = Templates.FirstOrDefault(template => template.Id == selected) ?? Templates.FirstOrDefault();
        }
        finally { _reloadingTemplates = false; }
        if (_preview is not null && (SelectedTemplate?.Id != _preview.Template.Id || SelectedTemplate?.Version != _preview.Template.Version)) InvalidatePreview();
        Proposals.Clear();
        foreach (ProposalSummary proposal in _quotationId is Guid id ? await _store.GetQuotationProposalsAsync(id, token) : await _store.GetProposalsAsync(token)) Proposals.Add(proposal);
    }
    [RelayCommand]
    private void NewTemplate()
    {
        SelectedTemplate = null;
        Editor.Load(new PdfTemplate { Id = Guid.Empty, Version = 0 });

    }
    [RelayCommand]
    private Task SaveTemplateAsync() => RunAsync(async token =>
    {
        PdfTemplate saved = await _store.SaveTemplateAsync(Editor.Build(), token);
        await ReloadAsync(token, saved.Id);
        Status = Strings.Format("TemplateSaved", saved.Version);
    });
    [RelayCommand]
    private void ValidateTemplate()
    {
        try { TemplateExtraction.Validate(Editor.Build()); Status = Strings["TemplateValid"]; }
        catch (ImportException exception) { Status = Strings["Error" + exception.Code]; }
    }
    public Task LoadTemplateAsync(string path) => RunAsync(async token =>
    {
        string json = await File.ReadAllTextAsync(path, token);
        PdfTemplate template = JsonSerializer.Deserialize<PdfTemplate>(json, SqliteImportStore.JsonOptions)
            ?? throw new ImportException(ImportErrorCode.InvalidTemplate, "The template file is empty.");
        TemplateExtraction.Validate(template);
        SelectedTemplate = null;
        Editor.Load(template with { Id = Guid.Empty, Version = 0 });

        Status = Strings["TemplateLoaded"];
    });
    public Task ExportTemplateAsync(string path) => RunAsync(async token =>
    {
        PdfTemplate template = Editor.Build();
        TemplateExtraction.Validate(template);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(template with { Id = Guid.Empty, Version = 0 }, SqliteImportStore.JsonOptions), token);
        Status = Strings["TemplateExported"];
    });
    public Task InspectSpreadsheetAsync(string path) => RunAsync(async token =>
    {
        FilePath = path;
        SpreadsheetDocument workbook = await _service.InspectSpreadsheetAsync(path, Editor.CultureName, token, Editor.TextOptions);
        Editor.ShowWorkbook(workbook);
        Status = Strings["SpreadsheetSampleLoaded"];
    });
    public Task SelectFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy || IsConsulting) return Task.CompletedTask;
        if (paths.Count != 1) { Status = Strings["ImportDropSingleFile"]; return Task.CompletedTask; }
        string path = paths[0];
        bool pdf = string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
        if (!pdf && !SpreadsheetFormats.Supports(Path.GetExtension(path))) { Status = Strings["ImportDropUnsupported"]; return Task.CompletedTask; }
        return RunAsync(async token =>
        {
            FilePath = path; ResetReview(); TemplateSuggestionStatus = string.Empty;
            int version = _fileVersion;
            var initialSelection = SelectedTemplate;
            if (!pdf) { Status = Strings["ImportSpreadsheetSelectTemplate"]; return; }
            var source = await _service.ReadPdfAsync(path, token, _pdfSource);
            if (!IsCurrentSource(version, token)) return;
            _pdfSource = source;
            var templates = SelectedTemplate is { } selected ? new[] { selected }.Concat(Templates.Where(template => template.Id != selected.Id)).ToArray() : Templates.ToArray();
            var suggestion = await _service.SuggestAsync(source, templates, token);
            if (!IsCurrentSource(version, token)) return;
            if (SelectedTemplate != initialSelection) { Status = Strings["ImportPreviewChanged"]; return; }
            if (suggestion.Preview is { } preview)
            {
                SelectedTemplate = preview.Template; ShowPreview(preview);
                TemplateSuggestionStatus = Strings.Format("ImportTemplateDetected", preview.Template.Name);
            }
            else
            {
                Status = Strings[suggestion.LimitReached ? "ImportTemplateDetectionLimit" : "ImportTemplateNotDetected"];
            }
            if (suggestion.SkippedTemplates > 0)
            {
                string skipped = Strings.Format("ImportTemplatesSkipped", suggestion.SkippedTemplates);
                if (suggestion.Preview is null) Status += " " + skipped; else TemplateSuggestionStatus += " " + skipped;
            }
        });
    }
    [RelayCommand] private Task PreviewAsync() => PreviewFileAsync(FilePath, false);
    [RelayCommand] private Task TestTemplateAsync() => PreviewFileAsync(FilePath, true);
    public Task PreviewFileAsync(string path, bool useEditor = false) => RunAsync(async token =>
    {
        PdfTemplate template = useEditor ? Editor.Build() : SelectedTemplate
            ?? throw new ImportException(ImportErrorCode.InvalidTemplate, "Select a template first.");
        IsConsulting = false;
        FilePath = path;
        ResetReview();
        int version = _fileVersion;
        ImportPreview preview;
        if (template.SourceFormat == ImportSourceFormat.Pdf && !SpreadsheetFormats.Supports(Path.GetExtension(path)))
        {
            var source = await _service.ReadPdfAsync(path, token, _pdfSource);
            if (!IsCurrentSource(version, token)) return;
            _pdfSource = source;
            preview = await _service.PreviewAsync(source, template, token);
        }
        else preview = await _service.PreviewAsync(path, template, token);
        if (!IsCurrentSource(version, token)) return;
        if (!useEditor && SelectedTemplate != template) { Status = Strings["ImportPreviewChanged"]; return; }
        ShowPreview(preview);
    });
    private void ResetReview()
    {
        _preview = null; CanSave = false; IsEditable = false; DocumentFinancials = null; FinancialEvidence = string.Empty;
        Items.Clear(); OnPropertyChanged(nameof(ReviewedSummary)); SelectedItem = null; Diagnostics = string.Empty; Summary = string.Empty; RawText = string.Empty;
    }
    private void ShowPreview(ImportPreview preview)
    {
        _preview = preview; _reviewTemplate = preview.Template;
        ShowDocument(preview.Document, preview.Extraction);
        foreach (CapturedItem item in preview.Extraction.Items) AddItem(item);
        OnPropertyChanged(nameof(ReviewedSummary));
        SelectedItem = Items.FirstOrDefault();
        IsEditable = true; CanSave = _quotationId.HasValue && !preview.Extraction.Issues.Any(issue => issue.IsBlocking) && Items.Count > 0;

        Status = Strings["ReviewBeforeSaving"];
    }
    [RelayCommand]
    private Task ConfirmAsync() => RunAsync(async token =>
    {
        if (_preview is null || !CanSave) throw new ImportException(ImportErrorCode.InvalidReview, "There is no confirmable preview.");
        if (Items.Any(item => !item.IsExcluded && !item.Financials.IsValid)) throw new ImportException(ImportErrorCode.InvalidReview, "A reviewed financial value is invalid.");
        SavedProposal saved = await _service.ConfirmAsync(_preview, Items.Select(item => item.ToReview()).ToArray(), token, _quotationId);
        _preview = null; _pdfSource = null; CanSave = false; IsEditable = false; IsConsulting = true;
        await ReloadAsync(token);
        SelectedProposal = Proposals.Single(proposal => proposal.Id == saved.Id);
        Status = Strings.Format("ProposalSaved", saved.IncludedItemCount);
        if (_automaticMappings is not null && saved.QuotationId is Guid quotationId)
        {
            try
            {
                int applied = await _automaticMappings.ApplyAsync(quotationId, saved.Id, token);
                if (applied > 0) Status += " " + Strings.Format("ImportedMappingsApplied", applied);
            }
            catch (Exception exception) when (exception is ErpException or AnalysisException or ProductMappingException or OperationCanceledException
                or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or Microsoft.EntityFrameworkCore.DbUpdateException)
            { Status += " " + Strings["AutoMappingReviewPending"]; }
        }
    });
    [RelayCommand]
    private Task OpenProposalAsync() => RunAsync(async token =>
    {
        if (SelectedProposal is null) return;
        SavedProposal? proposal = await _store.GetProposalAsync(SelectedProposal.Id, token);
        if (proposal is null) return;
        if (_quotationId.HasValue && proposal.QuotationId != _quotationId) throw new QuoteMatch.Domain.Quotations.QuotationException(QuoteMatch.Domain.Quotations.QuotationError.Conflict);
        _preview = null; CanSave = false; IsEditable = false;
        IsConsulting = true;
        FilePath = proposal.FileName;
        _reviewTemplate = proposal.Template;
        ShowDocument(proposal.Document, proposal.Extraction);
        Items.Clear(); OnPropertyChanged(nameof(ReviewedSummary));
        foreach (CapturedItem item in proposal.Extraction.Items)
            AddItem(item, proposal.Review.Single(review => review.CaptureId == item.Id));
        OnPropertyChanged(nameof(ReviewedSummary));
        SelectedItem = Items.FirstOrDefault();
        Status = string.Empty;
    });
    public Task ExportOriginalAsync(string path) => RunAsync(async token =>
    {
        if (SelectedProposal is null) return;
        byte[]? bytes = await _store.GetOriginalPdfAsync(SelectedProposal.Id, token);
        if (bytes is not null) await File.WriteAllBytesAsync(path, bytes, token);
        Status = Strings["OriginalExported"];
    });
    private void ShowDocument(ExtractedPdf document, ExtractionResult extraction)
    {
        OnPropertyChanged(nameof(ReviewedCodeLabel));
        DocumentFinancials = new(extraction.DocumentFinancials.Values, Strings, true);
        FinancialEvidence = string.Join(Environment.NewLine, extraction.DocumentFinancials.Evidence.Select(capture =>
            Strings.Format("FinancialEvidenceLine", Strings["FinancialField" + capture.Field], capture.PageNumber, capture.OriginalText)));
        RawText = string.Join("\n\n", document.Pages.Select(page => Strings.Format("PageNumber", page.Number) + "\n" + page.Text));
        Diagnostics = string.Join("\n", extraction.Issues.Select(issue => Strings["Issue" + issue.Code]));
        Summary = Strings.Format("ImportSummary", document.Pages.Count, extraction.Items.Count,
            extraction.Items.Count(item => item.Issues.Count > 0),
            Strings.Money(extraction.Items.Sum(item => item.ReportedTotal ?? 0m)));
        if (_reviewTemplate?.SourceFormat == ImportSourceFormat.Excel)
        {
            Summary = Strings.Format("SpreadsheetSummary", extraction.Items.Count, extraction.Items.Count(item => item.Issues.Count > 0),
                Strings.Money(extraction.Items.Sum(item => item.ReportedTotal ?? 0m)));
            RawText = string.Join("\n", document.Pages.Select(page => page.Text));
        }
        if (extraction.ReportedDocumentTotal is decimal total) Summary += " · " + Strings.Format("DocumentTotal", Strings.Money(total));
    }
    [RelayCommand] private void Cancel() => _operation?.Cancel();
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using CancellationTokenSource operation = new();
        _operation = operation; IsBusy = true; Status = Strings["ImportWorking"];
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status = Strings["ImportCanceled"]; }
        catch (ImportException exception) { Status = Strings["Error" + exception.Code]; }
        catch (QuoteMatch.Domain.Quotations.QuotationException exception) { Status = Strings["QuotationError" + exception.Code]; }
        catch (JsonException) { Status = Strings["ErrorInvalidTemplate"]; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { Status = Strings["ImportStorageError"]; }
        finally { IsBusy = false; _operation = null; }
    }
    public void Dispose() { _operation?.Cancel(); _pdfSource = null; }
}










