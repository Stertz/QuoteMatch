using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using QuoteMatch.Desktop.Preferences;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Infrastructure.Persistence;
using QuoteMatch.Infrastructure.Updating;

namespace QuoteMatch.Desktop;

public sealed partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Window.KeyDownEvent.AddClassHandler<Window>((window, args) =>
        {
            if (args.Key != Key.Escape || window.Owner is null || args.Handled) return;
            args.Handled = true; window.Close();
        });
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string? configured = Environment.GetEnvironmentVariable("QUOTEMATCH_DATA_DIRECTORY");
            string directory = !string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured)
                ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuoteMatch");
            SqliteImportStore store = new(directory);
            desktop.MainWindow = new WorkspaceWindow
            {
                DataContext = new WorkspaceViewModel(new PreferenceStore(Path.Combine(directory, "preferences.json")), store, new VelopackApplicationUpdater())
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
