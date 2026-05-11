using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TodoApi.Models;
using TodoApi.Sync.Data;
using TodoApi.Sync.Models;

public class TodoContext : DbContext, ISyncDbContext
{
    private readonly ILogger<TodoContext> _logger;

    public TodoContext(DbContextOptions<TodoContext> options, ILogger<TodoContext>? logger = null)
        : base(options)
    {
        _logger = logger ?? NullLogger<TodoContext>.Instance;
    }

    public DbSet<TodoList> TodoList { get; set; } = default!;
    public DbSet<TodoListItem> TodoListItem { get; set; } = default!;
    public DbSet<SyncMapping> SyncMappings { get; set; } = null!;
    public DbSet<SyncRun> SyncRuns { get; set; } = null!;
    public DbSet<OutboxEvent> OutboxEvents { get; set; } = null!;

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
        _logger.LogInformation(
            "Created external TodoList {ExternalId} -> local {LocalId} with name {Name}",
            plan.ExternalId,
            local.Id,
            plan.Name
        );

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
            _logger.LogInformation(
                "Created {ItemCount} TodoListItems for external TodoList {ExternalId}",
                newItems.Count,
                plan.ExternalId
            );

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
        _logger.LogInformation(
            "Created external TodoList {ExternalId} -> local {LocalId} with {ItemCount} items and mappings persisted",
            plan.ExternalId,
            local.Id,
            plan.Items.Count
        );
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

        _logger.LogInformation(
            "Reconciling TodoList {LocalId}: external wins (local={LocalName} → {NewName}, local={LocalUpdatedAt} → {ExternalUpdatedAt})",
            plan.LocalId,
            local.Name,
            plan.NewName,
            local.UpdatedAt,
            plan.ExternalUpdatedAt
        );

        local.Name = plan.NewName;
        local.UpdatedAt = plan.ExternalUpdatedAt;

        mapping.LocalUpdatedAtAtSync = plan.ExternalUpdatedAt;
        mapping.ExternalUpdatedAtAtSync = plan.ExternalUpdatedAt;
        mapping.LastSyncedAt = DateTime.UtcNow;

        await SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Reconciled TodoList {LocalId}: external wins applied",
            plan.LocalId
        );
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

        _logger.LogInformation(
            "Reconciling TodoListItem {LocalId}: external wins (local={LocalDescription} IsCompleted={LocalIsCompleted} → {NewIsCompleted}, local={LocalUpdatedAt} → {ExternalUpdatedAt})",
            plan.LocalId,
            item.Description,
            item.IsCompleted,
            plan.NewCompleted,
            item.UpdatedAt,
            plan.ExternalUpdatedAt
        );

        item.Description = plan.NewDescription;
        item.IsCompleted = plan.NewCompleted;
        item.UpdatedAt = plan.ExternalUpdatedAt;

        mapping.LocalUpdatedAtAtSync = plan.ExternalUpdatedAt;
        mapping.ExternalUpdatedAtAtSync = plan.ExternalUpdatedAt;
        mapping.LastSyncedAt = DateTime.UtcNow;

        await SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Reconciled TodoListItem {LocalId}: external wins applied",
            plan.LocalId
        );
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

        _logger.LogInformation(
            "Persisting {MappingCount} embedded item mappings for parent {ParentExternalId}",
            plan.Items.Count,
            plan.ParentExternalId
        );

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
        _logger.LogInformation(
            "Persisted {MappingCount} embedded item mappings for parent {ParentExternalId}",
            plan.Items.Count,
            plan.ParentExternalId
        );
    }

    public async Task<List<OrphanedListMapping>> GetOrphanedListMappingsAsync(
        CancellationToken cancellationToken = default
    ) =>
        await SyncMappings
            .Where(m =>
                m.EntityType == SyncEntityType.TodoList && !TodoList.Any(l => l.Id == m.LocalId)
            )
            .Select(m => new OrphanedListMapping(m.Id, m.ExternalId))
            .ToListAsync(cancellationToken);

    public async Task ApplyExternalDeleteListAsync(
        ApplyExternalDeleteListPlan plan,
        CancellationToken cancellationToken = default
    )
    {
        var localItems = await TodoListItem
            .Where(i => i.TodoListId == plan.LocalListId)
            .ToListAsync(cancellationToken);
        var localItemIds = localItems.Select(i => i.Id).ToList();

        var itemMappings = await SyncMappings
            .Where(m =>
                m.EntityType == SyncEntityType.TodoListItem && localItemIds.Contains(m.LocalId)
            )
            .ToListAsync(cancellationToken);

        _logger.LogInformation(
            "Deleting TodoList {LocalId}: {ItemCount} items, {MappingCount} mappings",
            plan.LocalListId,
            localItems.Count,
            itemMappings.Count + 1
        );

        SyncMappings.RemoveRange(itemMappings);
        TodoListItem.RemoveRange(localItems);

        var listMapping = await SyncMappings.FindAsync(
            new object?[] { plan.MappingId },
            cancellationToken
        );
        if (listMapping is not null)
        {
            SyncMappings.Remove(listMapping);
        }

        var local = await TodoList.FindAsync(new object?[] { plan.LocalListId }, cancellationToken);
        if (local is not null)
        {
            TodoList.Remove(local);
        }

        await SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Deleted TodoList {LocalId}", plan.LocalListId);
    }

    public async Task ApplyExternalDeleteItemAsync(
        ApplyExternalDeleteItemPlan plan,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Deleting TodoListItem {LocalId}", plan.LocalItemId);

        var mapping = await SyncMappings.FindAsync(
            new object?[] { plan.MappingId },
            cancellationToken
        );
        if (mapping is not null)
        {
            SyncMappings.Remove(mapping);
        }

        var item = await TodoListItem.FindAsync(
            new object?[] { plan.LocalItemId },
            cancellationToken
        );
        if (item is not null)
        {
            TodoListItem.Remove(item);
        }

        await SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Deleted TodoListItem {LocalId}", plan.LocalItemId);
    }

    public async Task<LocalTodoListRecord?> GetLocalTodoListByIdAsync(
        long localId,
        CancellationToken cancellationToken = default
    ) =>
        await TodoList
            .Where(l => l.Id == localId)
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

    public async Task<LocalTodoListItemRecord?> GetLocalTodoListItemByIdAsync(
        long localId,
        CancellationToken cancellationToken = default
    ) =>
        await TodoListItem
            .Where(i => i.Id == localId)
            .Select(i => new LocalTodoListItemRecord(
                i.Id,
                i.Description,
                i.IsCompleted,
                i.UpdatedAt
            ))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<List<OutboxEventRecord>> GetPendingOutboxEventsAsync(
        SyncEntityType entityType,
        int take,
        CancellationToken cancellationToken = default
    ) =>
        await OutboxEvents
            .Where(e => e.EntityType == entityType && e.ProcessedAt == null)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .Take(take)
            .Select(e => new OutboxEventRecord(
                e.Id,
                e.EntityType,
                e.EntityId,
                e.Operation,
                e.Payload,
                e.OccurredAt,
                e.IdempotencyKey
            ))
            .ToListAsync(cancellationToken);

    public async Task MarkOutboxEventProcessedAsync(
        long eventId,
        CancellationToken cancellationToken = default
    )
    {
        var evt = await OutboxEvents.FindAsync(new object?[] { eventId }, cancellationToken);
        if (evt is null || evt.ProcessedAt is not null)
        {
            return;
        }
        evt.ProcessedAt = DateTime.UtcNow;
        await SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Marked OutboxEvent {EventId} as processed", eventId);
    }

    public async Task<int> PurgeProcessedOutboxEventsAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Purging processed OutboxEvents older than {Cutoff}", cutoff);
        var deletedCount = await OutboxEvents
            .Where(e => e.ProcessedAt != null && e.OccurredAt < cutoff)
            .ExecuteBulkDeleteAsync(this, cancellationToken);
        _logger.LogInformation("Purged {DeletedCount} processed OutboxEvents", deletedCount);
        return deletedCount;
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

        modelBuilder.Entity<TodoList>().HasIndex(l => l.UpdatedAt);
        modelBuilder.Entity<TodoListItem>().HasIndex(i => i.UpdatedAt);

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

        modelBuilder.Entity<OutboxEvent>(b =>
        {
            b.HasIndex(e => e.IdempotencyKey).IsUnique();
            b.HasIndex(e => new { e.EntityType, e.EntityId });
            b.HasIndex(e => e.OccurredAt).HasFilter("[ProcessedAt] IS NULL");
            b.Property(e => e.Payload).HasColumnType("nvarchar(max)");
        });
    }
}
