using System.ComponentModel.DataAnnotations;

namespace TodoApi.Configuration;

/// <summary>
/// Configuration for the SignalR broadcast pipeline (<c>OutboxBroadcastService</c>). Bound
/// to the <c>Realtime</c> section in <c>appsettings.json</c>. Validated at startup via
/// <c>AddOptions&lt;RealtimeOptions&gt;().ValidateDataAnnotations()</c>.
/// </summary>
public sealed class RealtimeOptions
{
    /// <summary>
    /// How often the broadcast loop polls the <c>OutboxEvents</c> tail for new rows. Lower
    /// values reduce client-perceived latency; higher values reduce CPU and DB query
    /// frequency. The realistic range is <c>250 ms .. 60 s</c> — outside that, lower the
    /// sync interval first or rethink the design.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.250", "00:01:00")]
    public TimeSpan BroadcastInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum number of <c>OutboxEvents</c> to read per loop iteration. Bounds memory and
    /// SignalR throughput per tick. <c>200</c> matches the typical end-user write rate of a
    /// single-tenant interactive app with comfortable headroom.
    /// </summary>
    [Range(1, 1000)]
    public int BatchSize { get; set; } = 200;
}
