namespace TodoApi.Sync.Models;

/// <summary>
/// Lightweight projection of a local TodoListItem used by the sync push pipeline.
/// Avoids a direct dependency on TodoApi.Models from TodoApi.Sync.
/// </summary>
public record LocalTodoListItemRecord(
    long Id,
    string Description,
    bool IsCompleted,
    DateTime UpdatedAt
);
