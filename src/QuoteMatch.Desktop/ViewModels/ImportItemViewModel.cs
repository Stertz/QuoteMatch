using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Desktop.ViewModels;

public partial class ImportItemViewModel : ObservableObject
{
    private readonly CapturedItem _capture;
    private readonly CultureInfo _culture;
    private readonly Localizer _strings;
    [ObservableProperty] private string _productCode;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _color;
    [ObservableProperty] private string _quantity;
    [ObservableProperty] private string _unitPrice;
    [ObservableProperty] private string _reportedTotal;
    [ObservableProperty] private string _unit;
    [ObservableProperty] private bool _isExcluded;
    [ObservableProperty] private string _exclusionReason;

    public ImportItemViewModel(CapturedItem capture, Localizer strings, ReviewedItem? review = null)
    {
        _capture = capture; _strings = strings;
        _culture = strings.Culture;
        ProductCode = review?.ProductCode ?? capture.ProductCode;
        Description = review?.Description ?? capture.Description;
        Color = review?.Color ?? capture.OriginalFields.GetValueOrDefault("color", string.Empty).Trim();
        Quantity = Format(review is null ? capture.Quantity : review.Quantity);
        UnitPrice = Format(review is null ? capture.UnitPrice : review.UnitPrice);
        ReportedTotal = Format(review is null ? capture.ReportedTotal : review.ReportedTotal);
        Unit = review?.Unit ?? capture.Unit;
        IsExcluded = review?.IsExcluded ?? false;
        ExclusionReason = review?.ExclusionReason ?? string.Empty;
        ExcludedLabel = strings["ExcludeRecord"];
        Evidence = capture.SourceText;
        OriginalFields = string.Join("\n", capture.OriginalFields.Select(pair => $"{pair.Key}: {pair.Value}"));
        Financials = new(review?.Financials ?? capture.Financials, strings);
        foreach (var field in Financials.Fields) field.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Financials));
        Issues = string.Join(" · ", capture.Issues.Select(issue => strings["Issue" + issue.Code]));
        Position = capture.SourceSheet is string sheet ? strings.Format("SpreadsheetPosition", sheet, capture.RecordNumber)
            : strings.Format("ImportPosition", capture.RecordNumber, capture.PageNumber);
        if (capture.IsTotalCalculated) Issues = string.Join(" · ", new[] { Issues, strings["SpreadsheetCalculatedTotal"] }.Where(text => text.Length > 0));
    }

    public string QuantityDisplay => Quantity + " " + Unit;
    public string TotalDisplay => Parse(ReportedTotal) is decimal total ? _strings.Money(total) : ReportedTotal;
    partial void OnQuantityChanged(string value) => OnPropertyChanged(nameof(QuantityDisplay));
    partial void OnUnitChanged(string value) => OnPropertyChanged(nameof(QuantityDisplay));
    partial void OnReportedTotalChanged(string value) => OnPropertyChanged(nameof(TotalDisplay));
    public string ExcludedLabel { get; }
    public string Position { get; }
    public string Evidence { get; }
    public string OriginalFields { get; }
    public string Issues { get; }
    public FinancialFieldsViewModel Financials { get; }
    public ReviewedItem ToReview() => new(_capture.Id, ProductCode.Trim(), Description.Trim(), Parse(Quantity),
        Parse(UnitPrice), Parse(ReportedTotal), Unit.Trim(), IsExcluded, ExclusionReason.Trim())
    { Color = Color.Trim(), Financials = Financials.Build() };
    private string Format(decimal? value) => value?.ToString("G29", _culture) ?? string.Empty;
    private decimal? Parse(string value) => decimal.TryParse(value, NumberStyles.Number, _culture, out decimal result) ? result : null;
}


