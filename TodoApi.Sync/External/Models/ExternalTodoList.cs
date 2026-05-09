namespace TodoApi.Sync.External.Models;

public record ExternalTodoList(
    string Id,
    string SourceId,
    string Name,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<ExternalTodoItem> Items
);
