using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TodoApi.Configuration;
using TodoApi.Hosting;
using TodoApi.Hubs;
using TodoApi.Sync.Models;

namespace TodoApi.Tests.Hosting;

public class OutboxBroadcasterTests
{
    private static (
        IServiceScopeFactory ScopeFactory,
        Func<TodoContext> NewContext
    ) BuildScopeFactory()
    {
        var dbName = Guid.NewGuid().ToString();
        var sp = new ServiceCollection()
            .AddDbContext<TodoContext>(opt => opt.UseInMemoryDatabase(dbName))
            .BuildServiceProvider();
        return (
            sp.GetRequiredService<IServiceScopeFactory>(),
            () =>
            {
                var scope = sp.CreateScope();
                return scope.ServiceProvider.GetRequiredService<TodoContext>();
            }
        );
    }

    private sealed class SpyClient : ITodoSyncClient
    {
        public List<ChangeNotification> ListNotifications { get; } = new();
        public List<ChangeNotification> ItemNotifications { get; } = new();

        public Task TodoListChanged(ChangeNotification notification)
        {
            ListNotifications.Add(notification);
            return Task.CompletedTask;
        }

        public Task TodoListItemChanged(ChangeNotification notification)
        {
            ItemNotifications.Add(notification);
            return Task.CompletedTask;
        }
    }

    private static (IHubContext<TodoSyncHub, ITodoSyncClient> Hub, SpyClient Spy) BuildHubWithSpy()
    {
        var spy = new SpyClient();
        var clients = new Mock<IHubClients<ITodoSyncClient>>(MockBehavior.Strict);
        clients.SetupGet(c => c.All).Returns(spy);
        var hub = new Mock<IHubContext<TodoSyncHub, ITodoSyncClient>>(MockBehavior.Strict);
        hub.SetupGet(h => h.Clients).Returns(clients.Object);
        return (hub.Object, spy);
    }

    private static OutboxBroadcaster BuildBroadcaster(
        IServiceScopeFactory scopeFactory,
        IHubContext<TodoSyncHub, ITodoSyncClient> hub,
        int batchSize = 200
    )
    {
        var options = Options.Create(new RealtimeOptions { BatchSize = batchSize });
        return new OutboxBroadcaster(
            scopeFactory,
            hub,
            options,
            NullLogger<OutboxBroadcaster>.Instance
        );
    }

    [Fact]
    public async Task BroadcastBatchAsync_WithSingleTodoListEvent_InvokesTodoListChangedAndAdvancesCursor()
    {
        var (scopes, newCtx) = BuildScopeFactory();
        var (hub, spy) = BuildHubWithSpy();

        using (var seed = newCtx())
        {
            seed.OutboxEvents.Add(
                new OutboxEvent
                {
                    EntityType = SyncEntityType.TodoList,
                    EntityId = 42,
                    Operation = OutboxOperation.Create,
                    OccurredAt = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
                    IdempotencyKey = Guid.NewGuid(),
                }
            );
            await seed.SaveChangesAsync();
        }

        var sut = BuildBroadcaster(scopes, hub);

        var newCursor = await sut.BroadcastBatchAsync(0, CancellationToken.None);

        var sent = Assert.Single(spy.ListNotifications);
        Assert.Equal(SyncEntityType.TodoList, sent.EntityType);
        Assert.Equal(42, sent.EntityId);
        Assert.Equal(OutboxOperation.Create, sent.Operation);
        Assert.Equal(new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc), sent.OccurredAt);
        Assert.Empty(spy.ItemNotifications);
        Assert.True(newCursor > 0);
        Assert.Equal(sent.EventId, newCursor);
    }

    [Fact]
    public async Task BroadcastBatchAsync_WithTodoListItemEvent_InvokesTodoListItemChanged()
    {
        var (scopes, newCtx) = BuildScopeFactory();
        var (hub, spy) = BuildHubWithSpy();

        using (var seed = newCtx())
        {
            seed.OutboxEvents.Add(
                new OutboxEvent
                {
                    EntityType = SyncEntityType.TodoListItem,
                    EntityId = 7,
                    Operation = OutboxOperation.Update,
                    OccurredAt = DateTime.UtcNow,
                    IdempotencyKey = Guid.NewGuid(),
                }
            );
            await seed.SaveChangesAsync();
        }

        var sut = BuildBroadcaster(scopes, hub);

        await sut.BroadcastBatchAsync(0, CancellationToken.None);

        var sent = Assert.Single(spy.ItemNotifications);
        Assert.Equal(SyncEntityType.TodoListItem, sent.EntityType);
        Assert.Equal(7, sent.EntityId);
        Assert.Equal(OutboxOperation.Update, sent.Operation);
        Assert.Empty(spy.ListNotifications);
    }

    [Fact]
    public async Task BroadcastBatchAsync_WithEventsBeforeCursor_BroadcastsNothing()
    {
        var (scopes, newCtx) = BuildScopeFactory();
        var (hub, spy) = BuildHubWithSpy();

        long maxId;
        using (var seed = newCtx())
        {
            for (int i = 0; i < 3; i++)
            {
                seed.OutboxEvents.Add(
                    new OutboxEvent
                    {
                        EntityType = SyncEntityType.TodoList,
                        EntityId = i + 1,
                        Operation = OutboxOperation.Create,
                        OccurredAt = DateTime.UtcNow,
                        IdempotencyKey = Guid.NewGuid(),
                    }
                );
            }
            await seed.SaveChangesAsync();
            maxId = seed.OutboxEvents.Max(e => e.Id);
        }

        var sut = BuildBroadcaster(scopes, hub);

        var newCursor = await sut.BroadcastBatchAsync(maxId, CancellationToken.None);

        Assert.Empty(spy.ListNotifications);
        Assert.Empty(spy.ItemNotifications);
        Assert.Equal(maxId, newCursor); // cursor unchanged
    }

    [Fact]
    public async Task BroadcastBatchAsync_RespectsBatchSize()
    {
        var (scopes, newCtx) = BuildScopeFactory();
        var (hub, spy) = BuildHubWithSpy();

        using (var seed = newCtx())
        {
            for (int i = 0; i < 10; i++)
            {
                seed.OutboxEvents.Add(
                    new OutboxEvent
                    {
                        EntityType = SyncEntityType.TodoList,
                        EntityId = i + 1,
                        Operation = OutboxOperation.Create,
                        OccurredAt = DateTime.UtcNow.AddMilliseconds(i),
                        IdempotencyKey = Guid.NewGuid(),
                    }
                );
            }
            await seed.SaveChangesAsync();
        }

        var sut = BuildBroadcaster(scopes, hub, batchSize: 3);

        var newCursor = await sut.BroadcastBatchAsync(0, CancellationToken.None);

        Assert.Equal(3, spy.ListNotifications.Count);

        // Cursor must point at the third event (FIFO by Id), not the tenth.
        using var verify = newCtx();
        var thirdId = verify.OutboxEvents.OrderBy(e => e.Id).Skip(2).First().Id;
        Assert.Equal(thirdId, newCursor);
    }

    [Fact]
    public async Task BroadcastBatchAsync_DoesNotModifyProcessedAt()
    {
        var (scopes, newCtx) = BuildScopeFactory();
        var (hub, _) = BuildHubWithSpy();

        long evtId;
        using (var seed = newCtx())
        {
            var evt = new OutboxEvent
            {
                EntityType = SyncEntityType.TodoList,
                EntityId = 1,
                Operation = OutboxOperation.Create,
                OccurredAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            };
            seed.OutboxEvents.Add(evt);
            await seed.SaveChangesAsync();
            evtId = evt.Id;
        }

        var sut = BuildBroadcaster(scopes, hub);

        await sut.BroadcastBatchAsync(0, CancellationToken.None);

        using var verify = newCtx();
        var reloaded = await verify.OutboxEvents.SingleAsync(e => e.Id == evtId);
        Assert.Null(reloaded.ProcessedAt);
    }

    [Fact]
    public async Task GetInitialCursorAsync_WithExistingEvents_ReturnsMaxId()
    {
        var (scopes, newCtx) = BuildScopeFactory();
        var (hub, _) = BuildHubWithSpy();

        long maxId;
        using (var seed = newCtx())
        {
            for (int i = 0; i < 5; i++)
            {
                seed.OutboxEvents.Add(
                    new OutboxEvent
                    {
                        EntityType = SyncEntityType.TodoList,
                        EntityId = i + 1,
                        Operation = OutboxOperation.Create,
                        OccurredAt = DateTime.UtcNow,
                        IdempotencyKey = Guid.NewGuid(),
                    }
                );
            }
            await seed.SaveChangesAsync();
            maxId = seed.OutboxEvents.Max(e => e.Id);
        }

        var sut = BuildBroadcaster(scopes, hub);

        var cursor = await sut.GetInitialCursorAsync(CancellationToken.None);

        Assert.Equal(maxId, cursor);
    }

    [Fact]
    public async Task GetInitialCursorAsync_WithEmptyTable_ReturnsZero()
    {
        var (scopes, _) = BuildScopeFactory();
        var (hub, _) = BuildHubWithSpy();

        var sut = BuildBroadcaster(scopes, hub);

        var cursor = await sut.GetInitialCursorAsync(CancellationToken.None);

        Assert.Equal(0, cursor);
    }
}
