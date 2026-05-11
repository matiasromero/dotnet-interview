using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TodoApi.Dtos;
using TodoApi.Middleware;
using TodoApi.Models;
using TodoApi.Services;

namespace TodoApi.Controllers
{
    [Route("api/todolists/{todoListId}/items")]
    [ApiController]
    public class TodoListItemsController : ControllerBase
    {
        private readonly ITodoListItemService _todoListItemService;
        private readonly ILogger<TodoListItemsController> _logger;

        public TodoListItemsController(
            ITodoListItemService todoListItemService,
            ILogger<TodoListItemsController>? logger = null
        )
        {
            _todoListItemService = todoListItemService;
            _logger = logger ?? NullLogger<TodoListItemsController>.Instance;
        }

        // GET: api/todolists/5/items
        [HttpGet]
        public async Task<ActionResult<IList<TodoListItem>>> GetTodoListItems(long todoListId)
        {
            var items = await _todoListItemService.GetAllAsync(todoListId);

            if (items == null)
            {
                return NotFound();
            }

            return Ok(items);
        }

        // GET: api/todolists/5/items/3
        [HttpGet("{id}")]
        public async Task<ActionResult<TodoListItem>> GetTodoListItem(long todoListId, long id)
        {
            var item = await _todoListItemService.GetByIdAsync(todoListId, id);

            if (item == null)
            {
                return NotFound();
            }

            return Ok(item);
        }

        // PUT: api/todolists/5/items/3
        [HttpPut("{id}")]
        public async Task<ActionResult> PutTodoListItem(
            long todoListId,
            long id,
            UpdateTodoListItem payload
        )
        {
            var correlationId = HttpContext.GetCorrelationId();
            _logger.LogInformation(
                "PUT /todolists/{TodoListId}/items/{TodoListItemId} (CorrelationId: {CorrelationId})",
                todoListId,
                id,
                correlationId
            );
            var updated = await _todoListItemService.UpdateAsync(todoListId, id, payload);

            if (!updated)
            {
                return NotFound();
            }

            _logger.LogInformation(
                "Updated TodoListItem {TodoListItemId} in TodoList {TodoListId} (CorrelationId: {CorrelationId})",
                id,
                todoListId,
                correlationId
            );
            var item = await _todoListItemService.GetByIdAsync(todoListId, id);
            return Ok(item);
        }

        // POST: api/todolists/5/items
        [HttpPost]
        public async Task<ActionResult<TodoListItem>> PostTodoListItem(
            long todoListId,
            CreateTodoListItem payload
        )
        {
            var correlationId = HttpContext.GetCorrelationId();
            _logger.LogInformation(
                "POST /todolists/{TodoListId}/items {TodoListItemDescription} (CorrelationId: {CorrelationId})",
                todoListId,
                payload.Description,
                correlationId
            );
            var item = await _todoListItemService.CreateAsync(todoListId, payload);

            if (item == null)
            {
                return NotFound();
            }

            _logger.LogInformation(
                "Created TodoListItem {TodoListItemId} in TodoList {TodoListId} (CorrelationId: {CorrelationId})",
                item.Id,
                todoListId,
                correlationId
            );
            return CreatedAtAction("GetTodoListItem", new { todoListId, id = item.Id }, item);
        }

        // DELETE: api/todolists/5/items/3
        [HttpDelete("{id}")]
        public async Task<ActionResult> DeleteTodoListItem(long todoListId, long id)
        {
            var correlationId = HttpContext.GetCorrelationId();
            _logger.LogInformation(
                "DELETE /todolists/{TodoListId}/items/{TodoListItemId} (CorrelationId: {CorrelationId})",
                todoListId,
                id,
                correlationId
            );
            var deleted = await _todoListItemService.DeleteAsync(todoListId, id);

            if (!deleted)
            {
                return NotFound();
            }

            _logger.LogInformation(
                "Deleted TodoListItem {TodoListItemId} from TodoList {TodoListId} (CorrelationId: {CorrelationId})",
                id,
                todoListId,
                correlationId
            );
            return NoContent();
        }
    }
}
