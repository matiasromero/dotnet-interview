using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TodoApi.FakeExternalApi.Models;

namespace TodoApi.Tests.FakeExternalApi;

public class SmokeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public async Task Post_TodoList_PersistsAndReturnsCreated()
    {
        await using var factory = new WebApplicationFactory<TodoApi.FakeExternalApi.Program>();
        var client = factory.CreateClient();

        var body = new CreateTodoListRequest
        {
            SourceId = "42",
            Name = "groceries",
            Items = new List<CreateTodoItemRequest>
            {
                new CreateTodoItemRequest
                {
                    SourceId = "1",
                    Description = "eggs",
                    Completed = false,
                },
            },
        };

        var post = await client.PostAsJsonAsync("/todolists", body, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var created = await post.Content.ReadFromJsonAsync<ExternalTodoList>(JsonOptions);
        Assert.NotNull(created);
        Assert.False(string.IsNullOrEmpty(created!.Id));
        Assert.Equal("groceries", created.Name);
        Assert.Equal("42", created.SourceId);
        Assert.Single(created.Items);
        Assert.False(string.IsNullOrEmpty(created.Items[0].Id));

        var lists = await client.GetFromJsonAsync<List<ExternalTodoList>>(
            "/todolists",
            JsonOptions
        );
        Assert.NotNull(lists);
        Assert.Single(lists!);
        Assert.Equal(created.Id, lists![0].Id);
    }

    [Fact]
    public async Task Post_WithSameIdempotencyKey_ReturnsCachedResponse()
    {
        await using var factory = new WebApplicationFactory<TodoApi.FakeExternalApi.Program>();
        var client = factory.CreateClient();
        var key = Guid.NewGuid();

        var body = new CreateTodoListRequest { SourceId = "1", Name = "first" };

        var firstResp = await SendPostWithIdempotency(client, body, key);
        Assert.Equal(HttpStatusCode.Created, firstResp.StatusCode);
        var first = await firstResp.Content.ReadFromJsonAsync<ExternalTodoList>(JsonOptions);

        var secondResp = await SendPostWithIdempotency(client, body, key);
        Assert.Equal(HttpStatusCode.Created, secondResp.StatusCode);
        var second = await secondResp.Content.ReadFromJsonAsync<ExternalTodoList>(JsonOptions);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);

        var lists = await client.GetFromJsonAsync<List<ExternalTodoList>>(
            "/todolists",
            JsonOptions
        );
        Assert.NotNull(lists);
        Assert.Single(lists!);
    }

    [Fact]
    public async Task Patch_NonexistentList_Returns404()
    {
        await using var factory = new WebApplicationFactory<TodoApi.FakeExternalApi.Program>();
        var client = factory.CreateClient();

        var body = new UpdateTodoListRequest { Name = "new name" };
        var resp = await client.PatchAsJsonAsync("/todolists/does-not-exist", body, JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendPostWithIdempotency(
        HttpClient client,
        CreateTodoListRequest body,
        Guid key
    )
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/todolists")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        msg.Headers.Add("Idempotency-Key", key.ToString());
        return await client.SendAsync(msg);
    }
}
