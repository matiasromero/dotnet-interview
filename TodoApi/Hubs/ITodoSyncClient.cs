namespace TodoApi.Hubs;

/// <summary>
/// Strongly-typed SignalR client surface for <see cref="TodoSyncHub"/>. The server invokes
/// these methods on connected clients; clients implement them in TypeScript via
/// <c>connection.on("TodoListChanged", ...)</c> / <c>connection.on("TodoListItemChanged", ...)</c>.
///
/// <para>Typed hubs give us refactoring safety and autocomplete in tests
/// (<c>IHubContext&lt;TodoSyncHub, ITodoSyncClient&gt;</c>) instead of relying on stringly-typed
/// <c>SendAsync("MethodName", ...)</c> calls.</para>
/// </summary>
public interface ITodoSyncClient
{
    /// <summary>Invoked when a <c>TodoList</c> is created, updated, or deleted.</summary>
    Task TodoListChanged(ChangeNotification notification);

    /// <summary>Invoked when a <c>TodoListItem</c> is created, updated, or deleted.</summary>
    Task TodoListItemChanged(ChangeNotification notification);
}
