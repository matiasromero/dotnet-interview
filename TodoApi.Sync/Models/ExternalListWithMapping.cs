namespace TodoApi.Sync.Models;

using TodoApi.Sync.External.Models;

/// <summary>
/// Pairs an ExternalTodoList payload with the local + external ids of the parent TodoList,
/// so the item-level reconcile loop can iterate embedded items without re-querying mappings.
/// </summary>
public record ExternalListWithMapping(
    ExternalTodoList External,
    long ParentLocalId,
    string ParentExternalId
);
