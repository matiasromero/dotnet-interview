using Microsoft.Extensions.Logging;
using TodoApi.Sync.Data;
using TodoApi.Sync.External;
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

        // Slice 1 happy path: no candidates → Succeeded with zero counts.
        run.FinishedAt = DateTime.UtcNow;
        run.Status = SyncRunStatus.Succeeded;
        run.ItemsProcessed = 0;
        run.ItemsFailed = 0;
        await _db.SaveChangesAsync(cancellationToken);

        return new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded);
    }
}
