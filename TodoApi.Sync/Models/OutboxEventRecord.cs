namespace TodoApi.Sync.Models;

public record OutboxEventRecord(
    long Id,
    SyncEntityType EntityType,
    long EntityId,
    OutboxOperation Operation,
    string? Payload,
    DateTime OccurredAt,
    Guid IdempotencyKey
);
