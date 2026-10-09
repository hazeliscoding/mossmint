using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Profiles;

namespace Mossmint.Desktop.ViewModels;

// Owns which screen the window shows: welcome, profile creation, recovery key,
// unlock, restore, setup, or the app itself. The app is only reachable while a
// profile is unlocked.
public sealed partial class MainWindowViewModel : ViewModelBase, IAppNavigator
{
    private readonly AppServices _services;

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        Shell = new ShellViewModel(services, this);
        Welcome = new WelcomeViewModel(ShowCreateThenImport, ShowCreateThenSetup, ShowRestore);
        _currentScreen = Welcome;
        Start();
    }

    public ShellViewModel Shell { get; }

    public WelcomeViewModel Welcome { get; }

    [ObservableProperty]
    private ViewModelBase _currentScreen;

    public bool IsUnlocked => _services.Context.IsUnlocked;

    // A clean install shows the welcome screen; otherwise the unlock screen, which
    // tries "this PC" unlock straight away when there's only one profile to open.
    public void Start()
    {
        var profiles = _services.Profiles.List();
        if (profiles.Count == 0)
        {
            CurrentScreen = Welcome;
            return;
        }

        var unlock = NewUnlock();
        CurrentScreen = unlock;
        if (profiles.Count == 1 && profiles[0].UnlocksOnThisDevice)
        {
            unlock.UnlockWithThisDevice();
        }
    }

    [RelayCommand(CanExecute = nameof(IsUnlocked))]
    public void OpenApp(Destination destination)
    {
        Shell.Navigate(destination);
        Shell.Refresh();
        CurrentScreen = Shell;
    }

    public void Lock()
    {
        _services.Context.Lock();
        OpenAppCommand.NotifyCanExecuteChanged();
        CurrentScreen = NewUnlock();
    }

    public void StartSetup() => CurrentScreen = new SetupViewModel(_services, () => OpenApp(Destination.Overview));

    public void ShowRestore() =>
        CurrentScreen = new RestoreViewModel(_services, profile => ShowUnlock(profile.Id, $"Restored “{profile.Name}”. Unlock it to continue."), Back);

    public void ShowRecoveryKey(string recoveryKey, Action whenSaved)
    {
        var returnTo = CurrentScreen;
        CurrentScreen = new RecoveryKeyViewModel(_services, recoveryKey, () =>
        {
            CurrentScreen = returnTo;
            whenSaved();
        });
    }

    public void ShowWelcomeAfterDelete()
    {
        OpenAppCommand.NotifyCanExecuteChanged();
        Start();
    }

    private void ShowUnlock(ProfileId? select, string? notice) => CurrentScreen = NewUnlock(select, notice);

    private void ShowCreateThenSetup() => ShowCreate(StartSetup);

    private void ShowCreateThenImport() => ShowCreate(() => OpenApp(Destination.Imports));

    private void ShowCreate(Action next) =>
        CurrentScreen = new CreateProfileViewModel(_services, created =>
        {
            _services.Context.Open(created.Session);
            OpenAppCommand.NotifyCanExecuteChanged();
            CurrentScreen = new RecoveryKeyViewModel(_services, created.RecoveryKey, next);
        }, Back);

    private UnlockViewModel NewUnlock(ProfileId? select = null, string? notice = null) =>
        new(_services, () =>
        {
            OpenAppCommand.NotifyCanExecuteChanged();
            OpenApp(Destination.Overview);
        }, () => ShowCreate(StartSetup), ShowRestore, select, notice);

    private void Back()
    {
        if (_services.Context.IsUnlocked)
        {
            OpenApp(Shell.SelectedItem?.Destination ?? Destination.Overview);
        }
        else
        {
            Start();
        }
    }
}
