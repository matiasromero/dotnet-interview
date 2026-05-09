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

        var result = await client.CreateTodoListAsync(request, CancellationToken.None);

        Assert.Equal("ext-1", result.Id);
        Assert.Equal("42", result.SourceId);
        Assert.Equal("Groceries", result.Name);

        Assert.Single(handler.RequestBodies);
        var sentJson = JsonDocument.Parse(handler.RequestBodies[0]).RootElement;
        Assert.Equal("42", sentJson.GetProperty("source_id").GetString());
        Assert.Equal("Groceries", sentJson.GetProperty("name").GetString());
        Assert.Equal(0, sentJson.GetProperty("items").GetArrayLength());
    }
}
