using TodoApi.Sync.External.Models;

namespace TodoApi.Sync.External;

public interface IExternalTodoListClient
{
    Task<ExternalTodoList> CreateTodoListAsync(
        CreateExternalTodoListRequest request,
        Guid idempotencyKey,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<ExternalTodoList>> GetTodoListsAsync(CancellationToken cancellationToken);

    Task<ExternalTodoList> UpdateTodoListAsync(
        string externalId,
        UpdateExternalTodoListRequest request,
        CancellationToken cancellationToken
    );
}
