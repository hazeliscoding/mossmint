using CommunityToolkit.Mvvm.Input;

namespace Mossmint.Desktop.ViewModels;

public sealed partial class WelcomeViewModel(Action<Destination> openApp) : ViewModelBase
{
    [RelayCommand]
    private void ImportMyFinances() => openApp(Destination.Imports);

    [RelayCommand]
    private void StartFresh() => openApp(Destination.Overview);

    [RelayCommand]
    private void RestoreBackup() => openApp(Destination.Backups);
}
