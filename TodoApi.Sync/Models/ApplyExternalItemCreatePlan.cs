namespace TodoApi.Sync.Models;

/// <summary>
/// Inputs to create a brand-new local TodoListItem from an external entry seen during pull,
/// together with its SyncMapping. Atomicity caveats are documented in NOTES.md.
/// </summary>
public record ApplyExternalItemCreatePlan(
    long ParentLocalId,
    string ParentExternalId,
    string ExternalItemId,
    string Description,
    bool Completed,
    DateTime ExternalUpdatedAt,
    Guid IdempotencyKey
);
