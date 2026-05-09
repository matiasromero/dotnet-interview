namespace TodoApi.Sync.Models;

/// <summary>
/// Projection of an item embedded in an ExternalTodoList payload, normalized for use by the
/// pull reconcile loop without leaking the raw external DTO shape.
/// </summary>
public record EmbeddedExternalItem(
    string ExternalItemId,
    string Description,
    bool Completed,
    DateTime ExternalUpdatedAt
);
