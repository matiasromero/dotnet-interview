namespace TodoApi.Sync.External.Models;

public record CreateExternalTodoItemRequest(string SourceId, string Description, bool Completed);
