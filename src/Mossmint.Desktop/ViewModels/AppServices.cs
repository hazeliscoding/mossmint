using Mossmint.Application.Backups;
using Mossmint.Application.Import;
using Mossmint.Application.Profiles;
using Mossmint.Connectors.Abstractions;
using Mossmint.Desktop.Services;

namespace Mossmint.Desktop.ViewModels;

public sealed record AppServices(
    IProfileStore Profiles,
    IBackupService Backups,
    IDesktopDialogs Dialogs,
    ProfileContext Context,
    TimeProvider Clock)
{
    public ImportService Imports { get; init; } = new(new Import.Excel.WorkbookImporter(), Clock);

    public IAppUpdates Updates { get; init; } = new VelopackUpdates();

    // Makes no request until the user connects or syncs.
    public IConnectorProvider SimpleFin { get; init; } = new Connectors.SimpleFin.SimpleFinProvider(Clock);

    // Where app-wide preferences are saved; null keeps them in memory only.
    public string? PreferencesFolder { get; init; }
}

// The window-level moves a page can ask for.
public interface IAppNavigator
{
    void Lock();

    // A single step, from an empty page, returns to that page when it's done.
    void StartSetup(SetupStep? only = null, Destination returnTo = Destination.Overview);

    void ShowRestore();

    void ShowRecoveryKey(string recoveryKey, Action whenSaved);

    void ShowWelcomeAfterDelete();

    void OpenApp(Destination destination);
}
