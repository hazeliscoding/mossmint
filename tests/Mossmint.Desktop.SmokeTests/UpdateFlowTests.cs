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

    public Task<string?> CheckAsync() => Failure is null ? Task.FromResult(Latest) : Task.FromException<string?>(Failure);

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
