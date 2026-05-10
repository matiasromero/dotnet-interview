using TodoApi.FakeExternalApi.Models;
using TodoApi.FakeExternalApi.State;

namespace TodoApi.FakeExternalApi.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/__admin").WithTags("Admin");

        group.MapGet(
            "/state",
            (FakeStore store, ChaosState chaos) =>
                Results.Ok(
                    new FakeStateResponse
                    {
                        Lists = store.GetAll(),
                        Chaos = chaos.Snapshot(),
                        LastRequests = store.GetRecentRequests(),
                    }
                )
        );

        group.MapPost(
            "/reset",
            (FakeStore store, ChaosState chaos) =>
            {
                store.Reset();
                chaos.Reset();
                return Results.NoContent();
            }
        );

        group.MapPost(
            "/chaos",
            (ChaosRequest req, ChaosState chaos) =>
            {
                if (req.StatusCode < 400 || req.StatusCode > 599)
                {
                    return Results.BadRequest(
                        new { error = "status_code must be in the 4xx or 5xx range" }
                    );
                }
                chaos.Apply(req.FailRate, req.DelayMs, req.StatusCode);
                return Results.Ok(chaos.Snapshot());
            }
        );

        group.MapPost(
            "/seed",
            (SeedRequest req, FakeStore store) =>
            {
                store.Seed(req.Lists);
                return Results.Ok(new { count = req.Lists.Count });
            }
        );

        return app;
    }
}
