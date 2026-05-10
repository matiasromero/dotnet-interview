using TodoApi.FakeExternalApi.Models;

namespace TodoApi.FakeExternalApi.State;

public sealed class ChaosState
{
    private readonly object _lock = new();
    private int _failRate;
    private int _delayMs;
    private int _statusCode = 500;

    public ChaosSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new ChaosSnapshot
            {
                FailRate = _failRate,
                DelayMs = _delayMs,
                StatusCode = _statusCode,
            };
        }
    }

    public void Apply(int failRate, int delayMs, int statusCode)
    {
        lock (_lock)
        {
            _failRate = Math.Clamp(failRate, 0, 100);
            _delayMs = Math.Clamp(delayMs, 0, 30_000);
            _statusCode = statusCode;
        }
    }

    public void Reset() => Apply(0, 0, 500);
}
