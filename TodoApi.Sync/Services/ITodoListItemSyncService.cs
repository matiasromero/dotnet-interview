namespace TodoApi.Sync.Services;

using TodoApi.Sync.Models;

public interface ITodoListItemSyncService
{
    Task<SyncRunResult> PushTodoListItemsAsync(CancellationToken cancellationToken);

    Task<SyncRunResult> PullTodoListItemsAsync(
        IReadOnlyList<ExternalListWithMapping> mappedExternals,
        CancellationToken cancellationToken
    );
}
