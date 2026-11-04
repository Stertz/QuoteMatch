using Avalonia;
using Velopack;
using Velopack.Logging;

namespace QuoteMatch.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetArgs(args).SetLogger(NullVelopackLogger.Instance).SetAutoApplyOnStartup(false).Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
