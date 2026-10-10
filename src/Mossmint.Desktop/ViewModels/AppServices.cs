using Mossmint.Application.Backups;
using Mossmint.Application.Import;
using Mossmint.Application.Profiles;
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
}

// The window-level moves a page can ask for.
public interface IAppNavigator
{
    void Lock();

    void StartSetup();

    void ShowRestore();

    void ShowRecoveryKey(string recoveryKey, Action whenSaved);

    void ShowWelcomeAfterDelete();
}
