using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Data;
using Mossmint.Application.Profiles;
using Mossmint.Desktop.Controls;
using Mossmint.Desktop.Services;
using Mossmint.Domain;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Desktop.ViewModels;

public sealed record RecordRow(string Name, string Detail, string Amount, string Status, StatusTone Tone);

// Words for schedules and amounts, shared by the record pages.
public static class Describe
{
    public static string Money(Money money) => MoneyInput.Format(money, CultureInfo.CurrentCulture);

    public static string Schedule(Schedule schedule)
    {
        var rule = schedule.Rule switch
        {
            SemiMonthlyRule(var first, var second) => $"the {Day(first)} and {Day(second)}",
            MonthlyRule(var day) => $"monthly on the {Day(day)}",
            WeeklyRule { IntervalWeeks: 1 } w => $"every {w.Anchor.DayOfWeek}",
            WeeklyRule w => $"every {w.IntervalWeeks} weeks from {w.Anchor:d MMM}",
            _ => "custom",
        };
        return schedule.Adjustment switch
        {
            BusinessDayAdjustment.PreviousBusinessDay => rule + ", earlier on weekends and holidays",
            BusinessDayAdjustment.NextBusinessDay => rule + ", later on weekends and holidays",
            _ => rule,
        };
    }

    public static string Day(DayOfMonth day) => day.IsLastDay ? "last day" : Ordinal(day.Day);

    public static string Kind(Domain.Accounts.AccountKind kind) => kind switch
    {
        Domain.Accounts.AccountKind.InstallmentLoan => "Loan",
        Domain.Accounts.AccountKind.CreditCard => "Credit card",
        Domain.Accounts.AccountKind.TrackingOnly => "Tracking only",
        _ => kind.ToString(),
    };

    public static (string Status, StatusTone Tone) Confidence(Confidence confidence) => confidence switch
    {
        Domain.Confidence.Confirmed => ("Confirmed", StatusTone.Healthy),
        Domain.Confidence.Scheduled => ("Scheduled", StatusTone.Info),
        Domain.Confidence.Estimated => ("Estimated", StatusTone.Info),
        _ => ("Needs review", StatusTone.Warning),
    };

    private static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}

public sealed partial class OverviewPageViewModel(AppServices services, IAppNavigator navigator)
    : PageViewModel(Destination.Overview, "Overview", "What you have, what's coming, and the lowest point before payday.")
{
    public ObservableCollection<RecordRow> Accounts { get; } = [];

    [ObservableProperty]
    private string _summary = "";

    public override void OnNavigatedTo()
    {
        if (!services.Context.IsUnlocked)
        {
            return;
        }

        var data = services.Context.Current.Data;
        var counts = data.Counts();
        Summary = $"{Plural(counts.Accounts, "account")}, {Plural(counts.Income, "paycheck")}, {Plural(counts.Bills, "bill")} and {Plural(counts.Debts, "debt")}, all stored encrypted on this PC.";
        var open = services.Context.Current.Imports.ReviewItems().Count(r => r.Status == Application.Import.ReviewStatus.Open);
        if (open > 0)
        {
            Summary += $" {Plural(open, "imported item")} {(open == 1 ? "is" : "are")} waiting for your review on the Imports page.";
        }
        Accounts.Clear();
        foreach (var account in data.Accounts())
        {
            var latest = data.Balances(account.Id).FirstOrDefault();
            var (status, tone) = latest is null ? ("No balance yet", StatusTone.Warning) : Describe.Confidence(latest.Provenance.Confidence);
            Accounts.Add(new RecordRow(
                account.Name,
                latest is null ? Describe.Kind(account.Kind) : $"{Describe.Kind(account.Kind)} · as of {latest.Provenance.AsOf:d MMM yyyy}",
                latest is null ? "—" : Describe.Money(latest.Balance),
                status,
                tone));
        }
    }

    [RelayCommand]
    private void RunSetup() => navigator.StartSetup();

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

// Paychecks, Bills and Debts in M2: what was entered, read back from the profile.
// Editing and forecasting on these pages arrive with the dashboard (M4).
public sealed class RecordsPageViewModel(
    Destination destination,
    string title,
    string purpose,
    string emptyText,
    ProfileContext context,
    Func<IProfileData, IEnumerable<RecordRow>> rows) : PageViewModel(destination, title, purpose)
{
    public ObservableCollection<RecordRow> Rows { get; } = [];

    public string EmptyText { get; } = emptyText;

    public bool IsEmpty => Rows.Count == 0;

    public override void OnNavigatedTo()
    {
        Rows.Clear();
        if (context.IsUnlocked)
        {
            foreach (var row in rows(context.Current.Data))
            {
                Rows.Add(row);
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    public static IEnumerable<RecordRow> Paychecks(IProfileData data) =>
        data.Income().Select(i => Row(i.Name, Describe.Schedule(i.PayDates), Describe.Money(i.NetPay), i.Provenance.Confidence));

    public static IEnumerable<RecordRow> Bills(IProfileData data) =>
        data.Bills().Select(b => Row(
            b.Name,
            $"{(b.Purpose == BillPurpose.DebtPayment ? "Debt payment, not spending" : b.Purpose.ToString())} · due {Describe.Schedule(b.DueDates)}",
            b.Amount.Kind == AmountKind.Estimated && b.Amount.Low is { } low && b.Amount.High is { } high
                ? $"{Describe.Money(b.Amount.Expected!.Value)} ({Describe.Money(low)}–{Describe.Money(high)})"
                : b.Amount.Expected is { } amount ? Describe.Money(amount) : "Unknown",
            b.Provenance.Confidence));

    public static IEnumerable<RecordRow> Debts(IProfileData data)
    {
        var accounts = data.Accounts().ToDictionary(a => a.Id);
        foreach (var debt in data.Debts())
        {
            var name = accounts.TryGetValue(debt.Account, out var account) ? account.Name : "Debt";
            var latest = data.Balances(debt.Account).FirstOrDefault();
            var model = debt.Terms switch
            {
                SimpleInterestLoanTerms t => $"Loan · daily interest at {t.Rates.Segments[^1].Rate} · {Describe.Money(t.RegularPayment)} on the {Describe.Day(((MonthlyRule)t.PaymentDates.Rule).Day)}",
                CreditCardTerms c => $"Card · {c.Buckets[0].Rates.Segments[^1].Rate} APR · closes on the {Describe.Day(c.StatementClosingDay)} · forecasts are approximate",
                AmortizingLoanTerms a => $"Amortizing loan · {a.Rates.Segments[^1].Rate}",
                _ => "Debt",
            };
            yield return Row(name, model, latest is null ? "—" : Describe.Money(latest.Balance), latest?.Provenance.Confidence ?? Confidence.NeedsReview);
        }
    }

    private static RecordRow Row(string name, string detail, string amount, Confidence confidence)
    {
        var (status, tone) = Describe.Confidence(confidence);
        return new RecordRow(name, detail, amount, status, tone);
    }
}

public sealed partial class BackupsPageViewModel(AppServices services, IAppNavigator navigator)
    : PageViewModel(Destination.Backups, "Backups", "Encrypted backups you can restore on any PC.")
{
    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private StatusTone _statusTone = StatusTone.Neutral;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackUpNowCommand), nameof(KeepLocalCopyCommand))]
    private bool _isBusy;

    [RelayCommand(CanExecute = nameof(CanBackUp))]
    private async Task BackUpNow()
    {
        var name = $"Mossmint {services.Context.ProfileName} {services.Clock.GetLocalNow():yyyy-MM-dd}.mmbak";
        if (await services.Dialogs.PickBackupSaveLocationAsync(name) is not { } path)
        {
            return;
        }

        await Run(() => services.Backups.Create(services.Context.Current, path));
    }

    [RelayCommand(CanExecute = nameof(CanBackUp))]
    private Task KeepLocalCopy() => Run(() => services.Backups.CreateLocal(services.Context.Current));

    [RelayCommand]
    private void Restore() => navigator.ShowRestore();

    private bool CanBackUp() => !IsBusy;

    private async Task Run(Func<Application.Backups.BackupReport> backup)
    {
        IsBusy = true;
        try
        {
            var report = await Task.Run(backup);
            Status = $"Backed up and verified: {Path.GetFileName(report.Path)} ({report.SizeBytes / 1024.0:0.#} KB). It opens with your passphrase or recovery key on any PC.";
            StatusTone = StatusTone.Healthy;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = $"The backup didn't complete: {e.Message}";
            StatusTone = StatusTone.Critical;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed partial class SettingsPageViewModel(AppServices services, IAppNavigator navigator)
    : PageViewModel(Destination.Settings, "Settings", "Profile security and privacy.")
{
    [ObservableProperty]
    private string _newPassphrase = "";

    [ObservableProperty]
    private string _confirmPassphrase = "";

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private StatusTone _statusTone = StatusTone.Neutral;

    [ObservableProperty]
    private bool _unlockOnThisDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    private string _deleteConfirmation = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangePassphraseCommand))]
    private bool _isBusy;

    public bool CanUseThisDevice => OperatingSystem.IsWindows();

    public string ProfileName => services.Context.ProfileName;

    public override void OnNavigatedTo()
    {
        Status = null;
        UnlockOnThisDevice = services.Context.Session?.Profile.UnlocksOnThisDevice == true;
        OnPropertyChanged(nameof(ProfileName));
    }

    // Only a change the user makes reaches the profile; loading the current
    // value on navigation matches it, so nothing is written.
    partial void OnUnlockOnThisDeviceChanged(bool value)
    {
        if (services.Context.Session is not { } session || session.Profile.UnlocksOnThisDevice == value)
        {
            return;
        }

        session.SetUnlockOnThisDevice(value);
        Report(value ? "This PC can now unlock the profile without the passphrase." : "This PC no longer unlocks the profile on its own.", StatusTone.Healthy);
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ChangePassphrase()
    {
        if (PassphrasePolicy.Problem(NewPassphrase) is { } problem)
        {
            Report(problem, StatusTone.Warning);
            return;
        }

        if (NewPassphrase != ConfirmPassphrase)
        {
            Report("The two passphrases don't match.", StatusTone.Warning);
            return;
        }

        var passphrase = NewPassphrase;
        IsBusy = true;
        try
        {
            await Task.Run(() => services.Context.Current.ChangePassphrase(passphrase));
            NewPassphrase = ConfirmPassphrase = "";
            Report("Passphrase changed. Backups made before today still open with the old one.", StatusTone.Healthy);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanChange() => !IsBusy;

    [RelayCommand]
    private void ReplaceRecoveryKey()
    {
        var key = services.Context.Current.ReplaceRecoveryKey();
        navigator.ShowRecoveryKey(key, () => Report("Recovery key replaced. The old one no longer works.", StatusTone.Healthy));
    }

    [RelayCommand]
    private void Lock() => navigator.Lock();

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeleteProfile()
    {
        var id = services.Context.Current.Profile.Id;
        services.Context.Lock();
        services.Profiles.Delete(id);
        navigator.ShowWelcomeAfterDelete();
    }

    private bool CanDelete() => services.Context.IsUnlocked && DeleteConfirmation == services.Context.ProfileName;

    private void Report(string message, StatusTone tone) => (Status, StatusTone) = (message, tone);
}
