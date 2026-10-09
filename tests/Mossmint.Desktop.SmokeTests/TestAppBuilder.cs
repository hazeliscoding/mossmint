using Avalonia;
using Avalonia.Headless;
using Mossmint.Desktop;

[assembly: AvaloniaTestApplication(typeof(Mossmint.Desktop.SmokeTests.TestAppBuilder))]

namespace Mossmint.Desktop.SmokeTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .WithMossmintFonts();
}
