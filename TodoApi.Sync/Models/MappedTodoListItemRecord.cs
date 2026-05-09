namespace TodoApi.Sync.Models;

/// <summary>
/// Projection of a mapped TodoListItem joined with its current local state, used by the pull
/// reconcile loop. Lets the sync project compare local-vs-external state without depending
/// on TodoApi.Models.
/// </summary>
public record MappedTodoListItemRecord(
    long MappingId,
    long LocalId,
    string ExternalItemId,
    string ParentExternalId,
    Guid IdempotencyKey,
    DateTime LastSyncedAt,
    DateTime? LocalUpdatedAtAtSync,
    DateTime? ExternalUpdatedAtAtSync,
    string CurrentDescription,
    bool CurrentIsCompleted,
    DateTime CurrentLocalUpdatedAt
);
