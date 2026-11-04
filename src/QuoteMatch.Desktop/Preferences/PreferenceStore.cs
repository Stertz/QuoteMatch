using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuoteMatch.Desktop.Preferences;

public sealed class PreferenceStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<AppearanceMode>() }
    };

    public bool HasReadError { get; private set; }

    public static PreferenceStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuoteMatch", "preferences.json"));

    public UserPreferences Load()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new UserPreferences();
            }

            UserPreferences? preferences = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(filePath), JsonOptions);
            if (preferences is null || preferences.CultureName is not ("pt-BR" or "en-US")
                || !Enum.IsDefined(preferences.Appearance))
            {
                HasReadError = true;
                return new UserPreferences();
            }

            return preferences;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            HasReadError = true;
            return new UserPreferences();
        }
    }

    public bool TrySave(UserPreferences preferences)
    {
        string temporaryPath = filePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, JsonOptions));
            File.Move(temporaryPath, filePath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
