using System.Net.Http.Json;
using TodoApi.Dtos;
using TodoApi.Sync.Models;
using TodoApi.Sync.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace TodoApi.Tests.Integration;

public class SyncRunEndpointTests
{
    [Fact]
    public async Task PostRun_NoCandidates_Returns200WithZeroCounts()
    {
        using var wm = new WireMockFixture();
        wm.Server.Given(Request.Create().WithPath("/todolists").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        await using var factory = new TodoApiWebApplicationFactory(wm.Url);
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/sync/run", content: null);

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<SyncRunResponse>();

        Assert.NotNull(body);
        Assert.Equal(0, body!.ListPush.Total);
        Assert.Equal(0, body.ListPush.Pushed);
        Assert.Equal(SyncRunStatus.Succeeded, body.ListPush.Status);
        Assert.Equal(0, body.ItemPush.Total);
        Assert.Equal(SyncRunStatus.Succeeded, body.ItemPush.Status);
        Assert.Equal(0, body.ListPull.Total);
        Assert.Equal(SyncRunStatus.Succeeded, body.ListPull.Status);
        Assert.Equal(0, body.ItemPull.Total);
        Assert.Equal(SyncRunStatus.Succeeded, body.ItemPull.Status);
    }
}
