using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Desktop.ViewModels;

public partial class FinancialFieldRow(FinancialField field, Localizer strings, decimal? value, bool isReadOnly = false) : ObservableObject
{
    [ObservableProperty] private string _text = value?.ToString("G29", strings.Culture) ?? string.Empty;
    public FinancialField Field { get; } = field;
    public bool IsReadOnly { get; } = isReadOnly;
    public string Label => strings["FinancialField" + Field];
    public decimal? Parsed => decimal.TryParse(Text, NumberStyles.Number, strings.Culture, out var parsed) ? parsed : null;
    public bool IsValid => string.IsNullOrWhiteSpace(Text) || Parsed is { } parsed && parsed >= 0 && (!FinancialAmounts.IsRate(Field) || parsed <= 100);
}

public sealed class FinancialFieldsViewModel
{
    public FinancialFieldsViewModel(FinancialAmounts values, Localizer strings, bool isReadOnly = false) => Fields = Enum.GetValues<FinancialField>().Select(field => new FinancialFieldRow(field, strings, values.Get(field), isReadOnly)).ToArray();
    public IReadOnlyList<FinancialFieldRow> Fields { get; }
    public bool IsValid => Fields.All(row => row.IsValid);
    public FinancialAmounts Build() => Fields.Aggregate(new FinancialAmounts(), (values, field) => values.With(field.Field, field.Parsed));
}
