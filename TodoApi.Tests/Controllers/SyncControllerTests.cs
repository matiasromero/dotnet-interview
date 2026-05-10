using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TodoApi.Controllers;
using TodoApi.Dtos;
using TodoApi.Services;
using TodoApi.Sync.Configuration;
using TodoApi.Sync.Models;
using TodoApi.Sync.Services;

namespace TodoApi.Tests.Controllers;

public class SyncControllerTests
{
    private DbContextOptions<TodoContext> DatabaseContextOptions()
    {
        return new DbContextOptionsBuilder<TodoContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static SyncController BuildController(TodoContext context, SyncOptions options)
    {
        var statusService = new SyncStatusService(context, Options.Create(options));
        var listSync = new Mock<ITodoListSyncService>(MockBehavior.Strict).Object;
        var itemSync = new Mock<ITodoListItemSyncService>(MockBehavior.Strict).Object;
        return new SyncController(
            listSync,
            itemSync,
            NullLogger<SyncController>.Instance,
            statusService
        );
    }

    [Fact]
    public async Task GetStatus_WithSeededRunsAndPendingOutbox_ReturnsLatestRunPerPairAndPendingCounts()
    {
        using var context = new TodoContext(DatabaseContextOptions());

        // Two TodoList/Push runs — only the most recent should be reported.
        var olderListPush = new SyncRun
        {
            EntityType = SyncEntityType.TodoList,
            Direction = SyncDirection.Push,
            StartedAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
            FinishedAt = new DateTime(2026, 5, 1, 10, 0, 5, DateTimeKind.Utc),
            Status = SyncRunStatus.Succeeded,
            ItemsProcessed = 3,
            ItemsFailed = 0,
        };
        var newerListPush = new SyncRun
        {
            EntityType = SyncEntityType.TodoList,
            Direction = SyncDirection.Push,
            StartedAt = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
            FinishedAt = new DateTime(2026, 5, 10, 12, 0, 2, DateTimeKind.Utc),
            Status = SyncRunStatus.Succeeded,
            ItemsProcessed = 5,
            ItemsFailed = 1,
        };
        // One TodoListItem/Pull run with Failed status to assert it surfaces verbatim.
        var itemPull = new SyncRun
        {
            EntityType = SyncEntityType.TodoListItem,
            Direction = SyncDirection.Pull,
            StartedAt = new DateTime(2026, 5, 10, 12, 1, 0, DateTimeKind.Utc),
            FinishedAt = new DateTime(2026, 5, 10, 12, 1, 1, DateTimeKind.Utc),
            Status = SyncRunStatus.Failed,
            ItemsProcessed = 0,
            ItemsFailed = 4,
            Error = "external 503",
        };
        context.SyncRuns.AddRange(olderListPush, newerListPush, itemPull);

        // Three pending outbox events (oldest at 11:30) + one already processed.
        context.OutboxEvents.AddRange(
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoList,
                EntityId = 1,
                Operation = OutboxOperation.Create,
                OccurredAt = new DateTime(2026, 5, 10, 11, 30, 0, DateTimeKind.Utc),
                IdempotencyKey = Guid.NewGuid(),
            },
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoListItem,
                EntityId = 2,
                Operation = OutboxOperation.Update,
                OccurredAt = new DateTime(2026, 5, 10, 11, 45, 0, DateTimeKind.Utc),
                IdempotencyKey = Guid.NewGuid(),
            },
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoListItem,
                EntityId = 3,
                Operation = OutboxOperation.Delete,
                OccurredAt = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
                IdempotencyKey = Guid.NewGuid(),
            },
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoList,
                EntityId = 4,
                Operation = OutboxOperation.Create,
                OccurredAt = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc),
                ProcessedAt = new DateTime(2026, 5, 9, 10, 0, 5, DateTimeKind.Utc),
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        context.SaveChanges();

        var syncOptions = new SyncOptions
        {
            Interval = TimeSpan.FromSeconds(60),
            StartupDelay = TimeSpan.FromSeconds(5),
            Enabled = true,
            OutboxBatchSize = 1000,
            OutboxRetention = TimeSpan.FromDays(7),
        };
        var controller = BuildController(context, syncOptions);

        var result = await controller.GetStatus(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<SyncStatusResponse>(ok.Value);

        // Two distinct (EntityType, Direction) pairs were seeded → two summaries.
        Assert.Equal(2, body.LastRuns.Count);

        var listPushSummary = Assert.Single(
            body.LastRuns,
            r => r.EntityType == SyncEntityType.TodoList && r.Direction == SyncDirection.Push
        );
        Assert.Equal(newerListPush.StartedAt, listPushSummary.StartedAt);
        Assert.Equal(newerListPush.FinishedAt, listPushSummary.FinishedAt);
        Assert.Equal(SyncRunStatus.Succeeded, listPushSummary.Status);
        Assert.Equal(5, listPushSummary.ItemsProcessed);
        Assert.Equal(1, listPushSummary.ItemsFailed);

        var itemPullSummary = Assert.Single(
            body.LastRuns,
            r => r.EntityType == SyncEntityType.TodoListItem && r.Direction == SyncDirection.Pull
        );
        Assert.Equal(SyncRunStatus.Failed, itemPullSummary.Status);
        Assert.Equal(4, itemPullSummary.ItemsFailed);
        Assert.Equal("external 503", itemPullSummary.Error);

        Assert.Equal(3, body.PendingOutboxCount);
        Assert.Equal(
            new DateTime(2026, 5, 10, 11, 30, 0, DateTimeKind.Utc),
            body.OldestPendingOutboxOccurredAt
        );

        Assert.Equal(TimeSpan.FromSeconds(60), body.Config.Interval);
        Assert.Equal(TimeSpan.FromSeconds(5), body.Config.StartupDelay);
        Assert.True(body.Config.Enabled);
        Assert.Equal(1000, body.Config.OutboxBatchSize);
        Assert.Equal(TimeSpan.FromDays(7), body.Config.OutboxRetention);
    }

    [Fact]
    public async Task GetStatus_WithEmptyDatabase_ReturnsEmptyRunsZeroPendingNullOldest()
    {
        using var context = new TodoContext(DatabaseContextOptions());

        var controller = BuildController(context, new SyncOptions());

        var result = await controller.GetStatus(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<SyncStatusResponse>(ok.Value);

        Assert.Empty(body.LastRuns);
        Assert.Equal(0, body.PendingOutboxCount);
        Assert.Null(body.OldestPendingOutboxOccurredAt);
    }
}
