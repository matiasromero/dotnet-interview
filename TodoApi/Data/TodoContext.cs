using Microsoft.EntityFrameworkCore;
using TodoApi.Models;
using TodoApi.Sync.Data;
using TodoApi.Sync.Models;

public class TodoContext : DbContext, ISyncDbContext
{
    public TodoContext(DbContextOptions<TodoContext> options)
        : base(options) { }

    public DbSet<TodoList> TodoList { get; set; } = default!;
    public DbSet<TodoListItem> TodoListItem { get; set; } = default!;
    public DbSet<SyncMapping> SyncMappings { get; set; } = null!;
    public DbSet<SyncRun> SyncRuns { get; set; } = null!;

    public async Task<List<LocalTodoListRecord>> GetUnmappedTodoListsAsync(
        CancellationToken cancellationToken = default
    ) =>
        await TodoList
            .Where(l =>
                !SyncMappings.Any(m => m.EntityType == SyncEntityType.TodoList && m.LocalId == l.Id)
            )
            .OrderBy(l => l.Id)
            .Select(l => new LocalTodoListRecord(
                l.Id,
                l.Name,
                l.UpdatedAt,
                l.Items.Select(i => new LocalTodoListItemRecord(
                        i.Id,
                        i.Description,
                        i.IsCompleted,
                        i.UpdatedAt
                    ))
                    .ToList()
            ))
            .ToListAsync(cancellationToken);

    public async Task<List<MappedTodoListRecord>> GetMappedTodoListsAsync(
        CancellationToken cancellationToken = default
    ) =>
        await SyncMappings
            .Where(m => m.EntityType == SyncEntityType.TodoList)
            .Join(
                TodoList,
                m => m.LocalId,
                l => l.Id,
                (m, l) =>
                    new MappedTodoListRecord(
                        m.Id,
                        m.LocalId,
                        m.ExternalId,
                        m.IdempotencyKey,
                        m.LastSyncedAt,
                        m.LocalUpdatedAtAtSync,
                        m.ExternalUpdatedAtAtSync,
                        l.Name,
                        l.UpdatedAt
                    )
            )
            .ToListAsync(cancellationToken);

    public async Task<LocalTodoListRecord?> FindUnmappedLocalByIdAsync(
        long localId,
        CancellationToken cancellationToken = default
    ) =>
        await TodoList
            .Where(l =>
                l.Id == localId
                && !SyncMappings.Any(m =>
                    m.EntityType == SyncEntityType.TodoList && m.LocalId == l.Id
                )
            )
            .Select(l => new LocalTodoListRecord(
                l.Id,
                l.Name,
                l.UpdatedAt,
                l.Items.Select(i => new LocalTodoListItemRecord(
                        i.Id,
                        i.Description,
                        i.IsCompleted,
                        i.UpdatedAt
                    ))
                    .ToList()
            ))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task ApplyExternalCreateAsync(
        ApplyExternalCreatePlan plan,
        CancellationToken cancellationToken = default
    )
    {
        var local = new TodoList { Name = plan.Name, UpdatedAt = plan.ExternalUpdatedAt };
        TodoList.Add(local);
        await SaveChangesAsync(cancellationToken);

        SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = local.Id,
                ExternalId = plan.ExternalId,
                IdempotencyKey = plan.IdempotencyKey,
                LastSyncedAt = DateTime.UtcNow,
                LocalUpdatedAtAtSync = plan.ExternalUpdatedAt,
                ExternalUpdatedAtAtSync = plan.ExternalUpdatedAt,
            }
        );

        if (plan.Items.Count > 0)
        {
            var newItems = plan
                .Items.Select(ei => new TodoListItem
                {
                    Description = ei.Description,
                    IsCompleted = ei.Completed,
                    TodoListId = local.Id,
                    UpdatedAt = ei.ExternalUpdatedAt,
                })
                .ToList();
            TodoListItem.AddRange(newItems);
            await SaveChangesAsync(cancellationToken);

            var now = DateTime.UtcNow;
            for (int i = 0; i < newItems.Count; i++)
            {
                var ei = plan.Items[i];
                SyncMappings.Add(
                    new SyncMapping
                    {
                        EntityType = SyncEntityType.TodoListItem,
                        LocalId = newItems[i].Id,
                        ExternalId = ei.ExternalItemId,
                        ParentExternalId = plan.ExternalId,
                        IdempotencyKey = Guid.NewGuid(),
                        LastSyncedAt = now,
                        LocalUpdatedAtAtSync = ei.ExternalUpdatedAt,
                        ExternalUpdatedAtAtSync = ei.ExternalUpdatedAt,
                    }
                );
            }
        }

        await SaveChangesAsync(cancellationToken);
    }

    public async Task ApplyRemoteWinsAsync(
        ApplyRemoteWinsPlan plan,
        CancellationToken cancellationToken = default
    )
    {
        var local =
            await TodoList.FindAsync(new object?[] { plan.LocalId }, cancellationToken)
            ?? throw new InvalidOperationException(
                $"ApplyRemoteWinsAsync: local TodoList {plan.LocalId} not found"
            );
        var mapping =
            await SyncMappings.FindAsync(new object?[] { plan.MappingId }, cancellationToken)
            ?? throw new InvalidOperationException(
                $"ApplyRemoteWinsAsync: SyncMapping {plan.MappingId} not found"
            );

        local.Name = plan.NewName;
        local.UpdatedAt = plan.ExternalUpdatedAt;

        mapping.LocalUpdatedAtAtSync = plan.ExternalUpdatedAt;
        mapping.ExternalUpdatedAtAtSync = plan.ExternalUpdatedAt;
        mapping.LastSyncedAt = DateTime.UtcNow;

        await SaveChangesAsync(cancellationToken);
    }

    public async Task<List<LocalTodoListItemRecord>> GetUnmappedTodoListItemsWithMappedParentAsync(
        CancellationToken cancellationToken = default
    ) =>
        await TodoListItem
            .Where(i =>
                !SyncMappings.Any(m =>
                    m.EntityType == SyncEntityType.TodoListItem && m.LocalId == i.Id
                )
                && SyncMappings.Any(m =>
                    m.EntityType == SyncEntityType.TodoList && m.LocalId == i.TodoListId
                )
            )
            .OrderBy(i => i.Id)
            .Select(i => new LocalTodoListItemRecord(
                i.Id,
                i.Description,
                i.IsCompleted,
                i.UpdatedAt
            ))
            .ToListAsync(cancellationToken);

    public async Task<List<MappedTodoListItemRecord>> GetMappedTodoListItemsAsync(
        CancellationToken cancellationToken = default
    ) =>
        await SyncMappings
            .Where(m => m.EntityType == SyncEntityType.TodoListItem)
            .Join(
                TodoListItem,
                m => m.LocalId,
                i => i.Id,
                (m, i) =>
                    new MappedTodoListItemRecord(
                        m.Id,
                        m.LocalId,
                        m.ExternalId,
                        m.ParentExternalId!,
                        m.IdempotencyKey,
                        m.LastSyncedAt,
                        m.LocalUpdatedAtAtSync,
                        m.ExternalUpdatedAtAtSync,
                        i.Description,
                        i.IsCompleted,
                        i.UpdatedAt
                    )
            )
            .ToListAsync(cancellationToken);

    public async Task<List<OrphanedItemMapping>> GetOrphanedItemMappingsAsync(
        CancellationToken cancellationToken = default
    ) =>
        await SyncMappings
            .Where(m =>
                m.EntityType == SyncEntityType.TodoListItem
                && !TodoListItem.Any(i => i.Id == m.LocalId)
            )
            .Select(m => new OrphanedItemMapping(m.Id, m.ExternalId, m.ParentExternalId!))
            .ToListAsync(cancellationToken);

    public async Task<LocalTodoListItemRecord?> FindUnmappedLocalItemByIdAsync(
        long localId,
        long parentListId,
        CancellationToken cancellationToken = default
    ) =>
        await TodoListItem
            .Where(i =>
                i.Id == localId
                && i.TodoListId == parentListId
                && !SyncMappings.Any(m =>
                    m.EntityType == SyncEntityType.TodoListItem && m.LocalId == i.Id
                )
            )
            .Select(i => new LocalTodoListItemRecord(
                i.Id,
                i.Description,
                i.IsCompleted,
                i.UpdatedAt
            ))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task ApplyExternalItemCreateAsync(
        ApplyExternalItemCreatePlan plan,
        CancellationToken cancellationToken = default
    )
    {
        var item = new TodoListItem
        {
            Description = plan.Description,
            IsCompleted = plan.Completed,
            TodoListId = plan.ParentLocalId,
            UpdatedAt = plan.ExternalUpdatedAt,
        };
        TodoListItem.Add(item);
        await SaveChangesAsync(cancellationToken);

        SyncMappings.Add(
            new SyncMapping
            {
                EntityType = SyncEntityType.TodoListItem,
                LocalId = item.Id,
                ExternalId = plan.ExternalItemId,
                ParentExternalId = plan.ParentExternalId,
                IdempotencyKey = plan.IdempotencyKey,
                LastSyncedAt = DateTime.UtcNow,
                LocalUpdatedAtAtSync = plan.ExternalUpdatedAt,
                ExternalUpdatedAtAtSync = plan.ExternalUpdatedAt,
            }
        );
        await SaveChangesAsync(cancellationToken);
    }

    public async Task ApplyRemoteWinsItemAsync(
        ApplyRemoteWinsItemPlan plan,
        CancellationToken cancellationToken = default
    )
    {
        var item =
            await TodoListItem.FindAsync(new object?[] { plan.LocalId }, cancellationToken)
            ?? throw new InvalidOperationException(
                $"ApplyRemoteWinsItemAsync: local TodoListItem {plan.LocalId} not found"
            );
        var mapping =
            await SyncMappings.FindAsync(new object?[] { plan.MappingId }, cancellationToken)
            ?? throw new InvalidOperationException(
                $"ApplyRemoteWinsItemAsync: SyncMapping {plan.MappingId} not found"
            );

        item.Description = plan.NewDescription;
        item.IsCompleted = plan.NewCompleted;
        item.UpdatedAt = plan.ExternalUpdatedAt;

        mapping.LocalUpdatedAtAtSync = plan.ExternalUpdatedAt;
        mapping.ExternalUpdatedAtAtSync = plan.ExternalUpdatedAt;
        mapping.LastSyncedAt = DateTime.UtcNow;

        await SaveChangesAsync(cancellationToken);
    }

    public async Task PersistEmbeddedItemMappingsAsync(
        PersistEmbeddedItemMappingsPlan plan,
        CancellationToken cancellationToken = default
    )
    {
        if (plan.Items.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var item in plan.Items)
        {
            SyncMappings.Add(
                new SyncMapping
                {
                    EntityType = SyncEntityType.TodoListItem,
                    LocalId = item.LocalItemId,
                    ExternalId = item.ExternalItemId,
                    ParentExternalId = plan.ParentExternalId,
                    IdempotencyKey = Guid.NewGuid(),
                    LastSyncedAt = now,
                    LocalUpdatedAtAtSync = item.LocalUpdatedAt,
                    ExternalUpdatedAtAtSync = item.ExternalUpdatedAt,
                }
            );
        }
        await SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .Entity<TodoListItem>()
            .HasOne(i => i.TodoList)
            .WithMany(l => l.Items)
            .HasForeignKey(i => i.TodoListId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder
            .Entity<TodoListItem>()
            .Property(i => i.UpdatedAt)
            .HasDefaultValueSql("GETUTCDATE()");

        modelBuilder.Entity<SyncMapping>(b =>
        {
            b.HasIndex(m => new { m.EntityType, m.LocalId }).IsUnique();
            b.HasIndex(m => new { m.EntityType, m.ExternalId }).IsUnique();
            b.HasIndex(m => m.IdempotencyKey).IsUnique();
            b.Property(m => m.ExternalId).HasMaxLength(64).IsRequired();
            b.Property(m => m.ParentExternalId).HasMaxLength(64);
        });

        modelBuilder.Entity<SyncRun>(b =>
        {
            b.HasIndex(r => new { r.EntityType, r.StartedAt });
            b.Property(r => r.Error).HasColumnType("nvarchar(max)");
        });
    }
}
