using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using TodoApi.Dtos;
using TodoApi.Hubs;
using TodoApi.Sync.Models;
using Xunit;

namespace TodoApi.Tests.Integration;

public class SignalRBroadcastTests
{
    /// <summary>
    /// Variant of <see cref="TodoApiWebApplicationFactory"/> that also sets the realtime
    /// broadcast interval down to 250 ms so the test doesn't have to wait the production
    /// default of 2 s for the broadcaster to wake up. WireMock is not required: sync is
    /// disabled in tests, so the external HTTP client is never reached.
    /// </summary>
    private sealed class FastBroadcastFactory : TodoApiWebApplicationFactory
    {
        public FastBroadcastFactory()
            : base("http://signalr-tests.invalid") { }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(
                (_, cfg) =>
                    cfg.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["Realtime:BroadcastInterval"] = "00:00:00.250",
                            ["Realtime:BatchSize"] = "50",
                        }
                    )
            );
        }
    }

    private static HubConnection BuildConnection(FastBroadcastFactory factory)
    {
        // Long-polling transport over the in-process TestServer's HttpMessageHandler.
        // The factory's BaseAddress (http://localhost) is used as origin for negotiation;
        // we route the bytes through factory.Server.CreateHandler() to stay in-process.
        var hubUrl = new Uri(factory.Server.BaseAddress, "/hubs/todosync");
        return new HubConnectionBuilder()
            .WithUrl(
                hubUrl,
                opts =>
                {
                    opts.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    opts.Transports = HttpTransportType.LongPolling;
                }
            )
            .Build();
    }

    [Fact]
    public async Task PostTodoList_PublishesTodoListChangedToConnectedClient()
    {
        await using var factory = new FastBroadcastFactory();
        var http = factory.CreateClient();

        await using var connection = BuildConnection(factory);

        var received = new TaskCompletionSource<ChangeNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        connection.On<ChangeNotification>(
            nameof(ITodoSyncClient.TodoListChanged),
            n => received.TrySetResult(n)
        );

        await connection.StartAsync();

        var post = await http.PostAsJsonAsync(
            "/api/todolists",
            new CreateTodoList { Name = "from-signalr-test" }
        );
        post.EnsureSuccessStatusCode();

        var notification = await WaitWithTimeout(received.Task, TimeSpan.FromSeconds(10));

        Assert.Equal(SyncEntityType.TodoList, notification.EntityType);
        Assert.Equal(OutboxOperation.Create, notification.Operation);
        Assert.True(notification.EntityId > 0);
        Assert.True(notification.EventId > 0);
    }

    [Fact]
    public async Task PutTodoListItem_PublishesTodoListItemChangedToConnectedClient()
    {
        await using var factory = new FastBroadcastFactory();
        var http = factory.CreateClient();

        // Seed: list with one item via the real CRUD path so outbox events are emitted by
        // the same code that production exercises.
        var listResp = await http.PostAsJsonAsync(
            "/api/todolists",
            new CreateTodoList { Name = "parent" }
        );
        listResp.EnsureSuccessStatusCode();
        var list = await listResp.Content.ReadFromJsonAsync<TodoApi.Models.TodoList>();
        Assert.NotNull(list);

        var itemResp = await http.PostAsJsonAsync(
            $"/api/todolists/{list!.Id}/items",
            new CreateTodoListItem { Description = "child" }
        );
        itemResp.EnsureSuccessStatusCode();
        var item = await itemResp.Content.ReadFromJsonAsync<TodoApi.Models.TodoListItem>();
        Assert.NotNull(item);

        await using var connection = BuildConnection(factory);
        var received = new TaskCompletionSource<ChangeNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        connection.On<ChangeNotification>(
            nameof(ITodoSyncClient.TodoListItemChanged),
            n =>
            {
                if (n.Operation == OutboxOperation.Update)
                {
                    received.TrySetResult(n);
                }
            }
        );
        await connection.StartAsync();

        var update = await http.PutAsJsonAsync(
            $"/api/todolists/{list.Id}/items/{item!.Id}",
            new UpdateTodoListItem { Description = "child", IsCompleted = true }
        );
        update.EnsureSuccessStatusCode();

        var notification = await WaitWithTimeout(received.Task, TimeSpan.FromSeconds(10));

        Assert.Equal(SyncEntityType.TodoListItem, notification.EntityType);
        Assert.Equal(item.Id, notification.EntityId);
        Assert.Equal(OutboxOperation.Update, notification.Operation);
    }

    private static async Task<T> WaitWithTimeout<T>(Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            throw new TimeoutException(
                $"Expected SignalR notification within {timeout.TotalSeconds:F1}s but none arrived."
            );
        }
        return await task;
    }
}
