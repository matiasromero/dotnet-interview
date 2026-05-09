namespace TodoApi.Sync.Models;

/// <summary>
/// Inputs to persist SyncMapping rows for items that were created locally and pushed as part
/// of a parent TodoList POST: the external API returns the embedded item ids in the response,
/// and we have to record the local-to-external pairing in a single transaction.
/// </summary>
public record PersistEmbeddedItemMappingsPlan(
    string ParentExternalId,
    IReadOnlyList<EmbeddedItemMapping> Items
);

/// <summary>
/// Single local-to-external pairing inside a <see cref="PersistEmbeddedItemMappingsPlan"/>.
/// </summary>
public record EmbeddedItemMapping(
    long LocalItemId,
    string ExternalItemId,
    DateTime LocalUpdatedAt,
    DateTime ExternalUpdatedAt
);
