using Microsoft.EntityFrameworkCore;
using TodoApi.Sync.Models;

namespace TodoApi.Sync.Data;

public interface ISyncDbContext
{
    DbSet<SyncMapping> SyncMappings { get; }
    DbSet<SyncRun> SyncRuns { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all local TodoLists whose IDs are not in the given set of already-mapped IDs.
    /// Ordered by Id ascending.
    /// </summary>
    Task<List<LocalTodoListRecord>> GetUnmappedTodoListsAsync(
        IReadOnlyCollection<long> mappedIds,
        CancellationToken cancellationToken = default
    );
}
