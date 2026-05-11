using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TodoApi.Dtos;
using TodoApi.Middleware;
using TodoApi.Models;
using TodoApi.Services;

namespace TodoApi.Controllers
{
    [Route("api/todolists")]
    [ApiController]
    public class TodoListsController : ControllerBase
    {
        private readonly ITodoListService _todoListService;
        private readonly ILogger<TodoListsController> _logger;

        public TodoListsController(
            ITodoListService todoListService,
            ILogger<TodoListsController>? logger = null
        )
        {
            _todoListService = todoListService;
            _logger = logger ?? NullLogger<TodoListsController>.Instance;
        }

        // GET: api/todolists
        [HttpGet]
        public async Task<ActionResult<IList<TodoList>>> GetTodoLists()
        {
            return Ok(await _todoListService.GetAllAsync());
        }

        // GET: api/todolists/5
        [HttpGet("{id}")]
        public async Task<ActionResult<TodoList>> GetTodoList(long id)
        {
            var todoList = await _todoListService.GetByIdAsync(id);

            if (todoList == null)
            {
                return NotFound();
            }

            return Ok(todoList);
        }

        // PUT: api/todolists/5
        // To protect from over-posting attacks, see https://go.microsoft.com/fwlink/?linkid=2123754
        [HttpPut("{id}")]
        public async Task<ActionResult> PutTodoList(long id, UpdateTodoList payload)
        {
            var correlationId = HttpContext.GetCorrelationId();
            _logger.LogInformation(
                "PUT /todolists/{TodoListId} {TodoListName} (CorrelationId: {CorrelationId})",
                id,
                payload.Name,
                correlationId
            );
            var updated = await _todoListService.UpdateAsync(id, payload);

            if (!updated)
            {
                return NotFound();
            }

            _logger.LogInformation(
                "Updated TodoList {TodoListId} (CorrelationId: {CorrelationId})",
                id,
                correlationId
            );
            var todoList = await _todoListService.GetByIdAsync(id);
            return Ok(todoList);
        }

        // POST: api/todolists
        // To protect from over-posting attacks, see https://go.microsoft.com/fwlink/?linkid=2123754
        [HttpPost]
        public async Task<ActionResult<TodoList>> PostTodoList(CreateTodoList payload)
        {
            var correlationId = HttpContext.GetCorrelationId();
            _logger.LogInformation(
                "POST /todolists {TodoListName} (CorrelationId: {CorrelationId})",
                payload.Name,
                correlationId
            );
            var todoList = await _todoListService.CreateAsync(payload);
            _logger.LogInformation(
                "Created TodoList {TodoListId} (CorrelationId: {CorrelationId})",
                todoList.Id,
                correlationId
            );
            return CreatedAtAction("GetTodoList", new { id = todoList.Id }, todoList);
        }

        // DELETE: api/todolists/5
        [HttpDelete("{id}")]
        public async Task<ActionResult> DeleteTodoList(long id)
        {
            var correlationId = HttpContext.GetCorrelationId();
            _logger.LogInformation(
                "DELETE /todolists/{TodoListId} (CorrelationId: {CorrelationId})",
                id,
                correlationId
            );
            var deleted = await _todoListService.DeleteAsync(id);
            if (!deleted)
            {
                return NotFound();
            }

            _logger.LogInformation(
                "Deleted TodoList {TodoListId} (CorrelationId: {CorrelationId})",
                id,
                correlationId
            );
            return NoContent();
        }
    }
}
