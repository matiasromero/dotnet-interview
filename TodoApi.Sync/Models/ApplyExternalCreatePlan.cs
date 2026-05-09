namespace TodoApi.Sync.Models;

/// <summary>
/// Inputs to create a brand-new local TodoList from an external entry seen during pull,
/// together with its SyncMapping. Atomicity caveats are documented in NOTES.md.
/// </summary>
public record ApplyExternalCreatePlan(
    string ExternalId,
    string Name,
    DateTime ExternalUpdatedAt,
    Guid IdempotencyKey,
    IReadOnlyList<EmbeddedExternalItem> Items
);
