using Microsoft.Extensions.Logging;
using TodoApi.Sync.Data;
using TodoApi.Sync.External;
using TodoApi.Sync.External.Models;
using TodoApi.Sync.Models;

namespace TodoApi.Sync.Services;

public class TodoListItemSyncService : ITodoListItemSyncService
{
    private readonly ISyncDbContext _db;
    private readonly IExternalTodoListClient _client;
    private readonly ILogger<TodoListItemSyncService> _logger;

    public TodoListItemSyncService(
        ISyncDbContext db,
        IExternalTodoListClient client,
        ILogger<TodoListItemSyncService> logger
    )
    {
        _db = db;
        _client = client;
        _logger = logger;
    }

    public async Task<SyncRunResult> PushTodoListItemsAsync(CancellationToken cancellationToken)
    {
        var run = new SyncRun
        {
            EntityType = SyncEntityType.TodoListItem,
            Direction = SyncDirection.Push,
            StartedAt = DateTime.UtcNow,
            Status = SyncRunStatus.Running,
        };
        _db.SyncRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);

        // 1) Items created locally under an already-mapped parent: cannot be pushed because
        // the external API does not expose a standalone POST /todoitems. Warn-and-skip.
        // These do NOT count toward Total/Processed/Failed.
        var unmappedWithMappedParent = await _db.GetUnmappedTodoListItemsWithMappedParentAsync(
            cancellationToken
        );
        foreach (var item in unmappedWithMappedParent)
        {
            _logger.LogWarning(
                "TodoListItem {ItemId} created in already-synced list cannot be pushed: external API does not expose a standalone POST /todoitems",
                item.Id
            );
        }

        // 2) Items with an existing mapping: PATCH if local changed since the last sync.
        var mapped = await _db.GetMappedTodoListItemsAsync(cancellationToken);
        int processed = 0;
        int failed = 0;

        foreach (var m in mapped)
        {
            try
            {
                var localChanged =
                    m.CurrentLocalUpdatedAt > (m.LocalUpdatedAtAtSync ?? DateTime.MinValue);

                if (!localChanged)
                {
                    // No-op: item examined and decided no patch is needed.
                    processed++;
                    continue;
                }

                var response = await _client.UpdateTodoItemAsync(
                    m.ParentExternalId,
                    m.ExternalItemId,
                    new UpdateExternalTodoItemRequest(m.CurrentDescription, m.CurrentIsCompleted),
                    cancellationToken
                );

                var trackedMapping =
                    await _db.SyncMappings.FindAsync(
                        new object?[] { m.MappingId },
                        cancellationToken
                    )
                    ?? throw new InvalidOperationException(
                        $"SyncMapping {m.MappingId} disappeared mid-push"
                    );
                trackedMapping.LocalUpdatedAtAtSync = m.CurrentLocalUpdatedAt;
                trackedMapping.ExternalUpdatedAtAtSync = response.UpdatedAt;
                trackedMapping.LastSyncedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Pushed TodoListItem {LocalId} update to external {ExternalItemId}",
                    m.LocalId,
                    m.ExternalItemId
                );
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to push TodoListItem {LocalId} (External {ExternalItemId})",
                    m.LocalId,
                    m.ExternalItemId
                );
                failed++;
            }
        }

        // 3) Mappings whose local row was deleted: DELETE externally and clean the mapping.
        var orphans = await _db.GetOrphanedItemMappingsAsync(cancellationToken);
        foreach (var o in orphans)
        {
            try
            {
                await _client.DeleteTodoItemAsync(
                    o.ParentExternalId,
                    o.ExternalItemId,
                    cancellationToken
                );
                await RemoveMappingAsync(o.MappingId, cancellationToken);

                _logger.LogInformation(
                    "Deleted external TodoListItem {ExternalItemId} (parent {ParentExternalId}) and cleaned orphan mapping {MappingId}",
                    o.ExternalItemId,
                    o.ParentExternalId,
                    o.MappingId
                );
                processed++;
            }
            catch (ExternalApiException ex) when (ex.StatusCode == 404)
            {
                _logger.LogInformation(
                    "External TodoListItem {ExternalItemId} already deleted; cleaning up mapping {MappingId}",
                    o.ExternalItemId,
                    o.MappingId
                );
                await RemoveMappingAsync(o.MappingId, cancellationToken);
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to delete external TodoListItem {ExternalItemId} (mapping {MappingId})",
                    o.ExternalItemId,
                    o.MappingId
                );
                failed++;
            }
        }

        // 4) Close the SyncRun. Total = mapped + orphans (warnings are not counted).
        var total = mapped.Count + orphans.Count;
        run.FinishedAt = DateTime.UtcNow;
        run.ItemsProcessed = processed;
        run.ItemsFailed = failed;
        run.Status =
            failed == 0
                ? SyncRunStatus.Succeeded
                : (processed == 0 ? SyncRunStatus.Failed : SyncRunStatus.Partial);
        await _db.SaveChangesAsync(cancellationToken);

        return new SyncRunResult(total, processed, failed, run.Status);
    }

    public Task<SyncRunResult> PullTodoListItemsAsync(
        IReadOnlyList<ExternalListWithMapping> mappedExternals,
        CancellationToken cancellationToken
    )
    {
        throw new NotImplementedException("Pull will be implemented in Task 8");
    }

    private async Task RemoveMappingAsync(long mappingId, CancellationToken ct)
    {
        var trackedMapping = await _db.SyncMappings.FindAsync(new object?[] { mappingId }, ct);
        if (trackedMapping is null)
        {
            return;
        }
        _db.SyncMappings.Remove(trackedMapping);
        await _db.SaveChangesAsync(ct);
    }
}
