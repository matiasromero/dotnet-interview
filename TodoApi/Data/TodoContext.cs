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
        IReadOnlyCollection<long> mappedIds,
        CancellationToken cancellationToken = default
    ) =>
        await TodoList
            .Where(l => !mappedIds.Contains(l.Id))
            .OrderBy(l => l.Id)
            .Select(l => new LocalTodoListRecord(l.Id, l.Name))
            .ToListAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .Entity<TodoListItem>()
            .HasOne(i => i.TodoList)
            .WithMany(l => l.Items)
            .HasForeignKey(i => i.TodoListId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SyncMapping>(b =>
        {
            b.HasIndex(m => new { m.EntityType, m.LocalId }).IsUnique();
            b.HasIndex(m => new { m.EntityType, m.ExternalId }).IsUnique();
            b.Property(m => m.ExternalId).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<SyncRun>(b =>
        {
            b.HasIndex(r => new { r.EntityType, r.StartedAt });
            b.Property(r => r.Error).HasColumnType("nvarchar(max)");
        });
    }
}
