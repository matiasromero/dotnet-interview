namespace TodoApi.FakeExternalApi.Models;

public sealed record RequestLogEntry(
    DateTime Timestamp,
    string Method,
    string Path,
    int Status,
    string? IdempotencyKey,
    long DurationMs
);
