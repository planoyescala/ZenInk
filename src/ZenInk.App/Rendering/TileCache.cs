//-----------------------------------------------------------------------------------------
// <copyright file="TileCache.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using Microsoft.Graphics.Canvas;

using ZenInk.Core;

namespace ZenInk_App.Rendering;

/// <summary>
/// LRU cache of rasterized tiles, keyed by <see cref="TileKey"/>. Bounded by
/// byte budget rather than tile count, since tiles from different levels are
/// all the same pixel size but the budget is what actually matters for memory.
/// UI-thread only: <see cref="CanvasBitmap"/> instances are tied to the
/// CanvasControl's device.
/// </summary>
public sealed class TileCache
{
    private readonly long _budgetBytes;
    private readonly LinkedList<TileKey> _lru = new();
    private readonly Dictionary<TileKey, (CanvasBitmap Bitmap, LinkedListNode<TileKey> Node)> _entries = new();
    private long _usedBytes;

    public TileCache(long budgetBytes)
    {
        _budgetBytes = budgetBytes;
    }

    public bool TryGet(TileKey key, out CanvasBitmap bitmap)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            _lru.Remove(entry.Node);
            _lru.AddLast(entry.Node);
            bitmap = entry.Bitmap;
            return true;
        }

        bitmap = null!;
        return false;
    }

    public void Add(TileKey key, CanvasBitmap bitmap)
    {
        if (_entries.ContainsKey(key))
        {
            return;
        }

        var node = _lru.AddLast(key);
        _entries[key] = (bitmap, node);
        _usedBytes += EstimateBytes(bitmap);

        while (_usedBytes > _budgetBytes && _lru.First is { } oldest && oldest.Value != key)
        {
            EvictOldest();
        }
    }

    public void Clear()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Bitmap.Dispose();
        }
        _entries.Clear();
        _lru.Clear();
        _usedBytes = 0;
    }

    private void EvictOldest()
    {
        var oldest = _lru.First!;
        var key = oldest.Value;
        if (_entries.Remove(key, out var entry))
        {
            _usedBytes -= EstimateBytes(entry.Bitmap);
            entry.Bitmap.Dispose();
        }
        _lru.RemoveFirst();
    }

    private static long EstimateBytes(CanvasBitmap bitmap) =>
        (long)bitmap.SizeInPixels.Width * bitmap.SizeInPixels.Height * 4;
}
