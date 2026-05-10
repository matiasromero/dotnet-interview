using TodoApi.FakeExternalApi.Models;
using TodoApi.FakeExternalApi.State;

namespace TodoApi.FakeExternalApi.Endpoints;

public static class TodoListItemEndpoints
{
    public static IEndpointRouteBuilder MapTodoListItemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPatch(
                "/todolists/{listId}/todoitems/{itemId}",
                (string listId, string itemId, UpdateTodoItemRequest req, FakeStore store) =>
                {
                    var item = store.UpdateItem(listId, itemId, req);
                    return item is null ? Results.NotFound() : Results.Ok(item);
                }
            )
            .WithTags("TodoItem");

        app.MapDelete(
                "/todolists/{listId}/todoitems/{itemId}",
                (string listId, string itemId, FakeStore store) =>
                    store.DeleteItem(listId, itemId) ? Results.NoContent() : Results.NotFound()
            )
            .WithTags("TodoItem");

        return app;
    }
}
