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

    Task<ExternalTodoItem> UpdateTodoItemAsync(
        string externalListId,
        string externalItemId,
        UpdateExternalTodoItemRequest request,
        CancellationToken cancellationToken
    );

    Task DeleteTodoItemAsync(
        string externalListId,
        string externalItemId,
        CancellationToken cancellationToken
    );

    Task DeleteTodoListAsync(string externalId, CancellationToken cancellationToken);
}
