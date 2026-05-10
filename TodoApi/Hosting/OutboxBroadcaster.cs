using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TodoApi.Configuration;
using TodoApi.Hubs;
using TodoApi.Sync.Models;

namespace TodoApi.Hosting;

/// <summary>
/// Pure broadcast logic extracted from <see cref="OutboxBroadcastService"/> to keep timing
/// (the loop) decoupled from behaviour (read outbox + invoke hub). Both methods take an
/// explicit cursor and are safe to call from tests with arbitrary inputs.
/// </summary>
public interface IOutboxBroadcaster
{
    /// <summary>
    /// Returns the current MAX(<c>OutboxEvents.Id</c>), or <c>0</c> if the table is empty.
    /// Called once at startup so we don't replay events that pre-date the process. Existing
    /// historical events are forwarded by the existing sync engine, not by this broadcaster.
    /// </summary>
    Task<long> GetInitialCursorAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads up to <c>RealtimeOptions.BatchSize</c> outbox events with <c>Id &gt; cursor</c>,
    /// invokes the matching hub method (TodoList → TodoListChanged, TodoListItem →
    /// TodoListItemChanged) for each, and returns the new cursor (the largest Id seen, or
    /// the input <paramref name="cursor"/> if no events were found). <b>Does not modify
    /// <c>ProcessedAt</c></b> — that is the sync engine's responsibility.
    /// </summary>
    Task<long> BroadcastBatchAsync(long cursor, CancellationToken cancellationToken);
}

public sealed class OutboxBroadcaster : IOutboxBroadcaster
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<TodoSyncHub, ITodoSyncClient> _hub;
    private readonly IOptions<RealtimeOptions> _options;
    private readonly ILogger<OutboxBroadcaster> _logger;

    public OutboxBroadcaster(
        IServiceScopeFactory scopeFactory,
        IHubContext<TodoSyncHub, ITodoSyncClient> hub,
        IOptions<RealtimeOptions> options,
        ILogger<OutboxBroadcaster> logger
    )
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _options = options;
        _logger = logger;
    }

    public async Task<long> GetInitialCursorAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();
        return await ctx
            .OutboxEvents.OrderByDescending(e => e.Id)
            .Select(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<long> BroadcastBatchAsync(long cursor, CancellationToken cancellationToken)
    {
        var batchSize = _options.Value.BatchSize;
        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TodoContext>();

        var batch = await ctx
            .OutboxEvents.AsNoTracking()
            .Where(e => e.Id > cursor)
            .OrderBy(e => e.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        foreach (var evt in batch)
        {
            var notification = new ChangeNotification(
                evt.Id,
                evt.EntityType,
                evt.EntityId,
                evt.Operation,
                evt.OccurredAt
            );

            switch (evt.EntityType)
            {
                case SyncEntityType.TodoList:
                    await _hub.Clients.All.TodoListChanged(notification);
                    break;
                case SyncEntityType.TodoListItem:
                    await _hub.Clients.All.TodoListItemChanged(notification);
                    break;
                default:
                    _logger.LogWarning(
                        "OutboxBroadcaster: unknown EntityType={EntityType} for eventId={EventId}; skipping",
                        evt.EntityType,
                        evt.Id
                    );
                    break;
            }

            cursor = evt.Id;
            _logger.LogInformation(
                "OutboxBroadcaster: published eventId={EventId} entity={EntityType} op={Operation}",
                evt.Id,
                evt.EntityType,
                evt.Operation
            );
        }

        return cursor;
    }
}
