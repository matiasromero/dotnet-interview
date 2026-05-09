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
            using (var scope = _scopes.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<ITodoListSyncService>();

                try
                {
                    var pushResult = await svc.PushTodoListsAsync(stoppingToken);
                    _logger.LogInformation(
                        "Sync push tick: total={Total} pushed={Pushed} failed={Failed} status={Status}",
                        pushResult.Total,
                        pushResult.Pushed,
                        pushResult.Failed,
                        pushResult.Status
                    );
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sync push tick threw — continuing with pull");
                }

                try
                {
                    var (pullResult, _) = await svc.PullTodoListsAsync(stoppingToken);
                    _logger.LogInformation(
                        "Sync pull tick: total={Total} processed={Processed} failed={Failed} status={Status}",
                        pullResult.Total,
                        pullResult.Pushed,
                        pullResult.Failed,
                        pullResult.Status
                    );
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sync pull tick threw — will retry on next interval");
                }
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
