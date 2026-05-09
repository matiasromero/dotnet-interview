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
                        Items: Array.Empty<CreateExternalTodoItemRequest>()
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

        run.FinishedAt = DateTime.UtcNow;
        run.ItemsProcessed = pushed;
        run.ItemsFailed = failed;
        run.Status =
            failed == 0
                ? SyncRunStatus.Succeeded
                : (pushed == 0 ? SyncRunStatus.Failed : SyncRunStatus.Partial);
        await _db.SaveChangesAsync(cancellationToken);

        return new SyncRunResult(candidates.Count, pushed, failed, run.Status);
    }

    public async Task<SyncRunResult> PullTodoListsAsync(CancellationToken cancellationToken)
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
            return new SyncRunResult(0, 0, 0, SyncRunStatus.Failed);
        }

        var mappedByExternalId = (
            await _db.GetMappedTodoListsAsync(cancellationToken)
        ).ToDictionary(m => m.ExternalId, m => m, StringComparer.Ordinal);

        int processed = 0;
        int failed = 0;

        foreach (var external in externals)
        {
            try
            {
                if (mappedByExternalId.TryGetValue(external.Id, out var mapped))
                {
                    await ReconcileMappedAsync(external, mapped, cancellationToken);
                }
                else if (
                    long.TryParse(external.SourceId, out var sourceLocalId)
                    && await _db.FindUnmappedLocalByIdAsync(sourceLocalId, cancellationToken)
                        is { } orphan
                )
                {
                    await AdoptOrphanAsync(orphan, external, cancellationToken);
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

        run.FinishedAt = DateTime.UtcNow;
        run.ItemsProcessed = processed;
        run.ItemsFailed = failed;
        run.Status =
            failed == 0
                ? SyncRunStatus.Succeeded
                : (processed == 0 ? SyncRunStatus.Failed : SyncRunStatus.Partial);
        await _db.SaveChangesAsync(cancellationToken);

        return new SyncRunResult(externals.Count, processed, failed, run.Status);
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
        await _db.ApplyExternalCreateAsync(
            new ApplyExternalCreatePlan(
                external.Id,
                external.Name,
                external.UpdatedAt,
                Guid.NewGuid(),
                Array.Empty<EmbeddedExternalItem>()
            ),
            ct
        );
        _logger.LogInformation(
            "Pull created local TodoList from external {ExternalId} (Name={Name})",
            external.Id,
            external.Name
        );
    }
}
