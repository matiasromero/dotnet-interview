using Microsoft.EntityFrameworkCore;
using TodoApi.Models;

public class TodoContext : DbContext
{
    public TodoContext(DbContextOptions<TodoContext> options)
        : base(options) { }

    public DbSet<TodoList> TodoList { get; set; } = default!;
    public DbSet<TodoListItem> TodoListItem { get; set; } = default!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .Entity<TodoListItem>()
            .HasOne(i => i.TodoList)
            .WithMany(l => l.Items)
            .HasForeignKey(i => i.TodoListId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
