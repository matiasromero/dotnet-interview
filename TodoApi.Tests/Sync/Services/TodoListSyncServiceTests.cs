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

        // Mapping previo intacto + 2 nuevos.
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
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PullTodoListsAsync(CancellationToken.None);

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
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PullTodoListsAsync(CancellationToken.None);

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
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        Assert.Single(ctx.TodoList); // sigue siendo solo el orphan, no se duplicó
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
            .ReturnsAsync(new[] { ExternalListAt("ext-1", "1", "External renamed", externalNewer) });

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PullTodoListsAsync(CancellationToken.None);

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
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PullTodoListsAsync(CancellationToken.None);

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
            .ReturnsAsync(new[] { ExternalListAt("ext-1", "1", "External edit", externalEvenNewer) });

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PullTodoListsAsync(CancellationToken.None);

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
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PullTodoListsAsync(CancellationToken.None);

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
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PullTodoListsAsync(CancellationToken.None);

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
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PullTodoListsAsync(CancellationToken.None);

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
            .ThrowsAsync(new ExternalApiException("nope", 503, "PATCH", "todolists/ext-fail", null));

        var sut = new TodoListSyncService(
            ctx,
            client.Object,
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PullTodoListsAsync(CancellationToken.None);

        Assert.Equal(3, result.Total);
        Assert.Equal(2, result.Pushed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(SyncRunStatus.Partial, result.Status);

        // Las dos creaciones nuevas se persisten; la mapeada queda intacta (PATCH falló).
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
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PullTodoListsAsync(CancellationToken.None);

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
            NullLogger<TodoListSyncService>.Instance
        );

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Failed, result.Status);
        Assert.Empty(ctx.SyncMappings);
        Assert.Single(ctx.SyncRuns);
    }
}
