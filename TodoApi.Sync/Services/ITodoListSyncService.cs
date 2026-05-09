namespace TodoApi.Sync.Services;

public interface ITodoListSyncService
{
    Task<SyncRunResult> PushTodoListsAsync(CancellationToken cancellationToken);

    Task<SyncRunResult> PullTodoListsAsync(CancellationToken cancellationToken);
}
