using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Mossmint.Desktop;
using Mossmint.Desktop.ViewModels;

// Renders Mossmint screens to PNG without a visible window, through the real
// Skia renderer. Screens only ever show synthetic data.
// Usage: dotnet run --project tools/Mossmint.Screenshots -- [out-dir] [screen ...]
//        dotnet run --project tools/Mossmint.Screenshots -- --icons src/Mossmint.Desktop/Assets
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

var app = (App)Application.Current!;
var screens = new (string Name, Action<MainWindowViewModel> Show)[]
{
    ("welcome", _ => { }),
    ("overview", vm => vm.OpenApp(Destination.Overview)),
    ("connections", vm => vm.OpenApp(Destination.Connections)),
    ("sidebar-collapsed", vm =>
    {
        vm.OpenApp(Destination.CashFlow);
        vm.Shell.IsSidebarCollapsed = true;
    }),
};

foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
{
    app.RequestedThemeVariant = theme;
    foreach (var (name, show) in screens)
    {
        if (only.Count > 0 && !only.Contains(name))
        {
            continue;
        }

        var window = app.CreateMainWindow();
        window.Width = 1440;
        window.Height = 900;
        window.Show();
        show((MainWindowViewModel)window.DataContext!);

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
