namespace QuoteMatch.Domain.Importing;

public enum FinancialField { Freight = 0, Discount = 1, OtherCharges = 2, IpiAmount = 3, IpiRate = 4, IcmsAmount = 5, IcmsRate = 6, SubstitutionTaxAmount = 7, SubstitutionTaxRate = 8 }

public sealed record FinancialAmounts
{
    public decimal? Freight { get; init; }
    public decimal? Discount { get; init; }
    public decimal? OtherCharges { get; init; }
    public decimal? IpiAmount { get; init; }
    public decimal? IpiRate { get; init; }
    public decimal? IcmsAmount { get; init; }
    public decimal? IcmsRate { get; init; }
    public decimal? SubstitutionTaxAmount { get; init; }
    public decimal? SubstitutionTaxRate { get; init; }
    public decimal? Get(FinancialField field) => field switch
    {
        FinancialField.Freight => Freight,
        FinancialField.Discount => Discount,
        FinancialField.OtherCharges => OtherCharges,
        FinancialField.IpiAmount => IpiAmount,
        FinancialField.IpiRate => IpiRate,
        FinancialField.IcmsAmount => IcmsAmount,
        FinancialField.IcmsRate => IcmsRate,
        FinancialField.SubstitutionTaxAmount => SubstitutionTaxAmount,
        FinancialField.SubstitutionTaxRate => SubstitutionTaxRate,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    public FinancialAmounts With(FinancialField field, decimal? value) => field switch
    {
        FinancialField.Freight => this with { Freight = value },
        FinancialField.Discount => this with { Discount = value },
        FinancialField.OtherCharges => this with { OtherCharges = value },
        FinancialField.IpiAmount => this with { IpiAmount = value },
        FinancialField.IpiRate => this with { IpiRate = value },
        FinancialField.IcmsAmount => this with { IcmsAmount = value },
        FinancialField.IcmsRate => this with { IcmsRate = value },
        FinancialField.SubstitutionTaxAmount => this with { SubstitutionTaxAmount = value },
        FinancialField.SubstitutionTaxRate => this with { SubstitutionTaxRate = value },
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    public static bool IsRate(FinancialField field) => field is FinancialField.IpiRate or FinancialField.IcmsRate or FinancialField.SubstitutionTaxRate;
    public static string CaptureName(FinancialField field) => char.ToLowerInvariant(field.ToString()[0]) + field.ToString()[1..];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Enum.GetValues<FinancialField>().All(key => Get(key) is not { } value || value >= 0 && (!IsRate(key) || value <= 100));
}

public sealed record FinancialCaptureEvidence(FinancialField Field, string OriginalText, decimal? Value, int PageNumber, string SourceText);
public sealed record DocumentFinancialCapture(FinancialAmounts Values, IReadOnlyList<FinancialCaptureEvidence> Evidence);
