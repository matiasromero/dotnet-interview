namespace TodoApi.FakeExternalApi.Models;

public sealed class FakeStateResponse
{
    public IReadOnlyList<ExternalTodoList> Lists { get; set; } = Array.Empty<ExternalTodoList>();
    public ChaosSnapshot Chaos { get; set; } = new();
    public IReadOnlyList<RequestLogEntry> LastRequests { get; set; } =
        Array.Empty<RequestLogEntry>();
}

public sealed class ChaosSnapshot
{
    public int FailRate { get; set; }
    public int StatusCode { get; set; } = 500;
    public int DelayMs { get; set; }
}
