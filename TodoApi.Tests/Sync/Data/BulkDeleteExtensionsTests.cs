using Microsoft.EntityFrameworkCore;
using TodoApi.Sync.Data;
using TodoApi.Sync.Models;
using Xunit;

namespace TodoApi.Tests.Sync.Data;

public class BulkDeleteExtensionsTests
{
    private static DbContextOptions<TodoContext> NewDbOptions() =>
        new DbContextOptionsBuilder<TodoContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    [Fact]
    public async Task ExecuteBulkDeleteAsync_EmptyMatch_ReturnsZero()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoList,
                EntityId = 1,
                Operation = OutboxOperation.Create,
                OccurredAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        await ctx.SaveChangesAsync();

        var deleted = await ctx
            .OutboxEvents.Where(e => e.EntityId == 999)
            .ExecuteBulkDeleteAsync(ctx);

        Assert.Equal(0, deleted);
        Assert.Single(ctx.OutboxEvents);
    }

    [Fact]
    public async Task ExecuteBulkDeleteAsync_WithMatches_DeletesAndReturnsCount()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        ctx.OutboxEvents.AddRange(
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoList,
                EntityId = 1,
                Operation = OutboxOperation.Create,
                OccurredAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            },
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoList,
                EntityId = 2,
                Operation = OutboxOperation.Update,
                OccurredAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            },
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoListItem,
                EntityId = 3,
                Operation = OutboxOperation.Delete,
                OccurredAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        await ctx.SaveChangesAsync();

        var deleted = await ctx
            .OutboxEvents.Where(e => e.EntityType == SyncEntityType.TodoList)
            .ExecuteBulkDeleteAsync(ctx);

        Assert.Equal(2, deleted);
        var remaining = Assert.Single(ctx.OutboxEvents);
        Assert.Equal(SyncEntityType.TodoListItem, remaining.EntityType);
    }

    [Fact]
    public async Task ExecuteBulkDeleteAsync_NullSource_Throws()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        IQueryable<OutboxEvent> source = null!;

        await Assert.ThrowsAsync<ArgumentNullException>(() => source.ExecuteBulkDeleteAsync(ctx));
    }

    [Fact]
    public async Task ExecuteBulkDeleteAsync_NullContext_Throws()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var source = ctx.OutboxEvents.AsQueryable();

        await Assert.ThrowsAsync<ArgumentNullException>(() => source.ExecuteBulkDeleteAsync(null!));
    }
}
