using Microsoft.EntityFrameworkCore;

namespace TodoApi.Sync.Data;

public static class BulkDeleteExtensions
{
    public static async Task<int> ExecuteBulkDeleteAsync<T>(
        this IQueryable<T> source,
        DbContext context,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        var providerName = context.Database.ProviderName ?? string.Empty;
        var isInMemory = providerName.Contains("InMemory", StringComparison.Ordinal);

        if (isInMemory)
        {
            var entities = await source.ToListAsync(cancellationToken);
            if (entities.Count == 0)
                return 0;
            context.Set<T>().RemoveRange(entities);
            await context.SaveChangesAsync(cancellationToken);
            return entities.Count;
        }

        return await source.ExecuteDeleteAsync(cancellationToken);
    }
}
