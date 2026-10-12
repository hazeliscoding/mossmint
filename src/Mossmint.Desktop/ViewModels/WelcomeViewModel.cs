using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mossmint.Desktop.ViewModels;

public sealed partial class WelcomeViewModel(Action importMyFinances, Action startFresh, Action restoreBackup, Func<Task<string?>> trySample) : ViewModelBase
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrySampleCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    [RelayCommand]
    private void ImportMyFinances() => importMyFinances();

    [RelayCommand]
    private void StartFresh() => startFresh();

    [RelayCommand]
    private void RestoreBackup() => restoreBackup();

    [RelayCommand(CanExecute = nameof(CanTrySample))]
    private async Task TrySample()
    {
        IsBusy = true;
        try
        {
            Error = await trySample();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanTrySample() => !IsBusy;
}
