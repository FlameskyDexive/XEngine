// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.Rendering;

/// <summary>One sprite instance in the retained 2D collect list.</summary>
public readonly struct SpriteInstanceRecord
{
    public SpriteInstanceRecord(int rendererId, int sortingLayer, int sortingOrder, float distance, int materialId, int atlasId)
    {
        RendererId = rendererId;
        SortingLayer = sortingLayer;
        SortingOrder = sortingOrder;
        Distance = distance;
        MaterialId = materialId;
        AtlasId = atlasId;
    }

    public int RendererId { get; }
    public int SortingLayer { get; }
    public int SortingOrder { get; }
    public float Distance { get; }
    public int MaterialId { get; }
    public int AtlasId { get; }
}

/// <summary>A run of sorted instances that share material and atlas and can be one draw.</summary>
public readonly struct SpriteBatchRange
{
    public SpriteBatchRange(int start, int count, int materialId, int atlasId)
    {
        Start = start;
        Count = count;
        MaterialId = materialId;
        AtlasId = atlasId;
    }

    public int Start { get; }
    public int Count { get; }
    public int MaterialId { get; }
    public int AtlasId { get; }
}

/// <summary>
/// Sorts sprite instances by layer/order/distance/stable id, then merges adjacent runs that share
/// pipeline/material/atlas so transparent order is preserved.
/// </summary>
public sealed class SpriteBatchCollector
{
    private static readonly SpriteSortComparer Comparer = new();

    private readonly List<SpriteInstanceRecord> _items = new();
    private readonly List<SpriteBatchRange> _batches = new();
    private SpriteInstanceRecord[] _sorted = Array.Empty<SpriteInstanceRecord>();
    private int _count;

    public int InstanceCount => _count;
    public int BatchCount => _batches.Count;
    public SpriteInstanceRecord[] Sorted => _sorted;
    public IReadOnlyList<SpriteBatchRange> Batches => _batches;

    public void Begin()
    {
        _count = 0;
        _batches.Clear();
    }

    public void Add(in SpriteInstanceRecord record)
    {
        if (_count == _items.Count)
            _items.Add(record);
        else
            _items[_count] = record;
        _count++;
    }

    public void SortAndBatch()
    {
        _batches.Clear();
        if (_count == 0)
        {
            _sorted = Array.Empty<SpriteInstanceRecord>();
            return;
        }

        if (_sorted.Length < _count)
            _sorted = new SpriteInstanceRecord[_count];

        for (int i = 0; i < _count; i++)
            _sorted[i] = _items[i];

        Array.Sort(_sorted, 0, _count, Comparer);

        int start = 0;
        int materialId = _sorted[0].MaterialId;
        int atlasId = _sorted[0].AtlasId;
        for (int i = 1; i < _count; i++)
        {
            SpriteInstanceRecord current = _sorted[i];
            if (current.MaterialId == materialId && current.AtlasId == atlasId)
                continue;
            _batches.Add(new SpriteBatchRange(start, i - start, materialId, atlasId));
            start = i;
            materialId = current.MaterialId;
            atlasId = current.AtlasId;
        }

        _batches.Add(new SpriteBatchRange(start, _count - start, materialId, atlasId));
    }

    private sealed class SpriteSortComparer : IComparer<SpriteInstanceRecord>
    {
        public int Compare(SpriteInstanceRecord x, SpriteInstanceRecord y)
        {
            int c = x.SortingLayer.CompareTo(y.SortingLayer);
            if (c != 0) return c;
            c = x.SortingOrder.CompareTo(y.SortingOrder);
            if (c != 0) return c;
            c = x.Distance.CompareTo(y.Distance);
            if (c != 0) return c;
            return x.RendererId.CompareTo(y.RendererId);
        }
    }
}
