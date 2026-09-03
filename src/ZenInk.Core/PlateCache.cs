//-----------------------------------------------------------------------------------------
// <copyright file="PlateCache.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

namespace ZenInk.Core;

/// <summary>
/// One rasterized square of a page, before anything is done with it. The
/// comparison composes two of these into a tile.
///
/// Everything that decides the pixels is in the key, and nothing else is: the
/// page, the turn it was drawn at, the size the whole scaled page came out, and
/// where that page sits relative to the square. Two requests that agree on all
/// of those produce the same bitmap, which is what makes keeping it worth
/// anything.
///
/// What is deliberately *not* here is the palette. Which colours a comparison
/// is read in has nothing to do with rasterizing either half of it, and that is
/// the whole reason this cache pays: recolouring a sheet becomes composing
/// again rather than rendering again.
/// </summary>
public readonly record struct PlateKey(
    int DocumentId,
    int PageIndex,
    int Rotation,
    int PageWidth,
    int PageHeight,
    int OriginX,
    int OriginY,
    int Width,
    int Height);

/// <summary>
/// Keeps the rasterized halves of composed tiles, so that composing them a
/// second time does not mean rendering them a second time.
///
/// Measured on a 51 MB A0: a composed tile is 2,4 s, of which the composition
/// itself is 5 ms. Everything else is PDFium walking three million objects,
/// twice. So panning back over paper already seen, recolouring, moving the
/// unchanged-background slider and turning the comparison off and on again are
/// all the same work repeated — and this is what stops it being repeated.
///
/// Lives on the render thread and is touched from nowhere else, like the
/// documents themselves; there is no lock here because there is no second
/// thread to lock against.
/// </summary>
public sealed class PlateCache(long budgetBytes)
{
    private readonly LinkedList<PlateKey> _lru = new();
    private readonly Dictionary<PlateKey, (byte[] Pixels, LinkedListNode<PlateKey> Node)> _entries = new();
    private long _usedBytes;

    public long UsedBytes => _usedBytes;

    public int Count => _entries.Count;

    public bool TryGet(PlateKey key, out byte[] pixels)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            _lru.Remove(entry.Node);
            _lru.AddLast(entry.Node);
            pixels = entry.Pixels;
            return true;
        }

        pixels = [];
        return false;
    }

    public void Add(PlateKey key, byte[] pixels)
    {
        if (_entries.ContainsKey(key)) return;

        var node = _lru.AddLast(key);
        _entries[key] = (pixels, node);
        _usedBytes += pixels.LongLength;

        // Never the square just added, even when it is bigger than the whole
        // budget on its own: evicting it would leave the caller having paid for
        // a render and kept nothing.
        while (_usedBytes > budgetBytes && _lru.First is { } oldest && !oldest.Value.Equals(key))
        {
            _entries.Remove(oldest.Value, out var stale);
            _usedBytes -= stale.Pixels.LongLength;
            _lru.RemoveFirst();
        }
    }

    /// <summary>
    /// Forgets everything rasterized from a document. Called wherever its pages
    /// are let go — closed, released, or reparsed for a change of line weight —
    /// because a plate outliving the page it was drawn from is the wrong
    /// drawing on screen with nothing to say so.
    /// </summary>
    public void Drop(int documentId)
    {
        var node = _lru.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.DocumentId == documentId && _entries.Remove(node.Value, out var stale))
            {
                _usedBytes -= stale.Pixels.LongLength;
                _lru.Remove(node);
            }
            node = next;
        }
    }
}
