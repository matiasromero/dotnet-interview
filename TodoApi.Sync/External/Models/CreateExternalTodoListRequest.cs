namespace TodoApi.Sync.External.Models;

public record CreateExternalTodoListRequest(
    string SourceId,
    string Name,
    IReadOnlyList<CreateExternalTodoItemRequest> Items
);
