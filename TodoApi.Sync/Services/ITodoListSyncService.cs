namespace TodoApi.Sync.Services;

public interface ITodoListSyncService
{
    Task<SyncRunResult> PushTodoListsAsync(CancellationToken cancellationToken);
}
