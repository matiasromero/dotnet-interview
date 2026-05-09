namespace TodoApi.Sync.Models;

public class SyncMapping
{
    public long Id { get; set; }
    public SyncEntityType EntityType { get; set; }
    public long LocalId { get; set; }
    public string ExternalId { get; set; } = null!;
    public DateTime LastSyncedAt { get; set; }
}
