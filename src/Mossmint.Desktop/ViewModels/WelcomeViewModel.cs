using CommunityToolkit.Mvvm.Input;

namespace Mossmint.Desktop.ViewModels;

public sealed partial class WelcomeViewModel(Action importMyFinances, Action startFresh, Action restoreBackup) : ViewModelBase
{
    [RelayCommand]
    private void ImportMyFinances() => importMyFinances();

    [RelayCommand]
    private void StartFresh() => startFresh();

    [RelayCommand]
    private void RestoreBackup() => restoreBackup();
}
