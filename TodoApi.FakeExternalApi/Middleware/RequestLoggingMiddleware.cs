using System.Diagnostics;
using TodoApi.FakeExternalApi.Models;
using TodoApi.FakeExternalApi.State;

namespace TodoApi.FakeExternalApi.Middleware;

public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;

    public RequestLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, FakeStore store)
    {
        var path = context.Request.Path.Value ?? "";
        if (ShouldSkip(path))
        {
            await _next(context);
            return;
        }

        string? idempotencyKey = null;
        if (context.Request.Headers.TryGetValue("Idempotency-Key", out var headerValue))
        {
            idempotencyKey = headerValue.ToString();
        }

        var sw = Stopwatch.StartNew();
        await _next(context);
        sw.Stop();

        store.LogRequest(
            new RequestLogEntry(
                DateTime.UtcNow,
                context.Request.Method,
                path,
                context.Response.StatusCode,
                idempotencyKey,
                sw.ElapsedMilliseconds
            )
        );
    }

    private static bool ShouldSkip(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return true;
        return path.StartsWith("/__admin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase);
    }
}
