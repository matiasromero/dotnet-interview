using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TodoApi.Dtos;
using TodoApi.Sync.Configuration;
using TodoApi.Sync.Models;

namespace TodoApi.Services;

public interface ISyncStatusService
{
    /// <summary>
    /// Builds a <see cref="SyncStatusResponse"/> from the current persisted state of the
    /// sync engine. One indexed query per (EntityType, Direction) pair, plus a count and
    /// a top-1 query against the outbox. Safe to call frequently.
    /// </summary>
    Task<SyncStatusResponse> GetStatusAsync(CancellationToken cancellationToken);
}

public sealed class SyncStatusService : ISyncStatusService
{
    // The four pairs the background service can produce a SyncRun for. We project per-pair
    // because grouped LINQ over EF Core does not always translate cleanly across providers
    // and the result set is bounded to four rows anyway — four indexed lookups is cheaper
    // than a generic groupby.
    private static readonly (SyncEntityType EntityType, SyncDirection Direction)[] Pairs =
    {
        (SyncEntityType.TodoList, SyncDirection.Push),
        (SyncEntityType.TodoList, SyncDirection.Pull),
        (SyncEntityType.TodoListItem, SyncDirection.Push),
        (SyncEntityType.TodoListItem, SyncDirection.Pull),
    };

    private readonly TodoContext _context;
    private readonly IOptions<SyncOptions> _options;

    public SyncStatusService(TodoContext context, IOptions<SyncOptions> options)
    {
        _context = context;
        _options = options;
    }

    public async Task<SyncStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        var lastRuns = new List<SyncRunSummary>(capacity: Pairs.Length);
        foreach (var (entityType, direction) in Pairs)
        {
            var run = await _context
                .SyncRuns.Where(r => r.EntityType == entityType && r.Direction == direction)
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (run is not null)
            {
                lastRuns.Add(
                    new SyncRunSummary(
                        run.EntityType,
                        run.Direction,
                        run.StartedAt,
                        run.FinishedAt,
                        run.Status,
                        run.ItemsProcessed,
                        run.ItemsFailed,
                        run.Error
                    )
                );
            }
        }

        var pendingCount = await _context.OutboxEvents.CountAsync(
            e => e.ProcessedAt == null,
            cancellationToken
        );

        var oldestPending = await _context
            .OutboxEvents.Where(e => e.ProcessedAt == null)
            .OrderBy(e => e.OccurredAt)
            .Select(e => (DateTime?)e.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);

        var opts = _options.Value;
        var config = new SyncConfigSnapshot(
            opts.Interval,
            opts.StartupDelay,
            opts.Enabled,
            opts.OutboxBatchSize,
            opts.OutboxRetention
        );

        return new SyncStatusResponse(lastRuns, pendingCount, oldestPending, config);
    }
}
