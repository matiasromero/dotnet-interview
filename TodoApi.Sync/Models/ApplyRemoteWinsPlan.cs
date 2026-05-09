namespace TodoApi.Sync.Models;

/// <summary>
/// Inputs to apply a "remote wins" reconcile to a mapped TodoList: overwrite the local
/// Name + UpdatedAt with the external state, and bump the mapping snapshots in lockstep.
/// </summary>
public record ApplyRemoteWinsPlan(
    long MappingId,
    long LocalId,
    string NewName,
    DateTime ExternalUpdatedAt
);
