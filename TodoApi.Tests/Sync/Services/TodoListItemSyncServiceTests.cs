using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TodoApi.Sync.External;
using TodoApi.Sync.External.Models;
using TodoApi.Sync.Models;
using TodoApi.Sync.Services;
using Xunit;

namespace TodoApi.Tests.Sync.Services;

public class TodoListItemSyncServiceTests
{
    private static DbContextOptions<TodoContext> NewDbOptions() =>
        new DbContextOptionsBuilder<TodoContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static ExternalTodoItem ExternalItemAt(
        string id,
        string sourceId,
        string description,
        bool completed,
        DateTime updatedAt
    ) =>
        new(
            Id: id,
            SourceId: sourceId,
            Description: description,
            Completed: completed,
            CreatedAt: updatedAt,
            UpdatedAt: updatedAt
        );

    [Fact]
    public async Task PushTodoListItemsAsync_NoItems_ReturnsZeroAndSucceeded()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var result = await sut.PushTodoListItemsAsync(CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var run = Assert.Single(ctx.SyncRuns);
        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(SyncDirection.Push, run.Direction);
        Assert.Equal(SyncEntityType.TodoListItem, run.EntityType);
        Assert.NotNull(run.FinishedAt);

        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PushTodoListItemsAsync_UnmappedItemsWithMappedParent_LogsWarningAndDoesNotCallClient()
    {
        await using var ctx = new TodoContext(NewDbOptions());

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Parent",
                UpdatedAt = DateTime.UtcNow,
            }
        );
        ctx.TodoListItem.Add(
            new TodoApi.Models.TodoListItem
            {
                Id = 10,
                Description = "Orphan item",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = DateTime.UtcNow,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-list-1",
                LastSyncedAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = DateTime.UtcNow,
                ExternalUpdatedAtAtSync = DateTime.UtcNow,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        var loggerMock = new Mock<ILogger<TodoListItemSyncService>>();

        var sut = new TodoListItemSyncService(ctx, client.Object, loggerMock.Object);

        var result = await sut.PushTodoListItemsAsync(CancellationToken.None);

        // Unmapped items with mapped parent are warned but do NOT count toward totals.
        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        client.VerifyNoOtherCalls();

        loggerMock.Verify(
            l =>
                l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task PushTodoListItemsAsync_MappedItemLocalChanged_PatchesExternal()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = DateTime.UtcNow.AddHours(-1);
        var localNewer = snapshot.AddMinutes(5);
        var externalAfterPatch = localNewer.AddSeconds(1);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Parent",
                UpdatedAt = snapshot,
            }
        );
        ctx.TodoListItem.Add(
            new TodoApi.Models.TodoListItem
            {
                Id = 10,
                Description = "New description",
                IsCompleted = true,
                TodoListId = 1,
                UpdatedAt = localNewer,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 10,
                ExternalId = "ext-item-1",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.UpdateTodoItemAsync(
                    "ext-list-1",
                    "ext-item-1",
                    It.Is<UpdateExternalTodoItemRequest>(r =>
                        r.Description == "New description" && r.Completed == true
                    ),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                ExternalItemAt("ext-item-1", "10", "New description", true, externalAfterPatch)
            );

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var result = await sut.PushTodoListItemsAsync(CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        client.Verify(
            c =>
                c.UpdateTodoItemAsync(
                    "ext-list-1",
                    "ext-item-1",
                    It.IsAny<UpdateExternalTodoItemRequest>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        var itemMapping = await ctx
            .SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoListItem)
            .SingleAsync();
        Assert.Equal(localNewer, itemMapping.LocalUpdatedAtAtSync);
        Assert.Equal(externalAfterPatch, itemMapping.ExternalUpdatedAtAtSync);
        Assert.True(itemMapping.LastSyncedAt > snapshot);
    }

    [Fact]
    public async Task PushTodoListItemsAsync_MappedItemNoLocalChanges_DoesNotPatch()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Parent",
                UpdatedAt = snapshot,
            }
        );
        ctx.TodoListItem.Add(
            new TodoApi.Models.TodoListItem
            {
                Id = 10,
                Description = "Stable",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 10,
                ExternalId = "ext-item-1",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var result = await sut.PushTodoListItemsAsync(CancellationToken.None);

        // No-op mapped items count toward Total and Processed (examined and decided to skip),
        // not toward Failed.
        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PushTodoListItemsAsync_OrphanMapping_DeletesExternalAndRemovesMapping()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        // Item mapping points at local id 999 — no TodoListItem present → orphan.
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 999,
                ExternalId = "ext-item-1",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.DeleteTodoItemAsync("ext-list-1", "ext-item-1", It.IsAny<CancellationToken>())
            )
            .Returns(Task.CompletedTask);

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var result = await sut.PushTodoListItemsAsync(CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        client.Verify(
            c => c.DeleteTodoItemAsync("ext-list-1", "ext-item-1", It.IsAny<CancellationToken>()),
            Times.Once
        );

        Assert.Empty(ctx.SyncMappings);
    }

    [Fact]
    public async Task PushTodoListItemsAsync_OrphanMappingExternal404_TreatsAsResolved()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 999,
                ExternalId = "ext-item-1",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.DeleteTodoItemAsync("ext-list-1", "ext-item-1", It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(
                new ExternalApiException(
                    "not found",
                    404,
                    "DELETE",
                    "todolists/ext-list-1/todoitems/ext-item-1",
                    null
                )
            );

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var result = await sut.PushTodoListItemsAsync(CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        Assert.Empty(ctx.SyncMappings);
    }

    [Fact]
    public async Task PushTodoListItemsAsync_OneOfThreeFails_StatusPartial()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var localNewer = snapshot.AddMinutes(5);
        var externalAfterPatch = localNewer.AddSeconds(1);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Parent",
                UpdatedAt = snapshot,
            }
        );
        ctx.TodoListItem.AddRange(
            new TodoApi.Models.TodoListItem
            {
                Id = 10,
                Description = "Item 1",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = localNewer,
            },
            new TodoApi.Models.TodoListItem
            {
                Id = 20,
                Description = "Item 2",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = localNewer,
            },
            new TodoApi.Models.TodoListItem
            {
                Id = 30,
                Description = "Item 3",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = localNewer,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        ctx.SyncMappings.AddRange(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 10,
                ExternalId = "ext-item-1",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 20,
                ExternalId = "ext-item-2",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 30,
                ExternalId = "ext-item-3",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.UpdateTodoItemAsync(
                    "ext-list-1",
                    "ext-item-2",
                    It.IsAny<UpdateExternalTodoItemRequest>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(
                new ExternalApiException(
                    "boom",
                    503,
                    "PATCH",
                    "todolists/ext-list-1/todoitems/ext-item-2",
                    null
                )
            );
        client
            .Setup(c =>
                c.UpdateTodoItemAsync(
                    "ext-list-1",
                    It.Is<string>(id => id != "ext-item-2"),
                    It.IsAny<UpdateExternalTodoItemRequest>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (
                    string _,
                    string itemId,
                    UpdateExternalTodoItemRequest req,
                    CancellationToken __
                ) =>
                    ExternalItemAt(
                        itemId,
                        sourceId: itemId,
                        description: req.Description,
                        completed: req.Completed,
                        updatedAt: externalAfterPatch
                    )
            );

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var result = await sut.PushTodoListItemsAsync(CancellationToken.None);

        Assert.Equal(3, result.Total);
        Assert.Equal(2, result.Pushed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(SyncRunStatus.Partial, result.Status);

        var run = Assert.Single(ctx.SyncRuns);
        Assert.Equal(SyncRunStatus.Partial, run.Status);
        Assert.Equal(2, run.ItemsProcessed);
        Assert.Equal(1, run.ItemsFailed);
    }

    [Fact]
    public async Task PushTodoListItemsAsync_AllFail_StatusFailed()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var localNewer = snapshot.AddMinutes(5);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Parent",
                UpdatedAt = snapshot,
            }
        );
        ctx.TodoListItem.AddRange(
            new TodoApi.Models.TodoListItem
            {
                Id = 10,
                Description = "Item 1",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = localNewer,
            },
            new TodoApi.Models.TodoListItem
            {
                Id = 20,
                Description = "Item 2",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = localNewer,
            },
            new TodoApi.Models.TodoListItem
            {
                Id = 30,
                Description = "Item 3",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = localNewer,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        ctx.SyncMappings.AddRange(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 10,
                ExternalId = "ext-item-1",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 20,
                ExternalId = "ext-item-2",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 30,
                ExternalId = "ext-item-3",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.UpdateTodoItemAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<UpdateExternalTodoItemRequest>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new ExternalApiException("nope", 500, "PATCH", "todolists", null));

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var result = await sut.PushTodoListItemsAsync(CancellationToken.None);

        Assert.Equal(3, result.Total);
        Assert.Equal(0, result.Pushed);
        Assert.Equal(3, result.Failed);
        Assert.Equal(SyncRunStatus.Failed, result.Status);

        var run = Assert.Single(ctx.SyncRuns);
        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Equal(0, run.ItemsProcessed);
        Assert.Equal(3, run.ItemsFailed);
    }

    private static ExternalTodoList ExternalListWithItems(
        string id,
        string? sourceId,
        string name,
        DateTime updatedAt,
        params ExternalTodoItem[] items
    ) =>
        new(
            Id: id,
            SourceId: sourceId!,
            Name: name,
            CreatedAt: updatedAt,
            UpdatedAt: updatedAt,
            Items: items
        );

    [Fact]
    public async Task PullTodoListItemsAsync_NoExternalItems_ReturnsZeroAndSucceeded()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        // Either an empty list of mapped externals, or one with no items: both equivalent.
        var mappedExternals = new[]
        {
            new ExternalListWithMapping(
                ExternalListWithItems("ext-list-1", "1", "Empty", DateTime.UtcNow),
                ParentLocalId: 1,
                ParentExternalId: "ext-list-1"
            ),
        };

        var result = await sut.PullTodoListItemsAsync(mappedExternals, CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var run = Assert.Single(ctx.SyncRuns);
        Assert.Equal(SyncDirection.Pull, run.Direction);
        Assert.Equal(SyncEntityType.TodoListItem, run.EntityType);
        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.NotNull(run.FinishedAt);

        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PullTodoListItemsAsync_ExternalWithUnknownSourceId_CreatesLocalItem()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var t = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc);

        // Parent list exists locally with its mapping (a list-pull adopted/created it earlier).
        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Parent",
                UpdatedAt = t,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-list-1",
                LastSyncedAt = t,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = t,
                ExternalUpdatedAtAtSync = t,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var mappedExternals = new[]
        {
            new ExternalListWithMapping(
                ExternalListWithItems(
                    "ext-list-1",
                    "1",
                    "Parent",
                    t,
                    ExternalItemAt("ext-item-99", "non-numeric", "Brand new item", false, t)
                ),
                ParentLocalId: 1,
                ParentExternalId: "ext-list-1"
            ),
        };

        var result = await sut.PullTodoListItemsAsync(mappedExternals, CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var localItem = Assert.Single(ctx.TodoListItem);
        Assert.Equal("Brand new item", localItem.Description);
        Assert.False(localItem.IsCompleted);
        Assert.Equal(1L, localItem.TodoListId);
        Assert.Equal(t, localItem.UpdatedAt);

        var itemMapping = await ctx
            .SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoListItem)
            .SingleAsync();
        Assert.Equal(localItem.Id, itemMapping.LocalId);
        Assert.Equal("ext-item-99", itemMapping.ExternalId);
        Assert.Equal("ext-list-1", itemMapping.ParentExternalId);
        Assert.Equal(t, itemMapping.LocalUpdatedAtAtSync);
        Assert.Equal(t, itemMapping.ExternalUpdatedAtAtSync);

        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PullTodoListItemsAsync_ExternalWithLocalSourceIdNoMapping_AdoptsAsMapping()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var localUpdatedAt = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc);
        var externalUpdatedAt = localUpdatedAt.AddSeconds(1);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Parent",
                UpdatedAt = localUpdatedAt,
            }
        );
        ctx.TodoListItem.Add(
            new TodoApi.Models.TodoListItem
            {
                Id = 42,
                Description = "Orphan local",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = localUpdatedAt,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-list-1",
                LastSyncedAt = localUpdatedAt,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = localUpdatedAt,
                ExternalUpdatedAtAtSync = localUpdatedAt,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var mappedExternals = new[]
        {
            new ExternalListWithMapping(
                ExternalListWithItems(
                    "ext-list-1",
                    "1",
                    "Parent",
                    externalUpdatedAt,
                    ExternalItemAt("ext-item-42", "42", "Orphan local", false, externalUpdatedAt)
                ),
                ParentLocalId: 1,
                ParentExternalId: "ext-list-1"
            ),
        };

        var result = await sut.PullTodoListItemsAsync(mappedExternals, CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        // Local item is unchanged.
        var local = await ctx.TodoListItem.FindAsync(42L);
        Assert.NotNull(local);
        Assert.Equal("Orphan local", local!.Description);
        Assert.False(local.IsCompleted);
        Assert.Equal(localUpdatedAt, local.UpdatedAt);

        // Adoption created the mapping.
        var itemMapping = await ctx
            .SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoListItem)
            .SingleAsync();
        Assert.Equal(42L, itemMapping.LocalId);
        Assert.Equal("ext-item-42", itemMapping.ExternalId);
        Assert.Equal("ext-list-1", itemMapping.ParentExternalId);
        Assert.Equal(localUpdatedAt, itemMapping.LocalUpdatedAtAtSync);
        Assert.Equal(externalUpdatedAt, itemMapping.ExternalUpdatedAtAtSync);

        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PullTodoListItemsAsync_MappedExternalNewer_UpdatesLocal()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var externalNewer = snapshot.AddMinutes(5);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Parent",
                UpdatedAt = snapshot,
            }
        );
        ctx.TodoListItem.Add(
            new TodoApi.Models.TodoListItem
            {
                Id = 10,
                Description = "Old local desc",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 10,
                ExternalId = "ext-item-1",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var mappedExternals = new[]
        {
            new ExternalListWithMapping(
                ExternalListWithItems(
                    "ext-list-1",
                    "1",
                    "Parent",
                    externalNewer,
                    ExternalItemAt("ext-item-1", "10", "External edited desc", true, externalNewer)
                ),
                ParentLocalId: 1,
                ParentExternalId: "ext-list-1"
            ),
        };

        var result = await sut.PullTodoListItemsAsync(mappedExternals, CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var local = await ctx.TodoListItem.FindAsync(10L);
        Assert.Equal("External edited desc", local!.Description);
        Assert.True(local.IsCompleted);
        Assert.Equal(externalNewer, local.UpdatedAt);

        var itemMapping = await ctx
            .SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoListItem)
            .SingleAsync();
        Assert.Equal(externalNewer, itemMapping.LocalUpdatedAtAtSync);
        Assert.Equal(externalNewer, itemMapping.ExternalUpdatedAtAtSync);

        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PullTodoListItemsAsync_MappedLocalNewer_PatchesExternal()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var localNewer = snapshot.AddMinutes(5);
        var externalAfterPatch = localNewer.AddSeconds(1);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Parent",
                UpdatedAt = snapshot,
            }
        );
        ctx.TodoListItem.Add(
            new TodoApi.Models.TodoListItem
            {
                Id = 10,
                Description = "Local edited",
                IsCompleted = true,
                TodoListId = 1,
                UpdatedAt = localNewer,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 10,
                ExternalId = "ext-item-1",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.UpdateTodoItemAsync(
                    "ext-list-1",
                    "ext-item-1",
                    It.Is<UpdateExternalTodoItemRequest>(r =>
                        r.Description == "Local edited" && r.Completed == true
                    ),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                ExternalItemAt("ext-item-1", "10", "Local edited", true, externalAfterPatch)
            );

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var mappedExternals = new[]
        {
            new ExternalListWithMapping(
                ExternalListWithItems(
                    "ext-list-1",
                    "1",
                    "Parent",
                    snapshot,
                    ExternalItemAt("ext-item-1", "10", "Old external desc", false, snapshot)
                ),
                ParentLocalId: 1,
                ParentExternalId: "ext-list-1"
            ),
        };

        var result = await sut.PullTodoListItemsAsync(mappedExternals, CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var local = await ctx.TodoListItem.FindAsync(10L);
        Assert.Equal("Local edited", local!.Description);

        var itemMapping = await ctx
            .SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoListItem)
            .SingleAsync();
        Assert.Equal(localNewer, itemMapping.LocalUpdatedAtAtSync);
        Assert.Equal(externalAfterPatch, itemMapping.ExternalUpdatedAtAtSync);

        client.Verify(
            c =>
                c.UpdateTodoItemAsync(
                    "ext-list-1",
                    "ext-item-1",
                    It.IsAny<UpdateExternalTodoItemRequest>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task PullTodoListItemsAsync_MappedBothChanged_ExternalWinsOnTimestamp()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var localNewer = snapshot.AddMinutes(2);
        var externalEvenNewer = snapshot.AddMinutes(5);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Parent",
                UpdatedAt = snapshot,
            }
        );
        ctx.TodoListItem.Add(
            new TodoApi.Models.TodoListItem
            {
                Id = 10,
                Description = "Local edited",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = localNewer,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 10,
                ExternalId = "ext-item-1",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var mappedExternals = new[]
        {
            new ExternalListWithMapping(
                ExternalListWithItems(
                    "ext-list-1",
                    "1",
                    "Parent",
                    externalEvenNewer,
                    ExternalItemAt("ext-item-1", "10", "External edited", true, externalEvenNewer)
                ),
                ParentLocalId: 1,
                ParentExternalId: "ext-list-1"
            ),
        };

        var result = await sut.PullTodoListItemsAsync(mappedExternals, CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var local = await ctx.TodoListItem.FindAsync(10L);
        Assert.Equal("External edited", local!.Description);
        Assert.True(local.IsCompleted);
        Assert.Equal(externalEvenNewer, local.UpdatedAt);

        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PullTodoListItemsAsync_MappedBothChanged_LocalWinsOnTimestamp()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var externalNewer = snapshot.AddMinutes(2);
        var localEvenNewer = snapshot.AddMinutes(5);
        var externalAfterPatch = localEvenNewer.AddSeconds(1);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Parent",
                UpdatedAt = snapshot,
            }
        );
        ctx.TodoListItem.Add(
            new TodoApi.Models.TodoListItem
            {
                Id = 10,
                Description = "Local edited",
                IsCompleted = true,
                TodoListId = 1,
                UpdatedAt = localEvenNewer,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 10,
                ExternalId = "ext-item-1",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.UpdateTodoItemAsync(
                    "ext-list-1",
                    "ext-item-1",
                    It.Is<UpdateExternalTodoItemRequest>(r =>
                        r.Description == "Local edited" && r.Completed == true
                    ),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                ExternalItemAt("ext-item-1", "10", "Local edited", true, externalAfterPatch)
            );

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var mappedExternals = new[]
        {
            new ExternalListWithMapping(
                ExternalListWithItems(
                    "ext-list-1",
                    "1",
                    "Parent",
                    externalNewer,
                    ExternalItemAt("ext-item-1", "10", "External edited", false, externalNewer)
                ),
                ParentLocalId: 1,
                ParentExternalId: "ext-list-1"
            ),
        };

        var result = await sut.PullTodoListItemsAsync(mappedExternals, CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var local = await ctx.TodoListItem.FindAsync(10L);
        Assert.Equal("Local edited", local!.Description);

        client.Verify(
            c =>
                c.UpdateTodoItemAsync(
                    "ext-list-1",
                    "ext-item-1",
                    It.Is<UpdateExternalTodoItemRequest>(r =>
                        r.Description == "Local edited" && r.Completed == true
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task PullTodoListItemsAsync_MappedBothChanged_TieGoesToExternal()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var bothChangedAt = snapshot.AddMinutes(5);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Parent",
                UpdatedAt = snapshot,
            }
        );
        ctx.TodoListItem.Add(
            new TodoApi.Models.TodoListItem
            {
                Id = 10,
                Description = "Local tie",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = bothChangedAt,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 10,
                ExternalId = "ext-item-1",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var mappedExternals = new[]
        {
            new ExternalListWithMapping(
                ExternalListWithItems(
                    "ext-list-1",
                    "1",
                    "Parent",
                    bothChangedAt,
                    ExternalItemAt("ext-item-1", "10", "External tie", true, bothChangedAt)
                ),
                ParentLocalId: 1,
                ParentExternalId: "ext-list-1"
            ),
        };

        var result = await sut.PullTodoListItemsAsync(mappedExternals, CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        // Tie goes to external (rule `>=`).
        var local = await ctx.TodoListItem.FindAsync(10L);
        Assert.Equal("External tie", local!.Description);
        Assert.True(local.IsCompleted);

        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PullTodoListItemsAsync_MappedNoChanges_BumpsLastSyncedOnly()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = DateTime.UtcNow.AddHours(-1);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Parent",
                UpdatedAt = snapshot,
            }
        );
        ctx.TodoListItem.Add(
            new TodoApi.Models.TodoListItem
            {
                Id = 10,
                Description = "Stable",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 10,
                ExternalId = "ext-item-1",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        var mappedExternals = new[]
        {
            new ExternalListWithMapping(
                ExternalListWithItems(
                    "ext-list-1",
                    "1",
                    "Parent",
                    snapshot,
                    ExternalItemAt("ext-item-1", "10", "Stable", false, snapshot)
                ),
                ParentLocalId: 1,
                ParentExternalId: "ext-list-1"
            ),
        };

        var result = await sut.PullTodoListItemsAsync(mappedExternals, CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var local = await ctx.TodoListItem.FindAsync(10L);
        Assert.Equal("Stable", local!.Description);
        Assert.Equal(snapshot, local.UpdatedAt);

        var itemMapping = await ctx
            .SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoListItem)
            .SingleAsync();
        Assert.Equal(snapshot, itemMapping.LocalUpdatedAtAtSync);
        Assert.Equal(snapshot, itemMapping.ExternalUpdatedAtAtSync);
        Assert.True(itemMapping.LastSyncedAt > snapshot);

        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PullTodoListItemsAsync_OneOfThreeFails_StatusPartial()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var localNewer = snapshot.AddMinutes(5);
        var externalAfterPatch = localNewer.AddSeconds(1);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Parent",
                UpdatedAt = snapshot,
            }
        );
        ctx.TodoListItem.AddRange(
            new TodoApi.Models.TodoListItem
            {
                Id = 10,
                Description = "Item 1 local",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = localNewer,
            },
            new TodoApi.Models.TodoListItem
            {
                Id = 20,
                Description = "Item 2 local",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = localNewer,
            },
            new TodoApi.Models.TodoListItem
            {
                Id = 30,
                Description = "Item 3 local",
                IsCompleted = false,
                TodoListId = 1,
                UpdatedAt = localNewer,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        ctx.SyncMappings.AddRange(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 10,
                ExternalId = "ext-item-1",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 20,
                ExternalId = "ext-item-2",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 30,
                ExternalId = "ext-item-3",
                ParentExternalId = "ext-list-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.UpdateTodoItemAsync(
                    "ext-list-1",
                    "ext-item-2",
                    It.IsAny<UpdateExternalTodoItemRequest>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(
                new ExternalApiException(
                    "boom",
                    503,
                    "PATCH",
                    "todolists/ext-list-1/todoitems/ext-item-2",
                    null
                )
            );
        client
            .Setup(c =>
                c.UpdateTodoItemAsync(
                    "ext-list-1",
                    It.Is<string>(id => id != "ext-item-2"),
                    It.IsAny<UpdateExternalTodoItemRequest>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (
                    string _,
                    string itemId,
                    UpdateExternalTodoItemRequest req,
                    CancellationToken __
                ) =>
                    ExternalItemAt(
                        itemId,
                        sourceId: itemId,
                        description: req.Description,
                        completed: req.Completed,
                        updatedAt: externalAfterPatch
                    )
            );

        var sut = new TodoListItemSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListItemSyncService>.Instance
        );

        // Three items, all need PATCH (local newer, external on snapshot).
        var mappedExternals = new[]
        {
            new ExternalListWithMapping(
                ExternalListWithItems(
                    "ext-list-1",
                    "1",
                    "Parent",
                    snapshot,
                    ExternalItemAt("ext-item-1", "10", "Old 1", false, snapshot),
                    ExternalItemAt("ext-item-2", "20", "Old 2", false, snapshot),
                    ExternalItemAt("ext-item-3", "30", "Old 3", false, snapshot)
                ),
                ParentLocalId: 1,
                ParentExternalId: "ext-list-1"
            ),
        };

        var result = await sut.PullTodoListItemsAsync(mappedExternals, CancellationToken.None);

        Assert.Equal(3, result.Total);
        Assert.Equal(2, result.Pushed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(SyncRunStatus.Partial, result.Status);

        var run = Assert.Single(ctx.SyncRuns);
        Assert.Equal(SyncRunStatus.Partial, run.Status);
        Assert.Equal(2, run.ItemsProcessed);
        Assert.Equal(1, run.ItemsFailed);
    }
}
