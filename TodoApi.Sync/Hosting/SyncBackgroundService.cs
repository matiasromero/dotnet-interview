using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TodoApi.Sync.Configuration;
using TodoApi.Sync.Data;
using TodoApi.Sync.Models;
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
                var listSync = scope.ServiceProvider.GetRequiredService<ITodoListSyncService>();
                var itemSync = scope.ServiceProvider.GetRequiredService<ITodoListItemSyncService>();

                try
                {
                    var sw = Stopwatch.StartNew();
                    var pushList = await listSync.PushTodoListsAsync(stoppingToken);
                    sw.Stop();
                    _logger.LogInformation(
                        "Sync list push tick completed in {ElapsedMs}ms: total={Total} pushed={Pushed} failed={Failed} status={Status}",
                        sw.ElapsedMilliseconds,
                        pushList.Total,
                        pushList.Pushed,
                        pushList.Failed,
                        pushList.Status
                    );
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sync list push tick threw — continuing with item push");
                }

                try
                {
                    var sw = Stopwatch.StartNew();
                    var pushItem = await itemSync.PushTodoListItemsAsync(stoppingToken);
                    sw.Stop();
                    _logger.LogInformation(
                        "Sync item push tick completed in {ElapsedMs}ms: total={Total} pushed={Pushed} failed={Failed} status={Status}",
                        sw.ElapsedMilliseconds,
                        pushItem.Total,
                        pushItem.Pushed,
                        pushItem.Failed,
                        pushItem.Status
                    );
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sync item push tick threw — continuing with list pull");
                }

                IReadOnlyList<ExternalListWithMapping> mappedExternals =
                    Array.Empty<ExternalListWithMapping>();
                try
                {
                    var sw = Stopwatch.StartNew();
                    var (pullList, mes) = await listSync.PullTodoListsAsync(stoppingToken);
                    mappedExternals = mes;
                    sw.Stop();
                    _logger.LogInformation(
                        "Sync list pull tick completed in {ElapsedMs}ms: total={Total} processed={Processed} failed={Failed} status={Status}",
                        sw.ElapsedMilliseconds,
                        pullList.Total,
                        pullList.Pushed,
                        pullList.Failed,
                        pullList.Status
                    );
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sync list pull tick threw — continuing with item pull");
                }

                if (mappedExternals.Count > 0)
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var pullItem = await itemSync.PullTodoListItemsAsync(
                            mappedExternals,
                            stoppingToken
                        );
                        sw.Stop();
                        _logger.LogInformation(
                            "Sync item pull tick completed in {ElapsedMs}ms: total={Total} processed={Processed} failed={Failed} status={Status}",
                            sw.ElapsedMilliseconds,
                            pullItem.Total,
                            pullItem.Pushed,
                            pullItem.Failed,
                            pullItem.Status
                        );
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Sync item pull tick threw — will retry on next interval"
                        );
                    }
                }

                try
                {
                    var retention = _options.CurrentValue.OutboxRetention;
                    if (retention <= TimeSpan.Zero)
                    {
                        _logger.LogDebug(
                            "Outbox retention disabled (OutboxRetention={Retention})",
                            retention
                        );
                    }
                    else
                    {
                        var sw = Stopwatch.StartNew();
                        var db = scope.ServiceProvider.GetRequiredService<ISyncDbContext>();
                        var cutoff = DateTime.UtcNow - retention;
                        var purged = await db.PurgeProcessedOutboxEventsAsync(
                            cutoff,
                            stoppingToken
                        );
                        sw.Stop();
                        _logger.LogInformation(
                            "Sync outbox retention tick completed in {ElapsedMs}ms: purged={Count} olderThan={Cutoff:o}",
                            sw.ElapsedMilliseconds,
                            purged,
                            cutoff
                        );
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Outbox retention purge tick threw");
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
