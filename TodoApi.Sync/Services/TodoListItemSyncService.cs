using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TodoApi.Sync.Configuration;
using TodoApi.Sync.Data;
using TodoApi.Sync.External;
using TodoApi.Sync.External.Models;
using TodoApi.Sync.Models;

namespace TodoApi.Sync.Services;

public class TodoListItemSyncService : ITodoListItemSyncService
{
    private readonly ISyncDbContext _db;
    private readonly IExternalTodoListClient _client;
    private readonly SyncOptions _syncOptions;
    private readonly ILogger<TodoListItemSyncService> _logger;

    public TodoListItemSyncService(
        ISyncDbContext db,
        IExternalTodoListClient client,
        IOptions<SyncOptions> syncOptions,
        ILogger<TodoListItemSyncService> logger
    )
    {
        _db = db;
        _client = client;
        _syncOptions = syncOptions.Value;
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

        var sw = Stopwatch.StartNew();
        int processed = 0;
        int failed = 0;
        int total = 0;

        // Phase A: drain outbox events for TodoListItem.
        var events = await _db.GetPendingOutboxEventsAsync(
            SyncEntityType.TodoListItem,
            _syncOptions.OutboxBatchSize,
            cancellationToken
        );
        foreach (var evt in events)
        {
            total++;
            try
            {
                await DispatchTodoListItemOutboxEventAsync(evt, cancellationToken);
                await _db.MarkOutboxEventProcessedAsync(evt.Id, cancellationToken);
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to dispatch outbox event {EventId} ({Operation} TodoListItem {LocalId})",
                    evt.Id,
                    evt.Operation,
                    evt.EntityId
                );
                failed++;
            }
        }

        // Phase B: legacy fallback. Items unmapped under mapped parents (slice 3 limitation),
        // mapped items with local changes (PATCH), and orphan mappings (DELETE). Coexists
        // with outbox during the transitional period.

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

        // 4) Close the SyncRun. Total = events + mapped + orphans (warnings are not counted).
        total += mapped.Count + orphans.Count;
        run.FinishedAt = DateTime.UtcNow;
        run.ItemsProcessed = processed;
        run.ItemsFailed = failed;
        run.Status =
            failed == 0
                ? SyncRunStatus.Succeeded
                : (processed == 0 ? SyncRunStatus.Failed : SyncRunStatus.Partial);
        await _db.SaveChangesAsync(cancellationToken);

        sw.Stop();
        _logger.LogInformation(
            "Sync item push completed in {ElapsedMs}ms: total={Total} processed={Processed} failed={Failed}",
            sw.ElapsedMilliseconds,
            total,
            processed,
            failed
        );

        return new SyncRunResult(total, processed, failed, run.Status);
    }

    private async Task DispatchTodoListItemOutboxEventAsync(
        OutboxEventRecord evt,
        CancellationToken ct
    )
    {
        switch (evt.Operation)
        {
            case OutboxOperation.Create:
                await DispatchOutboxItemCreateAsync(evt, ct);
                break;
            case OutboxOperation.Update:
                await DispatchOutboxItemUpdateAsync(evt, ct);
                break;
            case OutboxOperation.Delete:
                await DispatchOutboxItemDeleteAsync(evt, ct);
                break;
        }
    }

    private async Task DispatchOutboxItemCreateAsync(OutboxEventRecord evt, CancellationToken ct)
    {
        // Already mapped? embedded by parent's POST. Skip.
        var existing = await _db
            .SyncMappings.Where(m =>
                m.EntityType == SyncEntityType.TodoListItem && m.LocalId == evt.EntityId
            )
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            _logger.LogDebug(
                "TodoListItem {LocalId} already mapped; skipping outbox create event {EventId}",
                evt.EntityId,
                evt.Id
            );
            return;
        }

        // Slice 3 limitation: external contract does not expose a standalone POST /todoitems,
        // so a Create event for an item whose parent is already mapped cannot propagate. The
        // item will only sync if the parent re-creates it embedded, which the contract does
        // not currently permit. Log Warning + mark processed to drain the queue.
        _logger.LogWarning(
            "Outbox Create event {EventId} for TodoListItem {LocalId} cannot be dispatched: external API does not expose standalone POST /todoitems",
            evt.Id,
            evt.EntityId
        );
    }

    private async Task DispatchOutboxItemUpdateAsync(OutboxEventRecord evt, CancellationToken ct)
    {
        var mapping = await _db
            .SyncMappings.Where(m =>
                m.EntityType == SyncEntityType.TodoListItem && m.LocalId == evt.EntityId
            )
            .FirstOrDefaultAsync(ct);
        if (mapping is null)
        {
            _logger.LogWarning(
                "Outbox Update event {EventId} for TodoListItem {LocalId} skipped: item is not mapped (never synced)",
                evt.Id,
                evt.EntityId
            );
            return;
        }

        var local = await _db.GetLocalTodoListItemByIdAsync(evt.EntityId, ct);
        if (local is null)
        {
            _logger.LogInformation(
                "Outbox Update event {EventId}: local TodoListItem {LocalId} disappeared, skipping (delete event will handle)",
                evt.Id,
                evt.EntityId
            );
            return;
        }

        var response = await _client.UpdateTodoItemAsync(
            mapping.ParentExternalId!,
            mapping.ExternalId,
            new UpdateExternalTodoItemRequest(local.Description, local.IsCompleted),
            ct
        );

        var trackedMapping =
            await _db.SyncMappings.FindAsync(new object?[] { mapping.Id }, ct)
            ?? throw new InvalidOperationException(
                $"SyncMapping {mapping.Id} disappeared mid-push"
            );
        trackedMapping.LocalUpdatedAtAtSync = local.UpdatedAt;
        trackedMapping.ExternalUpdatedAtAtSync = response.UpdatedAt;
        trackedMapping.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Pushed TodoListItem {LocalId} update to external {ExternalItemId} via outbox event {EventId}",
            local.Id,
            mapping.ExternalId,
            evt.Id
        );
    }

    private async Task DispatchOutboxItemDeleteAsync(OutboxEventRecord evt, CancellationToken ct)
    {
        var mapping = await _db
            .SyncMappings.Where(m =>
                m.EntityType == SyncEntityType.TodoListItem && m.LocalId == evt.EntityId
            )
            .FirstOrDefaultAsync(ct);
        if (mapping is null)
        {
            _logger.LogInformation(
                "TodoListItem {LocalId} has no mapping; outbox delete event {EventId} marked processed without external call",
                evt.EntityId,
                evt.Id
            );
            return;
        }

        try
        {
            await _client.DeleteTodoItemAsync(mapping.ParentExternalId!, mapping.ExternalId, ct);
            await RemoveMappingAsync(mapping.Id, ct);
            _logger.LogInformation(
                "Deleted external TodoListItem {ExternalItemId} (parent {ParentExternalId}) via outbox event {EventId}",
                mapping.ExternalId,
                mapping.ParentExternalId,
                evt.Id
            );
        }
        catch (ExternalApiException ex) when (ex.StatusCode == 404)
        {
            _logger.LogInformation(
                "External TodoListItem {ExternalItemId} already deleted; cleaning mapping (outbox event {EventId})",
                mapping.ExternalId,
                evt.Id
            );
            await RemoveMappingAsync(mapping.Id, ct);
        }
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

        var sw = Stopwatch.StartNew();
        var mappedItems = await _db.GetMappedTodoListItemsAsync(cancellationToken);
        var mappedItemsByExternalId = mappedItems.ToDictionary(
            m => m.ExternalItemId,
            m => m,
            StringComparer.Ordinal
        );

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

        // 2nd pass: mapped items whose external counterpart has disappeared from a still-alive
        // parent list. Filter by ParentExternalId IN seenExternalListIds — items whose parent
        // is gone are already cleaned up by ApplyExternalDeleteListAsync (cascade) in the list
        // pull. Mirror policy: log Warning if local had unsynced edits, then delete.
        var seenExternalListIds = new HashSet<string>(
            mappedExternals.Select(m => m.ParentExternalId),
            StringComparer.Ordinal
        );
        var seenExternalItemIds = new HashSet<string>(
            flattened.Select(f => f.Item.Id),
            StringComparer.Ordinal
        );

        var deleted = 0;
        var deleteCandidates = mappedItems
            .Where(m =>
                seenExternalListIds.Contains(m.ParentExternalId)
                && !seenExternalItemIds.Contains(m.ExternalItemId)
            )
            .ToList();

        foreach (var mapped in deleteCandidates)
        {
            try
            {
                if (
                    mapped.LocalUpdatedAtAtSync.HasValue
                    && mapped.CurrentLocalUpdatedAt > mapped.LocalUpdatedAtAtSync.Value
                )
                {
                    _logger.LogWarning(
                        "Discarding unsynced local edits on TodoListItem {LocalId} (External {ExternalItemId}) before mirror delete; LocalUpdatedAt={LocalUpdatedAt} > LocalUpdatedAtAtSync={LocalUpdatedAtAtSync}",
                        mapped.LocalId,
                        mapped.ExternalItemId,
                        mapped.CurrentLocalUpdatedAt,
                        mapped.LocalUpdatedAtAtSync
                    );
                }
                await _db.ApplyExternalDeleteItemAsync(
                    new ApplyExternalDeleteItemPlan(mapped.LocalId, mapped.MappingId),
                    cancellationToken
                );
                _logger.LogInformation(
                    "Pull deleted local TodoListItem {LocalId} (external {ExternalItemId} disappeared from parent {ParentExternalId})",
                    mapped.LocalId,
                    mapped.ExternalItemId,
                    mapped.ParentExternalId
                );
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Pull failed to delete local TodoListItem {LocalId} (external {ExternalItemId} disappeared)",
                    mapped.LocalId,
                    mapped.ExternalItemId
                );
                failed++;
            }
        }

        var total = flattened.Count + deleteCandidates.Count;
        processed += deleted;

        run.FinishedAt = DateTime.UtcNow;
        run.ItemsProcessed = processed;
        run.ItemsFailed = failed;
        run.Status =
            failed == 0
                ? SyncRunStatus.Succeeded
                : (processed == 0 ? SyncRunStatus.Failed : SyncRunStatus.Partial);
        await _db.SaveChangesAsync(cancellationToken);

        sw.Stop();
        _logger.LogInformation(
            "Sync item pull completed in {ElapsedMs}ms: total={Total} processed={Processed} failed={Failed}",
            sw.ElapsedMilliseconds,
            total,
            processed,
            failed
        );

        return new SyncRunResult(total, processed, failed, run.Status);
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
