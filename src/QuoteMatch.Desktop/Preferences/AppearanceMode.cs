namespace QuoteMatch.Desktop.Preferences;

public enum AppearanceMode
{
    System,
    Light,
    Dark
}

public sealed record UserPreferences(string CultureName = "pt-BR", AppearanceMode Appearance = AppearanceMode.System, bool RememberProductMappings = false,
    bool ShowDesiredQuantity = false, bool OnlyPendingProducts = true)
{
    public string LastPurchaseBranch { get; init; } = string.Empty;
}
