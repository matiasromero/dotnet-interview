using Microsoft.EntityFrameworkCore;
using TodoApi.Sync.Models;

namespace TodoApi.Sync.Data;

public interface ISyncDbContext
{
    DbSet<SyncMapping> SyncMappings { get; }
    DbSet<SyncRun> SyncRuns { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all local TodoLists that have no SyncMapping for SyncEntityType.TodoList.
    /// Ordered by Id ascending. Each list is projected with its current local items.
    /// </summary>
    Task<List<LocalTodoListRecord>> GetUnmappedTodoListsAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns all TodoList SyncMappings joined with their current local state. Used by
    /// the pull reconcile loop to compare local-vs-external timestamps.
    /// </summary>
    Task<List<MappedTodoListRecord>> GetMappedTodoListsAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns the local TodoList with the given Id only if it exists AND has no mapping.
    /// Used to detect orphans created by a crash mid-write of the push path
    /// (CreateTodoListAsync succeeded externally but mapping save crashed). Includes the
    /// list's current local items for downstream reconcile.
    /// </summary>
    Task<LocalTodoListRecord?> FindUnmappedLocalByIdAsync(
        long localId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Creates a new local TodoList from an external entry plus its SyncMapping. When the
    /// plan carries embedded items, also creates the corresponding local TodoListItem rows
    /// and their SyncMappings in the same call.
    /// </summary>
    Task ApplyExternalCreateAsync(
        ApplyExternalCreatePlan plan,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Overwrites the local TodoList's Name and UpdatedAt with the external state, and
    /// bumps the mapping snapshots and LastSyncedAt in the same SaveChanges.
    /// </summary>
    Task ApplyRemoteWinsAsync(
        ApplyRemoteWinsPlan plan,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns local TodoListItems that have no SyncMapping but whose parent TodoList
    /// already has a TodoList SyncMapping. Used by the push pipeline to log a warning and
    /// skip syncing items whose parent has been mapped without them (rare crash window).
    /// </summary>
    Task<List<LocalTodoListItemRecord>> GetUnmappedTodoListItemsWithMappedParentAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns all TodoListItem SyncMappings joined with their current local state. Used
    /// by the pull reconcile loop to compare local-vs-external timestamps for items.
    /// </summary>
    Task<List<MappedTodoListItemRecord>> GetMappedTodoListItemsAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns SyncMappings of TodoListItems whose local row has been deleted (anti-join
    /// against the TodoListItem table). Used by the push pipeline to issue DELETEs against
    /// the external API and clean up the orphaned mapping rows.
    /// </summary>
    Task<List<OrphanedItemMapping>> GetOrphanedItemMappingsAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns the local TodoListItem with the given Id only if it exists, belongs to the
    /// given parent list, AND has no SyncMapping. Used by the pull pipeline to adopt local
    /// items orphaned by a crash mid-write of the push path.
    /// </summary>
    Task<LocalTodoListItemRecord?> FindUnmappedLocalItemByIdAsync(
        long localId,
        long parentListId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Creates a new local TodoListItem from an external entry plus its SyncMapping.
    /// Persists in two SaveChanges (TodoListItem first to get its Id, then SyncMapping).
    /// </summary>
    Task ApplyExternalItemCreateAsync(
        ApplyExternalItemCreatePlan plan,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Overwrites the local TodoListItem's Description / IsCompleted / UpdatedAt with the
    /// external state, and bumps the mapping snapshots and LastSyncedAt in the same
    /// SaveChanges.
    /// </summary>
    Task ApplyRemoteWinsItemAsync(
        ApplyRemoteWinsItemPlan plan,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Persists SyncMapping rows for a batch of items that were embedded in a parent
    /// TodoList POST: pairs each local item with its external id from the API response.
    /// </summary>
    Task PersistEmbeddedItemMappingsAsync(
        PersistEmbeddedItemMappingsPlan plan,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns SyncMappings of TodoLists whose local row has been deleted (anti-join against
    /// the TodoList table). Used by the push pipeline to issue DELETEs against the external
    /// API and clean up the orphaned mapping rows.
    /// </summary>
    Task<List<OrphanedListMapping>> GetOrphanedListMappingsAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Cascade-deletes a local TodoList after detecting its external counterpart has
    /// disappeared: removes child TodoListItem rows, child item SyncMappings, the list
    /// SyncMapping, and the TodoList itself in a single SaveChanges (atomic on SqlServer;
    /// InMemory has no transactions but call sites accept the documented caveat).
    /// </summary>
    Task ApplyExternalDeleteListAsync(
        ApplyExternalDeleteListPlan plan,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Deletes a local TodoListItem and its SyncMapping after detecting that its external
    /// counterpart has disappeared (parent list still alive). Single SaveChanges.
    /// </summary>
    Task ApplyExternalDeleteItemAsync(
        ApplyExternalDeleteItemPlan plan,
        CancellationToken cancellationToken = default
    );
}
