using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TodoApi.Sync.External;
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
}
