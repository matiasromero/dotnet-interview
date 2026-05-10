namespace TodoApi.FakeExternalApi.Models;

public sealed class UpdateTodoItemRequest
{
    public string? Description { get; set; }
    public bool? Completed { get; set; }
}
