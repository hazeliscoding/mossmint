using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
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
