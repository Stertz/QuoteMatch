using QuoteMatch.Desktop.Preferences;

namespace QuoteMatch.Tests;

public sealed class PreferenceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_MissingFile_DefaultsToPortugueseAndSystemTheme()
    {
        PreferenceStore store = CreateStore();

        Assert.Equal(new UserPreferences("pt-BR", AppearanceMode.System), store.Load());
        Assert.False(store.HasReadError);
    }

    [Fact]
    public void SaveAndReload_PreservesLanguageAndAppearance()
    {
        UserPreferences preferences = new("en-US", AppearanceMode.Dark);

        Assert.True(CreateStore().TrySave(preferences));
        Assert.Equal(preferences, CreateStore().Load());
        Assert.False(File.Exists(Path.Combine(_directory, "preferences.json.tmp")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"CultureName\":\"unknown\",\"Appearance\":\"Light\"}")]
    [InlineData("{\"CultureName\":\"pt-BR\",\"Appearance\":999}")]
    public void Load_InvalidPreferences_RecoversWithVisibleErrorState(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "preferences.json"), contents);
        PreferenceStore store = CreateStore();

        Assert.Equal(new UserPreferences(), store.Load());
        Assert.True(store.HasReadError);
    }

    [Fact]
    public void Save_WhenTargetIsDirectory_ReturnsFailureInsteadOfCrashing()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "preferences.json"));

        Assert.False(CreateStore().TrySave(new UserPreferences()));
    }

    private PreferenceStore CreateStore() => new(Path.Combine(_directory, "preferences.json"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

