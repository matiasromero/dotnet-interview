using TodoApi.FakeExternalApi.Models;
using TodoApi.FakeExternalApi.State;

namespace TodoApi.FakeExternalApi.Endpoints;

public static class TodoListEndpoints
{
    public static IEndpointRouteBuilder MapTodoListEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/todolists", (FakeStore store) => Results.Ok(store.GetAll()))
            .WithTags("TodoList");

        app.MapPost(
                "/todolists",
                (CreateTodoListRequest req, HttpContext ctx, FakeStore store) =>
                {
                    Guid? key = TryParseIdempotencyKey(ctx);
                    if (key.HasValue && store.TryGetIdempotent(key.Value, out var cached))
                    {
                        return Results.Created($"/todolists/{cached!.Id}", cached);
                    }

                    var list = store.CreateList(req, key);
                    return Results.Created($"/todolists/{list.Id}", list);
                }
            )
            .WithTags("TodoList");

        app.MapPatch(
                "/todolists/{id}",
                (string id, UpdateTodoListRequest req, FakeStore store) =>
                {
                    var list = store.UpdateList(id, req);
                    return list is null ? Results.NotFound() : Results.Ok(list);
                }
            )
            .WithTags("TodoList");

        app.MapDelete(
                "/todolists/{id}",
                (string id, FakeStore store) =>
                    store.DeleteList(id) ? Results.NoContent() : Results.NotFound()
            )
            .WithTags("TodoList");

        return app;
    }

    private static Guid? TryParseIdempotencyKey(HttpContext ctx)
    {
        if (
            ctx.Request.Headers.TryGetValue("Idempotency-Key", out var value)
            && Guid.TryParse(value.ToString(), out var parsed)
        )
        {
            return parsed;
        }
        return null;
    }
}
