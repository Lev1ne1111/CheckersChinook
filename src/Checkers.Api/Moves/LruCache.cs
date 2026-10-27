namespace Checkers.Api.Moves;

/// <summary>
/// Thread-safe LRU cache with a time-to-live per entry. A hit moves the entry to the front;
/// when full, the entry at the back (least recently used) is dropped. Expired entries are
/// dropped when they are looked up or reach the back.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private sealed record Entry(TKey Key, TValue Value, DateTime ExpiresUtc);

    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _order = new();   // front = most recently used
    private readonly object _lock = new();

    public LruCache(int capacity, TimeSpan ttl, Func<DateTime>? clock = null)
    {
        _capacity = Math.Max(0, capacity);
        _ttl = ttl;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public int Count { get { lock (_lock) return _map.Count; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                if (node.Value.ExpiresUtc > _clock())
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
                _order.Remove(node);
                _map.Remove(key);
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_capacity == 0) return;
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var old))
            {
                _order.Remove(old);
                _map.Remove(key);
            }
            while (_map.Count >= _capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
            _map[key] = _order.AddFirst(new Entry(key, value, _clock() + _ttl));
        }
    }
}
