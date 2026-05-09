namespace TodoApi.Sync.Services;

using TodoApi.Sync.Models;

public interface ITodoListSyncService
{
    Task<SyncRunResult> PushTodoListsAsync(CancellationToken cancellationToken);

    Task<(
        SyncRunResult Result,
        IReadOnlyList<ExternalListWithMapping> MappedExternals
    )> PullTodoListsAsync(CancellationToken cancellationToken);
}
