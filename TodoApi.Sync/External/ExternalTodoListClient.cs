using System.Net.Http.Json;
using TodoApi.Sync.External.Models;

namespace TodoApi.Sync.External;

public class ExternalTodoListClient : IExternalTodoListClient
{
    private readonly HttpClient _http;

    public ExternalTodoListClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<ExternalTodoList> CreateTodoListAsync(
        CreateExternalTodoListRequest request,
        CancellationToken cancellationToken
    )
    {
        using var response = await _http.PostAsJsonAsync(
            "todolists",
            request,
            ExternalJsonOptions.Default,
            cancellationToken
        );

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ExternalApiException(
                $"POST todolists failed with {(int)response.StatusCode}",
                (int)response.StatusCode,
                "POST",
                "todolists",
                body
            );
        }

        var result = await response.Content.ReadFromJsonAsync<ExternalTodoList>(
            ExternalJsonOptions.Default,
            cancellationToken
        );

        return result
            ?? throw new ExternalApiException(
                "POST todolists returned empty body",
                (int)response.StatusCode,
                "POST",
                "todolists",
                null
            );
    }
}
