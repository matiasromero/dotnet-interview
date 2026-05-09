namespace TodoApi.Dtos;

public class UpdateTodoListItem
{
    public required string Description { get; set; }
    public bool IsCompleted { get; set; }
}
