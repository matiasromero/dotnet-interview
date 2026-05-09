using TodoApi.Sync.External.Models;

namespace TodoApi.Sync.External;

public interface IExternalTodoListClient
{
    Task<ExternalTodoList> CreateTodoListAsync(
        CreateExternalTodoListRequest request,
        CancellationToken cancellationToken
    );
}
