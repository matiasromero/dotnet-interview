using Microsoft.EntityFrameworkCore;
using TodoApi.Dtos;
using TodoApi.Models;
using TodoApi.Sync.Models;

namespace TodoApi.Services;

public class TodoListItemService : ITodoListItemService
{
    private readonly TodoContext _context;
    private readonly ILogger<TodoListItemService> _logger;

    public TodoListItemService(TodoContext context, ILogger<TodoListItemService> logger)
    {
        _context = context;
        _logger = logger;
    }

    private static OutboxEvent BuildOutboxEvent(long entityId, OutboxOperation operation) =>
        new()
        {
            EntityType = SyncEntityType.TodoListItem,
            EntityId = entityId,
            Operation = operation,
            OccurredAt = DateTime.UtcNow,
            IdempotencyKey = Guid.NewGuid(),
        };

    public async Task<IEnumerable<TodoListItem>?> GetAllAsync(long todoListId)
    {
        var todoListExists = await _context.TodoList.AnyAsync(l => l.Id == todoListId);
        if (!todoListExists)
        {
            _logger.LogWarning("GetAllAsync: parent TodoList {TodoListId} not found", todoListId);
            return null;
        }

        return await _context.TodoListItem.Where(i => i.TodoListId == todoListId).ToListAsync();
    }

    public async Task<TodoListItem?> GetByIdAsync(long todoListId, long id)
    {
        var todoListExists = await _context.TodoList.AnyAsync(l => l.Id == todoListId);
        if (!todoListExists)
        {
            _logger.LogWarning("GetByIdAsync: parent TodoList {TodoListId} not found", todoListId);
            return null;
        }

        return await _context.TodoListItem.FirstOrDefaultAsync(i =>
            i.Id == id && i.TodoListId == todoListId
        );
    }

    public async Task<TodoListItem?> CreateAsync(long todoListId, CreateTodoListItem dto)
    {
        var todoListExists = await _context.TodoList.AnyAsync(l => l.Id == todoListId);
        if (!todoListExists)
        {
            _logger.LogWarning("CreateAsync: parent TodoList {TodoListId} not found", todoListId);
            return null;
        }

        var item = new TodoListItem
        {
            Description = dto.Description,
            TodoListId = todoListId,
            IsCompleted = false,
            UpdatedAt = DateTime.UtcNow,
        };

        _context.TodoListItem.Add(item);
        await _context.SaveChangesAsync();

        _context.OutboxEvents.Add(BuildOutboxEvent(item.Id, OutboxOperation.Create));
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Created TodoListItem {ItemId} in TodoList {TodoListId}",
            item.Id,
            todoListId
        );
        return item;
    }

    public async Task<bool> UpdateAsync(long todoListId, long id, UpdateTodoListItem dto)
    {
        var todoListExists = await _context.TodoList.AnyAsync(l => l.Id == todoListId);
        if (!todoListExists)
        {
            _logger.LogWarning("UpdateAsync: parent TodoList {TodoListId} not found", todoListId);
            return false;
        }

        var item = await _context.TodoListItem.FirstOrDefaultAsync(i =>
            i.Id == id && i.TodoListId == todoListId
        );

        if (item == null)
        {
            _logger.LogWarning(
                "UpdateAsync: TodoListItem {ItemId} not found in TodoList {TodoListId}",
                id,
                todoListId
            );
            return false;
        }

        item.Description = dto.Description;
        item.IsCompleted = dto.IsCompleted;
        item.UpdatedAt = DateTime.UtcNow;
        _context.OutboxEvents.Add(BuildOutboxEvent(id, OutboxOperation.Update));
        await _context.SaveChangesAsync();
        _logger.LogInformation(
            "Updated TodoListItem {ItemId} in TodoList {TodoListId}",
            id,
            todoListId
        );
        return true;
    }

    public async Task<bool> DeleteAsync(long todoListId, long id)
    {
        var todoListExists = await _context.TodoList.AnyAsync(l => l.Id == todoListId);
        if (!todoListExists)
        {
            _logger.LogWarning("DeleteAsync: parent TodoList {TodoListId} not found", todoListId);
            return false;
        }

        var item = await _context.TodoListItem.FirstOrDefaultAsync(i =>
            i.Id == id && i.TodoListId == todoListId
        );

        if (item == null)
        {
            _logger.LogWarning(
                "DeleteAsync: TodoListItem {ItemId} not found in TodoList {TodoListId}",
                id,
                todoListId
            );
            return false;
        }

        _context.TodoListItem.Remove(item);
        _context.OutboxEvents.Add(BuildOutboxEvent(id, OutboxOperation.Delete));
        await _context.SaveChangesAsync();
        _logger.LogInformation(
            "Deleted TodoListItem {ItemId} in TodoList {TodoListId}",
            id,
            todoListId
        );
        return true;
    }
}
