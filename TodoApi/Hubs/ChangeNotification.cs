using TodoApi.Sync.Models;

namespace TodoApi.Hubs;

/// <summary>
/// Lightweight notification broadcast over SignalR when a sync-relevant entity changes.
/// Sourced from <see cref="OutboxEvent"/> rows by <c>OutboxBroadcastService</c>; consumers
/// react by re-fetching the affected REST endpoint (no payload duplication, no shape
/// coupling to the entity contract).
///
/// <para>
/// <b>Versioning:</b> this record is the public contract with frontend clients. Adding
/// fields is forward-compatible (extra properties are ignored by deserializers); removing
/// or renaming requires coordination with consumers.
/// </para>
/// </summary>
/// <param name="EventId">
/// Underlying <c>OutboxEvent.Id</c>. Stable, monotonically increasing per process. Clients
/// use it for client-side dedupe across reconnects.
/// </param>
/// <param name="EntityType">
/// One of <see cref="SyncEntityType.TodoList"/> or <see cref="SyncEntityType.TodoListItem"/>.
/// Frontend dispatches to the corresponding refetch.
/// </param>
/// <param name="EntityId">Local primary key of the affected row.</param>
/// <param name="Operation">Create / Update / Delete — drives the merge strategy on the client.</param>
/// <param name="OccurredAt">When the change was recorded locally (UTC).</param>
public sealed record ChangeNotification(
    long EventId,
    SyncEntityType EntityType,
    long EntityId,
    OutboxOperation Operation,
    DateTime OccurredAt
);
