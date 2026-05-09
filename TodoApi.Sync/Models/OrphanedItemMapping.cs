namespace TodoApi.Sync.Models;

/// <summary>
/// Projection of a TodoListItem mapping whose local row has been deleted, used by the push
/// pipeline to issue a DELETE against the external API and clean up the orphaned mapping.
/// </summary>
public record OrphanedItemMapping(long MappingId, string ExternalItemId, string ParentExternalId);
