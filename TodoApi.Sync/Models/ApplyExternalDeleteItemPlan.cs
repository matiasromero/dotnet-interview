namespace TodoApi.Sync.Models;

/// <summary>
/// Inputs to apply a local delete after detecting that an external TodoListItem has
/// disappeared (parent list still alive): removes the local TodoListItem and its
/// SyncMapping in a single SaveChanges.
/// </summary>
public record ApplyExternalDeleteItemPlan(long LocalItemId, long MappingId);
