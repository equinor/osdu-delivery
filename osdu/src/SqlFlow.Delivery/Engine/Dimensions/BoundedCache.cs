using System.Diagnostics.CodeAnalysis;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// Values kept by key up to a number of them, the one used longest ago let go first to make room for a new one: what a build
/// keeps of the records many keys reach, so it reads each once however many keys reach it, in bounded memory. A value may be
/// null, which is kept as what was found (a record the search does not hold) and told apart from no value kept. Safe to use
/// from several threads at once.
/// </summary>
internal sealed class BoundedCache<TKey, TValue>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly Lock _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _entries;
    private readonly LinkedList<(TKey Key, TValue Value)> _used = new();

    /// <param name="capacity">The most values kept.</param>
    /// <param name="comparer">How keys compare; the default comparer of the key's type when null.</param>
    public BoundedCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _entries = new Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>>(comparer);
    }

    /// <summary>How many values are kept.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The value kept under <paramref name="key"/>, now the one used last; false when none is kept.</summary>
    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                value = default;
                return false;
            }

            _used.Remove(node);
            _used.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    /// <summary>Keeps <paramref name="value"/> under <paramref name="key"/>, letting go of the value used longest ago when it is full.</summary>
    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                node.Value = (key, value);
                _used.Remove(node);
                _used.AddFirst(node);
                return;
            }

            _entries[key] = _used.AddFirst((key, value));
            if (_entries.Count > _capacity)
            {
                var oldest = _used.Last!;
                _used.RemoveLast();
                _entries.Remove(oldest.Value.Key);
            }
        }
    }
}
