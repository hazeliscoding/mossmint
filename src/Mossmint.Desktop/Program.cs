using Avalonia;
using Avalonia.Media;
using Velopack;

namespace Mossmint.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack handles install/update hooks and exits early when invoked by
        // the installer. It must run before anything else; it never checks for
        // updates on its own.
        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithMossmintFonts()
            .LogToTrace();

    public static AppBuilder WithMossmintFonts(this AppBuilder builder) =>
        builder.With(new FontManagerOptions
        {
            DefaultFamilyName = "avares://Mossmint.Desktop/Assets/Fonts#Manrope",
            FontFallbacks = [new FontFallback { FontFamily = new FontFamily("Segoe UI") }],
        });
}
