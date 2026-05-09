using System.Net;
using System.Text.Json;
using TodoApi.Sync.External;
using TodoApi.Sync.External.Models;
using TodoApi.Tests.Sync.TestHelpers;
using Xunit;

namespace TodoApi.Tests.Sync.External;

public class ExternalTodoListClientTests
{
    [Fact]
    public async Task CreateTodoListAsync_SerializesSnakeCaseAndDeserializesResponse()
    {
        var responseJson = """
            {
              "id": "ext-1",
              "source_id": "42",
              "name": "Groceries",
              "created_at": "2026-05-09T12:00:00Z",
              "updated_at": "2026-05-09T12:00:00Z",
              "items": []
            }
            """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.Created, responseJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        var request = new CreateExternalTodoListRequest(
            SourceId: "42",
            Name: "Groceries",
            Items: Array.Empty<CreateExternalTodoItemRequest>()
        );

        var result = await client.CreateTodoListAsync(
            request,
            Guid.NewGuid(),
            CancellationToken.None
        );

        Assert.Equal("ext-1", result.Id);
        Assert.Equal("42", result.SourceId);
        Assert.Equal("Groceries", result.Name);

        Assert.Single(handler.RequestBodies);
        var sentJson = JsonDocument.Parse(handler.RequestBodies[0]).RootElement;
        Assert.Equal("42", sentJson.GetProperty("source_id").GetString());
        Assert.Equal("Groceries", sentJson.GetProperty("name").GetString());
        Assert.Equal(0, sentJson.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task CreateTodoListAsync_4xxResponse_ThrowsExternalApiExceptionWithStatusCode()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.BadRequest, "{\"error\":\"bad\"}");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        var ex = await Assert.ThrowsAsync<ExternalApiException>(
            () =>
                client.CreateTodoListAsync(
                    new CreateExternalTodoListRequest(
                        "1",
                        "x",
                        Array.Empty<CreateExternalTodoItemRequest>()
                    ),
                    Guid.NewGuid(),
                    CancellationToken.None
                )
        );

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("POST", ex.Method);
        Assert.Equal("todolists", ex.Path);
        Assert.Contains("bad", ex.Body);
    }

    [Fact]
    public async Task CreateTodoListAsync_5xxResponse_ThrowsExternalApiException()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, null);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        var ex = await Assert.ThrowsAsync<ExternalApiException>(
            () =>
                client.CreateTodoListAsync(
                    new CreateExternalTodoListRequest(
                        "1",
                        "x",
                        Array.Empty<CreateExternalTodoItemRequest>()
                    ),
                    Guid.NewGuid(),
                    CancellationToken.None
                )
        );

        Assert.Equal(500, ex.StatusCode);
    }

    [Fact]
    public async Task CreateTodoListAsync_SendsIdempotencyKeyHeader()
    {
        var responseJson = """
            {
              "id": "ext-1",
              "source_id": "42",
              "name": "Groceries",
              "created_at": "2026-05-09T12:00:00Z",
              "updated_at": "2026-05-09T12:00:00Z",
              "items": []
            }
            """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.Created, responseJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);
        var key = Guid.NewGuid();

        await client.CreateTodoListAsync(
            new CreateExternalTodoListRequest(
                "42",
                "Groceries",
                Array.Empty<CreateExternalTodoItemRequest>()
            ),
            key,
            CancellationToken.None
        );

        var sent = Assert.Single(handler.Requests);
        Assert.True(sent.Headers.TryGetValues("Idempotency-Key", out var values));
        Assert.Equal(key.ToString(), Assert.Single(values));
    }

    [Fact]
    public async Task GetTodoListsAsync_HappyPath_DeserializesArray()
    {
        var responseJson = """
            [
              {
                "id": "ext-1",
                "source_id": "1",
                "name": "First",
                "created_at": "2026-05-09T12:00:00Z",
                "updated_at": "2026-05-09T12:30:00Z",
                "items": []
              },
              {
                "id": "ext-2",
                "source_id": null,
                "name": "Second",
                "created_at": "2026-05-09T12:05:00Z",
                "updated_at": "2026-05-09T12:35:00Z",
                "items": []
              }
            ]
            """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, responseJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        var result = await client.GetTodoListsAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("ext-1", result[0].Id);
        Assert.Equal("1", result[0].SourceId);
        Assert.Equal("First", result[0].Name);
        Assert.Equal("ext-2", result[1].Id);
        Assert.Null(result[1].SourceId);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.EndsWith("/todolists", sent.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetTodoListsAsync_5xxResponse_ThrowsExternalApiException()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.ServiceUnavailable,
            "{\"error\":\"down\"}"
        );
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        var ex = await Assert.ThrowsAsync<ExternalApiException>(
            () => client.GetTodoListsAsync(CancellationToken.None)
        );

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("GET", ex.Method);
        Assert.Equal("todolists", ex.Path);
        Assert.Contains("down", ex.Body);
    }

    [Fact]
    public async Task UpdateTodoListAsync_HappyPath_PatchesAndDeserializesResponse()
    {
        var responseJson = """
            {
              "id": "ext-1",
              "source_id": "42",
              "name": "Renamed",
              "created_at": "2026-05-09T12:00:00Z",
              "updated_at": "2026-05-09T13:00:00Z",
              "items": []
            }
            """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, responseJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        var result = await client.UpdateTodoListAsync(
            "ext-1",
            new UpdateExternalTodoListRequest("Renamed"),
            CancellationToken.None
        );

        Assert.Equal("Renamed", result.Name);
        Assert.Equal(new DateTime(2026, 5, 9, 13, 0, 0, DateTimeKind.Utc), result.UpdatedAt);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.EndsWith("/todolists/ext-1", sent.RequestUri!.AbsolutePath);

        var sentJson = JsonDocument.Parse(handler.RequestBodies[0]).RootElement;
        Assert.Equal("Renamed", sentJson.GetProperty("name").GetString());
    }

    [Fact]
    public async Task UpdateTodoListAsync_404Response_ThrowsExternalApiException()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.NotFound,
            "{\"error\":\"missing\"}"
        );
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        var ex = await Assert.ThrowsAsync<ExternalApiException>(
            () =>
                client.UpdateTodoListAsync(
                    "ext-missing",
                    new UpdateExternalTodoListRequest("name"),
                    CancellationToken.None
                )
        );

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("PATCH", ex.Method);
        Assert.Equal("todolists/ext-missing", ex.Path);
        Assert.Contains("missing", ex.Body);
    }

    [Fact]
    public async Task UpdateTodoItemAsync_HappyPath_PatchesAndDeserializesResponse()
    {
        var responseJson = """
            {
              "id": "itm-9",
              "source_id": "42",
              "description": "updated",
              "completed": true,
              "created_at": "2026-05-08T10:00:00Z",
              "updated_at": "2026-05-09T12:34:56Z"
            }
            """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, responseJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        var result = await client.UpdateTodoItemAsync(
            "lst-1",
            "itm-9",
            new UpdateExternalTodoItemRequest("updated", true),
            CancellationToken.None
        );

        Assert.Equal("itm-9", result.Id);
        Assert.Equal("updated", result.Description);
        Assert.True(result.Completed);
        Assert.Equal(new DateTime(2026, 5, 9, 12, 34, 56, DateTimeKind.Utc), result.UpdatedAt);
        Assert.Equal(DateTimeKind.Utc, result.UpdatedAt.Kind);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.EndsWith("/todolists/lst-1/todoitems/itm-9", sent.RequestUri!.AbsolutePath);

        var sentJson = JsonDocument.Parse(handler.RequestBodies[0]).RootElement;
        Assert.Equal("updated", sentJson.GetProperty("description").GetString());
        Assert.True(sentJson.GetProperty("completed").GetBoolean());
    }

    [Fact]
    public async Task UpdateTodoItemAsync_404Response_ThrowsExternalApiException()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.NotFound, "not found");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        var ex = await Assert.ThrowsAsync<ExternalApiException>(
            () =>
                client.UpdateTodoItemAsync(
                    "lst-1",
                    "itm-9",
                    new UpdateExternalTodoItemRequest("x", false),
                    CancellationToken.None
                )
        );

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("PATCH", ex.Method);
        Assert.EndsWith("todolists/lst-1/todoitems/itm-9", ex.Path);
        Assert.Equal("not found", ex.Body);
    }

    [Fact]
    public async Task UpdateTodoItemAsync_EmptyBody_ThrowsExternalApiException()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "null");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        var ex = await Assert.ThrowsAsync<ExternalApiException>(
            () =>
                client.UpdateTodoItemAsync(
                    "lst-1",
                    "itm-9",
                    new UpdateExternalTodoItemRequest("x", false),
                    CancellationToken.None
                )
        );

        Assert.Contains("empty body", ex.Message);
        Assert.Equal("PATCH", ex.Method);
    }

    [Fact]
    public async Task DeleteTodoItemAsync_HappyPath_Returns204_NoThrow()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.NoContent, null);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        await client.DeleteTodoItemAsync("lst-1", "itm-9", CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.EndsWith("/todolists/lst-1/todoitems/itm-9", sent.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task DeleteTodoItemAsync_404Response_ThrowsExternalApiException()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.NotFound,
            "{\"error\":\"missing\"}"
        );
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        var ex = await Assert.ThrowsAsync<ExternalApiException>(
            () => client.DeleteTodoItemAsync("lst-1", "itm-9", CancellationToken.None)
        );

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("DELETE", ex.Method);
        Assert.EndsWith("todolists/lst-1/todoitems/itm-9", ex.Path);
    }

    [Fact]
    public async Task DeleteTodoListAsync_HappyPath_SendsDeleteToCorrectPath()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.NoContent, null);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        await client.DeleteTodoListAsync("ext-1", CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.EndsWith("/todolists/ext-1", sent.RequestUri!.AbsolutePath);
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task DeleteTodoListAsync_NonSuccess_ThrowsExternalApiException()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.NotFound,
            "{\"error\":\"missing\"}"
        );
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        var ex = await Assert.ThrowsAsync<ExternalApiException>(
            () => client.DeleteTodoListAsync("ext-missing", CancellationToken.None)
        );

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("DELETE", ex.Method);
        Assert.Equal("todolists/ext-missing", ex.Path);
        Assert.Contains("missing", ex.Body);
    }
}
