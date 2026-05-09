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
            try
            {
                var external = await _client.CreateTodoListAsync(
                    new CreateExternalTodoListRequest(
                        SourceId: local.Id.ToString(),
                        Name: local.Name,
                        Items: Array.Empty<CreateExternalTodoItemRequest>()
                    ),
                    cancellationToken
                );

                _db.SyncMappings.Add(
                    new SyncMapping
                    {
                        EntityType = SyncEntityType.TodoList,
                        LocalId = local.Id,
                        ExternalId = external.Id,
                        LastSyncedAt = DateTime.UtcNow,
                    }
                );
                await _db.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Pushed TodoList {LocalId} to external as {ExternalId}",
                    local.Id,
                    external.Id
                );
                pushed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push TodoList {LocalId}", local.Id);
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
}
