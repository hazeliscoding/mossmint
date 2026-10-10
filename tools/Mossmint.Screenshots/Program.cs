using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Mossmint.Desktop;
using Mossmint.Desktop.ViewModels;
using Mossmint.Screenshots;
using Mossmint.Screenshots.EnginePreview;

// Renders Mossmint screens to PNG without a visible window, through the real
// Skia renderer. Screens only ever show synthetic data, from throwaway profiles
// in a temporary folder; the app's own data root is pointed there as well.
// Usage: dotnet run --project tools/Mossmint.Screenshots -- [out-dir] [screen ...]
//        dotnet run --project tools/Mossmint.Screenshots -- --icons src/Mossmint.Desktop/Assets
Environment.SetEnvironmentVariable(Mossmint.Desktop.Services.DataRoot.Variable, Path.Combine(Path.GetTempPath(), "mossmint-screenshots", "app-root"));
AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithMossmintFonts()
    .SetupWithoutStarting();

if (args.Length == 2 && args[0] == "--icons")
{
    foreach (var file in AppIcon.Write(Path.GetFullPath(args[1])))
    {
        Console.WriteLine(file);
    }

    return;
}

var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("artifacts", "screenshots"));
var only = args.Skip(1).ToHashSet(StringComparer.OrdinalIgnoreCase);
Directory.CreateDirectory(outDir);

var app = (App)Avalonia.Application.Current!;
using var scenes = new SyntheticScenes();

var screens = new (string Name, Func<Window> Create)[]
{
    ("welcome", scenes.Empty),
    ("create-profile", scenes.CreateProfile),
    ("recovery-key", scenes.RecoveryKey),
    ("unlock", scenes.Locked),
    ("setup", scenes.Setup),
    ("overview", () => scenes.Unlocked(vm => vm.OpenApp(Destination.Overview))),
    ("cash-flow", () => scenes.Unlocked(vm => vm.OpenApp(Destination.CashFlow))),
    ("paychecks", () => scenes.Unlocked(vm => vm.OpenApp(Destination.Paychecks))),
    ("bills", () => scenes.Unlocked(vm => vm.OpenApp(Destination.Bills))),
    ("debts", () => scenes.Unlocked(vm => vm.OpenApp(Destination.Debts))),
    ("savings", () => scenes.Unlocked(vm => vm.OpenApp(Destination.Savings))),
    ("backups", () => scenes.Unlocked(vm => vm.OpenApp(Destination.Backups))),
    ("import-preview", () => scenes.Imports(commit: false)),
    ("import-review", () => scenes.Imports(commit: true)),
    ("settings", () => scenes.Unlocked(vm => vm.OpenApp(Destination.Settings))),
    ("connections", () => scenes.Unlocked(vm => vm.OpenApp(Destination.Connections))),
    ("sidebar-collapsed", () => scenes.Unlocked(vm =>
    {
        vm.OpenApp(Destination.CashFlow);
        vm.Shell.IsSidebarCollapsed = true;
    })),
    ("engine-preview", EnginePreviewWindow.Create),
};

foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
{
    app.RequestedThemeVariant = theme;
    foreach (var (name, create) in screens)
    {
        if (only.Count > 0 && !only.Contains(name))
        {
            continue;
        }

        var window = create();
        window.Width = 1440;
        window.Height = 900;
        window.Show();

        var path = Path.Combine(outDir, $"{name}-{theme.Key.ToString()!.ToLowerInvariant()}.png");
        Capture(window, path);
        window.Close();
        Console.WriteLine(path);
    }
}

static void Capture(Window window, string path)
{
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    var frame = window.CaptureRenderedFrame()
        ?? throw new InvalidOperationException("The headless renderer produced no frame.");
    frame.Save(path, PngBitmapEncoderOptions.Default);
}
