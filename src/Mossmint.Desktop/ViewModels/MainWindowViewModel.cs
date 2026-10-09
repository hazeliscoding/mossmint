using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mossmint.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(ShellViewModel shell)
    {
        Shell = shell;
        Welcome = new WelcomeViewModel(OpenApp);
        // No profile storage exists yet (M2), so every launch is a clean install.
        _currentScreen = Welcome;
    }

    public ShellViewModel Shell { get; }
    public WelcomeViewModel Welcome { get; }

    [ObservableProperty]
    private ViewModelBase _currentScreen;

    [RelayCommand]
    public void OpenApp(Destination destination)
    {
        Shell.Navigate(destination);
        CurrentScreen = Shell;
    }
}
