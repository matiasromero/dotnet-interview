using System.Net;

namespace TodoApi.Tests.Sync.TestHelpers;

public class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> RequestBodies { get; } = new();

    public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public StubHttpMessageHandler(HttpStatusCode status, string? body = null)
        : this(_ =>
            Task.FromResult(
                new HttpResponseMessage(status)
                {
                    Content = body is null
                        ? null
                        : new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                }
            )
        ) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }
        return await _responder(request);
    }
}
