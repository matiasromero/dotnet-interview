namespace TodoApi.Models;

public class TodoListItem
{
    public long Id { get; set; }
    public required string Description { get; set; }
    public bool IsCompleted { get; set; } = false;
    public long TodoListId { get; set; }
    public TodoList TodoList { get; set; } = null!;
}
