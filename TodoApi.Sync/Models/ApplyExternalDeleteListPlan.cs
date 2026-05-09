namespace TodoApi.Sync.Models;

/// <summary>
/// Inputs to apply a cascade local delete after detecting that an external TodoList has
/// disappeared: removes the local TodoList, its TodoListItems, and the SyncMappings of both
/// the list and its child items in a single SaveChanges (atomic on SqlServer).
/// </summary>
public record ApplyExternalDeleteListPlan(long LocalListId, long MappingId);
