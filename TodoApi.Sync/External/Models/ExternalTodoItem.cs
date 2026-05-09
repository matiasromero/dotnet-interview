namespace TodoApi.Sync.External.Models;

public record ExternalTodoItem(
    string Id,
    string SourceId,
    string Description,
    bool Completed,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
