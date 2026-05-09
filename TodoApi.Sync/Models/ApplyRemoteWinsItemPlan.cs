namespace TodoApi.Sync.Models;

/// <summary>
/// Inputs to apply a "remote wins" reconcile to a mapped TodoListItem: overwrite the local
/// Description / IsCompleted / UpdatedAt with the external state, and bump the mapping
/// snapshots in lockstep.
/// </summary>
public record ApplyRemoteWinsItemPlan(
    long MappingId,
    long LocalId,
    string NewDescription,
    bool NewCompleted,
    DateTime ExternalUpdatedAt
);
