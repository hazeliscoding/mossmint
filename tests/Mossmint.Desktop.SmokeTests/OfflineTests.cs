using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Mossmint.Application.Forecasting;
using Mossmint.Application.Import;
using Mossmint.Application.Setup;
using Mossmint.Desktop.ViewModels;

namespace Mossmint.Desktop.SmokeTests;

// Both tests live in one class so they never run in parallel: the monitor is
// process-wide and the positive control deliberately makes a connection attempt.
public class OfflineTests
{
    [AvaloniaFact]
    public async Task Creating_unlocking_backing_up_and_every_screen_make_no_network_activity()
    {
        using var monitor = new NetworkActivityMonitor();
        using var app = new Harness();
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);

        foreach (var destination in Enum.GetValues<Destination>())
        {
            app.ViewModel.OpenApp(destination);
            Harness.Pump();
        }

        app.Services.Backups.CreateLocal(app.Services.Context.Current);

        monitor.Events.Should().BeEmpty();
    }

    // M7's done-when: a connection left configured, and stale, never syncs on
    // its own, and every offline workflow still works around it.
    [AvaloniaFact]
    public async Task A_stale_connection_never_syncs_on_its_own_and_everything_else_still_works()
    {
        using var monitor = new NetworkActivityMonitor();
        using var app = new Harness(simpleFin: new Connectors.SimpleFin.SimpleFinProvider(TimeProvider.System));
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        var session = app.Services.Context.Current;
        var entry = new ManualEntry(session.Data, TimeProvider.System);
        var checking = entry.AddCashAccount("Checking", 1_000m);
        entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_000m, PayPattern.TwiceMonthly), checking.Id);
        entry.AddBill(new BillEntry("Rent", 900m, 1), checking.Id);
        session.Imports.SaveConnection(new Connection(Guid.CreateVersion7(), "simplefin", "https://user:pw@bridge.invalid/simplefin", "1700000000",
            new Dictionary<string, Guid> { ["ACT-1"] = checking.Id.Value }, DateTimeOffset.Now.AddDays(-40), DateTimeOffset.Now.AddDays(-20)));

        foreach (var destination in Enum.GetValues<Destination>())
        {
            app.ViewModel.OpenApp(destination);
            Harness.Pump();
        }

        entry.RecordEntry(checking.Id, "Groceries", -42.10m, DateOnly.FromDateTime(DateTime.Now));
        app.ViewModel.OpenApp(Destination.Overview);
        app.Services.Backups.CreateLocal(session);

        monitor.Events.Should().BeEmpty();
        Forecasts.Build(session.Data, DateOnly.FromDateTime(DateTime.Now)).Should().BeOfType<ProfileForecast>();
        app.ViewModel.Shell.DataMode.Should().Be("SimpleFIN · Manual sync");
    }

    [Fact]
    public void The_monitor_sees_a_connection_attempt()
    {
        using var monitor = new NetworkActivityMonitor();

        // Positive control: a loopback connect to a port nothing listens on.
        using var client = new TcpClient();
        try
        {
            client.Connect(new IPEndPoint(IPAddress.Loopback, 9));
        }
        catch (SocketException)
        {
            // Refused is expected; the attempt is what counts.
        }

        monitor.Events.Should().Contain(e => e.StartsWith("System.Net.Sockets/", StringComparison.Ordinal));
    }
}
