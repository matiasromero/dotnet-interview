namespace TodoApi.Models;

public class TodoList
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ICollection<TodoListItem> Items { get; set; } = new List<TodoListItem>();
}
