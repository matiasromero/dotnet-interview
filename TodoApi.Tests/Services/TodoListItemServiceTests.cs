using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TodoApi.Dtos;
using TodoApi.Models;
using TodoApi.Services;

namespace TodoApi.Tests.Services;

#nullable disable
public class TodoListItemServiceTests
{
    private DbContextOptions<TodoContext> DatabaseContextOptions()
    {
        return new DbContextOptionsBuilder<TodoContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private void PopulateDatabaseContext(TodoContext context)
    {
        var list1 = new TodoList { Id = 1, Name = "List 1" };
        var list2 = new TodoList { Id = 2, Name = "List 2" };

        context.TodoList.Add(list1);
        context.TodoList.Add(list2);

        context.TodoListItem.Add(new TodoListItem
        {
            Id = 1,
            Description = "Item 1",
            IsCompleted = false,
            TodoListId = 1
        });

        context.TodoListItem.Add(new TodoListItem
        {
            Id = 2,
            Description = "Item 2",
            IsCompleted = true,
            TodoListId = 1
        });

        context.TodoListItem.Add(new TodoListItem
        {
            Id = 3,
            Description = "Item 3",
            IsCompleted = false,
            TodoListId = 2
        });

        context.SaveChanges();
    }

    [Fact]
    public async Task GetAllAsync_WhenCalled_ReturnsItemsByTodoListId()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);

            var result = await service.GetAllAsync(1);

            Assert.Equal(2, result.Count());
        }
    }

    [Fact]
    public async Task GetAllAsync_WhenTodoListDoesntExist_ReturnsNull()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);

            var result = await service.GetAllAsync(99);

            Assert.Null(result);
        }
    }

    [Fact]
    public async Task GetByIdAsync_WhenCalled_ReturnsItemById()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);

            var result = await service.GetByIdAsync(1, 1);

            Assert.NotNull(result);
            Assert.Equal(1, result.Id);
            Assert.Equal("Item 1", result.Description);
            Assert.False(result.IsCompleted);
        }
    }

    [Fact]
    public async Task GetByIdAsync_WhenIdDoesntExist_ReturnsNull()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);

            var result = await service.GetByIdAsync(1, 99);

            Assert.Null(result);
        }
    }

    [Fact]
    public async Task GetByIdAsync_WhenItemBelongsToDifferentList_ReturnsNull()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);

            // Item 3 belongs to TodoListId 2, not 1
            var result = await service.GetByIdAsync(1, 3);

            Assert.Null(result);
        }
    }

    [Fact]
    public async Task CreateAsync_WhenTodoListExists_CreatesItem()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var dto = new CreateTodoListItem { Description = "New Item" };

            var result = await service.CreateAsync(1, dto);

            Assert.NotNull(result);
            Assert.Equal("New Item", result.Description);
            Assert.False(result.IsCompleted);
            Assert.Equal(1, result.TodoListId);
            Assert.Equal(4, context.TodoListItem.Count());
        }
    }

    [Fact]
    public async Task CreateAsync_WhenTodoListDoesntExist_ReturnsNull()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var dto = new CreateTodoListItem { Description = "New Item" };

            var result = await service.CreateAsync(99, dto);

            Assert.Null(result);
            Assert.Equal(3, context.TodoListItem.Count());
        }
    }

    [Fact]
    public async Task UpdateAsync_WhenIdExists_UpdatesItem()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var dto = new UpdateTodoListItem
            {
                Description = "Updated Item 1",
                IsCompleted = true
            };

            var result = await service.UpdateAsync(1, 1, dto);

            Assert.True(result);
            var updated = await context.TodoListItem.FindAsync(1L);
            Assert.Equal("Updated Item 1", updated.Description);
            Assert.True(updated.IsCompleted);
        }
    }

    [Fact]
    public async Task UpdateAsync_WhenIdDoesntExist_ReturnsFalse()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var dto = new UpdateTodoListItem
            {
                Description = "Updated",
                IsCompleted = true
            };

            var result = await service.UpdateAsync(1, 99, dto);

            Assert.False(result);
        }
    }

    [Fact]
    public async Task UpdateAsync_WhenItemBelongsToDifferentList_ReturnsFalse()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var dto = new UpdateTodoListItem
            {
                Description = "Updated",
                IsCompleted = true
            };

            // Item 3 belongs to TodoListId 2, not 1
            var result = await service.UpdateAsync(1, 3, dto);

            Assert.False(result);
        }
    }

    [Fact]
    public async Task DeleteAsync_WhenIdExists_DeletesItem()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);

            var result = await service.DeleteAsync(1, 2);

            Assert.True(result);
            Assert.Equal(2, context.TodoListItem.Count());
        }
    }

    [Fact]
    public async Task DeleteAsync_WhenIdDoesntExist_ReturnsFalse()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);

            var result = await service.DeleteAsync(1, 99);

            Assert.False(result);
            Assert.Equal(3, context.TodoListItem.Count());
        }
    }

    [Fact]
    public async Task DeleteAsync_WhenItemBelongsToDifferentList_ReturnsFalse()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);

            // Item 3 belongs to TodoListId 2, not 1
            var result = await service.DeleteAsync(1, 3);

            Assert.False(result);
            Assert.Equal(3, context.TodoListItem.Count());
        }
    }
}
