using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TodoApi.Dtos;
using TodoApi.Models;
using TodoApi.Services;

namespace TodoApi.Tests.Services;

#nullable disable
public class TodoListServiceTests
{
    private DbContextOptions<TodoContext> DatabaseContextOptions()
    {
        return new DbContextOptionsBuilder<TodoContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private void PopulateDatabaseContext(TodoContext context)
    {
        context.TodoList.Add(new TodoList { Id = 1, Name = "Task 1" });
        context.TodoList.Add(new TodoList { Id = 2, Name = "Task 2" });
        context.SaveChanges();
    }

    [Fact]
    public async Task GetAllAsync_WhenCalled_ReturnsTodoLists()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListService(context, NullLogger<TodoListService>.Instance);

            var result = await service.GetAllAsync();

            Assert.Equal(2, result.Count());
        }
    }

    [Fact]
    public async Task GetByIdAsync_WhenCalled_ReturnsTodoListById()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListService(context, NullLogger<TodoListService>.Instance);

            var result = await service.GetByIdAsync(1);

            Assert.NotNull(result);
            Assert.Equal(1, result.Id);
            Assert.Equal("Task 1", result.Name);
        }
    }

    [Fact]
    public async Task GetByIdAsync_WhenIdDoesntExist_ReturnsNull()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListService(context, NullLogger<TodoListService>.Instance);

            var result = await service.GetByIdAsync(99);

            Assert.Null(result);
        }
    }

    [Fact]
    public async Task CreateAsync_WhenCalled_CreatesTodoList()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListService(context, NullLogger<TodoListService>.Instance);
            var dto = new CreateTodoList { Name = "Task 3" };

            var result = await service.CreateAsync(dto);

            Assert.NotNull(result);
            Assert.Equal("Task 3", result.Name);
            Assert.Equal(3, context.TodoList.Count());
        }
    }

    [Fact]
    public async Task UpdateAsync_WhenIdExists_UpdatesTodoList()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListService(context, NullLogger<TodoListService>.Instance);
            var dto = new UpdateTodoList { Name = "Updated Task 1" };

            var result = await service.UpdateAsync(1, dto);

            Assert.True(result);
            var updated = await context.TodoList.FindAsync(1L);
            Assert.Equal("Updated Task 1", updated.Name);
        }
    }

    [Fact]
    public async Task UpdateAsync_WhenIdDoesntExist_ReturnsFalse()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListService(context, NullLogger<TodoListService>.Instance);
            var dto = new UpdateTodoList { Name = "Updated Task" };

            var result = await service.UpdateAsync(99, dto);

            Assert.False(result);
        }
    }

    [Fact]
    public async Task DeleteAsync_WhenIdExists_DeletesTodoList()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListService(context, NullLogger<TodoListService>.Instance);

            var result = await service.DeleteAsync(2);

            Assert.True(result);
            Assert.Equal(1, context.TodoList.Count());
        }
    }

    [Fact]
    public async Task DeleteAsync_WhenIdDoesntExist_ReturnsFalse()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListService(context, NullLogger<TodoListService>.Instance);

            var result = await service.DeleteAsync(99);

            Assert.False(result);
            Assert.Equal(2, context.TodoList.Count());
        }
    }

    [Fact]
    public async Task GetAllAsync_WhenDatabaseEmpty_ReturnsEmptyList()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            var service = new TodoListService(context, NullLogger<TodoListService>.Instance);

            var result = await service.GetAllAsync();

            Assert.Empty(result);
        }
    }
}
