using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Mossmint.Persistence.Diagnostics;
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

        // `Mossmint.Desktop.exe --verify-encryption [report.json]` checks the build
        // that is actually installed (ADR-0001) and exits 0 only if every check passes.
        if (args is ["--verify-encryption", ..])
        {
            var report = EncryptionSelfCheck.Run();
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (args.Length > 1)
            {
                File.WriteAllText(args[1], json);
            }

            Console.WriteLine(json);
            Environment.Exit(report.Passed ? 0 : 1);
            return;
        }

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
