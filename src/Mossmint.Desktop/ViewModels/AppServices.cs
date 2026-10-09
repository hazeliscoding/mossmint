using Mossmint.Application.Backups;
using Mossmint.Application.Profiles;
using Mossmint.Desktop.Services;

namespace Mossmint.Desktop.ViewModels;

public sealed record AppServices(
    IProfileStore Profiles,
    IBackupService Backups,
    IDesktopDialogs Dialogs,
    ProfileContext Context,
    TimeProvider Clock);

// The window-level moves a page can ask for.
public interface IAppNavigator
{
    void Lock();

    void StartSetup();

    void ShowRestore();

    void ShowRecoveryKey(string recoveryKey, Action whenSaved);

    void ShowWelcomeAfterDelete();
}
