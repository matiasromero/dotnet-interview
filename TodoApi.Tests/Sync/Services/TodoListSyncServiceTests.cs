using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TodoApi.Sync.Configuration;
using TodoApi.Sync.External;
using TodoApi.Sync.External.Models;
using TodoApi.Sync.Models;
using TodoApi.Sync.Services;
using Xunit;

namespace TodoApi.Tests.Sync.Services;

public class TodoListSyncServiceTests
{
    private static DbContextOptions<TodoContext> NewDbOptions() =>
        new DbContextOptionsBuilder<TodoContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    [Fact]
    public async Task PushTodoListsAsync_NoLocalLists_ReturnsZeroAndSucceeded()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var run = Assert.Single(ctx.SyncRuns);
        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(SyncDirection.Push, run.Direction);
        Assert.Equal(SyncEntityType.TodoList, run.EntityType);
        Assert.NotNull(run.FinishedAt);

        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PushTodoListsAsync_ThreeUnsyncedLists_PushesAllAndCreatesMappings()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        ctx.TodoList.AddRange(
            new TodoApi.Models.TodoList { Id = 1, Name = "List 1" },
            new TodoApi.Models.TodoList { Id = 2, Name = "List 2" },
            new TodoApi.Models.TodoList { Id = 3, Name = "List 3" }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.CreateTodoListAsync(
                    It.IsAny<CreateExternalTodoListRequest>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (CreateExternalTodoListRequest req, Guid _, CancellationToken __) =>
                    new ExternalTodoList(
                        Id: $"ext-{req.SourceId}",
                        SourceId: req.SourceId,
                        Name: req.Name,
                        CreatedAt: DateTime.UtcNow,
                        UpdatedAt: DateTime.UtcNow,
                        Items: Array.Empty<ExternalTodoItem>()
                    )
            );

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var mappings = ctx.SyncMappings.OrderBy(m => m.LocalId).ToList();
        Assert.Equal(3, mappings.Count);
        Assert.Equal(new[] { 1L, 2L, 3L }, mappings.Select(m => m.LocalId));
        Assert.Equal(new[] { "ext-1", "ext-2", "ext-3" }, mappings.Select(m => m.ExternalId));
        Assert.All(mappings, m => Assert.Equal(SyncEntityType.TodoList, m.EntityType));

        client.Verify(
            c =>
                c.CreateTodoListAsync(
                    It.Is<CreateExternalTodoListRequest>(r =>
                        r.SourceId == "1" && r.Name == "List 1"
                    ),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task PushTodoListsAsync_WithExistingMapping_OnlyPushesUnmapped()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        ctx.TodoList.AddRange(
            new TodoApi.Models.TodoList { Id = 1, Name = "Already synced" },
            new TodoApi.Models.TodoList { Id = 2, Name = "New 2" },
            new TodoApi.Models.TodoList { Id = 3, Name = "New 3" }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-prev",
                LastSyncedAt = DateTime.UtcNow.AddHours(-1),
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.CreateTodoListAsync(
                    It.IsAny<CreateExternalTodoListRequest>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (CreateExternalTodoListRequest req, Guid _, CancellationToken __) =>
                    new ExternalTodoList(
                        Id: $"ext-{req.SourceId}",
                        SourceId: req.SourceId,
                        Name: req.Name,
                        CreatedAt: DateTime.UtcNow,
                        UpdatedAt: DateTime.UtcNow,
                        Items: Array.Empty<ExternalTodoItem>()
                    )
            );

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Pushed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        client.Verify(
            c =>
                c.CreateTodoListAsync(
                    It.Is<CreateExternalTodoListRequest>(r => r.SourceId == "1"),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        client.Verify(
            c =>
                c.CreateTodoListAsync(
                    It.IsAny<CreateExternalTodoListRequest>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Exactly(2)
        );

        // Previous mapping intact + 2 new ones.
        Assert.Equal(3, ctx.SyncMappings.Count());
    }

    [Fact]
    public async Task PushTodoListsAsync_OneOfThreeFails_StatusPartialAndOthersMapped()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        ctx.TodoList.AddRange(
            new TodoApi.Models.TodoList { Id = 1, Name = "L1" },
            new TodoApi.Models.TodoList { Id = 2, Name = "L2" },
            new TodoApi.Models.TodoList { Id = 3, Name = "L3" }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.CreateTodoListAsync(
                    It.Is<CreateExternalTodoListRequest>(r => r.SourceId == "2"),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new ExternalApiException("boom", 503, "POST", "todolists", null));
        client
            .Setup(c =>
                c.CreateTodoListAsync(
                    It.Is<CreateExternalTodoListRequest>(r => r.SourceId != "2"),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (CreateExternalTodoListRequest req, Guid _, CancellationToken __) =>
                    new ExternalTodoList(
                        $"ext-{req.SourceId}",
                        req.SourceId,
                        req.Name,
                        DateTime.UtcNow,
                        DateTime.UtcNow,
                        Array.Empty<ExternalTodoItem>()
                    )
            );

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(3, result.Total);
        Assert.Equal(2, result.Pushed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(SyncRunStatus.Partial, result.Status);

        var mappings = ctx.SyncMappings.OrderBy(m => m.LocalId).ToList();
        Assert.Equal(new[] { 1L, 3L }, mappings.Select(m => m.LocalId));

        var run = Assert.Single(ctx.SyncRuns);
        Assert.Equal(SyncRunStatus.Partial, run.Status);
        Assert.Equal(2, run.ItemsProcessed);
        Assert.Equal(1, run.ItemsFailed);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task PushTodoListsAsync_ListWithLocalItems_PostsEmbeddedAndPersistsItemMappings()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var listUpdatedAt = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var item1UpdatedAt = new DateTime(2026, 5, 9, 10, 1, 0, DateTimeKind.Utc);
        var item2UpdatedAt = new DateTime(2026, 5, 9, 10, 2, 0, DateTimeKind.Utc);
        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "List with items",
                UpdatedAt = listUpdatedAt,
                Items = new List<TodoApi.Models.TodoListItem>
                {
                    new()
                    {
                        Id = 10,
                        Description = "First",
                        IsCompleted = false,
                        TodoListId = 1,
                        UpdatedAt = item1UpdatedAt,
                    },
                    new()
                    {
                        Id = 11,
                        Description = "Second",
                        IsCompleted = true,
                        TodoListId = 1,
                        UpdatedAt = item2UpdatedAt,
                    },
                },
            }
        );
        await ctx.SaveChangesAsync();

        var ext1UpdatedAt = new DateTime(2026, 5, 9, 11, 0, 0, DateTimeKind.Utc);
        var ext2UpdatedAt = new DateTime(2026, 5, 9, 11, 1, 0, DateTimeKind.Utc);
        var listExtUpdatedAt = new DateTime(2026, 5, 9, 11, 2, 0, DateTimeKind.Utc);

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.CreateTodoListAsync(
                    It.Is<CreateExternalTodoListRequest>(r =>
                        r.SourceId == "1"
                        && r.Name == "List with items"
                        && r.Items.Count == 2
                        && r.Items.Any(i =>
                            i.SourceId == "10" && i.Description == "First" && i.Completed == false
                        )
                        && r.Items.Any(i =>
                            i.SourceId == "11" && i.Description == "Second" && i.Completed == true
                        )
                    ),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new ExternalTodoList(
                    Id: "ext-1",
                    SourceId: "1",
                    Name: "List with items",
                    CreatedAt: listExtUpdatedAt,
                    UpdatedAt: listExtUpdatedAt,
                    Items: new[]
                    {
                        new ExternalTodoItem(
                            Id: "ext-item-10",
                            SourceId: "10",
                            Description: "First",
                            Completed: false,
                            CreatedAt: ext1UpdatedAt,
                            UpdatedAt: ext1UpdatedAt
                        ),
                        new ExternalTodoItem(
                            Id: "ext-item-11",
                            SourceId: "11",
                            Description: "Second",
                            Completed: true,
                            CreatedAt: ext2UpdatedAt,
                            UpdatedAt: ext2UpdatedAt
                        ),
                    }
                )
            );

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var listMapping = Assert.Single(
            ctx.SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoList).ToList()
        );
        Assert.Equal(1L, listMapping.LocalId);
        Assert.Equal("ext-1", listMapping.ExternalId);
        Assert.Equal(listUpdatedAt, listMapping.LocalUpdatedAtAtSync);
        Assert.Equal(listExtUpdatedAt, listMapping.ExternalUpdatedAtAtSync);

        var itemMappings = ctx
            .SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoListItem)
            .OrderBy(m => m.LocalId)
            .ToList();
        Assert.Equal(2, itemMappings.Count);

        Assert.Equal(10L, itemMappings[0].LocalId);
        Assert.Equal("ext-item-10", itemMappings[0].ExternalId);
        Assert.Equal("ext-1", itemMappings[0].ParentExternalId);
        Assert.Equal(item1UpdatedAt, itemMappings[0].LocalUpdatedAtAtSync);
        Assert.Equal(ext1UpdatedAt, itemMappings[0].ExternalUpdatedAtAtSync);

        Assert.Equal(11L, itemMappings[1].LocalId);
        Assert.Equal("ext-item-11", itemMappings[1].ExternalId);
        Assert.Equal("ext-1", itemMappings[1].ParentExternalId);
        Assert.Equal(item2UpdatedAt, itemMappings[1].LocalUpdatedAtAtSync);
        Assert.Equal(ext2UpdatedAt, itemMappings[1].ExternalUpdatedAtAtSync);

        client.Verify(
            c =>
                c.CreateTodoListAsync(
                    It.IsAny<CreateExternalTodoListRequest>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task PushTodoListsAsync_ListWithoutLocalItems_PostsEmptyItemsArray()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        ctx.TodoList.Add(new TodoApi.Models.TodoList { Id = 1, Name = "Empty list" });
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.CreateTodoListAsync(
                    It.Is<CreateExternalTodoListRequest>(r =>
                        r.SourceId == "1" && r.Name == "Empty list" && r.Items.Count == 0
                    ),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (CreateExternalTodoListRequest req, Guid _, CancellationToken __) =>
                    new ExternalTodoList(
                        Id: $"ext-{req.SourceId}",
                        SourceId: req.SourceId,
                        Name: req.Name,
                        CreatedAt: DateTime.UtcNow,
                        UpdatedAt: DateTime.UtcNow,
                        Items: Array.Empty<ExternalTodoItem>()
                    )
            );

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var listMapping = Assert.Single(ctx.SyncMappings);
        Assert.Equal(SyncEntityType.TodoList, listMapping.EntityType);
        Assert.Equal(1L, listMapping.LocalId);
        Assert.Empty(
            ctx.SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoListItem).ToList()
        );

        client.Verify(
            c =>
                c.CreateTodoListAsync(
                    It.IsAny<CreateExternalTodoListRequest>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task PushTodoListsAsync_ResponseItemHasNonParseableSourceId_LogsWarningAndSkipsItemMapping()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var listUpdatedAt = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var item1UpdatedAt = new DateTime(2026, 5, 9, 10, 1, 0, DateTimeKind.Utc);
        var item2UpdatedAt = new DateTime(2026, 5, 9, 10, 2, 0, DateTimeKind.Utc);
        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "List with bad response",
                UpdatedAt = listUpdatedAt,
                Items = new List<TodoApi.Models.TodoListItem>
                {
                    new()
                    {
                        Id = 10,
                        Description = "First",
                        IsCompleted = false,
                        TodoListId = 1,
                        UpdatedAt = item1UpdatedAt,
                    },
                    new()
                    {
                        Id = 11,
                        Description = "Second",
                        IsCompleted = true,
                        TodoListId = 1,
                        UpdatedAt = item2UpdatedAt,
                    },
                },
            }
        );
        await ctx.SaveChangesAsync();

        var ext1UpdatedAt = new DateTime(2026, 5, 9, 11, 0, 0, DateTimeKind.Utc);
        var extBadUpdatedAt = new DateTime(2026, 5, 9, 11, 1, 0, DateTimeKind.Utc);
        var listExtUpdatedAt = new DateTime(2026, 5, 9, 11, 2, 0, DateTimeKind.Utc);

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.CreateTodoListAsync(
                    It.IsAny<CreateExternalTodoListRequest>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new ExternalTodoList(
                    Id: "ext-1",
                    SourceId: "1",
                    Name: "List with bad response",
                    CreatedAt: listExtUpdatedAt,
                    UpdatedAt: listExtUpdatedAt,
                    Items: new[]
                    {
                        new ExternalTodoItem(
                            Id: "ext-item-10",
                            SourceId: "10",
                            Description: "First",
                            Completed: false,
                            CreatedAt: ext1UpdatedAt,
                            UpdatedAt: ext1UpdatedAt
                        ),
                        new ExternalTodoItem(
                            Id: "ext-item-bad",
                            SourceId: "not-a-number",
                            Description: "Second",
                            Completed: true,
                            CreatedAt: extBadUpdatedAt,
                            UpdatedAt: extBadUpdatedAt
                        ),
                    }
                )
            );

        var loggerMock = new Mock<ILogger<TodoListSyncService>>();

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            loggerMock.Object
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var listMapping = Assert.Single(
            ctx.SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoList).ToList()
        );
        Assert.Equal(1L, listMapping.LocalId);
        Assert.Equal("ext-1", listMapping.ExternalId);

        // Only the parseable item is mapped; the one with malformed SourceId is skipped.
        var itemMapping = Assert.Single(
            ctx.SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoListItem).ToList()
        );
        Assert.Equal(10L, itemMapping.LocalId);
        Assert.Equal("ext-item-10", itemMapping.ExternalId);
        Assert.Equal("ext-1", itemMapping.ParentExternalId);

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
    public async Task PushTodoListsAsync_ResponseItemSourceIdDoesNotMatchAnyLocalItem_LogsWarningAndSkipsItemMapping()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var listUpdatedAt = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var itemUpdatedAt = new DateTime(2026, 5, 9, 10, 1, 0, DateTimeKind.Utc);
        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "List with mismatched response",
                UpdatedAt = listUpdatedAt,
                Items = new List<TodoApi.Models.TodoListItem>
                {
                    new()
                    {
                        Id = 10,
                        Description = "Only local",
                        IsCompleted = false,
                        TodoListId = 1,
                        UpdatedAt = itemUpdatedAt,
                    },
                },
            }
        );
        await ctx.SaveChangesAsync();

        var extItemUpdatedAt = new DateTime(2026, 5, 9, 11, 0, 0, DateTimeKind.Utc);
        var listExtUpdatedAt = new DateTime(2026, 5, 9, 11, 1, 0, DateTimeKind.Utc);

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.CreateTodoListAsync(
                    It.IsAny<CreateExternalTodoListRequest>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new ExternalTodoList(
                    Id: "ext-1",
                    SourceId: "1",
                    Name: "List with mismatched response",
                    CreatedAt: listExtUpdatedAt,
                    UpdatedAt: listExtUpdatedAt,
                    Items: new[]
                    {
                        new ExternalTodoItem(
                            Id: "ext-item-orphan",
                            SourceId: "999",
                            Description: "Phantom",
                            Completed: false,
                            CreatedAt: extItemUpdatedAt,
                            UpdatedAt: extItemUpdatedAt
                        ),
                    }
                )
            );

        var loggerMock = new Mock<ILogger<TodoListSyncService>>();

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            loggerMock.Object
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var listMapping = Assert.Single(
            ctx.SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoList).ToList()
        );
        Assert.Equal(1L, listMapping.LocalId);
        Assert.Equal("ext-1", listMapping.ExternalId);

        // SourceId parses but does not match any local item: 0 item mappings.
        Assert.Empty(
            ctx.SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoListItem).ToList()
        );

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

    private static ExternalTodoList ExternalListAt(
        string id,
        string? sourceId,
        string name,
        DateTime updatedAt
    ) =>
        new(
            id,
            sourceId!,
            name,
            CreatedAt: updatedAt,
            UpdatedAt: updatedAt,
            Items: Array.Empty<ExternalTodoItem>()
        );

    [Fact]
    public async Task PullTodoListsAsync_NoExternalLists_ReturnsZeroAndSucceeded()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ExternalTodoList>());

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var run = Assert.Single(ctx.SyncRuns);
        Assert.Equal(SyncDirection.Pull, run.Direction);
        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task PullTodoListsAsync_ExternalWithUnknownSourceId_CreatesLocalAndMapping()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var t = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc);

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ExternalListAt("ext-99", null, "Brand new", t) });

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var local = Assert.Single(ctx.TodoList);
        Assert.Equal("Brand new", local.Name);
        Assert.Equal(t, local.UpdatedAt);

        var mapping = Assert.Single(ctx.SyncMappings);
        Assert.Equal(local.Id, mapping.LocalId);
        Assert.Equal("ext-99", mapping.ExternalId);
        Assert.Equal(t, mapping.LocalUpdatedAtAtSync);
        Assert.Equal(t, mapping.ExternalUpdatedAtAtSync);
    }

    [Fact]
    public async Task PullTodoListsAsync_CreateNewLocalFromExternalWithItems_PersistsItemsAndMappings()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var listUpdatedAt = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc);
        var item1UpdatedAt = new DateTime(2026, 5, 9, 12, 1, 0, DateTimeKind.Utc);
        var item2UpdatedAt = new DateTime(2026, 5, 9, 12, 2, 0, DateTimeKind.Utc);

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new[]
                {
                    new ExternalTodoList(
                        Id: "ext-99",
                        SourceId: null!,
                        Name: "Brand new with items",
                        CreatedAt: listUpdatedAt,
                        UpdatedAt: listUpdatedAt,
                        Items: new[]
                        {
                            new ExternalTodoItem(
                                Id: "ext-item-1",
                                SourceId: null!,
                                Description: "First external",
                                Completed: false,
                                CreatedAt: item1UpdatedAt,
                                UpdatedAt: item1UpdatedAt
                            ),
                            new ExternalTodoItem(
                                Id: "ext-item-2",
                                SourceId: null!,
                                Description: "Second external",
                                Completed: true,
                                CreatedAt: item2UpdatedAt,
                                UpdatedAt: item2UpdatedAt
                            ),
                        }
                    ),
                }
            );

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var local = Assert.Single(ctx.TodoList);
        Assert.Equal("Brand new with items", local.Name);
        Assert.Equal(listUpdatedAt, local.UpdatedAt);

        var localItems = ctx
            .TodoListItem.Where(i => i.TodoListId == local.Id)
            .OrderBy(i => i.Description)
            .ToList();
        Assert.Equal(2, localItems.Count);

        Assert.Equal("First external", localItems[0].Description);
        Assert.False(localItems[0].IsCompleted);
        Assert.Equal(item1UpdatedAt, localItems[0].UpdatedAt);

        Assert.Equal("Second external", localItems[1].Description);
        Assert.True(localItems[1].IsCompleted);
        Assert.Equal(item2UpdatedAt, localItems[1].UpdatedAt);

        var listMapping = Assert.Single(
            ctx.SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoList).ToList()
        );
        Assert.Equal(local.Id, listMapping.LocalId);
        Assert.Equal("ext-99", listMapping.ExternalId);
        Assert.Null(listMapping.ParentExternalId);
        Assert.Equal(listUpdatedAt, listMapping.LocalUpdatedAtAtSync);
        Assert.Equal(listUpdatedAt, listMapping.ExternalUpdatedAtAtSync);

        var itemMappings = ctx
            .SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoListItem)
            .OrderBy(m => m.ExternalId)
            .ToList();
        Assert.Equal(2, itemMappings.Count);

        Assert.Equal("ext-item-1", itemMappings[0].ExternalId);
        Assert.Equal("ext-99", itemMappings[0].ParentExternalId);
        Assert.Equal(localItems[0].Id, itemMappings[0].LocalId);
        Assert.Equal(item1UpdatedAt, itemMappings[0].LocalUpdatedAtAtSync);
        Assert.Equal(item1UpdatedAt, itemMappings[0].ExternalUpdatedAtAtSync);

        Assert.Equal("ext-item-2", itemMappings[1].ExternalId);
        Assert.Equal("ext-99", itemMappings[1].ParentExternalId);
        Assert.Equal(localItems[1].Id, itemMappings[1].LocalId);
        Assert.Equal(item2UpdatedAt, itemMappings[1].LocalUpdatedAtAtSync);
        Assert.Equal(item2UpdatedAt, itemMappings[1].ExternalUpdatedAtAtSync);
    }

    [Fact]
    public async Task PullTodoListsAsync_ExternalWithLocalSourceIdNoMapping_AdoptsAsMapping()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var localUpdatedAt = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc);
        var externalUpdatedAt = localUpdatedAt.AddSeconds(1);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 42,
                Name = "Orphan",
                UpdatedAt = localUpdatedAt,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ExternalListAt("ext-42", "42", "Orphan", externalUpdatedAt) });

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        Assert.Single(ctx.TodoList); // still only the orphan, was not duplicated
        var mapping = Assert.Single(ctx.SyncMappings);
        Assert.Equal(42L, mapping.LocalId);
        Assert.Equal("ext-42", mapping.ExternalId);
        Assert.Equal(localUpdatedAt, mapping.LocalUpdatedAtAtSync);
        Assert.Equal(externalUpdatedAt, mapping.ExternalUpdatedAtAtSync);
    }

    [Fact]
    public async Task PullTodoListsAsync_MappedExternalNewer_UpdatesLocalName()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var externalNewer = snapshot.AddMinutes(5);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Old name",
                UpdatedAt = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new[] { ExternalListAt("ext-1", "1", "External renamed", externalNewer) }
            );

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var local = await ctx.TodoList.FindAsync(1L);
        Assert.Equal("External renamed", local!.Name);
        Assert.Equal(externalNewer, local.UpdatedAt);

        var mapping = await ctx.SyncMappings.SingleAsync();
        Assert.Equal(externalNewer, mapping.LocalUpdatedAtAtSync);
        Assert.Equal(externalNewer, mapping.ExternalUpdatedAtAtSync);

        client.Verify(
            c =>
                c.UpdateTodoListAsync(
                    It.IsAny<string>(),
                    It.IsAny<UpdateExternalTodoListRequest>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task PullTodoListsAsync_MappedLocalNewer_PatchesExternal()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var localNewer = snapshot.AddMinutes(5);
        var externalAfterPatch = localNewer.AddSeconds(1);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Local renamed",
                UpdatedAt = localNewer,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ExternalListAt("ext-1", "1", "Old external name", snapshot) });
        client
            .Setup(c =>
                c.UpdateTodoListAsync(
                    "ext-1",
                    It.Is<UpdateExternalTodoListRequest>(r => r.Name == "Local renamed"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(ExternalListAt("ext-1", "1", "Local renamed", externalAfterPatch));

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var local = await ctx.TodoList.FindAsync(1L);
        Assert.Equal("Local renamed", local!.Name);

        var mapping = await ctx.SyncMappings.SingleAsync();
        Assert.Equal(localNewer, mapping.LocalUpdatedAtAtSync);
        Assert.Equal(externalAfterPatch, mapping.ExternalUpdatedAtAtSync);

        client.Verify(
            c =>
                c.UpdateTodoListAsync(
                    "ext-1",
                    It.IsAny<UpdateExternalTodoListRequest>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task PullTodoListsAsync_MappedBothChanged_ExternalWinsOnTimestamp()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var localNewer = snapshot.AddMinutes(2);
        var externalEvenNewer = snapshot.AddMinutes(5);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Local edit",
                UpdatedAt = localNewer,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new[] { ExternalListAt("ext-1", "1", "External edit", externalEvenNewer) }
            );

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var local = await ctx.TodoList.FindAsync(1L);
        Assert.Equal("External edit", local!.Name);
        Assert.Equal(externalEvenNewer, local.UpdatedAt);

        client.Verify(
            c =>
                c.UpdateTodoListAsync(
                    It.IsAny<string>(),
                    It.IsAny<UpdateExternalTodoListRequest>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task PullTodoListsAsync_MappedBothChanged_LocalWinsOnTimestamp()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var externalNewer = snapshot.AddMinutes(2);
        var localEvenNewer = snapshot.AddMinutes(5);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Local edit",
                UpdatedAt = localEvenNewer,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ExternalListAt("ext-1", "1", "External edit", externalNewer) });
        client
            .Setup(c =>
                c.UpdateTodoListAsync(
                    "ext-1",
                    It.IsAny<UpdateExternalTodoListRequest>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(ExternalListAt("ext-1", "1", "Local edit", localEvenNewer));

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var local = await ctx.TodoList.FindAsync(1L);
        Assert.Equal("Local edit", local!.Name);

        client.Verify(
            c =>
                c.UpdateTodoListAsync(
                    "ext-1",
                    It.Is<UpdateExternalTodoListRequest>(r => r.Name == "Local edit"),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task PullTodoListsAsync_MappedBothChanged_TieGoesToExternal()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var bothChangedAt = snapshot.AddMinutes(5);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Local tie",
                UpdatedAt = bothChangedAt,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ExternalListAt("ext-1", "1", "External tie", bothChangedAt) });

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var local = await ctx.TodoList.FindAsync(1L);
        Assert.Equal("External tie", local!.Name);

        client.Verify(
            c =>
                c.UpdateTodoListAsync(
                    It.IsAny<string>(),
                    It.IsAny<UpdateExternalTodoListRequest>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task PullTodoListsAsync_MappedNoChanges_BumpsLastSyncedOnly()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = DateTime.UtcNow.AddHours(-1);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Stable",
                UpdatedAt = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ExternalListAt("ext-1", "1", "Stable", snapshot) });

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var local = await ctx.TodoList.FindAsync(1L);
        Assert.Equal("Stable", local!.Name);
        Assert.Equal(snapshot, local.UpdatedAt);

        var mapping = await ctx.SyncMappings.SingleAsync();
        Assert.Equal(snapshot, mapping.LocalUpdatedAtAtSync);
        Assert.Equal(snapshot, mapping.ExternalUpdatedAtAtSync);
        Assert.True(mapping.LastSyncedAt > snapshot);

        client.Verify(
            c =>
                c.UpdateTodoListAsync(
                    It.IsAny<string>(),
                    It.IsAny<UpdateExternalTodoListRequest>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task PullTodoListsAsync_OneOfThreeFails_StatusPartial()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var localNewer = snapshot.AddMinutes(5);

        // Mapped TodoList that needs PATCH (local wins). The patch will throw.
        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Patches will fail",
                UpdatedAt = localNewer,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-fail",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new[]
                {
                    ExternalListAt("ext-fail", "1", "Old", snapshot),
                    ExternalListAt("ext-new1", null, "Created from external", snapshot),
                    ExternalListAt("ext-new2", null, "Another from external", snapshot),
                }
            );
        client
            .Setup(c =>
                c.UpdateTodoListAsync(
                    "ext-fail",
                    It.IsAny<UpdateExternalTodoListRequest>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(
                new ExternalApiException("nope", 503, "PATCH", "todolists/ext-fail", null)
            );

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(3, result.Total);
        Assert.Equal(2, result.Pushed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(SyncRunStatus.Partial, result.Status);

        // The two new creations are persisted; the mapped one stays intact (PATCH failed).
        Assert.Equal(3, ctx.TodoList.Count());
        Assert.Equal(3, ctx.SyncMappings.Count());
    }

    [Fact]
    public async Task PullTodoListsAsync_GetThrows_StatusFailedAndZeroProcessed()
    {
        await using var ctx = new TodoContext(NewDbOptions());

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExternalApiException("API down", 503, "GET", "todolists", null));

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Failed, result.Status);
        Assert.Equal(0, result.Pushed);
        Assert.Equal(0, result.Failed);

        var run = Assert.Single(ctx.SyncRuns);
        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task PushTodoListsAsync_AllFail_StatusFailed()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        ctx.TodoList.Add(new TodoApi.Models.TodoList { Id = 1, Name = "L1" });
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.CreateTodoListAsync(
                    It.IsAny<CreateExternalTodoListRequest>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new ExternalApiException("nope", 500, "POST", "todolists", null));

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Failed, result.Status);
        Assert.Empty(ctx.SyncMappings);
        Assert.Single(ctx.SyncRuns);
    }

    [Fact]
    public async Task PushTodoListsAsync_OrphanListMapping_DeletesExternalAndRemovesMapping()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        // Orphan mapping: LocalId points to a TodoList that does not exist.
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 999,
                ExternalId = "ext-orphan",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.DeleteTodoListAsync("ext-orphan", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        Assert.Empty(ctx.SyncMappings);

        client.Verify(
            c => c.DeleteTodoListAsync("ext-orphan", It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PushTodoListsAsync_OrphanListMappingExternal404_TreatsAsResolved()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 999,
                ExternalId = "ext-already-gone",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.DeleteTodoListAsync("ext-already-gone", It.IsAny<CancellationToken>()))
            .ThrowsAsync(
                new ExternalApiException(
                    "missing",
                    404,
                    "DELETE",
                    "todolists/ext-already-gone",
                    null
                )
            );

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        Assert.Empty(ctx.SyncMappings);
    }

    [Fact]
    public async Task PushTodoListsAsync_OrphanListMappingExternal500_FailsAndKeepsMapping()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 999,
                ExternalId = "ext-flaky",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.DeleteTodoListAsync("ext-flaky", It.IsAny<CancellationToken>()))
            .ThrowsAsync(
                new ExternalApiException("boom", 500, "DELETE", "todolists/ext-flaky", null)
            );

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(0, result.Pushed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(SyncRunStatus.Failed, result.Status);

        // Mapping persists to be retried on the next tick.
        var mapping = Assert.Single(ctx.SyncMappings);
        Assert.Equal("ext-flaky", mapping.ExternalId);
    }

    [Fact]
    public async Task PushTodoListsAsync_OrphanListMappingWithChildItemMappings_DeletesListMappingChildMappingsRemain()
    {
        // The push-list does NOT touch orphan child item mappings. That is cleaned up by the next
        // push-item (with 404 grace because the list-DELETE already cascaded externally).
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        ctx.SyncMappings.AddRange(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 100,
                ExternalId = "ext-list-100",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 1000,
                ExternalId = "ext-item-1000",
                ParentExternalId = "ext-list-100",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 1001,
                ExternalId = "ext-item-1001",
                ParentExternalId = "ext-list-100",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.DeleteTodoListAsync("ext-list-100", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        // Only the TodoList mapping was deleted.
        Assert.Empty(ctx.SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoList).ToList());
        Assert.Equal(2, ctx.SyncMappings.Count(m => m.EntityType == SyncEntityType.TodoListItem));

        // The service does not call DeleteTodoItemAsync — that is the push-item's responsibility.
        client.Verify(
            c =>
                c.DeleteTodoItemAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task PushTodoListsAsync_NoOrphans_DoesNotCallDelete()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        // A list with its complete mapping (not-orphan).
        ctx.TodoList.Add(new TodoApi.Models.TodoList { Id = 1, Name = "Synced" });
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-1",
                LastSyncedAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        // Strict mock: if the service calls DeleteTodoListAsync, this test fails.

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.Pushed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PushTodoListsAsync_MixedCreateAndOrphan_BothProcessed()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        // A new list (will be POST) + an orphan mapping (will be DELETE).
        ctx.TodoList.Add(new TodoApi.Models.TodoList { Id = 5, Name = "New" });
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 999,
                ExternalId = "ext-orphan",
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
                c.CreateTodoListAsync(
                    It.IsAny<CreateExternalTodoListRequest>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (CreateExternalTodoListRequest req, Guid _, CancellationToken __) =>
                    new ExternalTodoList(
                        Id: $"ext-{req.SourceId}",
                        SourceId: req.SourceId,
                        Name: req.Name,
                        CreatedAt: DateTime.UtcNow,
                        UpdatedAt: DateTime.UtcNow,
                        Items: Array.Empty<ExternalTodoItem>()
                    )
            );
        client
            .Setup(c => c.DeleteTodoListAsync("ext-orphan", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        // Orphan mapping deleted, new list mapping persisted.
        var mapping = Assert.Single(ctx.SyncMappings);
        Assert.Equal(5L, mapping.LocalId);
        Assert.Equal("ext-5", mapping.ExternalId);

        client.Verify(
            c =>
                c.CreateTodoListAsync(
                    It.IsAny<CreateExternalTodoListRequest>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        client.Verify(
            c => c.DeleteTodoListAsync("ext-orphan", It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PullTodoListsAsync_MappedExternalDisappeared_DeletesLocalListAndAllMappings()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Will be deleted",
                UpdatedAt = snapshot,
                Items = new List<TodoApi.Models.TodoListItem>
                {
                    new()
                    {
                        Id = 100,
                        Description = "Child 1",
                        IsCompleted = false,
                        TodoListId = 1,
                        UpdatedAt = snapshot,
                    },
                    new()
                    {
                        Id = 101,
                        Description = "Child 2",
                        IsCompleted = true,
                        TodoListId = 1,
                        UpdatedAt = snapshot,
                    },
                },
            }
        );
        ctx.SyncMappings.AddRange(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 100,
                ExternalId = "ext-item-100",
                ParentExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = 101,
                ExternalId = "ext-item-101",
                ParentExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ExternalTodoList>());

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, mappedExternals) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        Assert.Empty(ctx.TodoList);
        Assert.Empty(ctx.TodoListItem);
        Assert.Empty(ctx.SyncMappings);

        // The 2nd pass does NOT return this list to the item pull (the list no longer exists).
        Assert.Empty(mappedExternals);
    }

    [Fact]
    public async Task PullTodoListsAsync_MappedExternalDisappearedWithUnsyncedLocalEdits_LogsWarningAndDeletes()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);
        var localEditedAt = snapshot.AddMinutes(5);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Local edit lost",
                UpdatedAt = localEditedAt,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ExternalTodoList>());

        var loggerMock = new Mock<ILogger<TodoListSyncService>>();

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            loggerMock.Object
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);
        Assert.Empty(ctx.TodoList);
        Assert.Empty(ctx.SyncMappings);

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
    public async Task PullTodoListsAsync_MappedExternalDisappearedWithLocalUpdatedAtAtSyncNull_DoesNotLogWarning()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var localUpdatedAt = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "No snapshot",
                UpdatedAt = localUpdatedAt,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-1",
                LastSyncedAt = DateTime.UtcNow.AddHours(-1),
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = null,
                ExternalUpdatedAtAtSync = null,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ExternalTodoList>());

        var loggerMock = new Mock<ILogger<TodoListSyncService>>();

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            loggerMock.Object
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);
        Assert.Empty(ctx.TodoList);
        Assert.Empty(ctx.SyncMappings);

        // CurrentLocalUpdatedAt > MinValue is strictly true when local.UpdatedAt > MinValue
        // but LocalUpdatedAtAtSync == null is treated as MinValue. To detect "edits since
        // last sync" we require local.UpdatedAt > LocalUpdatedAtAtSync ?? MinValue, which is
        // true here — but the decision is: with null treated as MinValue, avoid the noise of
        // the Warning for legacy mappings that never received a snapshot. Therefore, NO Warning.
        loggerMock.Verify(
            l =>
                l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task PullTodoListsAsync_MappedExternalDisappearedNoLocalChanges_DeletesSilently()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Stable",
                UpdatedAt = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ExternalTodoList>());

        var loggerMock = new Mock<ILogger<TodoListSyncService>>();

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            loggerMock.Object
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);
        Assert.Empty(ctx.TodoList);
        Assert.Empty(ctx.SyncMappings);

        loggerMock.Verify(
            l =>
                l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task PullTodoListsAsync_NoMissingExternals_DoesNotInvokeDeletePass()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Still there",
                UpdatedAt = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ExternalListAt("ext-1", "1", "Still there", snapshot) });

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, mappedExternals) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);
        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Pushed);

        Assert.Single(ctx.TodoList);
        Assert.Single(ctx.SyncMappings);
        Assert.Single(mappedExternals);
    }

    [Fact]
    public async Task PullTodoListsAsync_MultipleMissingExternals_AllDeleted()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        ctx.TodoList.AddRange(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "First",
                UpdatedAt = snapshot,
            },
            new TodoApi.Models.TodoList
            {
                Id = 2,
                Name = "Second",
                UpdatedAt = snapshot,
            },
            new TodoApi.Models.TodoList
            {
                Id = 3,
                Name = "Survivor",
                UpdatedAt = snapshot,
            }
        );
        ctx.SyncMappings.AddRange(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 2,
                ExternalId = "ext-2",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 3,
                ExternalId = "ext-3",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        // Only ext-3 remains in the GET; ext-1 and ext-2 disappeared.
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ExternalListAt("ext-3", "3", "Survivor", snapshot) });

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);
        Assert.Equal(3, result.Total); // 1 reconcile + 2 deletes
        Assert.Equal(3, result.Pushed);
        Assert.Equal(0, result.Failed);

        var survivor = Assert.Single(ctx.TodoList);
        Assert.Equal(3L, survivor.Id);
        var survivorMapping = Assert.Single(ctx.SyncMappings);
        Assert.Equal("ext-3", survivorMapping.ExternalId);
    }

    [Fact]
    public async Task PullTodoListsAsync_MissingExternalAndCreateExternal_BothProcessed()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        // Mapped local list whose external counterpart will be missing.
        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Will be deleted",
                UpdatedAt = snapshot,
            }
        );
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-disappeared",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        // GET returns only a new list (not the mapped one).
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ExternalListAt("ext-new", null, "Brand new", snapshot) });

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Total); // 1 fetched + 1 deleted
        Assert.Equal(2, result.Pushed);

        // Old local deleted, new local created.
        var local = Assert.Single(ctx.TodoList);
        Assert.Equal("Brand new", local.Name);

        var mapping = Assert.Single(ctx.SyncMappings);
        Assert.Equal("ext-new", mapping.ExternalId);
    }

    [Fact]
    public async Task PullTodoListsAsync_MissingExternalApplyDeleteThrows_StatusPartial()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        ctx.TodoList.AddRange(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "First",
                UpdatedAt = snapshot,
            },
            new TodoApi.Models.TodoList
            {
                Id = 2,
                Name = "Second",
                UpdatedAt = snapshot,
            }
        );
        ctx.SyncMappings.AddRange(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 1,
                ExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 2,
                ExternalId = "ext-2",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
                LocalUpdatedAtAtSync = snapshot,
                ExternalUpdatedAtAtSync = snapshot,
            }
        );
        await ctx.SaveChangesAsync();

        // Mock the db wrapper so ApplyExternalDeleteListAsync throws for one specific list
        // but the other delete proceeds normally. Use a wrapper that intercepts the call.
        var dbMock = new Mock<TodoApi.Sync.Data.ISyncDbContext>();
        dbMock.SetupGet(d => d.SyncMappings).Returns(ctx.SyncMappings);
        dbMock.SetupGet(d => d.SyncRuns).Returns(ctx.SyncRuns);
        dbMock
            .Setup(d => d.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => ctx.SaveChangesAsync(ct));
        dbMock
            .Setup(d => d.GetMappedTodoListsAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => ctx.GetMappedTodoListsAsync(ct));
        dbMock
            .Setup(d =>
                d.FindUnmappedLocalByIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())
            )
            .Returns(
                (long id, CancellationToken ct) =>
                    ((TodoApi.Sync.Data.ISyncDbContext)ctx).FindUnmappedLocalByIdAsync(id, ct)
            );
        dbMock
            .Setup(d =>
                d.ApplyExternalDeleteListAsync(
                    It.Is<ApplyExternalDeleteListPlan>(p => p.LocalListId == 2),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new InvalidOperationException("DB write failed"));
        dbMock
            .Setup(d =>
                d.ApplyExternalDeleteListAsync(
                    It.Is<ApplyExternalDeleteListPlan>(p => p.LocalListId == 1),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (ApplyExternalDeleteListPlan plan, CancellationToken ct) =>
                    ((TodoApi.Sync.Data.ISyncDbContext)ctx).ApplyExternalDeleteListAsync(plan, ct)
            );

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ExternalTodoList>());

        var sut = new TodoListSyncService(
            dbMock.Object,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(2, result.Total);
        Assert.Equal(1, result.Pushed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(SyncRunStatus.Partial, result.Status);
    }

    [Fact]
    public async Task PushTodoListsAsync_MultipleOrphansOneFails_StatusPartial()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var snapshot = new DateTime(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

        ctx.SyncMappings.AddRange(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 901,
                ExternalId = "ext-1",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 902,
                ExternalId = "ext-2",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
            },
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 903,
                ExternalId = "ext-3",
                LastSyncedAt = snapshot,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.DeleteTodoListAsync("ext-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        client
            .Setup(c => c.DeleteTodoListAsync("ext-2", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExternalApiException("boom", 503, "DELETE", "todolists/ext-2", null));
        client
            .Setup(c => c.DeleteTodoListAsync("ext-3", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(3, result.Total);
        Assert.Equal(2, result.Pushed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(SyncRunStatus.Partial, result.Status);

        // ext-2 remains; ext-1 and ext-3 were deleted.
        var remaining = Assert.Single(ctx.SyncMappings);
        Assert.Equal("ext-2", remaining.ExternalId);
    }

    [Fact]
    public async Task PullTodoListsAsync_ExternalSourceIdPointsToDeletedLocal_FallsToCaseC()
    {
        // Edge case (NOTES.md line 117): external entry has source_id="99" but local
        // Id=99 was deleted and its mapping already cleaned up. FindUnmappedLocalByIdAsync
        // returns null, so the pull falls to CASO C and creates a new local with a
        // different (auto-incremented) Id.
        await using var ctx = new TodoContext(NewDbOptions());

        // Seed an unrelated list so the auto-increment starts above 1.
        ctx.TodoList.Add(
            new TodoApi.Models.TodoList
            {
                Id = 1,
                Name = "Survivor",
                UpdatedAt = DateTime.UtcNow,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetTodoListsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ExternalListAt("ext-zz", "99", "Recovered", DateTime.UtcNow) });

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var (result, _) = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var lists = ctx.TodoList.OrderBy(l => l.Id).ToList();
        Assert.Equal(2, lists.Count);

        var recovered = lists.Single(l => l.Name == "Recovered");
        Assert.NotEqual(99L, recovered.Id);

        var mapping = Assert.Single(ctx.SyncMappings.Where(m => m.ExternalId == "ext-zz"));
        Assert.Equal(recovered.Id, mapping.LocalId);
    }

    [Fact]
    public async Task PushTodoListsAsync_OutboxCreateEvent_PostsAndMarksProcessed()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        ctx.TodoList.Add(new TodoApi.Models.TodoList { Id = 42, Name = "From outbox" });
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoList,
                EntityId = 42,
                Operation = OutboxOperation.Create,
                OccurredAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c =>
                c.CreateTodoListAsync(
                    It.IsAny<CreateExternalTodoListRequest>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new ExternalTodoList(
                    Id: "ext-42",
                    SourceId: "42",
                    Name: "From outbox",
                    CreatedAt: DateTime.UtcNow,
                    UpdatedAt: DateTime.UtcNow,
                    Items: Array.Empty<ExternalTodoItem>()
                )
            );

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var mapping = Assert.Single(ctx.SyncMappings);
        Assert.Equal(42L, mapping.LocalId);
        Assert.Equal("ext-42", mapping.ExternalId);

        var evt = Assert.Single(ctx.OutboxEvents);
        Assert.NotNull(evt.ProcessedAt);
    }

    [Fact]
    public async Task PushTodoListsAsync_OutboxCreateEvent_AlreadyMapped_SkipsPostAndMarksProcessed()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        ctx.TodoList.Add(new TodoApi.Models.TodoList { Id = 5, Name = "Adopted by pull" });
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 5,
                ExternalId = "ext-5",
                LastSyncedAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoList,
                EntityId = 5,
                Operation = OutboxOperation.Create,
                OccurredAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        var evt = Assert.Single(ctx.OutboxEvents);
        Assert.NotNull(evt.ProcessedAt);
        Assert.Single(ctx.SyncMappings);
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PushTodoListsAsync_OutboxCreateEvent_LocalDeletedMidFlight_MarksProcessedNoOp()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoList,
                EntityId = 99,
                Operation = OutboxOperation.Create,
                OccurredAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);
        var evt = Assert.Single(ctx.OutboxEvents);
        Assert.NotNull(evt.ProcessedAt);
        Assert.Empty(ctx.SyncMappings);
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PushTodoListsAsync_OutboxDeleteEvent_DeletesAndCleansMapping()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 7,
                ExternalId = "ext-7",
                LastSyncedAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoList,
                EntityId = 7,
                Operation = OutboxOperation.Delete,
                OccurredAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.DeleteTodoListAsync("ext-7", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Empty(ctx.SyncMappings);
        var evt = Assert.Single(ctx.OutboxEvents);
        Assert.NotNull(evt.ProcessedAt);
        client.Verify(
            c => c.DeleteTodoListAsync("ext-7", It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PushTodoListsAsync_OutboxDeleteEvent_404Grace_StillCleansMapping()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 8,
                ExternalId = "ext-8",
                LastSyncedAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoList,
                EntityId = 8,
                Operation = OutboxOperation.Delete,
                OccurredAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);
        client
            .Setup(c => c.DeleteTodoListAsync("ext-8", It.IsAny<CancellationToken>()))
            .ThrowsAsync(
                new ExternalApiException("not found", 404, "DELETE", "/todolists/ext-8", null)
            );

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Empty(ctx.SyncMappings);
        var evt = Assert.Single(ctx.OutboxEvents);
        Assert.NotNull(evt.ProcessedAt);
    }

    [Fact]
    public async Task PushTodoListsAsync_OutboxDeleteEvent_NoMapping_MarksProcessedNoOp()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoList,
                EntityId = 200,
                Operation = OutboxOperation.Delete,
                OccurredAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        var evt = Assert.Single(ctx.OutboxEvents);
        Assert.NotNull(evt.ProcessedAt);
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PushTodoListsAsync_OutboxUpdateEvent_NoOpInSlice6_MarksProcessed()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        ctx.TodoList.Add(new TodoApi.Models.TodoList { Id = 11, Name = "Mapped" });
        ctx.SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = 11,
                ExternalId = "ext-11",
                LastSyncedAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                EntityType = SyncEntityType.TodoList,
                EntityId = 11,
                Operation = OutboxOperation.Update,
                OccurredAt = DateTime.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
            }
        );
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions()),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Failed);
        var evt = Assert.Single(ctx.OutboxEvents);
        Assert.NotNull(evt.ProcessedAt);
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PushTodoListsAsync_OutboxBatchSize_RespectsLimit()
    {
        // Seed 5 already-mapped lists with pending Create events. Phase A dispatch sees the
        // mapping and marks processed without POST (slice 6 idempotent path). Phase B's legacy
        // anti-join finds no unmapped lists, so the only mechanism that touches events is the
        // Phase A drain capped by OutboxBatchSize.
        await using var ctx = new TodoContext(NewDbOptions());
        for (long i = 1; i <= 5; i++)
        {
            ctx.TodoList.Add(new TodoApi.Models.TodoList { Id = i, Name = $"List {i}" });
            ctx.SyncMappings.Add(
                new SyncMapping
                {
                    EntityType = SyncEntityType.TodoList,
                    LocalId = i,
                    ExternalId = $"ext-{i}",
                    LastSyncedAt = DateTime.UtcNow,
                    IdempotencyKey = Guid.NewGuid(),
                }
            );
            ctx.OutboxEvents.Add(
                new OutboxEvent
                {
                    EntityType = SyncEntityType.TodoList,
                    EntityId = i,
                    Operation = OutboxOperation.Create,
                    OccurredAt = DateTime.UtcNow.AddSeconds(i),
                    IdempotencyKey = Guid.NewGuid(),
                }
            );
        }
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions { OutboxBatchSize = 2 }),
            NullLogger<TodoListSyncService>.Instance
        );

        await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(2, ctx.OutboxEvents.Count(e => e.ProcessedAt != null));
        Assert.Equal(3, ctx.OutboxEvents.Count(e => e.ProcessedAt == null));
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PushTodoListsAsync_OutboxBatchSizeZero_DrainsNothingAndDoesNotCrash()
    {
        // Boundary: OutboxBatchSize=0 means Take(0), Phase A drains nothing. Pending
        // events stay pending for the next tick once configuration is corrected. We use
        // already-mapped lists so Phase B's anti-join also no-ops, isolating the
        // configuration boundary as the only mechanism in play.
        await using var ctx = new TodoContext(NewDbOptions());
        for (long i = 1; i <= 3; i++)
        {
            ctx.TodoList.Add(new TodoApi.Models.TodoList { Id = i, Name = $"List {i}" });
            ctx.SyncMappings.Add(
                new SyncMapping
                {
                    EntityType = SyncEntityType.TodoList,
                    LocalId = i,
                    ExternalId = $"ext-{i}",
                    LastSyncedAt = DateTime.UtcNow,
                    IdempotencyKey = Guid.NewGuid(),
                }
            );
            ctx.OutboxEvents.Add(
                new OutboxEvent
                {
                    EntityType = SyncEntityType.TodoList,
                    EntityId = i,
                    Operation = OutboxOperation.Create,
                    OccurredAt = DateTime.UtcNow.AddSeconds(i),
                    IdempotencyKey = Guid.NewGuid(),
                }
            );
        }
        await ctx.SaveChangesAsync();

        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            Options.Create(new SyncOptions { OutboxBatchSize = 0 }),
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);
        Assert.Equal(0, ctx.OutboxEvents.Count(e => e.ProcessedAt != null));
        Assert.Equal(3, ctx.OutboxEvents.Count(e => e.ProcessedAt == null));
        client.VerifyNoOtherCalls();
    }
}
