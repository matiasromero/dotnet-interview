namespace TodoApi.FakeExternalApi.Models;

public sealed class CreateTodoItemRequest
{
    public string? SourceId { get; set; }
    public string Description { get; set; } = "";
    public bool Completed { get; set; }
}
