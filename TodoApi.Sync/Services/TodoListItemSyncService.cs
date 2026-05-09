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

    public async Task<SyncRunResult> PullTodoListItemsAsync(
        IReadOnlyList<ExternalListWithMapping> mappedExternals,
        CancellationToken cancellationToken
    )
    {
        var run = new SyncRun
        {
            EntityType = SyncEntityType.TodoListItem,
            Direction = SyncDirection.Pull,
            StartedAt = DateTime.UtcNow,
            Status = SyncRunStatus.Running,
        };
        _db.SyncRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);

        var mappedItemsByExternalId = (
            await _db.GetMappedTodoListItemsAsync(cancellationToken)
        ).ToDictionary(m => m.ExternalItemId, m => m, StringComparer.Ordinal);

        // Flatten the mapped externals into per-item tuples so the reconcile loop never has to
        // re-derive the parent ids: the caller (TodoListSyncService) already knows which local
        // list each external belongs to.
        var flattened = mappedExternals
            .SelectMany(m =>
                m.External.Items.Select(i =>
                    (Item: i, ParentLocalId: m.ParentLocalId, ParentExternalId: m.ParentExternalId)
                )
            )
            .ToList();

        int processed = 0;
        int failed = 0;

        foreach (var (item, parentLocalId, parentExternalId) in flattened)
        {
            try
            {
                if (mappedItemsByExternalId.TryGetValue(item.Id, out var mapped))
                {
                    await ReconcileMappedItemAsync(item, mapped, cancellationToken);
                }
                else if (
                    long.TryParse(item.SourceId, out var localItemId)
                    && await _db.FindUnmappedLocalItemByIdAsync(
                        localItemId,
                        parentLocalId,
                        cancellationToken
                    )
                        is { } orphan
                )
                {
                    await AdoptOrphanItemAsync(orphan, item, parentExternalId, cancellationToken);
                }
                else
                {
                    await CreateLocalItemFromExternalAsync(
                        item,
                        parentLocalId,
                        parentExternalId,
                        cancellationToken
                    );
                }

                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Pull failed to reconcile external TodoListItem {ExternalItemId} (parent {ParentExternalId}, SourceId={SourceId})",
                    item.Id,
                    parentExternalId,
                    item.SourceId
                );
                failed++;
            }
        }

        run.FinishedAt = DateTime.UtcNow;
        run.ItemsProcessed = processed;
        run.ItemsFailed = failed;
        run.Status =
            failed == 0
                ? SyncRunStatus.Succeeded
                : (processed == 0 ? SyncRunStatus.Failed : SyncRunStatus.Partial);
        await _db.SaveChangesAsync(cancellationToken);

        return new SyncRunResult(flattened.Count, processed, failed, run.Status);
    }

    private async Task ReconcileMappedItemAsync(
        ExternalTodoItem external,
        MappedTodoListItemRecord mapped,
        CancellationToken ct
    )
    {
        var localChanged =
            mapped.CurrentLocalUpdatedAt > (mapped.LocalUpdatedAtAtSync ?? DateTime.MinValue);
        var externalChanged =
            external.UpdatedAt > (mapped.ExternalUpdatedAtAtSync ?? DateTime.MinValue);

        if (localChanged && externalChanged)
        {
            // Both sides changed since the last sync. Last-write-wins; tie goes to external.
            if (external.UpdatedAt >= mapped.CurrentLocalUpdatedAt)
            {
                await _db.ApplyRemoteWinsItemAsync(
                    new ApplyRemoteWinsItemPlan(
                        mapped.MappingId,
                        mapped.LocalId,
                        external.Description,
                        external.Completed,
                        external.UpdatedAt
                    ),
                    ct
                );
                _logger.LogInformation(
                    "Pull reconciled TodoListItem {LocalId}: remote wins (external={ExternalAt}, local={LocalAt})",
                    mapped.LocalId,
                    external.UpdatedAt,
                    mapped.CurrentLocalUpdatedAt
                );
            }
            else
            {
                await PatchExternalItemAsync(mapped, ct);
                _logger.LogInformation(
                    "Pull reconciled TodoListItem {LocalId}: local wins (local={LocalAt}, external={ExternalAt})",
                    mapped.LocalId,
                    mapped.CurrentLocalUpdatedAt,
                    external.UpdatedAt
                );
            }
        }
        else if (externalChanged)
        {
            await _db.ApplyRemoteWinsItemAsync(
                new ApplyRemoteWinsItemPlan(
                    mapped.MappingId,
                    mapped.LocalId,
                    external.Description,
                    external.Completed,
                    external.UpdatedAt
                ),
                ct
            );
            _logger.LogInformation(
                "Pull adopted external change for TodoListItem {LocalId}",
                mapped.LocalId
            );
        }
        else if (localChanged)
        {
            await PatchExternalItemAsync(mapped, ct);
            _logger.LogInformation(
                "Pull pushed local change for TodoListItem {LocalId}",
                mapped.LocalId
            );
        }
        else
        {
            await BumpItemLastSyncedAsync(mapped.MappingId, ct);
        }
    }

    private async Task PatchExternalItemAsync(MappedTodoListItemRecord mapped, CancellationToken ct)
    {
        var response = await _client.UpdateTodoItemAsync(
            mapped.ParentExternalId,
            mapped.ExternalItemId,
            new UpdateExternalTodoItemRequest(mapped.CurrentDescription, mapped.CurrentIsCompleted),
            ct
        );

        var trackedMapping =
            await _db.SyncMappings.FindAsync(new object?[] { mapped.MappingId }, ct)
            ?? throw new InvalidOperationException(
                $"SyncMapping {mapped.MappingId} disappeared mid-pull"
            );
        trackedMapping.LocalUpdatedAtAtSync = mapped.CurrentLocalUpdatedAt;
        trackedMapping.ExternalUpdatedAtAtSync = response.UpdatedAt;
        trackedMapping.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task BumpItemLastSyncedAsync(long mappingId, CancellationToken ct)
    {
        var trackedMapping = await _db.SyncMappings.FindAsync(new object?[] { mappingId }, ct);
        if (trackedMapping is null)
        {
            return;
        }
        trackedMapping.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task AdoptOrphanItemAsync(
        LocalTodoListItemRecord orphan,
        ExternalTodoItem external,
        string parentExternalId,
        CancellationToken ct
    )
    {
        _db.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = orphan.Id,
                ExternalId = external.Id,
                ParentExternalId = parentExternalId,
                IdempotencyKey = Guid.NewGuid(),
                LastSyncedAt = DateTime.UtcNow,
                LocalUpdatedAtAtSync = orphan.UpdatedAt,
                ExternalUpdatedAtAtSync = external.UpdatedAt,
            }
        );
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Pull adopted orphan TodoListItem {LocalId} as external {ExternalItemId}",
            orphan.Id,
            external.Id
        );
    }

    private async Task CreateLocalItemFromExternalAsync(
        ExternalTodoItem external,
        long parentLocalId,
        string parentExternalId,
        CancellationToken ct
    )
    {
        await _db.ApplyExternalItemCreateAsync(
            new ApplyExternalItemCreatePlan(
                parentLocalId,
                parentExternalId,
                external.Id,
                external.Description,
                external.Completed,
                external.UpdatedAt,
                Guid.NewGuid()
            ),
            ct
        );
        _logger.LogInformation(
            "Pull created local TodoListItem from external {ExternalItemId} (parent {ParentExternalId})",
            external.Id,
            parentExternalId
        );
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
