namespace TodoApi.Sync.Models;

public enum OutboxOperation
{
    Create = 1,
    Update = 2,
    Delete = 3,
}

public class OutboxEvent
{
    public long Id { get; set; }
    public SyncEntityType EntityType { get; set; }
    public long EntityId { get; set; }
    public OutboxOperation Operation { get; set; }
    public string? Payload { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public Guid IdempotencyKey { get; set; }
}
