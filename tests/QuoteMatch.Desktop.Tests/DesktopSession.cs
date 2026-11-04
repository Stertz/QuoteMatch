using Avalonia;
using Avalonia.Headless;

using QuoteMatch.Desktop;

namespace QuoteMatch.Desktop.Tests;

public sealed class DesktopSession : IAsyncDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(TestApplicationBuilder));

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}

public static class TestApplicationBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}


