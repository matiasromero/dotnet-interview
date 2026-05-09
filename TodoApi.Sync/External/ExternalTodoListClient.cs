using System.Net.Http.Json;
using TodoApi.Sync.External.Models;

namespace TodoApi.Sync.External;

public class ExternalTodoListClient : IExternalTodoListClient
{
    private const string TodoListsPath = "todolists";

    private readonly HttpClient _http;

    public ExternalTodoListClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<ExternalTodoList> CreateTodoListAsync(
        CreateExternalTodoListRequest request,
        Guid idempotencyKey,
        CancellationToken cancellationToken
    )
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, TodoListsPath)
        {
            Content = JsonContent.Create(request, options: ExternalJsonOptions.Default),
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey.ToString());

        using var response = await _http.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ExternalApiException(
                $"POST {TodoListsPath} failed with {(int)response.StatusCode}",
                (int)response.StatusCode,
                "POST",
                TodoListsPath,
                body
            );
        }

        var result = await response.Content.ReadFromJsonAsync<ExternalTodoList>(
            ExternalJsonOptions.Default,
            cancellationToken
        );

        return result
            ?? throw new ExternalApiException(
                $"POST {TodoListsPath} returned empty body",
                (int)response.StatusCode,
                "POST",
                TodoListsPath,
                null
            );
    }

    public async Task<IReadOnlyList<ExternalTodoList>> GetTodoListsAsync(
        CancellationToken cancellationToken
    )
    {
        using var response = await _http.GetAsync(TodoListsPath, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ExternalApiException(
                $"GET {TodoListsPath} failed with {(int)response.StatusCode}",
                (int)response.StatusCode,
                "GET",
                TodoListsPath,
                body
            );
        }

        var result = await response.Content.ReadFromJsonAsync<List<ExternalTodoList>>(
            ExternalJsonOptions.Default,
            cancellationToken
        );

        return result
            ?? throw new ExternalApiException(
                $"GET {TodoListsPath} returned empty body",
                (int)response.StatusCode,
                "GET",
                TodoListsPath,
                null
            );
    }

    public async Task<ExternalTodoList> UpdateTodoListAsync(
        string externalId,
        UpdateExternalTodoListRequest request,
        CancellationToken cancellationToken
    )
    {
        var path = $"{TodoListsPath}/{externalId}";
        using var message = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = JsonContent.Create(request, options: ExternalJsonOptions.Default),
        };

        using var response = await _http.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ExternalApiException(
                $"PATCH {path} failed with {(int)response.StatusCode}",
                (int)response.StatusCode,
                "PATCH",
                path,
                body
            );
        }

        var result = await response.Content.ReadFromJsonAsync<ExternalTodoList>(
            ExternalJsonOptions.Default,
            cancellationToken
        );

        return result
            ?? throw new ExternalApiException(
                $"PATCH {path} returned empty body",
                (int)response.StatusCode,
                "PATCH",
                path,
                null
            );
    }
}
