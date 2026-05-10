using Microsoft.Extensions.Options;
using TodoApi.Configuration;

namespace TodoApi.Hosting;

/// <summary>
/// <c>BackgroundService</c> shell that drives <see cref="IOutboxBroadcaster"/>: initialises
/// the cursor at startup (so historical events aren't replayed) and then loops every
/// <see cref="RealtimeOptions.BroadcastInterval"/>, asking the broadcaster to forward any
/// new outbox events. All real work lives in <see cref="IOutboxBroadcaster"/>; this class
/// is the timing wrapper, intentionally thin and lightly tested (the broadcaster has the
/// behavioural tests and the SignalR end-to-end test exercises the full path).
/// </summary>
public sealed class OutboxBroadcastService : BackgroundService
{
    private readonly IOutboxBroadcaster _broadcaster;
    private readonly IOptions<RealtimeOptions> _options;
    private readonly ILogger<OutboxBroadcastService> _logger;

    public OutboxBroadcastService(
        IOutboxBroadcaster broadcaster,
        IOptions<RealtimeOptions> options,
        ILogger<OutboxBroadcastService> logger
    )
    {
        _broadcaster = broadcaster;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long cursor;
        try
        {
            cursor = await _broadcaster.GetInitialCursorAsync(stoppingToken);
            _logger.LogInformation("OutboxBroadcastService starting at cursor={Cursor}", cursor);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // Failing to read MAX(Id) at startup is not fatal: we fall back to 0 and
            // accept replaying historical events on the first tick. Logged so it's
            // visible in monitoring.
            _logger.LogError(
                ex,
                "OutboxBroadcastService failed to read initial cursor; defaulting to 0"
            );
            cursor = 0;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                cursor = await _broadcaster.BroadcastBatchAsync(cursor, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Per-tick failures are isolated: log, keep cursor unchanged, retry next tick.
                // The BackgroundService must not die — the sync engine keeps writing events
                // and the next successful tick will catch up.
                _logger.LogError(
                    ex,
                    "OutboxBroadcastService tick failed; cursor preserved at {Cursor}",
                    cursor
                );
            }

            try
            {
                await Task.Delay(_options.Value.BroadcastInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
