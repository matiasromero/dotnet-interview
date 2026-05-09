using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TodoApi.Sync.Configuration;
using TodoApi.Sync.Hosting;
using Xunit;

namespace TodoApi.Tests.Sync.Hosting;

public class SyncBackgroundServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenDisabled_StopsImmediately()
    {
        var scopes = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var options = new TestOptionsMonitor<SyncOptions>(new SyncOptions { Enabled = false });

        var sut = new SyncBackgroundService(
            scopes,
            options,
            NullLogger<SyncBackgroundService>.Instance
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await sut.StartAsync(cts.Token);
        await sut.StopAsync(cts.Token);
    }

    private class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
