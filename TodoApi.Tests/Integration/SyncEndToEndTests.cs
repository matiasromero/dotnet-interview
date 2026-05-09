using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TodoApi.Dtos;
using TodoApi.Sync.Models;
using TodoApi.Sync.Services;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace TodoApi.Tests.Integration;

public class SyncEndToEndTests
{
    private static readonly DateTime BaseTime = new(2026, 5, 9, 10, 0, 0, DateTimeKind.Utc);

    private static string Iso(DateTime dt) =>
        dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    [Fact]
    public async Task Push_NewTodoListWithoutItems_PostsToExternalAndPersistsMapping()
    {
        var externalListJson = $$"""
            {
              "id": "ext-1",
              "source_id": "1",
              "name": "Groceries",
              "created_at": "{{Iso(BaseTime)}}",
              "updated_at": "{{Iso(BaseTime)}}",
              "items": []
            }
            """;

        using var wm = new WireMockFixture();
        wm.Server.Given(Request.Create().WithPath("/todolists").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBody(externalListJson));
        // GET must echo what was just pushed; otherwise the pull phase detects
        // "mapped local missing from external GET" and cascade-deletes the local
        // (slice 4 mirror-policy).
        wm.Server.Given(Request.Create().WithPath("/todolists").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($"[{externalListJson}]"));

        await using var factory = new TodoApiWebApplicationFactory(wm.Url);
        var client = factory.CreateClient();

        var createResp = await client.PostAsJsonAsync("/api/todolists", new { name = "Groceries" });
        createResp.EnsureSuccessStatusCode();

        var runResp = await client.PostAsync("/api/sync/run", content: null);
        runResp.EnsureSuccessStatusCode();
        var body = await runResp.Content.ReadFromJsonAsync<SyncRunResponse>();

        Assert.Equal(1, body!.ListPush.Pushed);
        Assert.Equal(SyncRunStatus.Succeeded, body.ListPush.Status);

        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TodoContext>();
        var mapping = await db.SyncMappings.SingleAsync();
        Assert.Equal(SyncEntityType.TodoList, mapping.EntityType);
        Assert.Equal(1, mapping.LocalId);
        Assert.Equal("ext-1", mapping.ExternalId);
        Assert.NotEqual(Guid.Empty, mapping.IdempotencyKey);

        var posts = wm
            .Server.LogEntries.Where(e =>
                e.RequestMessage.Path == "/todolists" && e.RequestMessage.Method == "POST"
            )
            .ToList();
        Assert.Single(posts);
        var post = posts.Single();
        Assert.Contains("Idempotency-Key", post.RequestMessage.Headers!.Keys);
        var sentJson = JsonDocument.Parse(post.RequestMessage.Body!).RootElement;
        Assert.Equal("1", sentJson.GetProperty("source_id").GetString());
        Assert.Equal("Groceries", sentJson.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Push_TodoListWithEmbeddedItems_PersistsListAndItemMappings()
    {
        var externalListJson = $$"""
            {
              "id": "ext-1",
              "source_id": "1",
              "name": "Groceries",
              "created_at": "{{Iso(BaseTime)}}",
              "updated_at": "{{Iso(BaseTime)}}",
              "items": [
                {
                  "id": "ext-it-1",
                  "source_id": "1",
                  "description": "Milk",
                  "completed": false,
                  "created_at": "{{Iso(BaseTime)}}",
                  "updated_at": "{{Iso(BaseTime)}}"
                },
                {
                  "id": "ext-it-2",
                  "source_id": "2",
                  "description": "Bread",
                  "completed": false,
                  "created_at": "{{Iso(BaseTime)}}",
                  "updated_at": "{{Iso(BaseTime)}}"
                }
              ]
            }
            """;

        using var wm = new WireMockFixture();
        wm.Server.Given(Request.Create().WithPath("/todolists").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBody(externalListJson));
        wm.Server.Given(Request.Create().WithPath("/todolists").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($"[{externalListJson}]"));

        await using var factory = new TodoApiWebApplicationFactory(wm.Url);
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/todolists", new { name = "Groceries" });
        await client.PostAsJsonAsync("/api/todolists/1/todoitems", new { description = "Milk" });
        await client.PostAsJsonAsync("/api/todolists/1/todoitems", new { description = "Bread" });

        var runResp = await client.PostAsync("/api/sync/run", content: null);
        runResp.EnsureSuccessStatusCode();

        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TodoContext>();
        var listMapping = await db.SyncMappings.SingleAsync(m =>
            m.EntityType == SyncEntityType.TodoList
        );
        Assert.Equal("ext-1", listMapping.ExternalId);

        var itemMappings = await db
            .SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoListItem)
            .OrderBy(m => m.LocalId)
            .ToListAsync();
        Assert.Equal(2, itemMappings.Count);
        Assert.Equal("ext-it-1", itemMappings[0].ExternalId);
        Assert.Equal("ext-1", itemMappings[0].ParentExternalId);
        Assert.Equal("ext-it-2", itemMappings[1].ExternalId);
        Assert.Equal("ext-1", itemMappings[1].ParentExternalId);
    }

    [Fact]
    public async Task Pull_NewExternalListWithoutSourceId_CreatesLocalAndMapping()
    {
        using var wm = new WireMockFixture();
        wm.Server.Given(Request.Create().WithPath("/todolists").UsingGet())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithBody(
                        $$"""
                        [
                          {
                            "id": "ext-99",
                            "source_id": null,
                            "name": "FromExternal",
                            "created_at": "{{Iso(BaseTime)}}",
                            "updated_at": "{{Iso(BaseTime)}}",
                            "items": []
                          }
                        ]
                        """
                    )
            );

        await using var factory = new TodoApiWebApplicationFactory(wm.Url);
        var client = factory.CreateClient();

        await client.PostAsync("/api/sync/run", content: null);

        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TodoContext>();
        var local = await db.TodoList.SingleAsync();
        Assert.Equal("FromExternal", local.Name);
        Assert.Equal(BaseTime, local.UpdatedAt);

        var mapping = await db.SyncMappings.SingleAsync();
        Assert.Equal(local.Id, mapping.LocalId);
        Assert.Equal("ext-99", mapping.ExternalId);

        Assert.Empty(
            wm.Server.LogEntries.Where(e =>
                e.RequestMessage.Path == "/todolists" && e.RequestMessage.Method == "POST"
            )
        );
    }

    [Fact]
    public async Task Pull_OrphanLocalWithSourceIdMatching_AdoptsAsMappingWithoutDuplicating()
    {
        using var wm = new WireMockFixture();
        // The push phase will see Id=1 as unmapped and try to POST it. Stub the POST to
        // return 409 Conflict (simulating "external rejects because already exists" — the
        // realistic post-crash state). The pull phase then sees the external entry with
        // source_id="1", finds local Id=1 unmapped, and adopts it.
        wm.Server.Given(Request.Create().WithPath("/todolists").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(409).WithBody("{}"));
        wm.Server.Given(Request.Create().WithPath("/todolists").UsingGet())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithBody(
                        $$"""
                        [
                          {
                            "id": "ext-1",
                            "source_id": "1",
                            "name": "Groceries",
                            "created_at": "{{Iso(BaseTime)}}",
                            "updated_at": "{{Iso(BaseTime)}}",
                            "items": []
                          }
                        ]
                        """
                    )
            );

        await using var factory = new TodoApiWebApplicationFactory(wm.Url);

        // Seed: local list Id=1 without mapping (simulates crash mid-write of slice 1)
        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TodoContext>();
            db.TodoList.Add(
                new TodoApi.Models.TodoList
                {
                    Id = 1,
                    Name = "Groceries",
                    UpdatedAt = BaseTime,
                }
            );
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        await client.PostAsync("/api/sync/run", content: null);

        using var verifyScope = factory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TodoContext>();
        var lists = await verifyDb.TodoList.ToListAsync();
        Assert.Single(lists);
        Assert.Equal(1L, lists[0].Id);

        var mapping = await verifyDb.SyncMappings.SingleAsync();
        Assert.Equal(1L, mapping.LocalId);
        Assert.Equal("ext-1", mapping.ExternalId);
        // Mapping was created via pull-adoption (CASO B), not via push.
        // The push attempt failed with 409, leaving the local unmapped, then the pull
        // saw source_id="1" matching the unmapped local and created the mapping.
    }

    [Fact]
    public async Task Pull_LastWriteWinsExternalNewer_OverwritesLocal()
    {
        var snapshot = BaseTime;
        var externalNewer = snapshot.AddMinutes(5);

        using var wm = new WireMockFixture();
        wm.Server.Given(Request.Create().WithPath("/todolists").UsingGet())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithBody(
                        $$"""
                        [
                          {
                            "id": "ext-1",
                            "source_id": "1",
                            "name": "Renamed externally",
                            "created_at": "{{Iso(snapshot)}}",
                            "updated_at": "{{Iso(externalNewer)}}",
                            "items": []
                          }
                        ]
                        """
                    )
            );

        await using var factory = new TodoApiWebApplicationFactory(wm.Url);
        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TodoContext>();
            db.TodoList.Add(
                new TodoApi.Models.TodoList
                {
                    Id = 1,
                    Name = "Old name",
                    UpdatedAt = snapshot,
                }
            );
            db.SyncMappings.Add(
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
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        await client.PostAsync("/api/sync/run", content: null);

        using var verifyScope = factory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TodoContext>();
        var local = await verifyDb.TodoList.SingleAsync();
        Assert.Equal("Renamed externally", local.Name);
        Assert.Equal(externalNewer, local.UpdatedAt);

        Assert.Empty(wm.Server.LogEntries.Where(e => e.RequestMessage.Method == "PATCH"));
    }

    [Fact]
    public async Task Pull_LastWriteWinsLocalNewer_PatchesExternal()
    {
        var snapshot = BaseTime;
        var localNewer = snapshot.AddMinutes(5);
        var externalAfterPatch = localNewer.AddSeconds(1);

        using var wm = new WireMockFixture();
        wm.Server.Given(Request.Create().WithPath("/todolists").UsingGet())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithBody(
                        $$"""
                        [
                          {
                            "id": "ext-1",
                            "source_id": "1",
                            "name": "Old external name",
                            "created_at": "{{Iso(snapshot)}}",
                            "updated_at": "{{Iso(snapshot)}}",
                            "items": []
                          }
                        ]
                        """
                    )
            );
        wm.Server.Given(Request.Create().WithPath("/todolists/ext-1").UsingPatch())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithBody(
                        $$"""
                        {
                          "id": "ext-1",
                          "source_id": "1",
                          "name": "Renamed locally",
                          "created_at": "{{Iso(snapshot)}}",
                          "updated_at": "{{Iso(externalAfterPatch)}}",
                          "items": []
                        }
                        """
                    )
            );

        await using var factory = new TodoApiWebApplicationFactory(wm.Url);
        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TodoContext>();
            db.TodoList.Add(
                new TodoApi.Models.TodoList
                {
                    Id = 1,
                    Name = "Renamed locally",
                    UpdatedAt = localNewer,
                }
            );
            db.SyncMappings.Add(
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
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        await client.PostAsync("/api/sync/run", content: null);

        var patches = wm
            .Server.LogEntries.Where(e =>
                e.RequestMessage.Path == "/todolists/ext-1" && e.RequestMessage.Method == "PATCH"
            )
            .ToList();
        Assert.Single(patches);
        var sentJson = JsonDocument.Parse(patches.Single().RequestMessage.Body!).RootElement;
        Assert.Equal("Renamed locally", sentJson.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Delete_LocalListDeleted_PropagatesDeleteToExternal()
    {
        using var wm = new WireMockFixture();
        wm.Server.Given(Request.Create().WithPath("/todolists").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));
        wm.Server.Given(Request.Create().WithPath("/todolists/ext-1").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        await using var factory = new TodoApiWebApplicationFactory(wm.Url);
        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TodoContext>();
            db.TodoList.Add(
                new TodoApi.Models.TodoList
                {
                    Id = 1,
                    Name = "ToDelete",
                    UpdatedAt = BaseTime,
                }
            );
            db.SyncMappings.Add(
                new SyncMapping
                {
                    EntityType = SyncEntityType.TodoList,
                    LocalId = 1,
                    ExternalId = "ext-1",
                    LastSyncedAt = BaseTime,
                    IdempotencyKey = Guid.NewGuid(),
                    LocalUpdatedAtAtSync = BaseTime,
                    ExternalUpdatedAtAtSync = BaseTime,
                }
            );
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var deleteResp = await client.DeleteAsync("/api/todolists/1");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        await client.PostAsync("/api/sync/run", content: null);

        var deletes = wm
            .Server.LogEntries.Where(e =>
                e.RequestMessage.Path == "/todolists/ext-1" && e.RequestMessage.Method == "DELETE"
            )
            .ToList();
        Assert.Single(deletes);

        using var verifyScope = factory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TodoContext>();
        Assert.Empty(verifyDb.SyncMappings.Where(m => m.EntityType == SyncEntityType.TodoList));
    }

    [Fact]
    public async Task Delete_ExternalListDisappears_CascadeDeletesLocalAndItems()
    {
        using var wm = new WireMockFixture();
        wm.Server.Given(Request.Create().WithPath("/todolists").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));

        await using var factory = new TodoApiWebApplicationFactory(wm.Url);
        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TodoContext>();
            db.TodoList.Add(
                new TodoApi.Models.TodoList
                {
                    Id = 1,
                    Name = "Disappearing",
                    UpdatedAt = BaseTime,
                }
            );
            db.TodoListItem.Add(
                new TodoApi.Models.TodoListItem
                {
                    Id = 10,
                    TodoListId = 1,
                    Description = "Item 1",
                    UpdatedAt = BaseTime,
                }
            );
            db.TodoListItem.Add(
                new TodoApi.Models.TodoListItem
                {
                    Id = 11,
                    TodoListId = 1,
                    Description = "Item 2",
                    UpdatedAt = BaseTime,
                }
            );
            db.SyncMappings.Add(
                new SyncMapping
                {
                    EntityType = SyncEntityType.TodoList,
                    LocalId = 1,
                    ExternalId = "ext-1",
                    LastSyncedAt = BaseTime,
                    IdempotencyKey = Guid.NewGuid(),
                    LocalUpdatedAtAtSync = BaseTime,
                    ExternalUpdatedAtAtSync = BaseTime,
                }
            );
            db.SyncMappings.Add(
                new SyncMapping
                {
                    EntityType = SyncEntityType.TodoListItem,
                    LocalId = 10,
                    ExternalId = "ext-it-10",
                    ParentExternalId = "ext-1",
                    LastSyncedAt = BaseTime,
                    IdempotencyKey = Guid.NewGuid(),
                    LocalUpdatedAtAtSync = BaseTime,
                    ExternalUpdatedAtAtSync = BaseTime,
                }
            );
            db.SyncMappings.Add(
                new SyncMapping
                {
                    EntityType = SyncEntityType.TodoListItem,
                    LocalId = 11,
                    ExternalId = "ext-it-11",
                    ParentExternalId = "ext-1",
                    LastSyncedAt = BaseTime,
                    IdempotencyKey = Guid.NewGuid(),
                    LocalUpdatedAtAtSync = BaseTime,
                    ExternalUpdatedAtAtSync = BaseTime,
                }
            );
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        await client.PostAsync("/api/sync/run", content: null);

        using var verifyScope = factory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TodoContext>();
        Assert.Empty(verifyDb.TodoList);
        Assert.Empty(verifyDb.TodoListItem);
        Assert.Empty(verifyDb.SyncMappings);
    }

    [Fact]
    public async Task Items_LocalItemNewer_PatchesItemExternally()
    {
        var snapshot = BaseTime;
        var localItemNewer = snapshot.AddMinutes(10);
        var externalItemAfterPatch = localItemNewer.AddSeconds(1);

        using var wm = new WireMockFixture();
        wm.Server.Given(Request.Create().WithPath("/todolists").UsingGet())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithBody(
                        $$"""
                        [
                          {
                            "id": "ext-1",
                            "source_id": "1",
                            "name": "List",
                            "created_at": "{{Iso(snapshot)}}",
                            "updated_at": "{{Iso(snapshot)}}",
                            "items": [
                              {
                                "id": "ext-it-1",
                                "source_id": "10",
                                "description": "Old description",
                                "completed": false,
                                "created_at": "{{Iso(snapshot)}}",
                                "updated_at": "{{Iso(snapshot)}}"
                              }
                            ]
                          }
                        ]
                        """
                    )
            );
        wm.Server.Given(
                Request.Create().WithPath("/todolists/ext-1/todoitems/ext-it-1").UsingPatch()
            )
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithBody(
                        $$"""
                        {
                          "id": "ext-it-1",
                          "source_id": "10",
                          "description": "New description",
                          "completed": true,
                          "created_at": "{{Iso(snapshot)}}",
                          "updated_at": "{{Iso(externalItemAfterPatch)}}"
                        }
                        """
                    )
            );

        await using var factory = new TodoApiWebApplicationFactory(wm.Url);
        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TodoContext>();
            db.TodoList.Add(
                new TodoApi.Models.TodoList
                {
                    Id = 1,
                    Name = "List",
                    UpdatedAt = snapshot,
                }
            );
            db.TodoListItem.Add(
                new TodoApi.Models.TodoListItem
                {
                    Id = 10,
                    TodoListId = 1,
                    Description = "New description",
                    IsCompleted = true,
                    UpdatedAt = localItemNewer,
                }
            );
            db.SyncMappings.Add(
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
            db.SyncMappings.Add(
                new SyncMapping
                {
                    EntityType = SyncEntityType.TodoListItem,
                    LocalId = 10,
                    ExternalId = "ext-it-1",
                    ParentExternalId = "ext-1",
                    LastSyncedAt = snapshot,
                    IdempotencyKey = Guid.NewGuid(),
                    LocalUpdatedAtAtSync = snapshot,
                    ExternalUpdatedAtAtSync = snapshot,
                }
            );
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        await client.PostAsync("/api/sync/run", content: null);

        var patches = wm
            .Server.LogEntries.Where(e =>
                e.RequestMessage.Path == "/todolists/ext-1/todoitems/ext-it-1"
                && e.RequestMessage.Method == "PATCH"
            )
            .ToList();
        Assert.Single(patches);
        var sentJson = JsonDocument.Parse(patches.Single().RequestMessage.Body!).RootElement;
        Assert.Equal("New description", sentJson.GetProperty("description").GetString());
        Assert.True(sentJson.GetProperty("completed").GetBoolean());
    }

    [Fact]
    public async Task Pull_SourceIdPointsToDeletedLocal_FallsToCaseCAndCreatesNewLocal()
    {
        using var wm = new WireMockFixture();
        wm.Server.Given(Request.Create().WithPath("/todolists").UsingGet())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithBody(
                        $$"""
                        [
                          {
                            "id": "ext-zz",
                            "source_id": "99",
                            "name": "Recovered",
                            "created_at": "{{Iso(BaseTime)}}",
                            "updated_at": "{{Iso(BaseTime)}}",
                            "items": []
                          }
                        ]
                        """
                    )
            );

        await using var factory = new TodoApiWebApplicationFactory(wm.Url);

        // Seed: one unrelated local list. No mapping for source_id=99 — it represents a
        // local that was deleted and whose orphan mapping was already cleaned up by
        // the orphan-DELETE push (multi-host race scenario or follow-up tick).
        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TodoContext>();
            db.TodoList.Add(
                new TodoApi.Models.TodoList
                {
                    Id = 1,
                    Name = "Survivor",
                    UpdatedAt = BaseTime,
                }
            );
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        await client.PostAsync("/api/sync/run", content: null);

        using var verifyScope = factory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TodoContext>();
        var lists = await verifyDb.TodoList.OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(2, lists.Count);

        var recovered = lists.Single(l => l.Name == "Recovered");
        Assert.NotEqual(99L, recovered.Id);

        var mapping = await verifyDb.SyncMappings.SingleAsync();
        Assert.Equal("ext-zz", mapping.ExternalId);
        Assert.Equal(recovered.Id, mapping.LocalId);
    }
}
