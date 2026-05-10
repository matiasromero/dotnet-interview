namespace TodoApi.FakeExternalApi.Models;

public sealed class ExternalTodoList
{
    public string Id { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<ExternalTodoItem> Items { get; set; } = new();
}
