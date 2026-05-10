namespace TodoApi.FakeExternalApi.Models;

public sealed class ExternalTodoItem
{
    public string Id { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Completed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
