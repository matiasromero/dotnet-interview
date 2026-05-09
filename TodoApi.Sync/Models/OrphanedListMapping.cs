namespace TodoApi.Sync.Models;

/// <summary>
/// Projection of a TodoList mapping whose local row has been deleted, used by the push
/// pipeline to issue a DELETE against the external API and clean up the orphaned mapping.
/// </summary>
public record OrphanedListMapping(long MappingId, string ExternalId);
