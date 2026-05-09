using Microsoft.AspNetCore.Mvc;
using TodoApi.Dtos;
using TodoApi.Models;
using TodoApi.Services;

namespace TodoApi.Controllers
{
    [Route("api/todolists/{todoListId}/items")]
    [ApiController]
    public class TodoListItemsController : ControllerBase
    {
        private readonly ITodoListItemService _todoListItemService;

        public TodoListItemsController(ITodoListItemService todoListItemService)
        {
            _todoListItemService = todoListItemService;
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
        public async Task<ActionResult> PutTodoListItem(long todoListId, long id, UpdateTodoListItem payload)
        {
            var updated = await _todoListItemService.UpdateAsync(todoListId, id, payload);

            if (!updated)
            {
                return NotFound();
            }

            var item = await _todoListItemService.GetByIdAsync(todoListId, id);
            return Ok(item);
        }

        // POST: api/todolists/5/items
        [HttpPost]
        public async Task<ActionResult<TodoListItem>> PostTodoListItem(long todoListId, CreateTodoListItem payload)
        {
            var item = await _todoListItemService.CreateAsync(todoListId, payload);

            if (item == null)
            {
                return NotFound();
            }

            return CreatedAtAction("GetTodoListItem", new { todoListId, id = item.Id }, item);
        }

        // DELETE: api/todolists/5/items/3
        [HttpDelete("{id}")]
        public async Task<ActionResult> DeleteTodoListItem(long todoListId, long id)
        {
            var deleted = await _todoListItemService.DeleteAsync(todoListId, id);

            if (!deleted)
            {
                return NotFound();
            }

            return NoContent();
        }
    }
}
