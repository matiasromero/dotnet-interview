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
    /// Ordered by Id ascending.
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
    /// (CreateTodoListAsync succeeded externally but mapping save crashed).
    /// </summary>
    Task<LocalTodoListRecord?> FindUnmappedLocalByIdAsync(
        long localId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Creates a new local TodoList from an external entry plus its SyncMapping.
    /// Persists in two SaveChanges (TodoList first to get its Id, then SyncMapping).
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
}
