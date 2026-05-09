namespace TodoApi.Sync.Models;

/// <summary>
/// Lightweight projection of a local TodoList used by the sync push pipeline.
/// Avoids a direct dependency on TodoApi.Models from TodoApi.Sync.
/// </summary>
public record LocalTodoListRecord(
    long Id,
    string Name,
    DateTime UpdatedAt,
    IReadOnlyList<LocalTodoListItemRecord> Items
);
