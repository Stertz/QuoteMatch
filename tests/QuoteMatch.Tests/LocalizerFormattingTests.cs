using System.Globalization;

using QuoteMatch.Desktop.Localization;

namespace QuoteMatch.Tests;

public sealed class LocalizerFormattingTests
{
    [Theory]
    [InlineData("pt-BR", "1500.5", "m", "1.500,5 m")]
    [InlineData("en-US", "1500.5", "m", "1,500.5 m")]
    [InlineData("pt-BR", "3.125", "par", "3,125 par")]
    [InlineData("en-US", "1500", "pc", "1,500 pc")]
    public void Quantity_PreservesFractionalDemandAndUsesTheSelectedCulture(string culture, string quantity, string unit, string expected)
    {
        Localizer localizer = new();
        localizer.SetCulture(culture);

        Assert.Equal(expected, localizer.Quantity(decimal.Parse(quantity, CultureInfo.InvariantCulture), unit));
    }

    [Fact]
    public void SetCulture_ChangesResourceLanguageAndNotifiesBindings()
    {
        Localizer localizer = new();
        bool notified = false;
        localizer.PropertyChanged += (_, args) => notified |= args.PropertyName == string.Empty;

        Assert.Equal("Comparação de cotações", localizer["QuotationTitle"]);
        localizer.SetCulture("en-US");

        Assert.True(notified);
        Assert.Equal("Quotation comparison", localizer["QuotationTitle"]);
        Assert.Equal("R$ 98,847.00", localizer.Money(98847m));
    }
}

