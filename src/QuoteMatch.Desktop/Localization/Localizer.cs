using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace QuoteMatch.Desktop.Localization;

public sealed class Localizer : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new(
        "QuoteMatch.Desktop.Resources.Strings", typeof(Localizer).Assembly);

    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("pt-BR");
    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Resources.GetString(key, Culture)
        ?? throw new MissingManifestResourceException($"Missing UI resource: {key}.");

    public void SetCulture(string cultureName)
    {
        if (cultureName is not ("pt-BR" or "en-US"))
        {
            throw new ArgumentException("The requested UI culture is not supported.", nameof(cultureName));
        }

        Culture = CultureInfo.GetCultureInfo(cultureName);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public string Format(string key, params object[] arguments) =>
        string.Format(Culture, this[key], arguments);

    public string Money(decimal value, int decimals = 2) =>
        $"R$ {value.ToString($"N{decimals}", Culture)}";

    public string Quantity(decimal value, string unit = "m") => $"{value.ToString("#,0.############################", Culture)} {unit}";
}
