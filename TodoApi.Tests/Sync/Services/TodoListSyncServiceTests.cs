using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

        var client = new Mock<IExternalTodoListClient>();
        client
            .Setup(c =>
                c.CreateTodoListAsync(
                    It.IsAny<CreateExternalTodoListRequest>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (CreateExternalTodoListRequest req, CancellationToken _) =>
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
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }
}
