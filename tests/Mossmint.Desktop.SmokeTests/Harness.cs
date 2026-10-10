using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mossmint.Application.Profiles;
using Mossmint.Desktop.Services;
using Mossmint.Desktop.ViewModels;
using Mossmint.Desktop.Views;
using Mossmint.Persistence.Backups;
using Mossmint.Persistence.Profiles;
using Mossmint.Persistence.Security;

namespace Mossmint.Desktop.SmokeTests;

internal sealed class FakeDialogs : IDesktopDialogs
{
    public string? NextOpen { get; set; }
    public string? NextSave { get; set; }
    public List<string> SavedTexts { get; } = [];
    public List<string> Copied { get; } = [];

    public Task<string?> PickBackupToOpenAsync() => Task.FromResult(NextOpen);

    public Task<string?> PickBackupSaveLocationAsync(string suggestedName) => Task.FromResult(NextSave);

    public List<string> NextWorkbooks { get; } = [];

    public Task<IReadOnlyList<string>> PickWorkbooksAsync() => Task.FromResult<IReadOnlyList<string>>([.. NextWorkbooks]);

    public Task<bool> SaveTextAsync(string suggestedName, string text, string title)
    {
        SavedTexts.Add(text);
        return Task.FromResult(true);
    }

    public Task CopyAsync(string text)
    {
        Copied.Add(text);
        return Task.CompletedTask;
    }
}

// A window over its own temporary data root, so tests never share profiles and
// never touch a real one.
internal sealed class Harness : IDisposable
{
    public const string Passphrase = "moss under the old oak";
    internal static readonly Argon2Settings Fast = new(1024, 1, 1);

    // Without fake updates the real Velopack service is used, which isn't installed under test.
    public Harness(IAppUpdates? updates = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "mossmint-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        var locations = new ProfileLocations(Root);
        Services = new AppServices(
            new ProfileStore(locations, TimeProvider.System, Fast),
            new BackupService(locations, TimeProvider.System),
            Dialogs,
            new ProfileContext(),
            TimeProvider.System);
        if (updates is not null)
        {
            Services = Services with { Updates = updates };
        }
    }

    public string Root { get; }

    public FakeDialogs Dialogs { get; } = new();

    public AppServices Services { get; }

    public MainWindow? Window { get; private set; }

    public MainWindowViewModel ViewModel => (MainWindowViewModel)Window!.DataContext!;

    public CreatedProfile CreateProfile(string name = "Juniper", bool thisDevice = false)
    {
        var created = Services.Profiles.Create(name, Passphrase, thisDevice);
        created.Session.Dispose();
        return created;
    }

    public MainWindow Open()
    {
        Window = new MainWindow { DataContext = new MainWindowViewModel(Services) };
        Window.Show();
        Pump();
        return Window;
    }

    public static void Pump() => Dispatcher.UIThread.RunJobs();

    public T Screen<T>() where T : ViewModelBase => (T)ViewModel.CurrentScreen;

    public string? PageTitle() =>
        Window!.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("page-title") && t.IsEffectivelyVisible)?.Text;

    public IEnumerable<string?> VisibleTexts() =>
        Window!.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text);

    public void Dispose()
    {
        Window?.Close();
        Services.Context.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a failed test; the OS temp cleanup gets it.
        }
    }
}

internal static class Visuals
{
    public static string? AutomationName(Control control) => Avalonia.Automation.AutomationProperties.GetName(control);
}
