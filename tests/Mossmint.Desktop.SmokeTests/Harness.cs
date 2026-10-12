using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mossmint.Application.Profiles;
using Mossmint.Connectors.Abstractions;
using Mossmint.Desktop.Services;
using Mossmint.Desktop.ViewModels;
using Mossmint.Desktop.Views;
using Mossmint.Persistence.Backups;
using Mossmint.Persistence.Profiles;
using Mossmint.Persistence.Security;
using Mossmint.Samples.Connectors;

namespace Mossmint.Desktop.SmokeTests;

internal sealed class FakeDialogs : IDesktopDialogs
{
    public string? NextOpen { get; set; }
    public string? NextSave { get; set; }
    public List<string> SavedTexts { get; } = [];
    public List<string> Copied { get; } = [];

    public Task<string?> PickBackupToOpenAsync() => Task.FromResult(NextOpen);

    public Task<string?> PickBackupSaveLocationAsync(string suggestedName) => Task.FromResult(NextSave);

    public string? NextFolder { get; set; }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult(NextFolder);

    public List<string> NextWorkbooks { get; } = [];

    public Task<IReadOnlyList<string>> PickWorkbooksAsync() => Task.FromResult<IReadOnlyList<string>>([.. NextWorkbooks]);

    public string? NextBankFile { get; set; }

    public Task<string?> PickBankFileAsync() => Task.FromResult(NextBankFile);

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
    // A real connector provider is only for proving it stays silent; the fake is the default.
    public Harness(IAppUpdates? updates = null, IConnectorProvider? simpleFin = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "mossmint-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        var locations = new ProfileLocations(Root);
        Services = new AppServices(
            new ProfileStore(locations, TimeProvider.System, Fast),
            new BackupService(locations, TimeProvider.System),
            Dialogs,
            new ProfileContext(),
            TimeProvider.System)
        {
            PreferencesFolder = Root,
            SimpleFin = simpleFin ?? SimpleFin,
        };
        if (updates is not null)
        {
            Services = Services with { Updates = updates };
        }
    }

    public string Root { get; }

    public FakeDialogs Dialogs { get; } = new();

    // Tests never reach a real SimpleFIN server.
    public FakeConnectorProvider SimpleFin { get; } = new();

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

// Plays the fake bank's syncs in order; "good-token" is the only token it takes.
internal sealed class FakeConnectorProvider : IConnectorProvider
{
    public const string Token = "good-token";
    public const string Credential = "https://user:s3cret@bridge.example/simplefin";

    public ConnectorId Id { get; } = new("simplefin");

    public string DisplayName => "SimpleFIN";

    public List<ConnectorSync> Script { get; } = [FakeBank.First(DateTimeOffset.Now) with { Connector = new("simplefin") }, FakeBank.Second(DateTimeOffset.Now) with { Connector = new("simplefin") }];

    public ConnectorException? NextFailure { get; set; }

    public string HostOf(string setupToken) =>
        setupToken == Token ? "bridge.example" : throw new FormatException("That isn't a SimpleFIN setup token.");

    public Task<string> ClaimAsync(string setupToken, CancellationToken cancellationToken) => Task.FromResult(Credential);

    public IConnector Open(string credential) => new Failing(this, new ScriptedConnector(Id, DisplayName, Script));

    private sealed class Failing(FakeConnectorProvider provider, IConnector inner) : IConnector
    {
        public ConnectorId Id => inner.Id;

        public string DisplayName => inner.DisplayName;

        public ConnectorCapabilities Capabilities => inner.Capabilities;

        public Task<ConnectorSync> SyncAsync(string? cursor, CancellationToken cancellationToken) =>
            provider.NextFailure is { } failure ? Task.FromException<ConnectorSync>(failure) : inner.SyncAsync(cursor, cancellationToken);
    }
}
