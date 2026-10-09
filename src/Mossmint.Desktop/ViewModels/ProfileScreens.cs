using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Backups;
using Mossmint.Application.Profiles;

namespace Mossmint.Desktop.ViewModels;

public sealed partial class CreateProfileViewModel(AppServices services, Action<CreatedProfile> created, Action back) : ViewModelBase
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PassphraseHint))]
    private string _passphrase = "";

    [ObservableProperty]
    private string _confirm = "";

    [ObservableProperty]
    private bool _unlockOnThisDevice;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private bool _isBusy;

    public bool CanUseThisDevice => OperatingSystem.IsWindows();

    public string PassphraseHint => Passphrase.Length == 0
        ? $"At least {PassphrasePolicy.MinimumLength} characters. A few unrelated words are easy to remember and hard to guess."
        : PassphrasePolicy.Problem(Passphrase) ?? "Good length.";

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task Create()
    {
        Error = string.IsNullOrWhiteSpace(Name) ? "Give the profile a name; it shows on the unlock screen."
            : PassphrasePolicy.Problem(Passphrase) is { } problem ? problem
            : Passphrase != Confirm ? "The two passphrases don't match."
            : null;
        if (Error is not null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (name, passphrase, device) = (Name, Passphrase, UnlockOnThisDevice);
            var result = await Task.Run(() => services.Profiles.Create(name, passphrase, device));
            Passphrase = Confirm = "";
            created(result);
        }
        catch (ArgumentException e)
        {
            Error = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = $"The profile couldn't be created: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCreate() => !IsBusy;

    [RelayCommand]
    private void Back() => back();
}

public sealed partial class RecoveryKeyViewModel(AppServices services, string recoveryKey, Action whenSaved) : ViewModelBase
{
    public string RecoveryKey { get; } = recoveryKey;

    public IReadOnlyList<string> Groups { get; } = recoveryKey.Split('-');

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private bool _stored;

    [ObservableProperty]
    private string? _status;

    [RelayCommand]
    private async Task SaveToFile()
    {
        var text = string.Join(Environment.NewLine,
            "Mossmint recovery key",
            "",
            RecoveryKey,
            "",
            "This key unlocks your profile and its backups if you forget your passphrase.",
            "Keep it somewhere safe and offline, like a printed copy or a password manager.",
            "Anyone who has it and your profile can read your finances.");
        Status = await services.Dialogs.SaveTextAsync("Mossmint recovery key.txt", text)
            ? "Saved. Move the file somewhere safe and offline."
            : null;
    }

    [RelayCommand]
    private async Task Copy()
    {
        await services.Dialogs.CopyAsync(RecoveryKey);
        Status = "Copied. Paste it into a password manager, then copy something else to clear the clipboard.";
    }

    [RelayCommand(CanExecute = nameof(Stored))]
    private void Continue() => whenSaved();
}

public sealed partial class UnlockViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly Action _unlocked;

    public UnlockViewModel(AppServices services, Action unlocked, Action create, Action restore, ProfileId? select = null, string? notice = null)
    {
        (_services, _unlocked) = (services, unlocked);
        CreateProfileCommand = new RelayCommand(create);
        RestoreCommand = new RelayCommand(restore);
        Profiles = new ObservableCollection<ProfileSummary>(services.Profiles.List());
        _selectedProfile = Profiles.FirstOrDefault(p => p.Id == select) ?? Profiles.FirstOrDefault();
        _notice = notice;
    }

    public ObservableCollection<ProfileSummary> Profiles { get; }

    public IRelayCommand CreateProfileCommand { get; }

    public IRelayCommand RestoreCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUnlockWithThisDevice))]
    private ProfileSummary? _selectedProfile;

    [ObservableProperty]
    private string _passphrase = "";

    [ObservableProperty]
    private bool _useRecoveryKey;

    [ObservableProperty]
    private string _recoveryKey = "";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    private bool _isBusy;

    public bool CanUnlockWithThisDevice => SelectedProfile?.UnlocksOnThisDevice == true;

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task Unlock()
    {
        if (SelectedProfile is not { } profile)
        {
            Error = "Choose a profile.";
            return;
        }

        UnlockSecret secret = UseRecoveryKey ? new UnlockSecret.RecoveryKey(RecoveryKey) : new UnlockSecret.Passphrase(Passphrase);
        IsBusy = true;
        try
        {
            Apply(await Task.Run(() => _services.Profiles.Unlock(profile.Id, secret)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUnlock() => !IsBusy;

    // "This PC" unlock skips Argon2, so it is quick enough to run at startup.
    [RelayCommand]
    public void UnlockWithThisDevice()
    {
        if (SelectedProfile is { } profile)
        {
            Apply(_services.Profiles.Unlock(profile.Id, new UnlockSecret.ThisDevice()));
        }
    }

    private void Apply(UnlockResult result)
    {
        switch (result)
        {
            case UnlockResult.Unlocked(var session):
                Passphrase = RecoveryKey = "";
                Error = null;
                _services.Context.Open(session);
                _unlocked();
                break;
            case UnlockResult.Failed(_, var message):
                Error = message;
                break;
        }
    }
}

public sealed partial class RestoreViewModel(AppServices services, Action<ProfileSummary> restored, Action back) : ViewModelBase
{
    [ObservableProperty]
    private string? _filePath;

    [ObservableProperty]
    private string _passphrase = "";

    [ObservableProperty]
    private bool _useRecoveryKey;

    [ObservableProperty]
    private string _recoveryKey = "";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private bool _isBusy;

    [RelayCommand]
    private async Task PickFile() => FilePath = await services.Dialogs.PickBackupToOpenAsync() ?? FilePath;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task Restore()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            Error = "Choose a backup file.";
            return;
        }

        UnlockSecret secret = UseRecoveryKey ? new UnlockSecret.RecoveryKey(RecoveryKey) : new UnlockSecret.Passphrase(Passphrase);
        var path = FilePath;
        IsBusy = true;
        try
        {
            switch (await Task.Run(() => services.Backups.Restore(path, secret)))
            {
                case RestoreResult.Restored(var profile, _):
                    Passphrase = RecoveryKey = "";
                    restored(profile);
                    break;
                case RestoreResult.Failed(_, var message):
                    Error = message;
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRestore() => !IsBusy;

    [RelayCommand]
    private void Back() => back();
}
