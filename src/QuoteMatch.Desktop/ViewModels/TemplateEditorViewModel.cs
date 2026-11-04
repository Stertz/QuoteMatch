using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using QuoteMatch.Application.Importing;
using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Desktop.ViewModels;

public partial class TemplateEditorViewModel : ObservableObject
{
    private PdfTemplate _source = new() { Id = Guid.Empty, Version = 0 };
    private string _loadedSnapshot = string.Empty;
    public bool HasUnsavedChanges => JsonSerializer.Serialize(Build()) != _loadedSnapshot;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _supplierName = string.Empty;
    [ObservableProperty] private string _cultureName = "pt-BR";
    [ObservableProperty] private string _defaultUnit = "m";
    [ObservableProperty] private int _strategyIndex;
    [ObservableProperty] private string _identificationPattern = string.Empty;
    [ObservableProperty] private string _recordStartPattern = string.Empty;
    [ObservableProperty] private string _recordPattern = string.Empty;
    [ObservableProperty] private string _tableEndPattern = string.Empty;
    [ObservableProperty] private string _expectedCountPattern = string.Empty;
    [ObservableProperty] private string _documentTotalPattern = string.Empty;
    [ObservableProperty] private string _documentFinancialPattern = string.Empty;
    [ObservableProperty] private decimal _absoluteTolerance = .01m;
    [ObservableProperty] private decimal _relativeTolerance = .001m;
    [ObservableProperty] private int _sourceFormatIndex;
    [ObservableProperty] private int _codeKindIndex;
    [ObservableProperty] private string _sheetName = string.Empty;
    [ObservableProperty] private int _headerRow = 1;
    [ObservableProperty] private int _firstDataRow = 2;
    [ObservableProperty] private int? _lastDataRow;
    [ObservableProperty] private string _codeColumn = "A";
    [ObservableProperty] private string _descriptionColumn = "B";
    [ObservableProperty] private string _quantityColumn = "C";
    [ObservableProperty] private string _unitPriceColumn = "D";
    [ObservableProperty] private string _totalColumn = string.Empty;
    [ObservableProperty] private string _unitColumn = string.Empty;
    [ObservableProperty] private int _separatorIndex;
    [ObservableProperty] private int _encodingIndex;
    public SpreadsheetTextOptions TextOptions => new() { Separator = (DelimitedSeparator)SeparatorIndex, Encoding = (DelimitedEncoding)EncodingIndex };
    private SpreadsheetDocument? _workbook;
    public bool IsExcel => SourceFormatIndex == (int)ImportSourceFormat.Excel;
    public bool IsPdf => !IsExcel;
    public bool HasWorkbookSample => _workbook is not null;
    public ObservableCollection<string> SheetNames { get; } = [];
    public ObservableCollection<SpreadsheetColumnPreview> Columns { get; } = [];
    partial void OnSourceFormatIndexChanged(int value) { OnPropertyChanged(nameof(IsExcel)); OnPropertyChanged(nameof(IsPdf)); }
    partial void OnSheetNameChanged(string value) => RefreshColumns();
    partial void OnHeaderRowChanged(int value) => RefreshColumns();

    public void ShowWorkbook(SpreadsheetDocument workbook)
    {
        _workbook = workbook;
        OnPropertyChanged(nameof(HasWorkbookSample));
        SheetNames.Clear();
        foreach (var sheet in workbook.Sheets) SheetNames.Add(sheet.Name);
        if (!SheetNames.Contains(SheetName)) SheetName = SheetNames.FirstOrDefault() ?? string.Empty;
        RefreshColumns();
    }

    private void RefreshColumns()
    {
        Columns.Clear();
        SpreadsheetSheet? sheet = _workbook?.Sheets.FirstOrDefault(sheet => sheet.Name == SheetName);
        SpreadsheetRow? header = sheet?.Rows.FirstOrDefault(row => row.Number == HeaderRow);
        if (header is null) return;
        for (int index = 0; index < header.Cells.Count; index++)
        {
            string sample = sheet!.Rows.FirstOrDefault(row => row.Number > HeaderRow && row.Cells.Count > index)?.Cells[index].Text ?? string.Empty;
            Columns.Add(new(SpreadsheetExtraction.ColumnName(index), header.Cells[index].Text, sample));
        }
    }
    public int CultureIndex { get => CultureName == "en-US" ? 1 : 0; set { CultureName = value == 1 ? "en-US" : "pt-BR"; OnPropertyChanged(); } }
    public decimal RelativeTolerancePercent { get => RelativeTolerance * 100m; set { RelativeTolerance = value / 100m; OnPropertyChanged(); } }

    public TemplateEditorViewModel() => Load(_source);
    public void Load(PdfTemplate template)
    {
        _source = template;
        _workbook = null; SheetNames.Clear(); Columns.Clear();
        OnPropertyChanged(nameof(HasWorkbookSample));
        Name = template.Name; SupplierName = template.SupplierName; CultureName = template.CultureName;
        DefaultUnit = template.DefaultUnit; StrategyIndex = (int)template.TextStrategy;
        IdentificationPattern = template.IdentificationPattern; RecordStartPattern = template.RecordStartPattern;
        RecordPattern = template.RecordPattern; TableEndPattern = template.TableEndPattern;
        ExpectedCountPattern = template.ExpectedCountPattern; DocumentTotalPattern = template.DocumentTotalPattern;
        DocumentFinancialPattern = template.DocumentFinancialPattern;
        AbsoluteTolerance = template.AbsoluteTolerance; RelativeTolerance = template.RelativeTolerance;
        SourceFormatIndex = (int)template.SourceFormat;
        CodeKindIndex = (int)template.CodeKind;
        SheetName = template.Spreadsheet.SheetName; HeaderRow = template.Spreadsheet.HeaderRow;
        FirstDataRow = template.Spreadsheet.FirstDataRow; LastDataRow = template.Spreadsheet.LastDataRow;
        CodeColumn = template.Spreadsheet.CodeColumn; DescriptionColumn = template.Spreadsheet.DescriptionColumn;
        QuantityColumn = template.Spreadsheet.QuantityColumn; UnitPriceColumn = template.Spreadsheet.UnitPriceColumn;
        TotalColumn = template.Spreadsheet.TotalColumn; UnitColumn = template.Spreadsheet.UnitColumn;
        SeparatorIndex = (int)template.Spreadsheet.TextOptions.Separator; EncodingIndex = (int)template.Spreadsheet.TextOptions.Encoding;
        RefreshColumns();
        OnPropertyChanged(nameof(CultureIndex)); OnPropertyChanged(nameof(RelativeTolerancePercent));
        _loadedSnapshot = JsonSerializer.Serialize(Build());
    }
    public PdfTemplate Build() => _source with
    {
        Name = Name.Trim(),
        SupplierName = SupplierName.Trim(),
        CultureName = CultureName.Trim(),
        DefaultUnit = DefaultUnit.Trim(),
        TextStrategy = (PdfTextStrategy)StrategyIndex,
        IdentificationPattern = IdentificationPattern,
        RecordStartPattern = RecordStartPattern,
        RecordPattern = RecordPattern,
        TableEndPattern = TableEndPattern,
        ExpectedCountPattern = ExpectedCountPattern,
        DocumentTotalPattern = DocumentTotalPattern,
        DocumentFinancialPattern = DocumentFinancialPattern,
        AbsoluteTolerance = AbsoluteTolerance,
        RelativeTolerance = RelativeTolerance,
        RemoveGroupingSpaces = true,
        SourceFormat = (ImportSourceFormat)SourceFormatIndex,
        CodeKind = (ProductCodeKind)CodeKindIndex,
        Spreadsheet = new()
        {
            TextOptions = TextOptions,
            SheetName = SheetName,
            HeaderRow = HeaderRow,
            FirstDataRow = FirstDataRow,
            LastDataRow = LastDataRow,
            CodeColumn = CodeColumn.Trim().ToUpperInvariant(),
            DescriptionColumn = DescriptionColumn.Trim().ToUpperInvariant(),
            QuantityColumn = QuantityColumn.Trim().ToUpperInvariant(),
            UnitPriceColumn = UnitPriceColumn.Trim().ToUpperInvariant(),
            TotalColumn = TotalColumn.Trim().ToUpperInvariant(),
            UnitColumn = UnitColumn.Trim().ToUpperInvariant(),
            ExpectedHeaders = BuildHeaders()
        }
    };

    private IReadOnlyDictionary<string, string> BuildHeaders()
    {
        string[] mapped = new[] { CodeColumn, DescriptionColumn, QuantityColumn, UnitPriceColumn, TotalColumn, UnitColumn }
            .Select(column => column.Trim().ToUpperInvariant()).Where(column => column.Length > 0).Distinct().ToArray();
        if (_workbook is null) return _source.Spreadsheet.ExpectedHeaders;
        return mapped.Where(column => Columns.Any(item => item.Column == column))
            .ToDictionary(column => column, column => Columns.Single(item => item.Column == column).Header);
    }
}

public sealed record SpreadsheetColumnPreview(string Column, string Header, string Sample);

