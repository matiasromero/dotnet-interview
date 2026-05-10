using Microsoft.EntityFrameworkCore;
using TodoApi.Sync.Models;
using Xunit;

namespace TodoApi.Tests.Sync.Data;

public class SyncDbContextTests
{
    private static DbContextOptions<TodoContext> NewDbOptions() =>
        new DbContextOptionsBuilder<TodoContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static OutboxEvent NewEvent(
        SyncEntityType type,
        long entityId,
        DateTime occurredAt,
        DateTime? processedAt = null
    ) =>
        new OutboxEvent
        {
            EntityType = type,
            EntityId = entityId,
            Operation = OutboxOperation.Create,
            OccurredAt = occurredAt,
            ProcessedAt = processedAt,
            IdempotencyKey = Guid.NewGuid(),
        };

    [Fact]
    public async Task PurgeProcessedOutboxEventsAsync_EmptyTable_ReturnsZero()
    {
        await using var ctx = new TodoContext(NewDbOptions());

        var deleted = await ctx.PurgeProcessedOutboxEventsAsync(
            DateTime.UtcNow,
            CancellationToken.None
        );

        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task PurgeProcessedOutboxEventsAsync_OnlyOldProcessedDeleted()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var now = DateTime.UtcNow;
        var oldOccurred = now - TimeSpan.FromDays(2);
        var recentOccurred = now - TimeSpan.FromHours(1);

        ctx.OutboxEvents.AddRange(
            NewEvent(SyncEntityType.TodoList, 1, oldOccurred, processedAt: oldOccurred),
            NewEvent(SyncEntityType.TodoList, 2, recentOccurred, processedAt: recentOccurred),
            NewEvent(SyncEntityType.TodoList, 3, oldOccurred, processedAt: null),
            NewEvent(SyncEntityType.TodoList, 4, recentOccurred, processedAt: null)
        );
        await ctx.SaveChangesAsync();

        var cutoff = now - TimeSpan.FromDays(1);
        var deleted = await ctx.PurgeProcessedOutboxEventsAsync(cutoff, CancellationToken.None);

        Assert.Equal(1, deleted);
        var remainingIds = ctx
            .OutboxEvents.OrderBy(e => e.EntityId)
            .Select(e => e.EntityId)
            .ToList();
        Assert.Equal(new long[] { 2, 3, 4 }, remainingIds);
    }

    [Fact]
    public async Task PurgeProcessedOutboxEventsAsync_AllOldProcessed_DeletesAll()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var oldOccurred = DateTime.UtcNow - TimeSpan.FromDays(10);
        ctx.OutboxEvents.AddRange(
            NewEvent(SyncEntityType.TodoList, 1, oldOccurred, processedAt: oldOccurred),
            NewEvent(SyncEntityType.TodoListItem, 2, oldOccurred, processedAt: oldOccurred)
        );
        await ctx.SaveChangesAsync();

        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
        var deleted = await ctx.PurgeProcessedOutboxEventsAsync(cutoff, CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Empty(ctx.OutboxEvents);
    }

    [Fact]
    public async Task PurgeProcessedOutboxEventsAsync_Idempotent_SecondCallReturnsZero()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var oldOccurred = DateTime.UtcNow - TimeSpan.FromDays(10);
        ctx.OutboxEvents.Add(
            NewEvent(SyncEntityType.TodoList, 1, oldOccurred, processedAt: oldOccurred)
        );
        await ctx.SaveChangesAsync();

        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
        var firstCall = await ctx.PurgeProcessedOutboxEventsAsync(cutoff, CancellationToken.None);
        var secondCall = await ctx.PurgeProcessedOutboxEventsAsync(cutoff, CancellationToken.None);

        Assert.Equal(1, firstCall);
        Assert.Equal(0, secondCall);
    }
}
