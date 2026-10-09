using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mossmint.Desktop.ViewModels;

namespace Mossmint.Desktop.SmokeTests;

// Both tests live in one class so they never run in parallel: the monitor is
// process-wide and the positive control deliberately makes a connection attempt.
public class OfflineTests
{
    [AvaloniaFact]
    public void Opening_every_screen_makes_no_network_activity()
    {
        using var monitor = new NetworkActivityMonitor();

        var (_, vm) = ShellTests.OpenMainWindow();
        foreach (var destination in Enum.GetValues<Destination>())
        {
            vm.OpenApp(destination);
            Dispatcher.UIThread.RunJobs();
        }

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
