using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Profiles;
using Mossmint.Application.Setup;

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
        Welcome = new WelcomeViewModel(ShowCreateThenImport, ShowCreateThenSetup, ShowRestore, TrySample);
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

    public void StartSetup(SetupStep? only = null, Destination returnTo = Destination.Overview) =>
        CurrentScreen = new SetupViewModel(_services, () => OpenApp(returnTo), only);

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

    private void ShowCreateThenSetup() => ShowCreate(() => StartSetup());

    private void ShowCreateThenImport() => ShowCreate(() => OpenApp(Destination.Imports));

    // The sample opens on this PC with a passphrase nobody sees: it holds nothing worth
    // recovering, so there's no recovery key to save. Returns why it failed, if it did.
    private async Task<string?> TrySample()
    {
        try
        {
            var created = await Task.Run(() =>
            {
                var created = _services.Profiles.Create(SampleHousehold.ProfileName, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), unlockOnThisDevice: true);
                SampleHousehold.Fill(created.Session.Data, _services.Clock);
                return created;
            });
            _services.Context.Open(created.Session);
            OpenAppCommand.NotifyCanExecuteChanged();
            OpenApp(Destination.Overview);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return $"The sample couldn't be created: {e.Message}";
        }
    }

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
        }, () => ShowCreate(() => StartSetup()), ShowRestore, select, notice);

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
