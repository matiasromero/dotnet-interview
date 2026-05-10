using Microsoft.AspNetCore.SignalR;

namespace TodoApi.Hubs;

/// <summary>
/// SignalR hub that broadcasts sync-relevant changes (sourced from <c>OutboxEvents</c>) to
/// connected frontend clients. Server-push only: no methods are invoked by clients on the
/// hub. The body intentionally stays empty — broadcasts come from
/// <c>OutboxBroadcastService</c> via <c>IHubContext&lt;TodoSyncHub, ITodoSyncClient&gt;</c>.
/// </summary>
public sealed class TodoSyncHub : Hub<ITodoSyncClient> { }
