using System.ComponentModel.DataAnnotations;

namespace TodoApi.Sync.Configuration;

public class ExternalApiOptions
{
    [Required, Url]
    public string BaseAddress { get; set; } = "http://localhost:8080";

    [Range(0, 10)]
    public int RetryMaxAttempts { get; set; } = 3;

    [Range(1, 60)]
    public int PerAttemptTimeoutSeconds { get; set; } = 10;
}
