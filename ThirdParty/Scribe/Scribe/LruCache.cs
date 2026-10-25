// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Scribe;

sealed class LruCache<K, V> where K : notnull
{
    int _capacity;
    readonly Dictionary<K, LinkedListNode<(K key, V val, long weight)>> _map = new();
    readonly LinkedList<(K key, V val, long weight)> _lru = new();
    long _maximumWeight = long.MaxValue;

    public LruCache(int capacity) { _capacity = Math.Max(1, capacity); }
    public int Count => _map.Count;
    public int Capacity { get => _capacity; set { _capacity = Math.Max(1, value); Trim(); } }
    public long Weight { get; private set; }
    public long MaximumWeight
    {
        get => _maximumWeight;
        set
        {
#if NETSTANDARD2_1
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
#else
            ArgumentOutOfRangeException.ThrowIfNegative(value);
#endif
            _maximumWeight = value; Trim();
        }
    }

    public bool TryGetValue(K k, out V v)
    {
        if (_map.TryGetValue(k, out var node))
        {
            _lru.Remove(node); _lru.AddFirst(node);
            v = node.Value.val; return true;
        }
        v = default!; return false;
    }

    public void Add(K k, V v, long weight = 1)
    {
#if NETSTANDARD2_1
        if (weight < 0) throw new ArgumentOutOfRangeException(nameof(weight));
#else
        ArgumentOutOfRangeException.ThrowIfNegative(weight);
#endif
        // Oversized values are usable by the caller but must not evict the whole working set.
        if (weight > _maximumWeight) { Remove(k); return; }
        if (_map.TryGetValue(k, out var node))
        {
            Weight -= node.Value.weight;
            node.Value = (k, v, 0);
            _lru.Remove(node); _lru.AddFirst(node);
            MakeRoom(weight);
            node.Value = (k, v, weight); Weight += weight;
        }
        else
        {
            MakeRoom(weight);
            var nn = new LinkedListNode<(K, V, long)>((k, v, weight));
            _lru.AddFirst(nn);
            _map[k] = nn;
            Weight += weight;
        }
        Trim();
    }

    public void UpdateWeight(K key, V expected, long weight)
    {
#if NETSTANDARD2_1
        if (weight < 0) throw new ArgumentOutOfRangeException(nameof(weight));
#else
        ArgumentOutOfRangeException.ThrowIfNegative(weight);
#endif
        if (!_map.TryGetValue(key, out var node) || !ReferenceEquals(node.Value.val, expected)) return;
        Add(key, expected, weight);
    }

    void MakeRoom(long weight)
    {
        while (Weight > _maximumWeight - weight)
            Remove(_lru.Last!.Value.key);
    }

    void Remove(K key)
    {
        if (!_map.Remove(key, out var node)) return;
        Weight -= node.Value.weight; _lru.Remove(node);
    }

    void Trim()
    {
        while (_map.Count > _capacity || Weight > _maximumWeight)
        {
            var last = _lru.Last; if (last == null) break;
            Remove(last.Value.key);
        }
    }

    public void Clear() { _map.Clear(); _lru.Clear(); Weight = 0; }
}
