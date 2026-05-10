namespace TodoApi.FakeExternalApi.Models;

public sealed class CreateTodoListRequest
{
    public string? SourceId { get; set; }
    public string Name { get; set; } = "";
    public List<CreateTodoItemRequest>? Items { get; set; }
}
