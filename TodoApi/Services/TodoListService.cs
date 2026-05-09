using Microsoft.EntityFrameworkCore;
using TodoApi.Dtos;
using TodoApi.Models;

namespace TodoApi.Services;

public class TodoListService : ITodoListService
{
    private readonly TodoContext _context;
    private readonly ILogger<TodoListService> _logger;

    public TodoListService(TodoContext context, ILogger<TodoListService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<TodoList>> GetAllAsync()
    {
        return await _context.TodoList.ToListAsync();
    }

    public async Task<TodoList?> GetByIdAsync(long id)
    {
        return await _context.TodoList.FindAsync(id);
    }

    public async Task<TodoList> CreateAsync(CreateTodoList dto)
    {
        var todoList = new TodoList { Name = dto.Name };
        _context.TodoList.Add(todoList);
        await _context.SaveChangesAsync();
        _logger.LogInformation("Created TodoList {TodoListId} with name {Name}", todoList.Id, todoList.Name);
        return todoList;
    }

    public async Task<bool> UpdateAsync(long id, UpdateTodoList dto)
    {
        var todoList = await _context.TodoList.FindAsync(id);
        if (todoList == null)
        {
            _logger.LogWarning("UpdateAsync: TodoList {TodoListId} not found", id);
            return false;
        }

        todoList.Name = dto.Name;
        await _context.SaveChangesAsync();
        _logger.LogInformation("Updated TodoList {TodoListId}", id);
        return true;
    }

    public async Task<bool> DeleteAsync(long id)
    {
        var todoList = await _context.TodoList.FindAsync(id);
        if (todoList == null)
        {
            _logger.LogWarning("DeleteAsync: TodoList {TodoListId} not found", id);
            return false;
        }

        _context.TodoList.Remove(todoList);
        await _context.SaveChangesAsync();
        _logger.LogInformation("Deleted TodoList {TodoListId}", id);
        return true;
    }
}
