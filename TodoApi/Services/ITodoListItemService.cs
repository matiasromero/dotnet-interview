using TodoApi.Dtos;
using TodoApi.Models;

namespace TodoApi.Services;

public interface ITodoListItemService
{
    Task<IEnumerable<TodoListItem>?> GetAllAsync(long todoListId);
    Task<TodoListItem?> GetByIdAsync(long todoListId, long id);
    Task<TodoListItem?> CreateAsync(long todoListId, CreateTodoListItem dto);
    Task<bool> UpdateAsync(long todoListId, long id, UpdateTodoListItem dto);
    Task<bool> DeleteAsync(long todoListId, long id);
}
