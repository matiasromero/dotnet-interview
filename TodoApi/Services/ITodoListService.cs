using TodoApi.Dtos;
using TodoApi.Models;

namespace TodoApi.Services;

public interface ITodoListService
{
    Task<IEnumerable<TodoList>> GetAllAsync();
    Task<TodoList?> GetByIdAsync(long id);
    Task<TodoList> CreateAsync(CreateTodoList dto);
    Task<bool> UpdateAsync(long id, UpdateTodoList dto);
    Task<bool> DeleteAsync(long id);
}
