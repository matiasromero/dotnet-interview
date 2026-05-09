using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TodoApi.Controllers;
using TodoApi.Dtos;
using TodoApi.Models;
using TodoApi.Services;

namespace TodoApi.Tests;

#nullable disable
public class TodoListItemsControllerTests
{
    private DbContextOptions<TodoContext> DatabaseContextOptions()
    {
        return new DbContextOptionsBuilder<TodoContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private void PopulateDatabaseContext(TodoContext context)
    {
        context.TodoList.Add(new TodoList { Id = 1, Name = "List 1" });
        context.TodoList.Add(new TodoList { Id = 2, Name = "List 2" });

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
    public async Task GetTodoListItems_WhenCalled_ReturnsItemsForThatList()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.GetTodoListItems(1);

            Assert.IsType<OkObjectResult>(result.Result);
            var items = (result.Result as OkObjectResult).Value as IEnumerable<TodoListItem>;
            Assert.Equal(2, items.Count());
            Assert.All(items, i => Assert.Equal(1, i.TodoListId));
        }
    }

    [Fact]
    public async Task GetTodoListItems_WhenListHasNoItems_ReturnsEmptyList()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            context.TodoList.Add(new TodoList { Id = 5, Name = "Empty List" });
            context.SaveChanges();

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.GetTodoListItems(5);

            Assert.IsType<OkObjectResult>(result.Result);
            Assert.Empty((result.Result as OkObjectResult).Value as IEnumerable<TodoListItem>);
        }
    }

    [Fact]
    public async Task GetTodoListItems_WhenTodoListDoesntExist_ReturnsNotFound()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.GetTodoListItems(99);

            Assert.IsType<NotFoundResult>(result.Result);
        }
    }

    [Fact]
    public async Task GetTodoListItem_WhenTodoListDoesntExist_ReturnsNotFound()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.GetTodoListItem(99, 1);

            Assert.IsType<NotFoundResult>(result.Result);
        }
    }

    [Fact]
    public async Task GetTodoListItem_WhenCalled_ReturnsItemById()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.GetTodoListItem(1, 1);

            Assert.IsType<OkObjectResult>(result.Result);
            var item = (result.Result as OkObjectResult).Value as TodoListItem;
            Assert.Equal(1, item.Id);
            Assert.Equal("Item 1", item.Description);
        }
    }

    [Fact]
    public async Task GetTodoListItem_WhenIdDoesntExist_ReturnsNotFound()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.GetTodoListItem(1, 99);

            Assert.IsType<NotFoundResult>(result.Result);
        }
    }

    [Fact]
    public async Task GetTodoListItem_WhenItemBelongsToDifferentList_ReturnsNotFound()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            // Item 3 belongs to TodoListId 2, not 1
            var result = await controller.GetTodoListItem(1, 3);

            Assert.IsType<NotFoundResult>(result.Result);
        }
    }

    [Fact]
    public async Task PutTodoListItem_WhenCalled_ReturnsOkWithUpdatedItem()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.PutTodoListItem(
                1,
                1,
                new UpdateTodoListItem { Description = "Updated Item 1", IsCompleted = true }
            );

            var ok = Assert.IsType<OkObjectResult>(result);
            var body = Assert.IsType<TodoListItem>(ok.Value);
            Assert.Equal("Updated Item 1", body.Description);
            Assert.True(body.IsCompleted);
        }
    }

    [Fact]
    public async Task PutTodoListItem_WhenCalled_PersistsChanges()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            await controller.PutTodoListItem(
                1,
                1,
                new UpdateTodoListItem { Description = "Persisted", IsCompleted = true }
            );

            var persisted = await context.TodoListItem.FindAsync(1L);
            Assert.Equal("Persisted", persisted.Description);
            Assert.True(persisted.IsCompleted);
        }
    }

    [Fact]
    public async Task PutTodoListItem_WhenIdDoesntExist_ReturnsNotFound()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.PutTodoListItem(
                1,
                99,
                new UpdateTodoListItem { Description = "Nope", IsCompleted = true }
            );

            Assert.IsType<NotFoundResult>(result);
        }
    }

    [Fact]
    public async Task PutTodoListItem_WhenTodoListDoesntExist_ReturnsNotFound()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.PutTodoListItem(
                99,
                1,
                new UpdateTodoListItem { Description = "Nope", IsCompleted = true }
            );

            Assert.IsType<NotFoundResult>(result);
        }
    }

    [Fact]
    public async Task PutTodoListItem_WhenItemBelongsToDifferentList_ReturnsNotFound()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            // Item 3 belongs to TodoListId 2, not 1
            var result = await controller.PutTodoListItem(
                1,
                3,
                new UpdateTodoListItem { Description = "Hijacked", IsCompleted = true }
            );

            Assert.IsType<NotFoundResult>(result);

            var untouched = await context.TodoListItem.FindAsync(3L);
            Assert.Equal("Item 3", untouched.Description);
            Assert.False(untouched.IsCompleted);
        }
    }

    [Fact]
    public async Task PutTodoListItem_CanToggleIsCompletedFromTrueToFalse()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            // Item 2 starts with IsCompleted = true
            var result = await controller.PutTodoListItem(
                1,
                2,
                new UpdateTodoListItem { Description = "Item 2", IsCompleted = false }
            );

            Assert.IsType<OkObjectResult>(result);
            var persisted = await context.TodoListItem.FindAsync(2L);
            Assert.False(persisted.IsCompleted);
        }
    }

    [Fact]
    public async Task PostTodoListItem_WhenCalled_ReturnsCreatedAtActionWithBody()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.PostTodoListItem(
                1,
                new CreateTodoListItem { Description = "Brand new" }
            );

            var created = Assert.IsType<CreatedAtActionResult>(result.Result);
            var body = Assert.IsType<TodoListItem>(created.Value);

            Assert.Equal("GetTodoListItem", created.ActionName);
            Assert.Equal(1L, created.RouteValues["todoListId"]);
            Assert.Equal(body.Id, created.RouteValues["id"]);
            Assert.Equal("Brand new", body.Description);
            Assert.Equal(1, body.TodoListId);
        }
    }

    [Fact]
    public async Task PostTodoListItem_DefaultsIsCompletedToFalse()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.PostTodoListItem(
                1,
                new CreateTodoListItem { Description = "Fresh" }
            );

            var created = Assert.IsType<CreatedAtActionResult>(result.Result);
            var body = Assert.IsType<TodoListItem>(created.Value);
            Assert.False(body.IsCompleted);
        }
    }

    [Fact]
    public async Task PostTodoListItem_PersistsItemInDatabase()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            await controller.PostTodoListItem(
                1,
                new CreateTodoListItem { Description = "Persist me" }
            );

            Assert.Equal(4, context.TodoListItem.Count());
            Assert.Contains(context.TodoListItem, i => i.Description == "Persist me" && i.TodoListId == 1);
        }
    }

    [Fact]
    public async Task PostTodoListItem_WhenTodoListDoesntExist_ReturnsNotFound()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.PostTodoListItem(
                99,
                new CreateTodoListItem { Description = "Orphan" }
            );

            Assert.IsType<NotFoundResult>(result.Result);
            Assert.Equal(3, context.TodoListItem.Count());
        }
    }

    [Fact]
    public async Task DeleteTodoListItem_WhenCalled_ReturnsNoContentAndRemovesItem()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.DeleteTodoListItem(1, 1);

            Assert.IsType<NoContentResult>(result);
            Assert.Equal(2, context.TodoListItem.Count());
            Assert.Null(await context.TodoListItem.FindAsync(1L));
        }
    }

    [Fact]
    public async Task DeleteTodoListItem_WhenIdDoesntExist_ReturnsNotFound()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.DeleteTodoListItem(1, 99);

            Assert.IsType<NotFoundResult>(result);
            Assert.Equal(3, context.TodoListItem.Count());
        }
    }

    [Fact]
    public async Task DeleteTodoListItem_WhenTodoListDoesntExist_ReturnsNotFound()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            var result = await controller.DeleteTodoListItem(99, 1);

            Assert.IsType<NotFoundResult>(result);
            Assert.Equal(3, context.TodoListItem.Count());
        }
    }

    [Fact]
    public async Task DeleteTodoListItem_WhenItemBelongsToDifferentList_ReturnsNotFound()
    {
        using (var context = new TodoContext(DatabaseContextOptions()))
        {
            PopulateDatabaseContext(context);

            var service = new TodoListItemService(context, NullLogger<TodoListItemService>.Instance);
            var controller = new TodoListItemsController(service);

            // Item 3 belongs to TodoListId 2, not 1
            var result = await controller.DeleteTodoListItem(1, 3);

            Assert.IsType<NotFoundResult>(result);
            Assert.Equal(3, context.TodoListItem.Count());
            Assert.NotNull(await context.TodoListItem.FindAsync(3L));
        }
    }
}
