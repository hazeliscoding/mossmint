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
// Usage: dotnet run --project tools/Mossmint.Screenshots -- [--size=WxH] [out-dir] [screen ...]
//        dotnet run --project tools/Mossmint.Screenshots -- --icons src/Mossmint.Desktop/Assets [docs/brand]
Environment.SetEnvironmentVariable(Mossmint.Desktop.Services.DataRoot.Variable, Path.Combine(Path.GetTempPath(), "mossmint-screenshots", "app-root"));
AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithMossmintFonts()
    .SetupWithoutStarting();

if (args.Length is 2 or 3 && args[0] == "--icons")
{
    var brandDir = args.Length == 3 ? Path.GetFullPath(args[2]) : null;
    foreach (var file in AppIcon.Write(Path.GetFullPath(args[1]), brandDir))
    {
        Console.WriteLine(file);
    }

    return;
}

// --size renders at another window size, such as the minimum, and adds it to the file names.
var sizeArg = args.FirstOrDefault(a => a.StartsWith("--size=", StringComparison.Ordinal));
args = [.. args.Where(a => a != sizeArg)];
var size = sizeArg?["--size=".Length..].Split('x').Select(int.Parse).ToArray() ?? [1440, 900];
var suffix = sizeArg is null ? "" : $"-{size[0]}x{size[1]}";

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
    ("transactions", () => scenes.Unlocked(vm => vm.OpenApp(Destination.Transactions))),
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
        (window.MinWidth, window.MinHeight) = (0, 0);
        window.Width = size[0];
        window.Height = size[1];
        window.Show();

        var path = Path.Combine(outDir, $"{name}-{theme.Key.ToString()!.ToLowerInvariant()}{suffix}.png");
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
