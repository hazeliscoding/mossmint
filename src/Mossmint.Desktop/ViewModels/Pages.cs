using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Data;
using Mossmint.Application.Forecasting;
using Mossmint.Application.Profiles;
using Mossmint.Desktop.Controls;
using Mossmint.Desktop.Services;
using Mossmint.Domain;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Desktop.ViewModels;

public sealed record RecordRow(string Name, string Detail, string Amount, string Status, StatusTone Tone);

// A metric card: a label, the value, a one-line headline, a status and a few mono facts.
public sealed record MetricCard(string Label, string Value, string Headline, string Status, StatusTone Tone, IReadOnlyList<string> Facts)
{
    public string AccessibleName => $"{Label}: {Value}. {Headline}. {Status}.";
}

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForecast), nameof(ChartTitle), nameof(ChartDescription))]
    private ProfileForecast? _forecast;

    [ObservableProperty]
    private string? _unavailable;

    public ObservableCollection<MetricCard> Metrics { get; } = [];

    public bool HasForecast => Forecast is not null;

    public string ChartTitle => Forecast is { } f ? $"{f.AccountName} balance · next {f.Days.Count} days" : "";

    public string ChartDescription => Forecast is { } f
        ? $"Lowest {Describe.Money(f.Lowest)} on {f.LowestDate:ddd d MMM}; cushion {Describe.Money(f.Cushion)}. The line dips to each day's low before it closes; paydays are marked. Cash Flow has the same numbers as a table."
        : "";

    public override void OnNavigatedTo()
    {
        if (!services.Context.IsUnlocked)
        {
            return;
        }

        var data = services.Context.Current.Data;
        var today = DateOnly.FromDateTime(services.Clock.GetLocalNow().DateTime);
        Metrics.Clear();
        (Forecast, Unavailable) = Forecasts.Build(data, today) switch
        {
            ProfileForecast f => (f, (string?)null),
            ForecastUnavailable u => (null, string.Join(" ", u.Missing)),
            _ => (null, null),
        };
        if (Forecast is { } forecast)
        {
            foreach (var card in OverviewCards.For(forecast))
            {
                Metrics.Add(card);
            }
        }

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

    // The chart's table twin.
    [RelayCommand]
    private void ShowTable() => navigator.OpenApp(Destination.CashFlow);

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

// The four Overview cards. Every figure says where it comes from; nothing shows a
// number the forecast couldn't support (ADR-0002).
public static class OverviewCards
{
    public static IEnumerable<MetricCard> For(ProfileForecast f)
    {
        var cushion = Describe.Money(f.Cushion);
        var safe = f.SafeToSpend;
        yield return safe.State switch
        {
            SafeToSpendState.Safe => new MetricCard(
                $"SAFE TO SPEND UNTIL {safe.Through:d MMM}".ToUpperInvariant(), Describe.Money(safe.Amount!.Value),
                $"After bills and the {cushion} cushion", Status(safe.Confidence!.Value).Status, Status(safe.Confidence!.Value).Tone,
                [$"through {safe.Through:ddd d MMM}", $"{cushion} cushion kept", "estimated bills at the top of their range"]),
            SafeToSpendState.Shortfall => new MetricCard(
                "SAFE TO SPEND", "−" + Describe.Money(safe.Amount!.Value), $"Short of the {cushion} cushion", "Shortfall", StatusTone.Critical,
                [$"by {safe.Through:ddd d MMM}", "even with no extra spending"]),
            _ => new MetricCard("SAFE TO SPEND", "—", "Needs confirmation", "Needs review", StatusTone.Warning, safe.Reasons),
        };

        var below = f.Lowest < f.Cushion;
        yield return new MetricCard(
            $"LOWEST POINT · NEXT {f.Days.Count} DAYS", Describe.Money(f.Lowest),
            below ? $"Dips below the {cushion} cushion" : $"Stays above the {cushion} cushion",
            below ? "Below cushion" : "Above cushion", below ? StatusTone.Critical : StatusTone.Healthy,
            [$"{f.LowestDate:ddd d MMM}", $"from {Describe.Money(f.Opening)} as of {f.OpeningProvenance.AsOf:d MMM}", $"{f.Days.Count} days simulated"]);

        yield return f.Paychecks.FirstOrDefault(p => p.Payday is not null) is { Payday: { } payday } next
            ? new MetricCard(
                $"NEXT PAYCHECK · {payday:d MMM}".ToUpperInvariant(), Describe.Money(next.NetPay),
                $"Covers {Plural(next.BillCount, "bill")}, leaves {Describe.Money(next.Leftover)}", "Scheduled", StatusTone.Info,
                [$"bills {Describe.Money(next.Bills)}", $"savings {Describe.Money(next.SavingsFunded)} of {Describe.Money(next.SavingsTarget)}"])
            : new MetricCard("NEXT PAYCHECK", "—", $"None in the next {f.Days.Count} days", "None scheduled", StatusTone.Neutral, ["add one on Paychecks"]);

        yield return f.Debts.Count == 0
            ? new MetricCard("DEBT-FREE FORECAST", "—", "No debts recorded", "Nothing owed", StatusTone.Neutral, [])
            : f.DebtFree is { } free
                ? new MetricCard(
                    "DEBT-FREE FORECAST", free.ToString("MMM yyyy", CultureInfo.CurrentCulture),
                    f.Debts.Any(d => d.IsApproximate) ? "Card payoffs are approximations" : "With regular payments only", "Estimated", StatusTone.Info,
                    [.. f.Debts.Take(3).Select(d => $"{d.Name} {d.PayoffDate:MMM yyyy}")])
                : new MetricCard(
                    "DEBT-FREE FORECAST", "—", "No date yet", "Needs review", StatusTone.Warning,
                    [.. f.Debts.Where(d => d.Problem is not null).Take(3).Select(d => $"{d.Name}: {d.Problem}")]);
    }

    private static (string Status, StatusTone Tone) Status(Confidence confidence) => Describe.Confidence(confidence);

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
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

    partial void OnIsBusyChanged(bool value) => services.Context.ActiveWork += value ? 1 : -1;

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
    : PageViewModel(Destination.Settings, "Settings", "Profile security, privacy and updates.")
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

    // The update check keeps its own status, so it survives moving between pages.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(DownloadUpdateCommand))]
    private bool _isUpdating;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    private string? _availableVersion;

    [ObservableProperty]
    private int _downloadProgress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand), nameof(RestartToUpdateCommand))]
    private bool _isDownloaded;

    [ObservableProperty]
    private string? _updateStatus;

    [ObservableProperty]
    private StatusTone _updateTone = StatusTone.Neutral;

    public bool CanUseThisDevice => OperatingSystem.IsWindows();

    public string ProfileName => services.Context.ProfileName;

    public string Version => services.Updates.Version;

    public bool IsInstalled => services.Updates.IsInstalled;

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

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckForUpdates() => Updating("Couldn't check for updates", async () =>
    {
        (AvailableVersion, IsDownloaded, DownloadProgress) = (null, false, 0);
        AvailableVersion = await services.Updates.CheckAsync();
        (UpdateStatus, UpdateTone) = AvailableVersion is { } version
            ? ($"Mossmint {version} is available.", StatusTone.Info)
            : ("Mossmint is up to date.", StatusTone.Healthy);
    });

    private bool CanCheck() => IsInstalled && !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task DownloadUpdate() => Updating("The download didn't finish", async () =>
    {
        var progress = new Progress<int>(percent => DownloadProgress = percent);
        await services.Updates.DownloadAsync(((IProgress<int>)progress).Report);
        IsDownloaded = true;
        (UpdateStatus, UpdateTone) = ($"Mossmint {AvailableVersion} is downloaded. It installs the next time Mossmint starts.", StatusTone.Healthy);
    });

    private bool CanDownload() => AvailableVersion is not null && !IsDownloaded && !IsUpdating;

    // Locks the profile first, so the database is closed and the key zeroed before Mossmint exits.
    [RelayCommand(CanExecute = nameof(IsDownloaded))]
    private void RestartToUpdate()
    {
        if (services.Context.ActiveWork > 0)
        {
            (UpdateStatus, UpdateTone) = ("A backup or import is still running. Restart once it has finished.", StatusTone.Warning);
            return;
        }

        navigator.Lock();
        services.Updates.ApplyAndRestart();
    }

    // Any failure (offline, GitHub, disk) is reported here; Mossmint keeps working without the update.
    private async Task Updating(string failure, Func<Task> work)
    {
        IsUpdating = true;
        try
        {
            await work();
        }
        catch (Exception e)
        {
            (UpdateStatus, UpdateTone) = ($"{failure}: {e.Message}", StatusTone.Warning);
        }
        finally
        {
            IsUpdating = false;
        }
    }
}
