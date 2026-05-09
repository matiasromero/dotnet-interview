namespace TodoApi.Sync.External;

public sealed class ExternalApiException : Exception
{
    public int? StatusCode { get; }
    public string? Method { get; }
    public string? Path { get; }
    public string? Body { get; }

    public ExternalApiException(
        string message,
        int? statusCode,
        string? method,
        string? path,
        string? body,
        Exception? inner = null
    )
        : base(message, inner)
    {
        StatusCode = statusCode;
        Method = method;
        Path = path;
        Body = body;
    }
}
