using System.Numerics;

namespace ZenInk.Core;

/// <summary>What a step back or forward changed, so the viewer knows how much to redraw.</summary>
public readonly record struct EditStep(int PageIndex, bool Rearranged)
{
    public static readonly EditStep None = new(-1, false);

    public bool Happened => PageIndex >= 0 || Rearranged;
}

/// <summary>
/// Everything pending on a document — the marks that have been drawn and the
/// order the sheets are in — plus the one history that takes any of it back.
///
/// The store is the single truth about what has changed and has not reached the
/// file: the viewer paints and lays out from it, the print path reads it, and a
/// save walks it. It knows nothing about PDFium or about the canvas, which is
/// what lets the whole of undo, hit-testing and the dirty flag be checked
/// without opening a window.
///
/// Marks and sheets share one history on purpose. They are one document to the
/// person editing it, and two undo stacks behind one Ctrl+Z is a coin toss
/// about which one it lands on.
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

    /// <summary>
    /// What each sheet is drawn to. It lives here, beside the marks, for the
    /// one reason that matters: this is what already moves when the sheets do,
    /// so a sheet brought in from another drawing cannot arrive under its
    /// neighbour's scale.
    /// </summary>
    private readonly Dictionary<int, SheetScale> _scaleByPage = new();

    private readonly List<Edit> _undo = new();
    private readonly List<Edit> _redo = new();

    /// <summary>
    /// One reversible step. Either side may be absent: no <see cref="Before"/>
    /// is a mark being made, no <see cref="After"/> is one being rubbed out,
    /// and both present is one being changed. A step that carries
    /// <see cref="Pages"/> instead is the sheets being rearranged.
    /// </summary>
    private readonly record struct Edit(
        int Page, Annotation? Before, Annotation? After, PageStep? Pages = null, ScaleStep? Scale = null);

    /// <summary>
    /// A sheet being calibrated, and every measurement on it that the new scale
    /// changes the number of.
    ///
    /// The two travel together because they are one thing to take back: a
    /// calibration undone that left the numbers behind would leave the sheet
    /// saying lengths no scale on it produces.
    /// </summary>
    private sealed record ScaleStep(
        int Page,
        SheetScale? Before,
        SheetScale? After,
        List<Annotation> MarksBefore,
        List<Annotation> MarksAfter);

    /// <summary>
    /// The sheets moving, and the marks moving with them.
    ///
    /// Both sides of the marks are kept whole rather than as a remapping. It
    /// costs a copy of a small dictionary and it buys exactness: a rearrangement
    /// can drop a sheet, or make two of one, and there is no index arithmetic
    /// that undoes those and still leaves the older steps underneath it
    /// meaning what they meant.
    /// </summary>
    private sealed record PageStep(
        PagePlan Before,
        PagePlan After,
        Dictionary<int, List<Annotation>> MarksBefore,
        Dictionary<int, List<Annotation>> MarksAfter,
        Dictionary<int, SheetScale> ScalesBefore,
        Dictionary<int, SheetScale> ScalesAfter);

    /// <summary>Bumped on every change, so a viewer can tell whether it must redraw.</summary>
    public int Version { get; private set; }

    /// <summary>True when what is on screen is not what is in the file.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// How the sheets are arranged. Empty until a document is loaded, so a
    /// store used for marks alone — as the checks do — needs to know nothing
    /// about it.
    /// </summary>
    public PagePlan Plan { get; private set; } = PagePlan.Identity([], string.Empty);

    /// <summary>True when the sheets have been moved, removed, duplicated or brought in.</summary>
    public bool IsRearranged => Plan.IsRearranged;

    /// <summary>True when a sheet carries a turn that is not in the file yet.</summary>
    public bool HasTurns => Plan.HasTurns;

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
        _scaleByPage.Clear();
        _undo.Clear();
        _redo.Clear();

        foreach (var (page, marks) in pages)
        {
            if (marks.Count == 0) continue;
            _byPage[page] = [.. marks];

            // A sheet's calibration comes back from its own measurements: each
            // one carries the scale it was taken at, and the last one taken is
            // the one the reader was working to. It is the only place a
            // calibration can be kept without inventing somewhere in the file
            // to put it — and a sheet with no measurements has nothing to
            // remember, because nothing was ever measured on it.
            for (int i = marks.Count - 1; i >= 0; i--)
            {
                if (marks[i].Scale is not { } scale) continue;

                _scaleByPage[page] = scale;
                break;
            }
        }

        IsDirty = false;
        Version++;
    }

    /// <summary>What this sheet is drawn to, or null while it has never been calibrated.</summary>
    public SheetScale? ScaleOf(int pageIndex) =>
        _scaleByPage.TryGetValue(pageIndex, out var scale) ? scale : null;

    /// <summary>True for a sheet that can be measured on.</summary>
    public bool IsCalibrated(int pageIndex) => _scaleByPage.ContainsKey(pageIndex);

    /// <summary>
    /// Calibrates a sheet, and brings every measurement already on it to the
    /// new scale. One step, so taking it back takes back both.
    /// </summary>
    public void SetScale(int pageIndex, SheetScale? scale)
    {
        var before = ScaleOf(pageIndex);
        if (Nullable.Equals(before, scale)) return;

        var marks = ForPage(pageIndex);
        Apply(
            new Edit(pageIndex, null, null, null, new ScaleStep(
                pageIndex,
                before,
                scale,
                [.. marks],
                [.. marks.Select(mark => Measures.Is(mark.Kind) ? mark.WithScale(scale) : mark)])),
            remember: true);
    }

    /// <summary>
    /// Takes the sheets a file arrived with, as the arrangement nothing has
    /// been done to yet. Called on opening and again after every save, which is
    /// what makes "the sheets have been moved" mean since the last write.
    /// </summary>
    public void LoadPlan(PagePlan plan)
    {
        Plan = plan;
        _undo.Clear();
        _redo.Clear();
    }

    public void Clear()
    {
        _byPage.Clear();
        _scaleByPage.Clear();
        _undo.Clear();
        _redo.Clear();
        Plan = PagePlan.Identity([], string.Empty);
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

    /// <summary>
    /// Takes the sheets somewhere else, and takes their marks with them.
    ///
    /// A sheet that is copied gets a copy of its marks, with fresh ids: they
    /// are two sheets from here on, and two marks that answered to the same id
    /// would be one mark to undo, to hit-test and to write.
    /// </summary>
    public void Rearrange(PageEdit edit)
    {
        var after = new Dictionary<int, List<Annotation>>();
        var afterScales = new Dictionary<int, SheetScale>();
        var seen = new HashSet<int>();

        for (int i = 0; i < edit.OriginOfNew.Length; i++)
        {
            int origin = edit.OriginOfNew[i];
            if (origin < 0) continue;

            // The scale goes where the sheet goes, by the same route as its
            // marks. A drawing at 1:50 moved to the front of the set is still
            // at 1:50, and one duplicated is at 1:50 twice.
            if (_scaleByPage.TryGetValue(origin, out var scale))
            {
                afterScales[i] = scale;
            }

            if (!_byPage.TryGetValue(origin, out var marks)) continue;

            after[i] = seen.Add(origin) ? [.. marks] : [.. marks.Select(Copy)];
        }

        Apply(
            new Edit(-1, null, null, new PageStep(
                Plan,
                edit.Plan.Compacted(),
                CopyOfMarks(),
                after,
                new Dictionary<int, SheetScale>(_scaleByPage),
                afterScales)),
            remember: true);
    }

    private static Annotation Copy(Annotation mark) => new(
        mark.Kind, mark.Points, mark.Style, mark.Text, mark.Author,
        id: Guid.NewGuid(), created: mark.Created, rotationDeg: mark.RotationDeg, scale: mark.Scale);

    private Dictionary<int, List<Annotation>> CopyOfMarks()
    {
        var copy = new Dictionary<int, List<Annotation>>(_byPage.Count);
        foreach (var (page, list) in _byPage)
        {
            copy[page] = [.. list];
        }
        return copy;
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

    /// <summary>Takes back the last change and says what it touched.</summary>
    public EditStep Undo()
    {
        if (_undo.Count == 0) return EditStep.None;

        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        var inverse = edit switch
        {
            { Pages: { } pages } => new Edit(edit.Page, null, null, new PageStep(
                pages.After, pages.Before, pages.MarksAfter, pages.MarksBefore, pages.ScalesAfter, pages.ScalesBefore)),

            { Scale: { } scale } => new Edit(edit.Page, null, null, null, new ScaleStep(
                scale.Page, scale.After, scale.Before, scale.MarksAfter, scale.MarksBefore)),

            _ => new Edit(edit.Page, edit.After, edit.Before),
        };

        Apply(inverse, remember: false);
        _redo.Add(edit);
        return new EditStep(edit.Page, edit.Pages is not null);
    }

    /// <summary>Puts back the last undone change and says what it touched.</summary>
    public EditStep Redo()
    {
        if (_redo.Count == 0) return EditStep.None;

        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        Apply(edit, remember: false);
        _undo.Add(edit);
        return new EditStep(edit.Page, edit.Pages is not null);
    }

    /// <summary>
    /// The one place the collections change. Recording the step is separate
    /// from performing it, so undo and redo replay through exactly the same
    /// code that made the change in the first place.
    /// </summary>
    private void Apply(Edit edit, bool remember)
    {
        if (edit.Pages is { } pages)
        {
            Plan = pages.After;
            _byPage.Clear();
            foreach (var (page, marks) in pages.MarksAfter)
            {
                if (marks.Count > 0) _byPage[page] = [.. marks];
            }

            _scaleByPage.Clear();
            foreach (var (page, scale) in pages.ScalesAfter)
            {
                _scaleByPage[page] = scale;
            }

            Remember(edit, remember);
            return;
        }

        if (edit.Scale is { } calibration)
        {
            if (calibration.After is { } scale)
            {
                _scaleByPage[calibration.Page] = scale;
            }
            else
            {
                _scaleByPage.Remove(calibration.Page);
            }

            if (calibration.MarksAfter.Count > 0)
            {
                _byPage[calibration.Page] = [.. calibration.MarksAfter];
            }
            else
            {
                _byPage.Remove(calibration.Page);
            }

            Remember(edit, remember);
            return;
        }

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

        Remember(edit, remember);
    }

    private void Remember(Edit edit, bool remember)
    {
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
