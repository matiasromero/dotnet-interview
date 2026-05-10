using Microsoft.AspNetCore.Mvc;
using TodoApi.Dtos;
using TodoApi.Services;
using TodoApi.Sync.Models;
using TodoApi.Sync.Services;

namespace TodoApi.Controllers
{
    [Route("api/sync")]
    [ApiController]
    public class SyncController : ControllerBase
    {
        private readonly ITodoListSyncService _listSync;
        private readonly ITodoListItemSyncService _itemSync;
        private readonly ILogger<SyncController> _logger;
        private readonly ISyncStatusService _statusService;

        public SyncController(
            ITodoListSyncService listSync,
            ITodoListItemSyncService itemSync,
            ILogger<SyncController> logger,
            ISyncStatusService statusService
        )
        {
            _listSync = listSync;
            _itemSync = itemSync;
            _logger = logger;
            _statusService = statusService;
        }

        // GET: api/sync/status
        [HttpGet("status")]
        public async Task<ActionResult<SyncStatusResponse>> GetStatus(
            CancellationToken cancellationToken
        )
        {
            var snapshot = await _statusService.GetStatusAsync(cancellationToken);
            return Ok(snapshot);
        }

        // POST: api/sync/run
        [HttpPost("run")]
        public async Task<ActionResult<SyncRunResponse>> Run(CancellationToken cancellationToken)
        {
            var listPush = await SafeAsync(
                () => _listSync.PushTodoListsAsync(cancellationToken),
                "list push"
            );

            var itemPush = await SafeAsync(
                () => _itemSync.PushTodoListItemsAsync(cancellationToken),
                "item push"
            );

            SyncRunResult listPull;
            IReadOnlyList<ExternalListWithMapping> mappedExternals =
                Array.Empty<ExternalListWithMapping>();
            try
            {
                var (result, mapped) = await _listSync.PullTodoListsAsync(cancellationToken);
                listPull = result;
                mappedExternals = mapped;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync list pull threw — reporting Failed");
                listPull = new SyncRunResult(0, 0, 0, SyncRunStatus.Failed);
            }

            SyncRunResult itemPull;
            if (mappedExternals.Count > 0)
            {
                itemPull = await SafeAsync(
                    () => _itemSync.PullTodoListItemsAsync(mappedExternals, cancellationToken),
                    "item pull"
                );
            }
            else
            {
                itemPull = new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded);
            }

            return Ok(new SyncRunResponse(listPush, itemPush, listPull, itemPull));
        }

        private async Task<SyncRunResult> SafeAsync(
            Func<Task<SyncRunResult>> operation,
            string label
        )
        {
            try
            {
                return await operation();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync {Label} threw — reporting Failed", label);
                return new SyncRunResult(0, 0, 0, SyncRunStatus.Failed);
            }
        }
    }
}
