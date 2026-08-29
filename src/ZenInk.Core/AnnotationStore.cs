using System.Numerics;

namespace ZenInk.Core;

/// <summary>
/// Every mark on a document, by sheet, plus the history that lets them be taken
/// back.
///
/// The store is the single truth about what has been drawn: the viewer paints
/// from it, the print path reads it, and a save writes it into the file. It
/// knows nothing about PDFium or about the canvas, which is what lets the whole
/// of undo, hit-testing and the dirty flag be checked without opening a window.
/// </summary>
public sealed class AnnotationStore
{
    /// <summary>
    /// How far back undo reaches. A reviewer marking up a set makes hundreds of
    /// marks in a sitting; keeping every one of them costs nothing next to the
    /// tiles, but the list should not be unbounded either.
    /// </summary>
    private const int MaxHistory = 500;

    private readonly Dictionary<int, List<Annotation>> _byPage = new();
    private readonly List<Edit> _undo = new();
    private readonly List<Edit> _redo = new();

    /// <summary>
    /// One reversible step. Either side may be absent: no <see cref="Before"/>
    /// is a mark being made, no <see cref="After"/> is one being rubbed out,
    /// and both present is one being changed.
    /// </summary>
    private readonly record struct Edit(int Page, Annotation? Before, Annotation? After);

    /// <summary>Bumped on every change, so a viewer can tell whether it must redraw.</summary>
    public int Version { get; private set; }

    /// <summary>True when the marks on screen are not the marks in the file.</summary>
    public bool IsDirty { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int Count
    {
        get
        {
            int total = 0;
            foreach (var page in _byPage.Values) total += page.Count;
            return total;
        }
    }

    /// <summary>The sheets that carry marks, in no particular order.</summary>
    public IEnumerable<int> Pages => _byPage.Keys;

    /// <summary>This sheet's marks, oldest first — which is the order they are drawn in.</summary>
    public IReadOnlyList<Annotation> ForPage(int pageIndex) =>
        _byPage.TryGetValue(pageIndex, out var list) ? list : [];

    public int CountForPage(int pageIndex) => _byPage.TryGetValue(pageIndex, out var list) ? list.Count : 0;

    /// <summary>
    /// Takes the marks a file arrived with. This is the baseline, not an edit:
    /// it neither dirties the document nor becomes something to undo.
    /// </summary>
    public void Load(IReadOnlyDictionary<int, IReadOnlyList<Annotation>> pages)
    {
        _byPage.Clear();
        _undo.Clear();
        _redo.Clear();

        foreach (var (page, marks) in pages)
        {
            if (marks.Count == 0) continue;
            _byPage[page] = [.. marks];
        }

        IsDirty = false;
        Version++;
    }

    public void Clear()
    {
        _byPage.Clear();
        _undo.Clear();
        _redo.Clear();
        IsDirty = false;
        Version++;
    }

    /// <summary>Called once a save has put the marks in the file.</summary>
    public void MarkSaved() => IsDirty = false;

    public void Add(int pageIndex, Annotation annotation)
    {
        Apply(new Edit(pageIndex, null, annotation), remember: true);
    }

    public void Remove(int pageIndex, Annotation annotation)
    {
        Apply(new Edit(pageIndex, annotation, null), remember: true);
    }

    /// <summary>
    /// Puts a changed version of a mark in place of the one with the same id —
    /// moving it, recolouring it, or editing its text.
    /// </summary>
    public void Replace(int pageIndex, Annotation before, Annotation after)
    {
        if (ReferenceEquals(before, after)) return;
        Apply(new Edit(pageIndex, before, after), remember: true);
    }

    /// <summary>
    /// Puts a changed version in place without recording a step. For a drag in
    /// progress: the mark has to be where the pointer is on every frame, but
    /// the whole drag is one thing to take back, not a hundred.
    /// </summary>
    public void ReplaceLive(int pageIndex, Annotation before, Annotation after)
    {
        if (ReferenceEquals(before, after)) return;
        Apply(new Edit(pageIndex, before, after), remember: false);
    }

    /// <summary>The topmost mark under a point, or null. Later marks win, as they are drawn on top.</summary>
    public Annotation? HitTest(int pageIndex, Vector2 point, float extraTolerancePt = 0f)
    {
        if (!_byPage.TryGetValue(pageIndex, out var list)) return null;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].HitTest(point, extraTolerancePt)) return list[i];
        }
        return null;
    }

    /// <summary>Finds a mark by id, whichever sheet it is on.</summary>
    public bool TryFind(Guid id, out int pageIndex, out Annotation annotation)
    {
        foreach (var (page, list) in _byPage)
        {
            foreach (var candidate in list)
            {
                if (candidate.Id != id) continue;
                pageIndex = page;
                annotation = candidate;
                return true;
            }
        }

        pageIndex = -1;
        annotation = null!;
        return false;
    }

    /// <summary>A snapshot for the save path: every sheet that has marks, with them.</summary>
    public Dictionary<int, IReadOnlyList<Annotation>> Snapshot()
    {
        var snapshot = new Dictionary<int, IReadOnlyList<Annotation>>(_byPage.Count);
        foreach (var (page, list) in _byPage)
        {
            snapshot[page] = list.ToArray();
        }
        return snapshot;
    }

    /// <summary>Takes back the last change and returns the sheet it happened on, or -1.</summary>
    public int Undo()
    {
        if (_undo.Count == 0) return -1;

        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        var inverse = new Edit(edit.Page, edit.After, edit.Before);
        Apply(inverse, remember: false);
        _redo.Add(edit);
        return edit.Page;
    }

    /// <summary>Puts back the last undone change and returns its sheet, or -1.</summary>
    public int Redo()
    {
        if (_redo.Count == 0) return -1;

        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        Apply(edit, remember: false);
        _undo.Add(edit);
        return edit.Page;
    }

    /// <summary>
    /// The one place the collections change. Recording the step is separate
    /// from performing it, so undo and redo replay through exactly the same
    /// code that made the change in the first place.
    /// </summary>
    private void Apply(Edit edit, bool remember)
    {
        var list = GetOrCreate(edit.Page);

        if (edit.Before is { } before)
        {
            int index = IndexOf(list, before.Id);
            if (index >= 0)
            {
                if (edit.After is { } replacement)
                {
                    list[index] = replacement;
                }
                else
                {
                    list.RemoveAt(index);
                }
            }
            else if (edit.After is { } added)
            {
                list.Add(added);
            }
        }
        else if (edit.After is { } created)
        {
            list.Add(created);
        }

        if (list.Count == 0)
        {
            _byPage.Remove(edit.Page);
        }

        if (remember)
        {
            _undo.Add(edit);
            if (_undo.Count > MaxHistory)
            {
                _undo.RemoveAt(0);
            }
            _redo.Clear();
        }

        IsDirty = true;
        Version++;
    }

    private List<Annotation> GetOrCreate(int pageIndex)
    {
        if (!_byPage.TryGetValue(pageIndex, out var list))
        {
            list = [];
            _byPage[pageIndex] = list;
        }
        return list;
    }

    private static int IndexOf(List<Annotation> list, Guid id)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Id == id) return i;
        }
        return -1;
    }
}
