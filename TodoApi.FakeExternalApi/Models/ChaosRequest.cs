namespace TodoApi.FakeExternalApi.Models;

public sealed class ChaosRequest
{
    public int FailRate { get; set; }
    public int StatusCode { get; set; } = 500;
    public int DelayMs { get; set; }
}
