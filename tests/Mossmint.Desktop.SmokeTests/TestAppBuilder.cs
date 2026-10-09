using Avalonia;
using Avalonia.Headless;
using Mossmint.Desktop;

[assembly: AvaloniaTestApplication(typeof(Mossmint.Desktop.SmokeTests.TestAppBuilder))]

namespace Mossmint.Desktop.SmokeTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Belt and braces: even the app's own container points at a throwaway folder.
        Environment.SetEnvironmentVariable(Services.DataRoot.Variable, Path.Combine(Path.GetTempPath(), "mossmint-ui-tests", "app-root"));
        return AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .WithMossmintFonts();
    }
}
