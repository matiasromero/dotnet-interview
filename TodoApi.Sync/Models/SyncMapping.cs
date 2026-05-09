namespace TodoApi.Sync.Models;

public class SyncMapping
{
    public long Id { get; set; }
    public SyncEntityType EntityType { get; set; }
    public long LocalId { get; set; }
    public string ExternalId { get; set; } = null!;
    public string? ParentExternalId { get; set; }
    public DateTime LastSyncedAt { get; set; }
    public Guid IdempotencyKey { get; set; }
    public DateTime? LocalUpdatedAtAtSync { get; set; }
    public DateTime? ExternalUpdatedAtAtSync { get; set; }
}
