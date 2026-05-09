using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using TodoApi.Sync.Configuration;
using TodoApi.Sync.External;
using TodoApi.Sync.Hosting;
using TodoApi.Sync.Services;

namespace TodoApi.Sync.DependencyInjection;

public static class SyncServiceCollectionExtensions
{
    public static IServiceCollection AddTodoSync(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<ExternalApiOptions>()
            .Bind(configuration.GetSection("ExternalApi"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SyncOptions>().Bind(configuration.GetSection("Sync"));

        services
            .AddHttpClient<IExternalTodoListClient, ExternalTodoListClient>(
                (sp, http) =>
                {
                    var opts = sp.GetRequiredService<IOptions<ExternalApiOptions>>().Value;
                    http.BaseAddress = new Uri(opts.BaseAddress);
                }
            )
            .AddResilienceHandler(
                "external-todo",
                (builder, ctx) =>
                {
                    var opts = ctx
                        .ServiceProvider.GetRequiredService<IOptions<ExternalApiOptions>>()
                        .Value;

                    builder
                        .AddRetry(
                            new HttpRetryStrategyOptions
                            {
                                MaxRetryAttempts = opts.RetryMaxAttempts,
                                BackoffType = DelayBackoffType.Exponential,
                                UseJitter = true,
                                Delay = TimeSpan.FromSeconds(1),
                                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                                    .Handle<HttpRequestException>()
                                    .Handle<Polly.Timeout.TimeoutRejectedException>()
                                    .HandleResult(r =>
                                        (int)r.StatusCode >= 500
                                        || r.StatusCode == HttpStatusCode.RequestTimeout
                                        || r.StatusCode == HttpStatusCode.TooManyRequests
                                    ),
                            }
                        )
                        .AddCircuitBreaker(
                            new HttpCircuitBreakerStrategyOptions
                            {
                                FailureRatio = 0.5,
                                SamplingDuration = TimeSpan.FromSeconds(30),
                                MinimumThroughput = 5,
                                BreakDuration = TimeSpan.FromSeconds(30),
                            }
                        )
                        .AddTimeout(TimeSpan.FromSeconds(opts.PerAttemptTimeoutSeconds));
                }
            );

        services.AddScoped<ITodoListSyncService, TodoListSyncService>();
        services.AddScoped<ITodoListItemSyncService, TodoListItemSyncService>();
        services.AddHostedService<SyncBackgroundService>();

        return services;
    }
}
