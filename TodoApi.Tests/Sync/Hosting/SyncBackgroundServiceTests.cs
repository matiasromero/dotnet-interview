using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TodoApi.Sync.Configuration;
using TodoApi.Sync.Data;
using TodoApi.Sync.Hosting;
using TodoApi.Sync.Models;
using TodoApi.Sync.Services;
using TodoApi.Tests.Sync.TestHelpers;
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

    [Fact]
    public async Task ExecuteAsync_AfterStartupDelay_InvokesAllFourPhases()
    {
        var listSync = new Mock<ITodoListSyncService>(MockBehavior.Strict);
        var itemSync = new Mock<ITodoListItemSyncService>(MockBehavior.Strict);

        var pullListResult = new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded);
        var ext = new TodoApi.Sync.External.Models.ExternalTodoList(
            "ext-1",
            "1",
            "L1",
            DateTime.UtcNow,
            DateTime.UtcNow,
            Array.Empty<TodoApi.Sync.External.Models.ExternalTodoItem>()
        );
        var mapped = new[] { new ExternalListWithMapping(ext, 1, "ext-1") };

        var itemPullCalled = new TaskCompletionSource();

        listSync
            .Setup(s => s.PushTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded));
        itemSync
            .Setup(s => s.PushTodoListItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded));
        listSync
            .Setup(s => s.PullTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((pullListResult, (IReadOnlyList<ExternalListWithMapping>)mapped));
        itemSync
            .Setup(s =>
                s.PullTodoListItemsAsync(
                    It.IsAny<IReadOnlyList<ExternalListWithMapping>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded))
            .Callback(() => itemPullCalled.TrySetResult());

        var sut = BuildSut(
            listSync.Object,
            itemSync.Object,
            new SyncOptions
            {
                Enabled = true,
                StartupDelay = TimeSpan.FromMilliseconds(10),
                Interval = TimeSpan.FromSeconds(10),
            }
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sut.StartAsync(cts.Token);
        await itemPullCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        listSync.Verify(
            s => s.PushTodoListsAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
        itemSync.Verify(
            s => s.PushTodoListItemsAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
        listSync.Verify(
            s => s.PullTodoListsAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
        itemSync.Verify(
            s =>
                s.PullTodoListItemsAsync(
                    It.IsAny<IReadOnlyList<ExternalListWithMapping>>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task ExecuteAsync_PushListThrows_ContinuesWithRemainingPhases()
    {
        var listSync = new Mock<ITodoListSyncService>(MockBehavior.Strict);
        var itemSync = new Mock<ITodoListItemSyncService>(MockBehavior.Strict);
        var capture = new CapturingLoggerProvider();

        var itemPullCalled = new TaskCompletionSource();

        listSync
            .Setup(s => s.PushTodoListsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom-list-push"));
        itemSync
            .Setup(s => s.PushTodoListItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded));
        var extForExceptionTest = new TodoApi.Sync.External.Models.ExternalTodoList(
            "ext-1",
            "1",
            "L1",
            DateTime.UtcNow,
            DateTime.UtcNow,
            Array.Empty<TodoApi.Sync.External.Models.ExternalTodoItem>()
        );
        var mappedForExceptionTest = new[]
        {
            new ExternalListWithMapping(extForExceptionTest, 1, "ext-1"),
        };
        listSync
            .Setup(s => s.PullTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                (
                    new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded),
                    (IReadOnlyList<ExternalListWithMapping>)mappedForExceptionTest
                )
            );
        itemSync
            .Setup(s =>
                s.PullTodoListItemsAsync(
                    It.IsAny<IReadOnlyList<ExternalListWithMapping>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded))
            .Callback(() => itemPullCalled.TrySetResult());

        var sut = BuildSut(
            listSync.Object,
            itemSync.Object,
            new SyncOptions
            {
                Enabled = true,
                StartupDelay = TimeSpan.FromMilliseconds(10),
                Interval = TimeSpan.FromSeconds(10),
            },
            capture
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sut.StartAsync(cts.Token);
        await itemPullCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        // The remaining 3 phases ran despite list push throwing.
        itemSync.Verify(
            s => s.PushTodoListItemsAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
        listSync.Verify(
            s => s.PullTodoListsAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
        itemSync.Verify(
            s =>
                s.PullTodoListItemsAsync(
                    It.IsAny<IReadOnlyList<ExternalListWithMapping>>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.AtLeastOnce
        );

        Assert.Contains(
            capture.Entries,
            e =>
                e.Level == LogLevel.Error
                && e.Message.Contains("list push tick threw", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task ExecuteAsync_PullListThrows_SkipsPullItemPhase()
    {
        var listSync = new Mock<ITodoListSyncService>(MockBehavior.Strict);
        var itemSync = new Mock<ITodoListItemSyncService>(MockBehavior.Strict);
        var capture = new CapturingLoggerProvider();

        var pullListCalled = new TaskCompletionSource();

        listSync
            .Setup(s => s.PushTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded));
        itemSync
            .Setup(s => s.PushTodoListItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded));
        listSync
            .Setup(s => s.PullTodoListsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom-list-pull"))
            .Callback(() => pullListCalled.TrySetResult());

        var sut = BuildSut(
            listSync.Object,
            itemSync.Object,
            new SyncOptions
            {
                Enabled = true,
                StartupDelay = TimeSpan.FromMilliseconds(10),
                Interval = TimeSpan.FromSeconds(10),
            },
            capture
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sut.StartAsync(cts.Token);
        await pullListCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Give the loop a moment to evaluate the mappedExternals guard before stopping.
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        // PullTodoListItemsAsync was never invoked because mappedExternals stayed empty.
        itemSync.Verify(
            s =>
                s.PullTodoListItemsAsync(
                    It.IsAny<IReadOnlyList<ExternalListWithMapping>>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        Assert.Contains(
            capture.Entries,
            e =>
                e.Level == LogLevel.Error
                && e.Message.Contains("list pull tick threw", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task ExecuteAsync_PullListReturnsZeroMapped_SkipsPullItemPhase()
    {
        var listSync = new Mock<ITodoListSyncService>(MockBehavior.Strict);
        var itemSync = new Mock<ITodoListItemSyncService>(MockBehavior.Strict);

        var pullListCalled = new TaskCompletionSource();

        listSync
            .Setup(s => s.PushTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded));
        itemSync
            .Setup(s => s.PushTodoListItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded));
        listSync
            .Setup(s => s.PullTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                (
                    new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded),
                    (IReadOnlyList<ExternalListWithMapping>)Array.Empty<ExternalListWithMapping>()
                )
            )
            .Callback(() => pullListCalled.TrySetResult());

        var sut = BuildSut(
            listSync.Object,
            itemSync.Object,
            new SyncOptions
            {
                Enabled = true,
                StartupDelay = TimeSpan.FromMilliseconds(10),
                Interval = TimeSpan.FromSeconds(10),
            }
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sut.StartAsync(cts.Token);
        await pullListCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        // mappedExternals was empty so PullTodoListItemsAsync was never invoked.
        itemSync.Verify(
            s =>
                s.PullTodoListItemsAsync(
                    It.IsAny<IReadOnlyList<ExternalListWithMapping>>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task ExecuteAsync_OutboxRetention_LogsPurgedCount()
    {
        var listSync = new Mock<ITodoListSyncService>(MockBehavior.Loose);
        var itemSync = new Mock<ITodoListItemSyncService>(MockBehavior.Loose);
        listSync
            .Setup(s => s.PushTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded));
        itemSync
            .Setup(s => s.PushTodoListItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded));
        listSync
            .Setup(s => s.PullTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                (
                    new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded),
                    (IReadOnlyList<ExternalListWithMapping>)Array.Empty<ExternalListWithMapping>()
                )
            );

        var capture = new CapturingLoggerProvider();
        var dbMock = new Mock<ISyncDbContext>(MockBehavior.Loose);
        var purgeCalled = new TaskCompletionSource();
        dbMock
            .Setup(d =>
                d.PurgeProcessedOutboxEventsAsync(
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(7)
            .Callback(() => purgeCalled.TrySetResult());

        var sut = BuildSut(
            listSync.Object,
            itemSync.Object,
            new SyncOptions
            {
                Enabled = true,
                StartupDelay = TimeSpan.FromMilliseconds(10),
                Interval = TimeSpan.FromSeconds(10),
                OutboxRetention = TimeSpan.FromDays(7),
            },
            capture,
            dbMock
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sut.StartAsync(cts.Token);
        await purgeCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        dbMock.Verify(
            d =>
                d.PurgeProcessedOutboxEventsAsync(
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.AtLeastOnce
        );
        Assert.Contains(
            capture.Entries,
            e =>
                e.Level == LogLevel.Information
                && e.Message.Contains("retention", StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains("7", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task ExecuteAsync_OutboxRetentionThrows_OtherPhasesUnaffected()
    {
        var listSync = new Mock<ITodoListSyncService>(MockBehavior.Loose);
        var itemSync = new Mock<ITodoListItemSyncService>(MockBehavior.Loose);
        var pushListCalled = new TaskCompletionSource();
        var pushItemCalled = new TaskCompletionSource();
        var pullListCalled = new TaskCompletionSource();
        listSync
            .Setup(s => s.PushTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded))
            .Callback(() => pushListCalled.TrySetResult());
        itemSync
            .Setup(s => s.PushTodoListItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded))
            .Callback(() => pushItemCalled.TrySetResult());
        listSync
            .Setup(s => s.PullTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                (
                    new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded),
                    (IReadOnlyList<ExternalListWithMapping>)Array.Empty<ExternalListWithMapping>()
                )
            )
            .Callback(() => pullListCalled.TrySetResult());

        var capture = new CapturingLoggerProvider();
        var dbMock = new Mock<ISyncDbContext>(MockBehavior.Loose);
        var purgeCalled = new TaskCompletionSource();
        dbMock
            .Setup(d =>
                d.PurgeProcessedOutboxEventsAsync(
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new InvalidOperationException("boom-retention"))
            .Callback(() => purgeCalled.TrySetResult());

        var sut = BuildSut(
            listSync.Object,
            itemSync.Object,
            new SyncOptions
            {
                Enabled = true,
                StartupDelay = TimeSpan.FromMilliseconds(10),
                Interval = TimeSpan.FromSeconds(10),
                OutboxRetention = TimeSpan.FromDays(7),
            },
            capture,
            dbMock
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sut.StartAsync(cts.Token);
        await Task.WhenAll(
            pushListCalled.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            pushItemCalled.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            pullListCalled.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            purgeCalled.Task.WaitAsync(TimeSpan.FromSeconds(5))
        );
        await sut.StopAsync(CancellationToken.None);

        Assert.Contains(
            capture.Entries,
            e =>
                e.Level == LogLevel.Error
                && e.Message.Contains("retention", StringComparison.OrdinalIgnoreCase)
        );
        listSync.Verify(
            s => s.PushTodoListsAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
        itemSync.Verify(
            s => s.PushTodoListItemsAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
        listSync.Verify(
            s => s.PullTodoListsAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task ExecuteAsync_OutboxRetentionDisabled_SkipsPhase()
    {
        var listSync = new Mock<ITodoListSyncService>(MockBehavior.Loose);
        var itemSync = new Mock<ITodoListItemSyncService>(MockBehavior.Loose);
        var pullListCalled = new TaskCompletionSource();
        listSync
            .Setup(s => s.PushTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded));
        itemSync
            .Setup(s => s.PushTodoListItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded));
        listSync
            .Setup(s => s.PullTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                (
                    new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded),
                    (IReadOnlyList<ExternalListWithMapping>)Array.Empty<ExternalListWithMapping>()
                )
            )
            .Callback(() => pullListCalled.TrySetResult());

        var dbMock = new Mock<ISyncDbContext>(MockBehavior.Strict);

        var sut = BuildSut(
            listSync.Object,
            itemSync.Object,
            new SyncOptions
            {
                Enabled = true,
                StartupDelay = TimeSpan.FromMilliseconds(10),
                Interval = TimeSpan.FromSeconds(10),
                OutboxRetention = TimeSpan.Zero,
            },
            null,
            dbMock
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sut.StartAsync(cts.Token);
        await pullListCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        // OutboxRetention=Zero means the phase short-circuits without invoking the purge.
        dbMock.Verify(
            d =>
                d.PurgeProcessedOutboxEventsAsync(
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringStartupDelay_ExitsCleanly()
    {
        var listSync = new Mock<ITodoListSyncService>(MockBehavior.Strict);
        var itemSync = new Mock<ITodoListItemSyncService>(MockBehavior.Strict);

        var sut = BuildSut(
            listSync.Object,
            itemSync.Object,
            new SyncOptions
            {
                Enabled = true,
                StartupDelay = TimeSpan.FromSeconds(10),
                Interval = TimeSpan.FromSeconds(10),
            }
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await sut.StartAsync(cts.Token);
        // Wait for cancellation to propagate and ExecuteAsync to exit.
        await Task.Delay(200);
        await sut.StopAsync(CancellationToken.None);

        // Cancellation interrupted StartupDelay before any sync phase ran.
        listSync.Verify(s => s.PushTodoListsAsync(It.IsAny<CancellationToken>()), Times.Never);
        itemSync.Verify(s => s.PushTodoListItemsAsync(It.IsAny<CancellationToken>()), Times.Never);
        listSync.Verify(s => s.PullTodoListsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static SyncBackgroundService BuildSut(
        ITodoListSyncService listSync,
        ITodoListItemSyncService itemSync,
        SyncOptions options,
        CapturingLoggerProvider? capture = null,
        Mock<ISyncDbContext>? syncDbContext = null
    )
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => listSync);
        services.AddScoped(_ => itemSync);

        var dbMock = syncDbContext ?? new Mock<ISyncDbContext>(MockBehavior.Loose);
        if (syncDbContext is null)
        {
            dbMock
                .Setup(d =>
                    d.PurgeProcessedOutboxEventsAsync(
                        It.IsAny<DateTime>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync(0);
        }
        services.AddScoped(_ => dbMock.Object);

        var sp = services.BuildServiceProvider();
        var scopes = sp.GetRequiredService<IServiceScopeFactory>();

        ILogger<SyncBackgroundService> logger;
        if (capture is not null)
        {
            using var lf = LoggerFactory.Create(b => b.AddProvider(capture));
            logger = lf.CreateLogger<SyncBackgroundService>();
        }
        else
        {
            logger = NullLogger<SyncBackgroundService>.Instance;
        }

        return new SyncBackgroundService(
            scopes,
            new TestOptionsMonitor<SyncOptions>(options),
            logger
        );
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
