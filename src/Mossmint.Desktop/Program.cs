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

        // `Mossmint.Desktop.exe --verify-upgrade <data-root> <secrets-file> <backup> [report.json]` is the release
        // workflow's upgrade test: a profile and backup made by the previous release, with the passphrase on the
        // secrets file's "Passphrase:" line (as the import tool writes it). Never the app's own data root.
        if (args is ["--verify-upgrade", var root, var secrets, var backup, ..])
        {
            var own = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mossmint");
            if (Path.GetFullPath(root).TrimEnd('\\', '/').Equals(own, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Use a separate data root; the upgrade test never opens the app's own profiles.");
                Environment.Exit(64);
                return;
            }

            var passphrase = File.ReadLines(secrets).First(l => l.StartsWith("Passphrase: ", StringComparison.Ordinal))["Passphrase: ".Length..];
            var upgrade = UpgradeCheck.Run(root, passphrase, backup, TimeProvider.System);
            var json = JsonSerializer.Serialize(upgrade, new JsonSerializerOptions { WriteIndented = true });
            if (args.Length > 4)
            {
                File.WriteAllText(args[4], json);
            }

            Console.WriteLine(json);
            Environment.Exit(upgrade.Passed ? 0 : 1);
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
