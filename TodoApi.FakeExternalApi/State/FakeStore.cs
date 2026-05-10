using System.Collections.Concurrent;
using TodoApi.FakeExternalApi.Models;

namespace TodoApi.FakeExternalApi.State;

public sealed class FakeStore
{
    private const int RequestLogCapacity = 50;

    private readonly ConcurrentDictionary<string, ExternalTodoList> _lists = new();
    private readonly ConcurrentDictionary<Guid, ExternalTodoList> _idempotencyCache = new();
    private readonly ConcurrentQueue<RequestLogEntry> _requestLog = new();
    private readonly object _writeLock = new();

    public IReadOnlyList<ExternalTodoList> GetAll()
    {
        return _lists.Values.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).ToList();
    }

    public ExternalTodoList? Find(string id)
    {
        return _lists.TryGetValue(id, out var list) ? list : null;
    }

    public bool TryGetIdempotent(Guid key, out ExternalTodoList? cached)
    {
        if (_idempotencyCache.TryGetValue(key, out var found))
        {
            cached = found;
            return true;
        }
        cached = null;
        return false;
    }

    public ExternalTodoList CreateList(CreateTodoListRequest req, Guid? idempotencyKey)
    {
        var now = DateTime.UtcNow;
        var list = new ExternalTodoList
        {
            Id = Guid.NewGuid().ToString(),
            SourceId = req.SourceId ?? "",
            Name = req.Name,
            CreatedAt = now,
            UpdatedAt = now,
            Items = (req.Items ?? new List<CreateTodoItemRequest>())
                .Select(i => new ExternalTodoItem
                {
                    Id = Guid.NewGuid().ToString(),
                    SourceId = i.SourceId ?? "",
                    Description = i.Description,
                    Completed = i.Completed,
                    CreatedAt = now,
                    UpdatedAt = now,
                })
                .ToList(),
        };

        lock (_writeLock)
        {
            _lists[list.Id] = list;
            if (idempotencyKey.HasValue)
            {
                _idempotencyCache[idempotencyKey.Value] = list;
            }
        }

        return list;
    }

    public ExternalTodoList? UpdateList(string id, UpdateTodoListRequest req)
    {
        lock (_writeLock)
        {
            if (!_lists.TryGetValue(id, out var list))
                return null;

            if (req.Name is not null)
                list.Name = req.Name;
            list.UpdatedAt = DateTime.UtcNow;
            return list;
        }
    }

    public bool DeleteList(string id)
    {
        lock (_writeLock)
        {
            return _lists.TryRemove(id, out _);
        }
    }

    public ExternalTodoItem? UpdateItem(string listId, string itemId, UpdateTodoItemRequest req)
    {
        lock (_writeLock)
        {
            if (!_lists.TryGetValue(listId, out var list))
                return null;
            var item = list.Items.FirstOrDefault(i => i.Id == itemId);
            if (item is null)
                return null;
            if (req.Description is not null)
                item.Description = req.Description;
            if (req.Completed is not null)
                item.Completed = req.Completed.Value;
            item.UpdatedAt = DateTime.UtcNow;
            list.UpdatedAt = item.UpdatedAt;
            return item;
        }
    }

    public bool DeleteItem(string listId, string itemId)
    {
        lock (_writeLock)
        {
            if (!_lists.TryGetValue(listId, out var list))
                return false;
            var idx = list.Items.FindIndex(i => i.Id == itemId);
            if (idx < 0)
                return false;
            list.Items.RemoveAt(idx);
            list.UpdatedAt = DateTime.UtcNow;
            return true;
        }
    }

    public void Reset()
    {
        lock (_writeLock)
        {
            _lists.Clear();
            _idempotencyCache.Clear();
            _requestLog.Clear();
        }
    }

    public void Seed(IEnumerable<ExternalTodoList> lists)
    {
        lock (_writeLock)
        {
            foreach (var list in lists)
            {
                if (string.IsNullOrEmpty(list.Id))
                    list.Id = Guid.NewGuid().ToString();
                foreach (var item in list.Items)
                {
                    if (string.IsNullOrEmpty(item.Id))
                        item.Id = Guid.NewGuid().ToString();
                }
                _lists[list.Id] = list;
            }
        }
    }

    public void LogRequest(RequestLogEntry entry)
    {
        _requestLog.Enqueue(entry);
        while (_requestLog.Count > RequestLogCapacity)
        {
            _requestLog.TryDequeue(out _);
        }
    }

    public IReadOnlyList<RequestLogEntry> GetRecentRequests()
    {
        return _requestLog.ToArray().Reverse().ToList();
    }
}
