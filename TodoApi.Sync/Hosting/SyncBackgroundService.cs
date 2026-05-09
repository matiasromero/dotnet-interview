using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TodoApi.Sync.Configuration;
using TodoApi.Sync.Services;

namespace TodoApi.Sync.Hosting;

public sealed class SyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<SyncOptions> _options;
    private readonly ILogger<SyncBackgroundService> _logger;

    public SyncBackgroundService(
        IServiceScopeFactory scopes,
        IOptionsMonitor<SyncOptions> options,
        ILogger<SyncBackgroundService> logger
    )
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startup = _options.CurrentValue;
        if (!startup.Enabled)
        {
            _logger.LogInformation("Sync background service disabled via config; idling");
            return;
        }

        try
        {
            await Task.Delay(startup.StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<ITodoListSyncService>();
                var result = await svc.PushTodoListsAsync(stoppingToken);
                _logger.LogInformation(
                    "Sync tick completed: total={Total} pushed={Pushed} failed={Failed} status={Status}",
                    result.Total,
                    result.Pushed,
                    result.Failed,
                    result.Status
                );
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync tick threw — will retry on next interval");
            }

            try
            {
                await Task.Delay(_options.CurrentValue.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
