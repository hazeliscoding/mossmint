using Avalonia.Headless.XUnit;
using Mossmint.Desktop.Services;
using Mossmint.Desktop.ViewModels;

namespace Mossmint.Desktop.SmokeTests;

internal sealed class FakeUpdates : IAppUpdates
{
    public string? Latest { get; set; }
    public Exception? Failure { get; set; }
    public bool Applied { get; private set; }

    public string Version => "0.3.1";

    public bool IsInstalled { get; set; } = true;

    public int Checks { get; private set; }

    public Task<string?> CheckAsync()
    {
        Checks++;
        return Failure is null ? Task.FromResult(Latest) : Task.FromException<string?>(Failure);
    }

    public Task DownloadAsync(Action<int> progress)
    {
        progress(100);
        return Task.CompletedTask;
    }

    public void ApplyAndRestart() => Applied = true;
}

public class UpdateFlowTests
{
    [AvaloniaFact]
    public async Task Check_download_and_restart_wait_for_a_running_backup()
    {
        var updates = new FakeUpdates { Latest = "0.4.0" };
        using var app = new Harness(updates);
        var settings = await OpenSettings(app);

        await settings.CheckForUpdatesCommand.ExecuteAsync(null);
        settings.UpdateStatus.Should().Be("Mossmint 0.4.0 is available.");
        settings.RestartToUpdateCommand.CanExecute(null).Should().BeFalse("nothing is downloaded yet");

        await settings.DownloadUpdateCommand.ExecuteAsync(null);
        settings.IsDownloaded.Should().BeTrue();

        app.Services.Context.ActiveWork = 1;
        settings.RestartToUpdateCommand.Execute(null);
        updates.Applied.Should().BeFalse("a backup or import is running");
        app.Services.Context.IsUnlocked.Should().BeTrue();

        app.Services.Context.ActiveWork = 0;
        settings.RestartToUpdateCommand.Execute(null);
        updates.Applied.Should().BeTrue();
        app.Services.Context.IsUnlocked.Should().BeFalse("the profile is locked before Mossmint exits");
    }

    [AvaloniaFact]
    public async Task Up_to_date_offline_and_running_from_source_are_reported()
    {
        var updates = new FakeUpdates();
        using var app = new Harness(updates);
        var settings = await OpenSettings(app);

        await settings.CheckForUpdatesCommand.ExecuteAsync(null);
        settings.UpdateStatus.Should().Be("Mossmint is up to date.");
        settings.DownloadUpdateCommand.CanExecute(null).Should().BeFalse();

        updates.Failure = new IOException("No route to host.");
        await settings.CheckForUpdatesCommand.ExecuteAsync(null);
        settings.UpdateStatus.Should().Be("Couldn't check for updates: No route to host.");

        updates.IsInstalled = false;
        settings.CheckForUpdatesCommand.NotifyCanExecuteChanged();
        settings.CheckForUpdatesCommand.CanExecute(null).Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task A_backup_counts_as_running_work_until_it_finishes()
    {
        using var app = new Harness();
        var settings = await OpenSettings(app);
        settings.IsInstalled.Should().BeFalse("the real Velopack service isn't installed under test");

        app.ViewModel.Shell.Navigate(Destination.Backups);
        var backups = (BackupsPageViewModel)app.ViewModel.Shell.CurrentPage!;
        var running = backups.KeepLocalCopyCommand.ExecuteAsync(null);
        app.Services.Context.ActiveWork.Should().Be(1);
        await running;

        app.Services.Context.ActiveWork.Should().Be(0);
    }

    // ADR-0003: off until the user turns it on, then GitHub is asked at most once a day.
    [AvaloniaFact]
    public async Task The_daily_check_is_off_by_default_and_asks_at_most_once_a_day()
    {
        var updates = new FakeUpdates { Latest = "0.4.0", Failure = new HttpRequestException("offline") };
        using var app = new Harness(updates);
        var settings = await OpenSettings(app);

        settings.CheckDaily.Should().BeFalse();
        await settings.CheckDailyAsync();
        updates.Checks.Should().Be(0, "nothing is asked while the daily check is off");

        settings.CheckDaily = true;
        await settings.CheckDailyAsync();
        updates.Failure = null;
        await settings.CheckDailyAsync();
        await settings.CheckDailyAsync();

        updates.Checks.Should().Be(2, "a failed check is tried again, a successful one waits a day");
        app.ViewModel.Shell.Settings.ShowsUpdateStrip.Should().BeTrue();
        new SettingsPageViewModel(app.Services, app.ViewModel).CheckDaily.Should().BeTrue("the choice outlives a restart");
    }

    [AvaloniaFact]
    public async Task Not_now_hides_the_strip_until_a_newer_version()
    {
        var updates = new FakeUpdates { Latest = "0.4.0" };
        using var app = new Harness(updates);
        var settings = await OpenSettings(app);
        settings.CheckDaily = true;
        await settings.CheckDailyAsync();

        settings.NotNowCommand.Execute(null);
        settings.ShowsUpdateStrip.Should().BeFalse();
        var afterRestart = new SettingsPageViewModel(app.Services, app.ViewModel);
        await afterRestart.CheckForUpdatesCommand.ExecuteAsync(null);
        afterRestart.ShowsUpdateStrip.Should().BeFalse("0.4.0 was put off");

        updates.Latest = "0.5.0";
        await settings.CheckForUpdatesCommand.ExecuteAsync(null);
        settings.ShowsUpdateStrip.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Download_on_the_strip_opens_settings_and_downloads()
    {
        var updates = new FakeUpdates { Latest = "0.4.0" };
        using var app = new Harness(updates);
        var settings = await OpenSettings(app);
        settings.CheckDaily = true;
        await settings.CheckDailyAsync();
        app.ViewModel.Shell.Navigate(Destination.Overview);

        await settings.DownloadFromStripCommand.ExecuteAsync(null);

        app.ViewModel.Shell.CurrentPage.Should().BeSameAs(settings);
        settings.IsDownloaded.Should().BeTrue();
        settings.ShowsUpdateStrip.Should().BeFalse("Settings now offers the restart");
    }

    private static async Task<SettingsPageViewModel> OpenSettings(Harness app)
    {
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        app.ViewModel.Shell.Navigate(Destination.Settings);
        return (SettingsPageViewModel)app.ViewModel.Shell.CurrentPage!;
    }
}
