using Microsoft.Extensions.Logging;
using TodoApi.Sync.Data;
using TodoApi.Sync.External;
using TodoApi.Sync.External.Models;
using TodoApi.Sync.Models;

namespace TodoApi.Sync.Services;

public class TodoListSyncService : ITodoListSyncService
{
    private readonly ISyncDbContext _db;
    private readonly IExternalTodoListClient _client;
    private readonly ILogger<TodoListSyncService> _logger;

    public TodoListSyncService(
        ISyncDbContext db,
        IExternalTodoListClient client,
        ILogger<TodoListSyncService> logger
    )
    {
        _db = db;
        _client = client;
        _logger = logger;
    }

    public async Task<SyncRunResult> PushTodoListsAsync(CancellationToken cancellationToken)
    {
        var run = new SyncRun
        {
            EntityType = SyncEntityType.TodoList,
            Direction = SyncDirection.Push,
            StartedAt = DateTime.UtcNow,
            Status = SyncRunStatus.Running,
        };
        _db.SyncRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);

        var candidates = await _db.GetUnmappedTodoListsAsync(cancellationToken);
        var orphans = await _db.GetOrphanedListMappingsAsync(cancellationToken);

        int pushed = 0;
        int failed = 0;

        foreach (var local in candidates)
        {
            var idempotencyKey = Guid.NewGuid();
            try
            {
                var external = await _client.CreateTodoListAsync(
                    new CreateExternalTodoListRequest(
                        SourceId: local.Id.ToString(),
                        Name: local.Name,
                        Items: local
                            .Items.Select(i => new CreateExternalTodoItemRequest(
                                i.Id.ToString(),
                                i.Description,
                                i.IsCompleted
                            ))
                            .ToList()
                    ),
                    idempotencyKey,
                    cancellationToken
                );

                _db.SyncMappings.Add(
                    new SyncMapping
                    {
                        EntityType = SyncEntityType.TodoList,
                        LocalId = local.Id,
                        ExternalId = external.Id,
                        LastSyncedAt = DateTime.UtcNow,
                        IdempotencyKey = idempotencyKey,
                        LocalUpdatedAtAtSync = local.UpdatedAt,
                        ExternalUpdatedAtAtSync = external.UpdatedAt,
                    }
                );
                await _db.SaveChangesAsync(cancellationToken);

                if (external.Items.Count > 0)
                {
                    var embeddedMappings = new List<EmbeddedItemMapping>();
                    foreach (var ei in external.Items)
                    {
                        if (!long.TryParse(ei.SourceId, out var localItemId))
                        {
                            _logger.LogWarning(
                                "External item {ExtId} returned with non-parseable source_id; skipping mapping",
                                ei.Id
                            );
                            continue;
                        }
                        var localItem = local.Items.SingleOrDefault(li => li.Id == localItemId);
                        if (localItem is null)
                        {
                            _logger.LogWarning(
                                "External item {ExtId} source_id {SourceId} does not match any pushed local item",
                                ei.Id,
                                ei.SourceId
                            );
                            continue;
                        }
                        embeddedMappings.Add(
                            new EmbeddedItemMapping(
                                localItemId,
                                ei.Id,
                                localItem.UpdatedAt,
                                ei.UpdatedAt
                            )
                        );
                    }
                    if (embeddedMappings.Count > 0)
                    {
                        await _db.PersistEmbeddedItemMappingsAsync(
                            new PersistEmbeddedItemMappingsPlan(external.Id, embeddedMappings),
                            cancellationToken
                        );
                    }
                }

                _logger.LogInformation(
                    "Pushed TodoList {LocalId} to external as {ExternalId} with IdempotencyKey {IdempotencyKey}",
                    local.Id,
                    external.Id,
                    idempotencyKey
                );
                pushed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to push TodoList {LocalId} (IdempotencyKey {IdempotencyKey})",
                    local.Id,
                    idempotencyKey
                );
                failed++;
            }
        }

        // 2nd pass: mappings whose local TodoList has been hard-deleted. DELETE externally
        // (which cascades child items per the API contract) and clean up the mapping row.
        // Child item mappings of this list remain — they'll be cleaned by PushTodoListItemsAsync
        // via the existing 404-grace path (since their external counterparts are already gone).
        foreach (var orphan in orphans)
        {
            try
            {
                await _client.DeleteTodoListAsync(orphan.ExternalId, cancellationToken);
                await RemoveMappingAsync(orphan.MappingId, cancellationToken);
                _logger.LogInformation(
                    "Deleted external TodoList {ExternalId} and cleaned orphan mapping {MappingId}",
                    orphan.ExternalId,
                    orphan.MappingId
                );
                pushed++;
            }
            catch (ExternalApiException ex) when (ex.StatusCode == 404)
            {
                _logger.LogInformation(
                    "External TodoList {ExternalId} already deleted; cleaning up mapping {MappingId}",
                    orphan.ExternalId,
                    orphan.MappingId
                );
                await RemoveMappingAsync(orphan.MappingId, cancellationToken);
                pushed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to delete external TodoList {ExternalId} (mapping {MappingId})",
                    orphan.ExternalId,
                    orphan.MappingId
                );
                failed++;
            }
        }

        var total = candidates.Count + orphans.Count;
        run.FinishedAt = DateTime.UtcNow;
        run.ItemsProcessed = pushed;
        run.ItemsFailed = failed;
        run.Status =
            failed == 0
                ? SyncRunStatus.Succeeded
                : (pushed == 0 ? SyncRunStatus.Failed : SyncRunStatus.Partial);
        await _db.SaveChangesAsync(cancellationToken);

        return new SyncRunResult(total, pushed, failed, run.Status);
    }

    public async Task<(
        SyncRunResult Result,
        IReadOnlyList<ExternalListWithMapping> MappedExternals
    )> PullTodoListsAsync(CancellationToken cancellationToken)
    {
        var run = new SyncRun
        {
            EntityType = SyncEntityType.TodoList,
            Direction = SyncDirection.Pull,
            StartedAt = DateTime.UtcNow,
            Status = SyncRunStatus.Running,
        };
        _db.SyncRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);

        var mappedExternals = new List<ExternalListWithMapping>();

        IReadOnlyList<ExternalTodoList> externals;
        try
        {
            externals = await _client.GetTodoListsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pull failed to fetch external TodoLists");
            run.FinishedAt = DateTime.UtcNow;
            run.ItemsProcessed = 0;
            run.ItemsFailed = 0;
            run.Status = SyncRunStatus.Failed;
            await _db.SaveChangesAsync(cancellationToken);
            return (new SyncRunResult(0, 0, 0, SyncRunStatus.Failed), mappedExternals);
        }

        var mappedLists = await _db.GetMappedTodoListsAsync(cancellationToken);
        var mappedByExternalId = mappedLists.ToDictionary(
            m => m.ExternalId,
            m => m,
            StringComparer.Ordinal
        );

        int processed = 0;
        int failed = 0;

        foreach (var external in externals)
        {
            try
            {
                if (mappedByExternalId.TryGetValue(external.Id, out var mapped))
                {
                    await ReconcileMappedAsync(external, mapped, cancellationToken);
                    mappedExternals.Add(
                        new ExternalListWithMapping(external, mapped.LocalId, external.Id)
                    );
                }
                else if (
                    long.TryParse(external.SourceId, out var sourceLocalId)
                    && await _db.FindUnmappedLocalByIdAsync(sourceLocalId, cancellationToken)
                        is { } orphan
                )
                {
                    await AdoptOrphanAsync(orphan, external, cancellationToken);
                    mappedExternals.Add(
                        new ExternalListWithMapping(external, orphan.Id, external.Id)
                    );
                }
                else
                {
                    await CreateLocalFromExternalAsync(external, cancellationToken);
                }

                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Pull failed to reconcile external TodoList {ExternalId} (SourceId={SourceId})",
                    external.Id,
                    external.SourceId
                );
                failed++;
            }
        }

        // 2nd pass: detect mapped local lists whose external counterpart has disappeared from
        // the GET response. Cascade local delete (list + child items + child item mappings)
        // per Mirror policy. If the local row had unsynced edits since the last snapshot, log
        // a Warning to surface that data was discarded.
        var seenExternalIds = new HashSet<string>(
            externals.Select(e => e.Id),
            StringComparer.Ordinal
        );
        var deleted = 0;
        foreach (var mapped in mappedLists)
        {
            if (seenExternalIds.Contains(mapped.ExternalId))
            {
                continue;
            }
            try
            {
                if (
                    mapped.LocalUpdatedAtAtSync.HasValue
                    && mapped.CurrentLocalUpdatedAt > mapped.LocalUpdatedAtAtSync.Value
                )
                {
                    _logger.LogWarning(
                        "Discarding unsynced local edits on TodoList {LocalId} (External {ExternalId}) before mirror delete; LocalUpdatedAt={LocalUpdatedAt} > LocalUpdatedAtAtSync={LocalUpdatedAtAtSync}",
                        mapped.LocalId,
                        mapped.ExternalId,
                        mapped.CurrentLocalUpdatedAt,
                        mapped.LocalUpdatedAtAtSync
                    );
                }
                await _db.ApplyExternalDeleteListAsync(
                    new ApplyExternalDeleteListPlan(mapped.LocalId, mapped.MappingId),
                    cancellationToken
                );
                _logger.LogInformation(
                    "Pull deleted local TodoList {LocalId} (external {ExternalId} disappeared)",
                    mapped.LocalId,
                    mapped.ExternalId
                );
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Pull failed to delete local TodoList {LocalId} (external {ExternalId} disappeared)",
                    mapped.LocalId,
                    mapped.ExternalId
                );
                failed++;
            }
        }

        var deleteCandidates = mappedLists.Count(m => !seenExternalIds.Contains(m.ExternalId));
        var total = externals.Count + deleteCandidates;

        processed += deleted;

        run.FinishedAt = DateTime.UtcNow;
        run.ItemsProcessed = processed;
        run.ItemsFailed = failed;
        run.Status =
            failed == 0
                ? SyncRunStatus.Succeeded
                : (processed == 0 ? SyncRunStatus.Failed : SyncRunStatus.Partial);
        await _db.SaveChangesAsync(cancellationToken);

        return (new SyncRunResult(total, processed, failed, run.Status), mappedExternals);
    }

    private async Task ReconcileMappedAsync(
        ExternalTodoList external,
        MappedTodoListRecord mapped,
        CancellationToken ct
    )
    {
        var externalChanged =
            external.UpdatedAt > (mapped.ExternalUpdatedAtAtSync ?? DateTime.MinValue);
        var localChanged =
            mapped.CurrentLocalUpdatedAt > (mapped.LocalUpdatedAtAtSync ?? DateTime.MinValue);

        if (externalChanged && localChanged)
        {
            // Both sides changed since the last sync. Last-write-wins; tie goes to external.
            if (external.UpdatedAt >= mapped.CurrentLocalUpdatedAt)
            {
                await _db.ApplyRemoteWinsAsync(
                    new ApplyRemoteWinsPlan(
                        mapped.MappingId,
                        mapped.LocalId,
                        external.Name,
                        external.UpdatedAt
                    ),
                    ct
                );
                _logger.LogInformation(
                    "Pull reconciled TodoList {LocalId}: remote wins (external={ExternalAt}, local={LocalAt})",
                    mapped.LocalId,
                    external.UpdatedAt,
                    mapped.CurrentLocalUpdatedAt
                );
            }
            else
            {
                await PatchExternalAsync(mapped, ct);
                _logger.LogInformation(
                    "Pull reconciled TodoList {LocalId}: local wins (local={LocalAt}, external={ExternalAt})",
                    mapped.LocalId,
                    mapped.CurrentLocalUpdatedAt,
                    external.UpdatedAt
                );
            }
        }
        else if (externalChanged)
        {
            await _db.ApplyRemoteWinsAsync(
                new ApplyRemoteWinsPlan(
                    mapped.MappingId,
                    mapped.LocalId,
                    external.Name,
                    external.UpdatedAt
                ),
                ct
            );
            _logger.LogInformation(
                "Pull adopted external change for TodoList {LocalId}",
                mapped.LocalId
            );
        }
        else if (localChanged)
        {
            await PatchExternalAsync(mapped, ct);
            _logger.LogInformation(
                "Pull pushed local change for TodoList {LocalId}",
                mapped.LocalId
            );
        }
        else
        {
            await BumpLastSyncedAsync(mapped.MappingId, ct);
        }
    }

    private async Task PatchExternalAsync(MappedTodoListRecord mapped, CancellationToken ct)
    {
        var response = await _client.UpdateTodoListAsync(
            mapped.ExternalId,
            new UpdateExternalTodoListRequest(mapped.CurrentLocalName),
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

    private async Task BumpLastSyncedAsync(long mappingId, CancellationToken ct)
    {
        var trackedMapping = await _db.SyncMappings.FindAsync(new object?[] { mappingId }, ct);
        if (trackedMapping is null)
        {
            return;
        }
        trackedMapping.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task AdoptOrphanAsync(
        LocalTodoListRecord orphan,
        ExternalTodoList external,
        CancellationToken ct
    )
    {
        _db.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = orphan.Id,
                ExternalId = external.Id,
                IdempotencyKey = Guid.NewGuid(),
                LastSyncedAt = DateTime.UtcNow,
                LocalUpdatedAtAtSync = orphan.UpdatedAt,
                ExternalUpdatedAtAtSync = external.UpdatedAt,
            }
        );
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Pull adopted orphan TodoList {LocalId} as external {ExternalId}",
            orphan.Id,
            external.Id
        );
    }

    private async Task CreateLocalFromExternalAsync(ExternalTodoList external, CancellationToken ct)
    {
        var embeddedItems = external
            .Items.Select(ei => new EmbeddedExternalItem(
                ei.Id,
                ei.Description,
                ei.Completed,
                ei.UpdatedAt
            ))
            .ToList();

        await _db.ApplyExternalCreateAsync(
            new ApplyExternalCreatePlan(
                external.Id,
                external.Name,
                external.UpdatedAt,
                Guid.NewGuid(),
                embeddedItems
            ),
            ct
        );
        _logger.LogInformation(
            "Pull created local TodoList from external {ExternalId} (Name={Name}, Items={ItemCount})",
            external.Id,
            external.Name,
            embeddedItems.Count
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
