//-----------------------------------------------------------------------------------------
// <copyright file="PagePlan.cs" company="plano y escala">
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
/// A file sheets are read from. Index 0 of a plan's sources is always the
/// document the reader opened; the rest arrive when sheets are brought in from
/// somewhere else.
/// </summary>
public sealed record PagePlanSource(string Path, string? Password = null);

/// <summary>
/// One sheet of a pending arrangement: where it comes from, and the reader's
/// own turn on it.
///
/// The turn lives here rather than in an array beside the document because a
/// sheet that moves has to take its turn with it, and two copies of the same
/// source page have to be able to sit at different angles.
/// </summary>
public readonly record struct PageSlot(int Source, int PageIndex, PdfPageSize Size, int QuarterTurns = 0)
{
    /// <summary>A sheet that comes from no file: blank paper.</summary>
    public const int BlankSource = -1;

    public bool IsBlank => Source == BlankSource;

    /// <summary>The size as the reader sees it, with an odd turn swapping the sides.</summary>
    public PdfPageSize EffectiveSize => (QuarterTurns & 1) == 1
        ? new PdfPageSize(Size.HeightPt, Size.WidthPt)
        : Size;

    public static PageSlot BlankSheet(PdfPageSize size) => new(BlankSource, -1, size);
}

/// <summary>Sheets to bring in from one file, in the order they should land.</summary>
public sealed record PageBatch(PagePlanSource Source, IReadOnlyList<(int PageIndex, PdfPageSize Size)> Pages);

/// <summary>
/// The result of rearranging: the new plan, and where each of its sheets came
/// from.
///
/// <see cref="OriginOfNew"/> is what lets everything hanging off a sheet index
/// — the marks on it, the undo steps that made them — travel with the sheet
/// instead of staying behind at a number that now means a different drawing.
/// A sheet with no origin (−1) is one that was not there before: blank paper,
/// or a sheet brought in from another file.
/// </summary>
public sealed record PageEdit(PagePlan Plan, int[] OriginOfNew)
{
    /// <summary>
    /// Where each old sheet ended up, or −1 if it is gone. Derived from
    /// <see cref="OriginOfNew"/>; when a sheet was duplicated, the first copy
    /// is the one that counts as it.
    /// </summary>
    public int[] DestinationOfOld(int oldCount)
    {
        var destination = new int[oldCount];
        Array.Fill(destination, -1);

        for (int i = 0; i < OriginOfNew.Length; i++)
        {
            int origin = OriginOfNew[i];
            if (origin >= 0 && origin < oldCount && destination[origin] < 0)
            {
                destination[origin] = i;
            }
        }
        return destination;
    }
}

/// <summary>
/// How the sheets of a document are arranged, before any of it reaches the
/// file. Every operation returns a new plan rather than changing this one,
/// which is what makes taking a step back a matter of holding on to the
/// previous value.
///
/// The plan is the single truth about which sheets there are and in what
/// order: the viewer lays out from it, the thumbnail strip lists it, and a save
/// walks it. It knows nothing about PDFium, so all of the rearranging can be
/// checked without opening a file.
/// </summary>
public sealed class PagePlan
{
    private readonly PageSlot[] _slots;
    private readonly PagePlanSource[] _sources;

    private PagePlan(PageSlot[] slots, PagePlanSource[] sources)
    {
        _slots = slots;
        _sources = sources;
    }

    /// <summary>The document exactly as it came: every sheet, in order, unturned.</summary>
    public static PagePlan Identity(IReadOnlyList<PdfPageSize> pages, string path, string? password = null)
    {
        var slots = new PageSlot[pages.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = new PageSlot(0, i, pages[i]);
        }
        return new PagePlan(slots, [new PagePlanSource(path, password)]);
    }

    public IReadOnlyList<PageSlot> Slots => _slots;

    public IReadOnlyList<PagePlanSource> Sources => _sources;

    public int Count => _slots.Length;

    public PageSlot this[int index] => _slots[index];

    /// <summary>The sizes the viewer lays out, turns included.</summary>
    public PdfPageSize[] EffectiveSizes()
    {
        var sizes = new PdfPageSize[_slots.Length];
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = _slots[i].EffectiveSize;
        }
        return sizes;
    }

    /// <summary>
    /// True when the sheets are no longer the document's own, in its own order:
    /// something has been moved, removed, duplicated or brought in. A turn does
    /// not count — that is <see cref="HasTurns"/>, and it is written a different
    /// way.
    /// </summary>
    public bool IsRearranged
    {
        get
        {
            if (_sources.Length > 1) return true;

            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Source != 0 || _slots[i].PageIndex != i) return true;
            }
            return false;
        }
    }

    public bool HasTurns
    {
        get
        {
            foreach (var slot in _slots)
            {
                if ((slot.QuarterTurns & 3) != 0) return true;
            }
            return false;
        }
    }

    /// <summary>Sheets that come from the file at <paramref name="source"/>, in plan order.</summary>
    public IEnumerable<int> SlotsFromSource(int source)
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].Source == source) yield return i;
        }
    }

    // --- rearranging ----------------------------------------------------

    /// <summary>
    /// Moves the given sheets so they sit together starting at
    /// <paramref name="destination"/>, keeping their relative order. The
    /// destination is read against the list as it is now, before the move —
    /// which is how a drag reads: the reader points at a gap they can see.
    /// </summary>
    public PageEdit Move(IReadOnlyCollection<int> indices, int destination)
    {
        var moving = Clean(indices);
        if (moving.Count == 0) return Unchanged();

        // Sheets before the gap leave holes behind them, so the gap slides up
        // by as many as are lifted out from above it. That correction is what
        // separates "the gap I dropped it on" from "the number it ends up as",
        // and only a drag needs it.
        int landing = destination;
        foreach (int index in moving)
        {
            if (index < destination) landing--;
        }

        return MoveTo(moving, landing);
    }

    /// <summary>
    /// Moves the given sheets so that the first of them ends up at
    /// <paramref name="landing"/>, keeping their order between them and
    /// gathering a scattered selection into one block.
    ///
    /// This is the plain form — a sheet number in, a sheet number out — and it
    /// is what someone means when they say which sheet a drawing should be. A
    /// drag is the one that needs converting, not this.
    /// </summary>
    public PageEdit MoveTo(IReadOnlyCollection<int> indices, int landing)
    {
        var moving = Clean(indices);
        if (moving.Count == 0) return Unchanged();

        int at = Math.Clamp(landing, 0, _slots.Length - moving.Count);

        var lifted = new HashSet<int>(moving);
        var rest = new List<int>(_slots.Length);
        for (int i = 0; i < _slots.Length; i++)
        {
            if (!lifted.Contains(i)) rest.Add(i);
        }

        var origins = new List<int>(_slots.Length);
        origins.AddRange(rest.Take(at));
        origins.AddRange(moving);
        origins.AddRange(rest.Skip(at));

        return Rebuilt(origins);
    }

    /// <summary>
    /// Where a set of sheets would land if asked to move to
    /// <paramref name="landing"/> — the same clamping <see cref="MoveTo"/>
    /// applies, so a caller can say beforehand what will happen.
    /// </summary>
    public int LandingFor(IReadOnlyCollection<int> indices, int landing)
    {
        int count = Clean(indices).Count;
        return count == 0 ? 0 : Math.Clamp(landing, 0, _slots.Length - count);
    }

    /// <summary>
    /// The sheets in exactly this order, named by where they are now. What a
    /// drag leaves behind is an order, not a move: the list the reader let go
    /// of is the answer, and reading it back beats reconstructing which gap
    /// they aimed at.
    /// </summary>
    public PageEdit Reorder(IReadOnlyList<int> order)
    {
        if (order.Count != _slots.Length) return Unchanged();

        var seen = new bool[_slots.Length];
        foreach (int index in order)
        {
            if (index < 0 || index >= _slots.Length || seen[index]) return Unchanged();
            seen[index] = true;
        }

        return Rebuilt(order);
    }

    /// <summary>
    /// Keeps only the given sheets, in plan order. This is what taking sheets
    /// out to their own file is: a plan of just those, written somewhere else.
    /// </summary>
    public PageEdit Keep(IReadOnlyCollection<int> indices)
    {
        var kept = Clean(indices);
        return kept.Count == 0 ? Unchanged() : Rebuilt(kept);
    }

    /// <summary>Takes the given sheets out. The plan may end up empty; the caller decides whether that is allowed.</summary>
    public PageEdit Remove(IReadOnlyCollection<int> indices)
    {
        var dropped = new HashSet<int>(Clean(indices));
        if (dropped.Count == 0) return Unchanged();

        var origins = new List<int>(_slots.Length);
        for (int i = 0; i < _slots.Length; i++)
        {
            if (!dropped.Contains(i)) origins.Add(i);
        }

        return Rebuilt(origins);
    }

    /// <summary>
    /// Puts a copy of the given sheets straight after the last of them, in
    /// their own order. Behind rather than in front, because the reader is
    /// looking at the original and the copy is the new thing.
    /// </summary>
    public PageEdit Duplicate(IReadOnlyCollection<int> indices)
    {
        var copied = Clean(indices);
        if (copied.Count == 0) return Unchanged();

        int after = copied[^1] + 1;

        var origins = new List<int>(_slots.Length + copied.Count);
        for (int i = 0; i < after; i++) origins.Add(i);
        origins.AddRange(copied);
        for (int i = after; i < _slots.Length; i++) origins.Add(i);

        return Rebuilt(origins);
    }

    /// <summary>Turns the given sheets. Positive is clockwise, in quarter turns.</summary>
    public PageEdit Rotate(IReadOnlyCollection<int> indices, int quarterTurns)
    {
        var turning = new HashSet<int>(Clean(indices));
        if (turning.Count == 0 || (quarterTurns & 3) == 0) return Unchanged();

        var slots = (PageSlot[])_slots.Clone();
        foreach (int index in turning)
        {
            slots[index] = slots[index] with { QuarterTurns = (slots[index].QuarterTurns + quarterTurns) & 3 };
        }

        var origins = new int[_slots.Length];
        for (int i = 0; i < origins.Length; i++) origins[i] = i;

        return new PageEdit(new PagePlan(slots, _sources), origins);
    }

    /// <summary>
    /// Puts sheets from <paramref name="source"/> in at
    /// <paramref name="destination"/>. The source is added to the plan's list
    /// if it is not on it, so bringing two batches from the same file opens it
    /// once.
    /// </summary>
    public PageEdit Insert(int destination, PagePlanSource source, IReadOnlyList<(int PageIndex, PdfPageSize Size)> pages) =>
        InsertMany(destination, [new PageBatch(source, pages)]);

    /// <summary>
    /// Puts sheets from several files in at <paramref name="destination"/>, one
    /// batch after another in the order given.
    ///
    /// One call rather than one per file, because to the person who picked
    /// fourteen drawings that was a single act: it has to be a single thing to
    /// take back, and a single renumbering of the marks.
    /// </summary>
    public PageEdit InsertMany(int destination, IReadOnlyList<PageBatch> batches)
    {
        int total = 0;
        foreach (var batch in batches) total += batch.Pages.Count;
        if (total == 0) return Unchanged();

        var sources = new List<PagePlanSource>(_sources);
        var brought = new PageSlot[total];
        int at = 0;

        foreach (var batch in batches)
        {
            if (batch.Pages.Count == 0) continue;

            int sourceIndex = sources.FindIndex(s => PathsMatch(s.Path, batch.Source.Path));
            if (sourceIndex < 0)
            {
                sourceIndex = sources.Count;
                sources.Add(batch.Source);
            }

            foreach (var (pageIndex, size) in batch.Pages)
            {
                brought[at++] = new PageSlot(sourceIndex, pageIndex, size);
            }
        }

        return SpliceIn(Math.Clamp(destination, 0, _slots.Length), brought, [.. sources]);
    }

    /// <summary>Puts blank paper in at <paramref name="destination"/>.</summary>
    public PageEdit InsertBlank(int destination, PdfPageSize size, int count = 1)
    {
        if (count <= 0) return Unchanged();

        var blank = new PageSlot[count];
        Array.Fill(blank, PageSlot.BlankSheet(size));

        return SpliceIn(Math.Clamp(destination, 0, _slots.Length), blank, _sources);
    }

    /// <summary>
    /// Drops sources no sheet reads from any more. Removing the last sheet that
    /// came from another file should also let go of the file — otherwise a save
    /// would open it for nothing, and a plan that looks untouched would still
    /// say it is rearranged.
    /// </summary>
    public PagePlan Compacted()
    {
        if (_sources.Length <= 1) return this;

        // Source 0 stays whatever happens: it is the document itself, and it is
        // what an empty plan would still be a plan of.
        var kept = new List<int> { 0 };
        var moved = new int[_sources.Length];
        Array.Fill(moved, -1);
        moved[0] = 0;

        foreach (var slot in _slots)
        {
            if (slot.IsBlank || moved[slot.Source] >= 0) continue;
            moved[slot.Source] = kept.Count;
            kept.Add(slot.Source);
        }

        if (kept.Count == _sources.Length) return this;

        var slots = new PageSlot[_slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = _slots[i].IsBlank ? _slots[i] : _slots[i] with { Source = moved[_slots[i].Source] };
        }

        return new PagePlan(slots, [.. kept.Select(i => _sources[i])]);
    }

    private PageEdit SpliceIn(int at, PageSlot[] added, PagePlanSource[] sources)
    {
        var slots = new PageSlot[_slots.Length + added.Length];
        var origins = new int[slots.Length];

        for (int i = 0; i < at; i++)
        {
            slots[i] = _slots[i];
            origins[i] = i;
        }

        for (int i = 0; i < added.Length; i++)
        {
            slots[at + i] = added[i];
            origins[at + i] = -1;
        }

        for (int i = at; i < _slots.Length; i++)
        {
            slots[added.Length + i] = _slots[i];
            origins[added.Length + i] = i;
        }

        return new PageEdit(new PagePlan(slots, sources), origins);
    }

    private PageEdit Rebuilt(IReadOnlyList<int> origins)
    {
        var slots = new PageSlot[origins.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = _slots[origins[i]];
        }
        return new PageEdit(new PagePlan(slots, _sources), [.. origins]);
    }

    private PageEdit Unchanged()
    {
        var origins = new int[_slots.Length];
        for (int i = 0; i < origins.Length; i++) origins[i] = i;
        return new PageEdit(this, origins);
    }

    /// <summary>Sorted, de-duplicated and inside the document — every operation takes a reader's selection.</summary>
    private List<int> Clean(IReadOnlyCollection<int> indices)
    {
        var clean = new SortedSet<int>();
        foreach (int index in indices)
        {
            if (index >= 0 && index < _slots.Length) clean.Add(index);
        }
        return [.. clean];
    }

    private static bool PathsMatch(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
