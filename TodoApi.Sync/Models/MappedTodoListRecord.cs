namespace TodoApi.Sync.Models;

/// <summary>
/// Projection of a mapped TodoList joined with its current local state, used by the pull
/// reconcile loop. Lets the sync project compare local-vs-external state without depending
/// on TodoApi.Models.
/// </summary>
public record MappedTodoListRecord(
    long MappingId,
    long LocalId,
    string ExternalId,
    Guid IdempotencyKey,
    DateTime LastSyncedAt,
    DateTime? LocalUpdatedAtAtSync,
    DateTime? ExternalUpdatedAtAtSync,
    string CurrentLocalName,
    DateTime CurrentLocalUpdatedAt
);
