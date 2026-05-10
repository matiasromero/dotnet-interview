using TodoApi.FakeExternalApi.State;

namespace TodoApi.FakeExternalApi.Middleware;

public sealed class ChaosMiddleware
{
    private readonly RequestDelegate _next;

    public ChaosMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ChaosState chaos)
    {
        var path = context.Request.Path.Value ?? "";
        if (ShouldBypass(path))
        {
            await _next(context);
            return;
        }

        var snapshot = chaos.Snapshot();

        if (snapshot.FailRate > 0 && Random.Shared.Next(100) < snapshot.FailRate)
        {
            context.Response.StatusCode = snapshot.StatusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"error\":\"chaos\"}");
            return;
        }

        if (snapshot.DelayMs > 0)
        {
            await Task.Delay(snapshot.DelayMs, context.RequestAborted);
        }

        await _next(context);
    }

    private static bool ShouldBypass(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return true;
        return path.StartsWith("/__admin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase);
    }
}
