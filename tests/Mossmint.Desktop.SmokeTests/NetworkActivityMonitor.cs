using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace Mossmint.Desktop.SmokeTests;

// Records any socket, DNS or HTTP activity in this process. The runtime's own
// networking event sources fire for every outbound connection attempt, so an
// empty log means nothing tried to reach the network.
public sealed class NetworkActivityMonitor : EventListener
{
    private static readonly string[] Sources =
        ["System.Net.Sockets", "System.Net.Http", "System.Net.NameResolution", "System.Net.Security"];

    private readonly ConcurrentQueue<string> _events = new();

    public IReadOnlyCollection<string> Events => _events;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (Sources.Contains(eventSource.Name))
        {
            EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        // Counter payloads are periodic metrics, not activity.
        if (eventData.EventName is not null && eventData.EventName != "EventCounters")
        {
            _events.Enqueue($"{eventData.EventSource.Name}/{eventData.EventName}");
        }
    }
}
