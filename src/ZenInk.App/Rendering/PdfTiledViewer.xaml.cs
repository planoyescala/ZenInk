using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.System;
using Windows.UI;

using ZenInk.Core;

namespace ZenInk_App.Rendering;

public enum ViewerTool
{
    Pan,

    SelectText,

    /// <summary>
    /// Drag a rectangle and the view goes there. On an A0 this is the fastest
    /// way from the whole sheet to a legible dimension — one gesture instead of
    /// a dozen wheel notches and a drag to recentre.
    /// </summary>
    ZoomRectangle,
}

public enum ViewerLayoutMode
{
    /// <summary>Every sheet stacked in one scrollable strip.</summary>
    Continuous,

    /// <summary>One sheet at a time; scrolling stays on it and pages change explicitly.</summary>
    SinglePage,
}

/// <summary>
/// How the zoom follows the window. Width and Page are standing choices, not
/// one-off actions: they are reapplied when the window resizes or the sheet
/// changes, which is what makes "ancho completo" mean something after the
/// reader drags the window wider.
/// </summary>
public enum ViewerFitMode
{
    /// <summary>Whatever zoom the reader set.</summary>
    Free,

    /// <summary>The strip's full width fills the viewport.</summary>
    Width,

    /// <summary>A whole sheet — or a whole spread — fits inside the viewport.</summary>
    Page,
}

/// <summary>
/// Scrolls a whole PDF as one continuous vertical strip of tiled pages.
///
/// Coordinates live in "document space" — PDF points, y down, every page
/// stacked by <see cref="DocumentLayout"/> — so scrolling is a single vector
/// and the math never depends on which discrete zoom level currently backs the
/// tiles on screen. <see cref="_origin"/> is the document point sitting at the
/// viewport's top-left corner; <see cref="_scale"/> is DIPs per point.
///
/// Drawing is progressive: coarser cached tiles are painted first (so panning
/// into new territory shows a soft, already-seen version immediately) and the
/// current level is painted over them, requesting whatever is missing.
/// </summary>
public sealed partial class PdfTiledViewer : UserControl
{
    private const double MinScale = 0.02;
    private const double MaxScale = 16.0;
    private const int FallbackLevels = 5;
    private const long CacheBudgetBytes = 384L * 1024 * 1024;
    private const double WheelScrollDips = 90.0;
    private const double ZoomStep = 1.15;
    private const int MaxTextLayers = 24;

    /// <summary>How far off a glyph the pointer may be and still select it, in points.</summary>
    private const float SelectionSnapPt = 24f;

    /// <summary>Below this, a zoom-rectangle drag was a click and is treated as one.</summary>
    private const double MinZoomBandDips = 12.0;

    /// <summary>Arrow-key step, as a fraction of the viewport.</summary>
    private const double ArrowStepFraction = 0.12;

    private static readonly Color ZoomBandFill = Color.FromArgb(46, 0, 103, 192);

    private static readonly Color ZoomBandStroke = Color.FromArgb(210, 0, 103, 192);

    private static readonly Color SelectionFill = Color.FromArgb(70, 0, 103, 192);

    /// <summary>
    /// Search highlights are drawn over white paper and black ink, so they are
    /// translucent enough to read the drawing through and saturated enough to
    /// spot at a glance. The current hit is a different hue, not just a
    /// stronger one, so it stands out among its neighbours.
    /// </summary>
    private static readonly Color SearchFill = Color.FromArgb(105, 255, 214, 0);

    private static readonly Color SearchCurrentFill = Color.FromArgb(150, 255, 122, 0);

    private static readonly Color SearchCurrentStroke = Color.FromArgb(220, 190, 70, 0);

    private readonly PdfRenderQueue _queue = PdfRenderQueue.Shared;
    private readonly TileCache _cache = new(CacheBudgetBytes);
    private readonly HashSet<TileKey> _inFlight = new();

    /// <summary>
    /// Extracted text, keyed by sheet *and* the turn it was extracted at: the
    /// glyph boxes live in the same space the tiles are drawn in, so turning a
    /// sheet makes the old boxes wrong.
    /// </summary>
    private readonly Dictionary<TextLayerKey, PageTextLayer> _textLayers = new();

    private readonly LinkedList<TextLayerKey> _textLru = new();
    private readonly HashSet<TextLayerKey> _textInFlight = new();

    /// <summary>Search hits in document order, so next/previous is a walk along one list.</summary>
    private readonly List<SearchHit> _hits = new();

    /// <summary>Where each sheet's hits sit in <see cref="_hits"/>; the scan fills it in page order.</summary>
    private readonly Dictionary<int, (int Start, int Count)> _hitsByPage = new();

    private DocumentLayout? _layout;
    private int _documentId = -1;
    private bool _isActive = true;

    /// <summary>Sheet sizes as the file reports them, before the reader's own turns.</summary>
    private IReadOnlyList<PdfPageSize> _sourceSizes = [];

    /// <summary>Sheet sizes as laid out, with the axes swapped on a quarter turn.</summary>
    private IReadOnlyList<PdfPageSize> _pageSizes = [];

    /// <summary>The reader's quarter-turn per sheet, on top of whatever /Rotate the file carries.</summary>
    private int[] _rotations = [];

    private ViewerLayoutMode _layoutMode = ViewerLayoutMode.Continuous;
    private int _columns = 1;
    private int _currentPageIndex;
    private double _scale = 1.0;
    private Vector2 _origin;
    private ViewerFitMode _fitMode = ViewerFitMode.Width;
    private bool _fitDirty;
    private bool _fitSnap;
    private bool _isPanning;
    private bool _isSelecting;
    private bool _isZoomBanding;
    private Point _zoomBandStart;
    private Point _zoomBandEnd;
    private Point _lastPointerPosition;
    private ViewerTool _tool = ViewerTool.Pan;
    private bool _thinLines;
    private bool _suppressScrollEvents;

    /// <summary>Bumped on every open, so results for a previous document are discarded.</summary>
    private int _documentGeneration;

    private int _selectionPage = -1;
    private int _selectionAnchor = -1;
    private int _selectionFocus = -1;

    private string _searchQuery = string.Empty;
    private TextSearchOptions _searchOptions;
    private int _searchGeneration;
    private int _searchScanned;
    private bool _searchRunning;
    private int _searchCursor = -1;

    public PdfTiledViewer()
    {
        InitializeComponent();
        UpdateCursor();
    }

    /// <summary>Raised when the visible page, zoom level or selection changes.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>Raised when a document finishes opening, so page-dependent UI can rebuild.</summary>
    public event EventHandler? DocumentOpened;

    /// <summary>Raised when the sheets change shape — a turn — so previews can be redrawn.</summary>
    public event EventHandler? PagesChanged;

    private readonly record struct TextLayerKey(int PageIndex, int Rotation);

    /// <summary>
    /// One occurrence of the search term. The glyph range is what makes it
    /// navigable; the runs are the rectangles to paint, kept alongside because
    /// the text layer they came from may well be evicted before the reader
    /// scrolls back to this hit.
    /// </summary>
    private sealed record SearchHit(int PageIndex, TextMatch Range, IReadOnlyList<TextRun> Runs);

    /// <summary>
    /// Claims all the space on offer. A CanvasControl reports no size of its
    /// own, so in a host that arranges children at their desired size — a
    /// TabView's content presenter, for one — this control would otherwise
    /// collapse to zero height and never draw.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = base.MeasureOverride(availableSize);

        double width = double.IsInfinity(availableSize.Width) ? desired.Width : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? desired.Height : availableSize.Height;
        return new Size(width, height);
    }

    public int PageCount => _layout?.DocumentPageCount ?? 0;

    /// <summary>Identifies this viewer's document to the shared render queue.</summary>
    public int DocumentId => _documentId;

    /// <summary>The file this document was opened from, which is where a save writes back.</summary>
    public string? SourcePath { get; private set; }

    /// <summary>Every sheet's size as laid out, so the thumbnail strip can take its shape before any rendering.</summary>
    public IReadOnlyList<PdfPageSize> PageSizes => _pageSizes;

    /// <summary>The reader's quarter-turn on a sheet, for anyone rendering it themselves.</summary>
    public int RotationOf(int pageIndex) =>
        pageIndex >= 0 && pageIndex < _rotations.Length ? _rotations[pageIndex] & 3 : 0;

    /// <summary>
    /// Anything on screen that is not yet in the file. Sheet turns are all
    /// there is today; this is the seam annotations and comments will join, so
    /// the toolbar asks this rather than asking about turns.
    /// </summary>
    public bool HasUnsavedChanges => HasUnsavedRotations;

    /// <summary>True while a turn is on screen but not yet written to the file.</summary>
    public bool HasUnsavedRotations
    {
        get
        {
            foreach (int rotation in _rotations)
            {
                if ((rotation & 3) != 0) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// In single-sheet mode the page is whichever one is laid out; in
    /// continuous mode it is whichever fills most of the viewport.
    /// </summary>
    public int CurrentPageIndex
    {
        get
        {
            if (_layout is not { } layout) return 0;
            if (_layoutMode == ViewerLayoutMode.SinglePage) return _currentPageIndex;

            var view = ViewportInDocSpace();
            return layout.DominantPageIndex(view.Top, view.Bottom);
        }
    }

    public int CurrentPageNumber => _layout is null ? 0 : CurrentPageIndex + 1;

    public bool CanGoPrevious => _layout is not null && CurrentPageIndex > 0;

    public bool CanGoNext => _layout is not null && CurrentPageIndex < PageCount - 1;

    public ViewerLayoutMode LayoutMode
    {
        get => _layoutMode;
        set
        {
            if (_layoutMode == value) return;

            // Carry the sheet you were looking at across the switch.
            int page = CurrentPageIndex;
            _layoutMode = value;
            _currentPageIndex = page;
            RebuildLayout();
        }
    }

    /// <summary>Sheets side by side: one, or a two-up spread.</summary>
    public int Columns
    {
        get => _columns;
        set
        {
            int columns = Math.Clamp(value, 1, 2);
            if (_columns == columns) return;

            int page = CurrentPageIndex;
            _columns = columns;
            _currentPageIndex = page;
            RebuildLayout();
        }
    }

    /// <summary>
    /// How the zoom follows the window. Setting it re-fits at once; the reader
    /// zooming by hand drops it back to <see cref="ViewerFitMode.Free"/>.
    /// </summary>
    public ViewerFitMode FitMode
    {
        get => _fitMode;
        set
        {
            _fitMode = value;
            if (value == ViewerFitMode.Free) return;

            _fitDirty = true;
            _fitSnap = true;
            Canvas.Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void GoToPage(int pageIndex)
    {
        if (_pageSizes.Count == 0 || _layout is null) return;

        int target = Math.Clamp(pageIndex, 0, _pageSizes.Count - 1);
        if (target == CurrentPageIndex && _layoutMode == ViewerLayoutMode.SinglePage) return;

        _currentPageIndex = target;

        if (_layoutMode == ViewerLayoutMode.SinglePage)
        {
            RebuildLayout();
            return;
        }

        foreach (var page in _layout.Pages)
        {
            if (page.Index != target) continue;
            _origin = new Vector2(_origin.X, page.YPt);
            break;
        }

        if (_fitMode == ViewerFitMode.Page)
        {
            _fitDirty = true;
            _fitSnap = true;
        }

        ClampOrigin();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NextPage() => GoToPage(CurrentPageIndex + 1);

    public void PreviousPage() => GoToPage(CurrentPageIndex - 1);

    /// <summary>Rebuilds document space for the current mode, keeping the zoom level.</summary>
    private void RebuildLayout()
    {
        if (_pageSizes.Count == 0)
        {
            _layout = null;
            return;
        }

        ClearSelection();

        _layout = _layoutMode == ViewerLayoutMode.SinglePage
            ? DocumentLayout.SinglePage(_pageSizes, _currentPageIndex, _columns)
            : DocumentLayout.Continuous(_pageSizes, _columns);

        if (_layoutMode == ViewerLayoutMode.SinglePage)
        {
            _origin = Vector2.Zero;
        }
        else
        {
            foreach (var page in _layout.Pages)
            {
                if (page.Index != _currentPageIndex) continue;
                _origin = new Vector2(_origin.X, page.YPt);
                break;
            }
        }

        if (_fitMode != ViewerFitMode.Free)
        {
            _fitDirty = true;
            _fitSnap = true;
        }

        ClampOrigin();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public double ZoomPercent => _scale * 100.0;

    public bool HasSelection => _selectionPage >= 0 && _selectionAnchor >= 0 && _selectionFocus >= 0;

    /// <summary>
    /// Draws every stroke as a hairline instead of at its authored width.
    /// Every cached tile is discarded, since they were rasterized with the
    /// other setting.
    /// </summary>
    public bool ThinLines
    {
        get => _thinLines;
        set
        {
            if (_thinLines == value) return;
            _thinLines = value;

            if (_documentId >= 0)
            {
                _queue.SetThinLines(_documentId, value);
            }

            _cache.Clear();
            _inFlight.Clear();
            Canvas.Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ViewerTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            _tool = value;
            if (value == ViewerTool.Pan)
            {
                ClearSelection();
            }
            UpdateCursor();
            Canvas.Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task OpenAsync(string path)
    {
        var info = await _queue.OpenDocumentAsync(path);

        if (_documentId >= 0)
        {
            _ = _queue.CloseDocumentAsync(_documentId);
        }
        _documentId = info.DocumentId;
        SourcePath = path;
        _documentGeneration++;
        _cache.Clear();
        _inFlight.Clear();
        _textLayers.Clear();
        _textLru.Clear();
        _textInFlight.Clear();
        ClearSelection();
        ResetSearch();

        _sourceSizes = info.Pages;
        _rotations = new int[info.Pages.Count];
        _pageSizes = BuildEffectiveSizes();
        _currentPageIndex = 0;
        _layout = _layoutMode == ViewerLayoutMode.SinglePage
            ? DocumentLayout.SinglePage(_pageSizes, 0, _columns)
            : DocumentLayout.Continuous(_pageSizes, _columns);
        _origin = Vector2.Zero;
        _fitDirty = true;
        _fitSnap = true;

        Canvas.Invalidate();
        // Opening a drawing is the reader saying they want to move around it,
        // so the canvas takes the keyboard rather than making them click first.
        Canvas.Focus(FocusState.Programmatic);
        DocumentOpened?.Invoke(this, EventArgs.Empty);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-fits to the current mode; falls back to fitting the width.</summary>
    public void RefitNow()
    {
        if (_fitMode == ViewerFitMode.Free)
        {
            _fitMode = ViewerFitMode.Width;
        }

        _fitDirty = true;
        _fitSnap = true;
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }


    /// <summary>
    /// Back to the zoom the readout calls 100 %. Reading a drawing means moving
    /// between "the whole sheet" and "this detail" all day, and a known
    /// magnification to return to is what keeps that from becoming guesswork.
    /// </summary>
    public void ZoomToActualSize()
    {
        _fitMode = ViewerFitMode.Free;
        _fitDirty = false;
        _scale = 1.0;
        ClampOrigin();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Zooms about the centre of the viewport, for the toolbar's zoom buttons.</summary>
    public void ZoomBy(double factor) =>
        ZoomAt(new Point(Canvas.ActualWidth / 2, Canvas.ActualHeight / 2), factor);

    // --- rotation -------------------------------------------------------

    /// <summary>Turns the sheet in view. Positive is clockwise, in quarter turns.</summary>
    public void RotateCurrentPage(int quarterTurns) => Rotate([CurrentPageIndex], quarterTurns);

    /// <summary>Turns every sheet in the document by the same amount.</summary>
    public void RotateAllPages(int quarterTurns) => Rotate(Enumerable.Range(0, _rotations.Length), quarterTurns);

    /// <summary>
    /// Applies a turn to a set of sheets. The tiles of the old orientation stay
    /// cached under their own key, so turning a sheet back is instant; only the
    /// text of the turned sheets is dropped, since its boxes are the one thing
    /// a turn genuinely invalidates.
    /// </summary>
    private void Rotate(IEnumerable<int> pageIndices, int quarterTurns)
    {
        if (_rotations.Length == 0) return;

        bool changed = false;
        foreach (int index in pageIndices)
        {
            if (index < 0 || index >= _rotations.Length) continue;
            _rotations[index] = (_rotations[index] + quarterTurns) & 3;
            changed = true;
        }

        if (!changed) return;

        _pageSizes = BuildEffectiveSizes();
        ClearSelection();

        // Hit rectangles are in page-local space, so a turn moves them. The
        // ranges would survive, but rescanning is simpler than repairing them
        // and a turn is a deliberate, occasional act.
        if (_searchQuery.Length > 0)
        {
            RestartSearch();
        }

        int page = CurrentPageIndex;
        _currentPageIndex = page;
        RebuildLayout();
        PagesChanged?.Invoke(this, EventArgs.Empty);
    }

    private PdfPageSize[] BuildEffectiveSizes()
    {
        var sizes = new PdfPageSize[_sourceSizes.Count];
        for (int i = 0; i < sizes.Length; i++)
        {
            var size = _sourceSizes[i];
            sizes[i] = (_rotations[i] & 1) == 1
                ? new PdfPageSize(size.HeightPt, size.WidthPt)
                : size;
        }
        return sizes;
    }

    /// <summary>
    /// Writes the pending turns into the file the document came from, then
    /// picks the reopened document back up where the reader left off. Returns
    /// the failure to report, or null when it worked.
    /// </summary>
    public async Task<string?> SaveRotationsAsync()
    {
        if (SourcePath is not { } path || _documentId < 0) return "El documento no tiene un archivo asociado.";

        var outcome = await _queue.ApplyRotationsInPlaceAsync(_documentId, path, _rotations);
        AdoptReopenedDocument(outcome.Document);
        return outcome.Error;
    }

    /// <summary>Writes the turns to another file, leaving this document as it is.</summary>
    public Task SaveRotationsCopyAsync(string targetPath)
    {
        if (SourcePath is not { } path) throw new InvalidOperationException("El documento no tiene un archivo asociado.");
        return _queue.SaveRotatedCopyAsync(path, targetPath, _rotations);
    }

    /// <summary>
    /// Takes over the document handle a save handed back. The sheets now carry
    /// their turns in the file, so the reader's own turns reset to none and the
    /// layout comes out identical — which is what lets the view stay put across
    /// a save.
    /// </summary>
    private void AdoptReopenedDocument(PdfDocumentInfo info)
    {
        _documentId = info.DocumentId;
        _documentGeneration++;
        _cache.Clear();
        _inFlight.Clear();
        _textLayers.Clear();
        _textLru.Clear();
        _textInFlight.Clear();

        _sourceSizes = info.Pages;
        _rotations = new int[info.Pages.Count];
        _pageSizes = BuildEffectiveSizes();

        if (_thinLines)
        {
            _queue.SetThinLines(_documentId, true);
        }

        if (_searchQuery.Length > 0)
        {
            RestartSearch();
        }

        Canvas.Invalidate();
        PagesChanged?.Invoke(this, EventArgs.Empty);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // --- selection ------------------------------------------------------

    /// <summary>Selects all the text on the sheet in view, if its text layer is ready.</summary>
    public void SelectCurrentPage()
    {
        if (_layout is null) return;

        int pageIndex = CurrentPageIndex;
        EnsureTextLayer(pageIndex);

        if (!_textLayers.TryGetValue(new TextLayerKey(pageIndex, RotationOf(pageIndex)), out var layer)
            || layer.Count == 0)
        {
            return;
        }

        _selectionPage = pageIndex;
        _selectionAnchor = 0;
        _selectionFocus = layer.Count - 1;
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public string? GetSelectedText()
    {
        if (!HasSelection) return null;
        if (!_textLayers.TryGetValue(new TextLayerKey(_selectionPage, RotationOf(_selectionPage)), out var layer))
        {
            return null;
        }

        string text = layer.GetText(_selectionAnchor, _selectionFocus);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public void CopySelection()
    {
        if (GetSelectedText() is not { } text) return;

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    /// <summary>
    /// Called when this viewer's tab is shown or hidden. A hidden tab drops its
    /// rasterized tiles and its parsed pages: only one document is ever on
    /// screen, and re-rendering the visible tiles on return is fast, whereas
    /// keeping every tab's tiles would multiply the memory budget by the tab
    /// count. It also sidesteps holding GPU bitmaps across the unload/reload
    /// the TabView performs when switching tabs.
    /// </summary>
    public void SetActive(bool active)
    {
        _isActive = active;

        if (active)
        {
            Canvas.Invalidate();
            return;
        }

        _cache.Clear();
        _inFlight.Clear();
        if (_documentId >= 0)
        {
            _queue.ReleasePages(_documentId);
        }
    }

    /// <summary>
    /// Releases the document and its GPU resources. Called explicitly when the
    /// tab closes — not from Unloaded, which a TabView also raises merely for
    /// switching away from this tab.
    /// </summary>
    public void CloseDocument()
    {
        _cache.Clear();
        _inFlight.Clear();
        _textLayers.Clear();
        _textLru.Clear();
        _textInFlight.Clear();
        ClearSelection();
        ResetSearch();

        if (_documentId >= 0)
        {
            _ = _queue.CloseDocumentAsync(_documentId);
            _documentId = -1;
        }

        _layout = null;
        _sourceSizes = [];
        _pageSizes = [];
        _rotations = [];
        Canvas.RemoveFromVisualTree();
    }

    private void UpdateCursor() => ProtectedCursor = InputSystemCursor.Create(_tool switch
    {
        ViewerTool.SelectText => InputSystemCursorShape.IBeam,
        ViewerTool.ZoomRectangle => InputSystemCursorShape.Cross,
        _ => InputSystemCursorShape.Hand,
    });

    private void ClearSelection()
    {
        _selectionPage = -1;
        _selectionAnchor = -1;
        _selectionFocus = -1;
    }

    private void OnCopyAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        CopySelection();
        args.Handled = true;
    }

    private void OnSelectAllAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_tool != ViewerTool.SelectText) return;
        SelectCurrentPage();
        args.Handled = true;
    }

    // --- viewport -------------------------------------------------------

    private Rect ViewportInDocSpace()
    {
        double width = Canvas.ActualWidth / _scale;
        double height = Canvas.ActualHeight / _scale;
        return new Rect(_origin.X, _origin.Y, Math.Max(width, 0), Math.Max(height, 0));
    }

    /// <summary>
    /// Applies a deferred fit once the canvas actually has a size. Fitting
    /// straight after the document loads is unreliable — the canvas may still
    /// be zero-sized or mid-layout, which is what produced inconsistent side
    /// margins before.
    ///
    /// A fit the reader asked for jumps to the top of what it fitted; a fit
    /// merely re-run because the window changed size keeps the middle of the
    /// view where it was, so dragging a window edge does not lose their place.
    /// </summary>
    private void ApplyPendingFit()
    {
        if (!_fitDirty || _fitMode == ViewerFitMode.Free || _layout is not { } layout) return;
        if (Canvas.ActualWidth <= 0 || Canvas.ActualHeight <= 0) return;

        var centre = _origin + new Vector2(
            (float)(Canvas.ActualWidth / _scale / 2.0),
            (float)(Canvas.ActualHeight / _scale / 2.0));

        RowBox? row = _fitMode == ViewerFitMode.Page ? layout.RowOfPage(CurrentPageIndex) : null;

        if (_fitMode == ViewerFitMode.Page && row is { } box && box.WidthPt > 0 && box.HeightPt > 0)
        {
            _scale = Math.Clamp(
                Math.Min(Canvas.ActualWidth / box.WidthPt, Canvas.ActualHeight / box.HeightPt),
                MinScale,
                MaxScale);
        }
        else if (layout.WidthPt > 0)
        {
            _scale = Math.Clamp(Canvas.ActualWidth / layout.WidthPt, MinScale, MaxScale);
        }
        else
        {
            return;
        }

        if (_fitSnap)
        {
            _origin = row is { } target
                ? new Vector2(target.XPt, target.YPt)
                : new Vector2(0f, _origin.Y);
        }
        else
        {
            _origin = centre - new Vector2(
                (float)(Canvas.ActualWidth / _scale / 2.0),
                (float)(Canvas.ActualHeight / _scale / 2.0));
        }

        _fitDirty = false;
        _fitSnap = false;
        ClampOrigin();

        // The fit lands during a draw, after whatever raised ViewChanged has
        // already been handled, so the zoom readout would otherwise show the
        // scale from before the fit. Deferred, because a draw is no place to
        // run someone else's handler.
        DispatcherQueue.TryEnqueue(() => ViewChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Keeps the viewport inside the document, and centers whichever axis has
    /// slack instead of letting the content drift to one side.
    /// </summary>
    private void ClampOrigin()
    {
        if (_layout is not { } layout) return;

        double viewWidth = Canvas.ActualWidth / _scale;
        double viewHeight = Canvas.ActualHeight / _scale;

        float x = layout.WidthPt <= viewWidth
            ? (float)((layout.WidthPt - viewWidth) / 2.0)
            : (float)Math.Clamp(_origin.X, 0, layout.WidthPt - viewWidth);

        float y = layout.HeightPt <= viewHeight
            ? (float)((layout.HeightPt - viewHeight) / 2.0)
            : (float)Math.Clamp(_origin.Y, 0, layout.HeightPt - viewHeight);

        _origin = new Vector2(x, y);
        SyncScrollBars();
    }

    private void ScrollBy(Vector2 deltaPt)
    {
        _origin += deltaPt;
        ClampOrigin();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pushes the viewport into the scroll bars. Every path that moves or
    /// resizes the view ends up in ClampOrigin, so syncing from there is what
    /// keeps the bars from drifting out of step with the canvas.
    /// </summary>
    private void SyncScrollBars()
    {
        if (_layout is not { } layout)
        {
            VerticalScroll.Visibility = Visibility.Collapsed;
            HorizontalScroll.Visibility = Visibility.Collapsed;
            return;
        }

        _suppressScrollEvents = true;
        try
        {
            double viewWidth = Canvas.ActualWidth / _scale;
            double viewHeight = Canvas.ActualHeight / _scale;

            double verticalRange = Math.Max(0, layout.HeightPt - viewHeight);
            VerticalScroll.Maximum = verticalRange;
            VerticalScroll.ViewportSize = viewHeight;
            VerticalScroll.SmallChange = viewHeight * 0.1;
            VerticalScroll.LargeChange = viewHeight * 0.9;
            VerticalScroll.Value = Math.Clamp(_origin.Y, 0, verticalRange);
            VerticalScroll.Visibility = verticalRange > 0.5 ? Visibility.Visible : Visibility.Collapsed;

            double horizontalRange = Math.Max(0, layout.WidthPt - viewWidth);
            HorizontalScroll.Maximum = horizontalRange;
            HorizontalScroll.ViewportSize = viewWidth;
            HorizontalScroll.SmallChange = viewWidth * 0.1;
            HorizontalScroll.LargeChange = viewWidth * 0.9;
            HorizontalScroll.Value = Math.Clamp(_origin.X, 0, horizontalRange);
            HorizontalScroll.Visibility = horizontalRange > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _suppressScrollEvents = false;
        }
    }

    private void OnVerticalScroll(object sender, ScrollEventArgs e)
    {
        if (_suppressScrollEvents || _layout is null) return;
        _origin = new Vector2(_origin.X, (float)e.NewValue);
        ClampOrigin();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnHorizontalScroll(object sender, ScrollEventArgs e)
    {
        if (_suppressScrollEvents || _layout is null) return;
        _origin = new Vector2((float)e.NewValue, _origin.Y);
        ClampOrigin();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ZoomAt(Point anchor, double factor)
    {
        double newScale = Math.Clamp(_scale * factor, MinScale, MaxScale);
        if (Math.Abs(newScale - _scale) < double.Epsilon) return;

        var anchorPx = new Vector2((float)anchor.X, (float)anchor.Y);
        var docUnderAnchor = _origin + anchorPx / (float)_scale;

        _scale = newScale;
        _origin = docUnderAnchor - anchorPx / (float)_scale;

        // Zooming by hand is the reader overriding the standing fit.
        _fitMode = ViewerFitMode.Free;
        _fitDirty = false;

        ClampOrigin();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Maps a pointer position to the page under it and the point in that page's local space.</summary>
    private bool TryHitPage(Point position, out PageBox page, out float localX, out float localY)
    {
        page = default;
        localX = localY = 0;
        if (_layout is not { } layout) return false;

        double docX = _origin.X + position.X / _scale;
        double docY = _origin.Y + position.Y / _scale;

        foreach (var candidate in layout.Pages)
        {
            if (docY < candidate.YPt || docY > candidate.BottomPt) continue;
            if (docX < candidate.XPt || docX > candidate.RightPt) continue;
            page = candidate;
            localX = (float)(docX - candidate.XPt);
            localY = (float)(docY - candidate.YPt);
            return true;
        }

        return false;
    }

    // --- input ----------------------------------------------------------

    /// <summary>
    /// Moving around the sheet from the keyboard. Handled on the canvas rather
    /// than on the control, so an arrow key typed into the find bar moves the
    /// caret and not the drawing.
    ///
    /// In single-sheet mode the page keys change sheet, because there is
    /// nowhere else for them to go; in continuous mode they step a viewport at
    /// a time through the strip.
    /// </summary>
    private void OnCanvasKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_layout is null) return;

        bool control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        double stepX = Canvas.ActualWidth / _scale * ArrowStepFraction;
        double stepY = Canvas.ActualHeight / _scale * ArrowStepFraction;
        double pageY = Canvas.ActualHeight / _scale * 0.9;

        switch (e.Key)
        {
            case VirtualKey.Left:
                ScrollBy(new Vector2((float)-stepX, 0f));
                break;
            case VirtualKey.Right:
                ScrollBy(new Vector2((float)stepX, 0f));
                break;
            case VirtualKey.Up:
                ScrollBy(new Vector2(0f, (float)-stepY));
                break;
            case VirtualKey.Down:
                ScrollBy(new Vector2(0f, (float)stepY));
                break;

            case VirtualKey.PageUp:
                StepPage(-1, pageY);
                break;
            case VirtualKey.PageDown:
                StepPage(1, pageY);
                break;

            // Space reads on, the way it does in every document reader.
            case VirtualKey.Space:
                StepPage(shift ? -1 : 1, pageY);
                break;

            case VirtualKey.Home:
                if (control)
                {
                    GoToPage(0);
                }
                else
                {
                    _origin = new Vector2(_origin.X, 0f);
                    ClampOrigin();
                    Canvas.Invalidate();
                    ViewChanged?.Invoke(this, EventArgs.Empty);
                }
                break;

            case VirtualKey.End:
                if (control)
                {
                    GoToPage(PageCount - 1);
                }
                else
                {
                    _origin = new Vector2(_origin.X, _layout.HeightPt);
                    ClampOrigin();
                    Canvas.Invalidate();
                    ViewChanged?.Invoke(this, EventArgs.Empty);
                }
                break;

            case VirtualKey.Add:
            case (VirtualKey)0xBB:   // '+' on the main row
                ZoomBy(ZoomStep);
                break;
            case VirtualKey.Subtract:
            case (VirtualKey)0xBD:   // '-' on the main row
                ZoomBy(1.0 / ZoomStep);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// A viewport-sized step, which in single-sheet mode means changing sheet
    /// once there is no more of this one to show.
    /// </summary>
    private void StepPage(int direction, double viewportPt)
    {
        if (_layout is not { } layout) return;

        if (_layoutMode == ViewerLayoutMode.SinglePage)
        {
            double room = layout.HeightPt - Canvas.ActualHeight / _scale;
            bool atEnd = direction > 0
                ? _origin.Y >= room - 0.5
                : _origin.Y <= 0.5;

            if (atEnd || room <= 0.5)
            {
                GoToPage(CurrentPageIndex + direction * _columns);
                return;
            }
        }

        ScrollBy(new Vector2(0f, (float)(direction * viewportPt)));
    }

    private void OnCanvasSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        // A standing fit follows the window, without losing the reader's place.
        if (_fitMode != ViewerFitMode.Free)
        {
            _fitDirty = true;
        }

        ClampOrigin();
        Canvas.Invalidate();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_layout is null) return;

        var point = e.GetCurrentPoint(Canvas);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        var modifiers = e.KeyModifiers;
        double notches = delta / 120.0;
        bool shift = modifiers.HasFlag(VirtualKeyModifiers.Shift);

        // On a single sheet the wheel zooms, the way a CAD viewport behaves —
        // there is nowhere to scroll to, since no other page shares the space.
        // In continuous view the wheel has to scroll the strip, so zooming
        // moves to Ctrl+wheel.
        bool zoom = modifiers.HasFlag(VirtualKeyModifiers.Control)
            || (_layoutMode == ViewerLayoutMode.SinglePage && !shift);

        if (zoom)
        {
            ZoomAt(point.Position, delta > 0 ? ZoomStep : 1.0 / ZoomStep);
        }
        else if (shift)
        {
            ScrollBy(new Vector2((float)(-notches * WheelScrollDips / _scale), 0f));
        }
        else
        {
            ScrollBy(new Vector2(0f, (float)(-notches * WheelScrollDips / _scale)));
        }

        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_layout is null) return;

        var point = e.GetCurrentPoint(Canvas);
        bool left = point.Properties.IsLeftButtonPressed;
        bool middle = point.Properties.IsMiddleButtonPressed;
        if (!left && !middle) return;

        Canvas.Focus(Microsoft.UI.Xaml.FocusState.Pointer);

        // Middle-drag always pans, whichever tool is active — so the zoom
        // rectangle can stay selected while you still move around freely.
        if (left && _tool == ViewerTool.SelectText)
        {
            BeginSelection(point.Position);
        }
        else if (left && _tool == ViewerTool.ZoomRectangle)
        {
            _isZoomBanding = true;
            _zoomBandStart = point.Position;
            _zoomBandEnd = point.Position;
        }
        else
        {
            _isPanning = true;
            _lastPointerPosition = point.Position;
        }

        Canvas.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_layout is null) return;
        var position = e.GetCurrentPoint(Canvas).Position;

        if (_isSelecting)
        {
            ExtendSelection(position);
            return;
        }

        if (_isZoomBanding)
        {
            _zoomBandEnd = position;
            Canvas.Invalidate();
            return;
        }

        if (!_isPanning) return;

        var deltaDips = new Vector2(
            (float)(position.X - _lastPointerPosition.X),
            (float)(position.Y - _lastPointerPosition.Y));
        _lastPointerPosition = position;

        ScrollBy(-deltaDips / (float)_scale);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isZoomBanding)
        {
            _isZoomBanding = false;
            ApplyZoomBand(e.GetCurrentPoint(Canvas).Position);
            Canvas.ReleasePointerCapture(e.Pointer);
            return;
        }

        if (!_isPanning && !_isSelecting) return;
        _isPanning = false;
        _isSelecting = false;
        Canvas.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>
    /// Puts the dragged rectangle in the viewport. A drag too small to be
    /// deliberate is treated as a click, and just zooms in a step about that
    /// point — otherwise a stray click would fling the view to a random
    /// magnification.
    /// </summary>
    private void ApplyZoomBand(Point end)
    {
        _zoomBandEnd = end;

        double width = Math.Abs(_zoomBandEnd.X - _zoomBandStart.X);
        double height = Math.Abs(_zoomBandEnd.Y - _zoomBandStart.Y);

        if (width < MinZoomBandDips || height < MinZoomBandDips)
        {
            ZoomAt(_zoomBandStart, ZoomStep * ZoomStep);
            Canvas.Invalidate();
            return;
        }

        double left = Math.Min(_zoomBandStart.X, _zoomBandEnd.X);
        double top = Math.Min(_zoomBandStart.Y, _zoomBandEnd.Y);

        // The centre of the band, in document space, before the scale changes.
        var centre = _origin + new Vector2(
            (float)((left + width / 2.0) / _scale),
            (float)((top + height / 2.0) / _scale));

        double target = Math.Min(Canvas.ActualWidth / (width / _scale), Canvas.ActualHeight / (height / _scale));
        _scale = Math.Clamp(target, MinScale, MaxScale);

        _origin = centre - new Vector2(
            (float)(Canvas.ActualWidth / _scale / 2.0),
            (float)(Canvas.ActualHeight / _scale / 2.0));

        // The reader just chose a magnification by hand; no standing fit survives that.
        _fitMode = ViewerFitMode.Free;
        _fitDirty = false;

        ClampOrigin();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BeginSelection(Point position)
    {
        if (!TryHitPage(position, out var page, out float localX, out float localY)) return;
        if (!_textLayers.TryGetValue(new TextLayerKey(page.Index, RotationOf(page.Index)), out var layer)) return;

        int index = layer.HitTest(localX, localY, SelectionSnapPt);
        if (index < 0)
        {
            ClearSelection();
        }
        else
        {
            _selectionPage = page.Index;
            _selectionAnchor = index;
            _selectionFocus = index;
            _isSelecting = true;
        }

        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ExtendSelection(Point position)
    {
        if (_selectionPage < 0) return;
        if (!TryHitPage(position, out var page, out float localX, out float localY)) return;

        // Selection stays within the page it started on.
        if (page.Index != _selectionPage) return;
        if (!_textLayers.TryGetValue(new TextLayerKey(_selectionPage, RotationOf(_selectionPage)), out var layer))
        {
            return;
        }

        int index = layer.HitTest(localX, localY, float.MaxValue);
        if (index < 0 || index == _selectionFocus) return;

        _selectionFocus = index;
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // --- drawing --------------------------------------------------------

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        ApplyPendingFit();
        if (_layout is not { } layout) return;

        var ds = args.DrawingSession;
        var view = ViewportInDocSpace();

        // Tiles are rasterized in physical device pixels, so the level has to
        // account for the display's scaling factor. Choosing it from DIPs alone
        // renders every tile short of the panel's real resolution, which is what
        // made a 150%-scaled display look permanently soft.
        double dpiScale = sender.Dpi / 96.0;
        int level = ZoomLevels.LevelForScale(_scale * dpiScale);

        foreach (var page in layout.PagesInBand(view.Top, view.Bottom))
        {
            var pageRect = ToScreenRect(page);
            if (pageRect.Width <= 0 || pageRect.Height <= 0) continue;

            ds.FillRectangle(pageRect, Colors.White);

            using (ds.CreateLayer(1f, pageRect))
            {
                // Tiles abut, so their edges must not be antialiased: two
                // touching edges at half coverage each compose to three
                // quarters, and that shortfall is the pale seam that shows up
                // across a solid fill. Snapping the edges to whole device
                // pixels (below) is what makes drawing them aliased safe.
                var antialiasing = ds.Antialiasing;
                ds.Antialiasing = CanvasAntialiasing.Aliased;

                int floor = Math.Max(ZoomLevels.MinLevel, level - FallbackLevels);
                for (int fallback = floor; fallback < level; fallback++)
                {
                    DrawPageTiles(ds, page, fallback, view, dpiScale, requestMissing: false);
                }

                DrawPageTiles(ds, page, level, view, dpiScale, requestMissing: true);

                ds.Antialiasing = antialiasing;

                DrawSearchHits(ds, page);
                DrawSelection(ds, page);
            }

            if (_tool == ViewerTool.SelectText)
            {
                EnsureTextLayer(page.Index);
            }
        }

        // Outside the per-page clip: the band is a piece of interface, and it
        // may well be dragged across the gap between two sheets.
        DrawZoomBand(ds);
    }

    private void DrawZoomBand(CanvasDrawingSession ds)
    {
        if (!_isZoomBanding) return;

        var band = new Rect(
            Math.Min(_zoomBandStart.X, _zoomBandEnd.X),
            Math.Min(_zoomBandStart.Y, _zoomBandEnd.Y),
            Math.Abs(_zoomBandEnd.X - _zoomBandStart.X),
            Math.Abs(_zoomBandEnd.Y - _zoomBandStart.Y));

        if (band.Width <= 0 || band.Height <= 0) return;

        ds.FillRectangle(band, ZoomBandFill);
        ds.DrawRectangle(band, ZoomBandStroke, 1.2f);
    }

    private Rect ToScreenRect(PageBox page) => new(
        (page.XPt - _origin.X) * _scale,
        (page.YPt - _origin.Y) * _scale,
        Math.Max(0, page.WidthPt * _scale),
        Math.Max(0, page.HeightPt * _scale));

    private void DrawPageTiles(
        CanvasDrawingSession ds, PageBox page, int level, Rect view, double dpiScale, bool requestMissing)
    {
        int tileSize = ZoomLevels.TileSize;
        double levelScale = ZoomLevels.ScaleForLevel(level);
        double tilePt = tileSize / levelScale;
        if (tilePt <= 0) return;

        // Visible slice of this page, in page-local points.
        double left = Math.Max(0, view.Left - page.XPt);
        double top = Math.Max(0, view.Top - page.YPt);
        double right = Math.Min(page.WidthPt, view.Right - page.XPt);
        double bottom = Math.Min(page.HeightPt, view.Bottom - page.YPt);
        if (right <= left || bottom <= top) return;

        int rotation = RotationOf(page.Index);
        int colStart = (int)Math.Floor(left / tilePt);
        int colEnd = (int)Math.Floor((right - 1e-6) / tilePt);
        int rowStart = (int)Math.Floor(top / tilePt);
        int rowEnd = (int)Math.Floor((bottom - 1e-6) / tilePt);

        for (int row = rowStart; row <= rowEnd; row++)
        {
            // Each boundary is derived from its grid index alone and snapped
            // the same way, so the bottom edge of one tile and the top edge of
            // the next are the very same number rather than two roundings of
            // it. That is what leaves no gap and no overlap between them.
            double destTop = SnapToPixel((page.YPt + row * tilePt - _origin.Y) * _scale, dpiScale);
            double destBottom = SnapToPixel((page.YPt + (row + 1) * tilePt - _origin.Y) * _scale, dpiScale);

            for (int col = colStart; col <= colEnd; col++)
            {
                var key = new TileKey(page.Index, level, col, row, rotation);

                if (!_cache.TryGet(key, out var bitmap))
                {
                    if (requestMissing)
                    {
                        RequestTile(key);
                    }
                    continue;
                }

                double destLeft = SnapToPixel((page.XPt + col * tilePt - _origin.X) * _scale, dpiScale);
                double destRight = SnapToPixel((page.XPt + (col + 1) * tilePt - _origin.X) * _scale, dpiScale);

                ds.DrawImage(bitmap, new Rect(
                    destLeft,
                    destTop,
                    Math.Max(0, destRight - destLeft),
                    Math.Max(0, destBottom - destTop)));
            }
        }
    }

    /// <summary>
    /// Rounds a position in DIPs to the display's own pixel grid. Tiles are
    /// rasterized in physical pixels, so this is the grid their edges have to
    /// land on to stay crisp and to meet each other exactly.
    /// </summary>
    private static double SnapToPixel(double dips, double dpiScale) =>
        dpiScale <= 0 ? dips : Math.Round(dips * dpiScale, MidpointRounding.AwayFromZero) / dpiScale;

    private void DrawSelection(CanvasDrawingSession ds, PageBox page)
    {
        if (!HasSelection || page.Index != _selectionPage) return;
        if (!_textLayers.TryGetValue(new TextLayerKey(_selectionPage, RotationOf(_selectionPage)), out var layer))
        {
            return;
        }

        foreach (var run in layer.BuildRuns(_selectionAnchor, _selectionFocus))
        {
            ds.FillRectangle(ToScreenRect(page, run), SelectionFill);
        }
    }

    /// <summary>
    /// Paints this sheet's search hits: every one in amber, the one the reader
    /// is on in orange with an outline, so it reads as "here" among the rest
    /// the way a browser's find does.
    /// </summary>
    private void DrawSearchHits(CanvasDrawingSession ds, PageBox page)
    {
        if (_hits.Count == 0) return;
        if (!_hitsByPage.TryGetValue(page.Index, out var range)) return;

        for (int i = range.Start; i < range.Start + range.Count && i < _hits.Count; i++)
        {
            bool current = i == _searchCursor;

            foreach (var run in _hits[i].Runs)
            {
                var rect = ToScreenRect(page, run);
                // A hit on a tiny label can land under a pixel; widen it so it
                // is still findable when the whole sheet is on screen.
                if (rect.Width < 3) rect = new Rect(rect.X - 1.5, rect.Y, 3, rect.Height);
                if (rect.Height < 3) rect = new Rect(rect.X, rect.Y - 1.5, rect.Width, 3);

                ds.FillRectangle(rect, current ? SearchCurrentFill : SearchFill);
                if (current)
                {
                    ds.DrawRectangle(rect, SearchCurrentStroke, 1.4f);
                }
            }
        }
    }

    private Rect ToScreenRect(PageBox page, TextRun run) => new(
        (page.XPt + run.Left - _origin.X) * _scale,
        (page.YPt + run.Top - _origin.Y) * _scale,
        Math.Max(0, (run.Right - run.Left) * _scale),
        Math.Max(0, (run.Bottom - run.Top) * _scale));

    private void RequestTile(TileKey key)
    {
        if (_documentId < 0 || !_inFlight.Add(key)) return;
        _ = LoadTileAsync(key, _documentId, _documentGeneration);
    }

    private async Task LoadTileAsync(TileKey key, int documentId, int generation)
    {
        try
        {
            var data = await _queue.RequestTileAsync(documentId, key, ZoomLevels.TileSize);
            if (generation != _documentGeneration || !_isActive) return;

            if (data is { } tile)
            {
                var bitmap = CanvasBitmap.CreateFromBytes(
                    Canvas,
                    tile.Bgra,
                    tile.Width,
                    tile.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    Canvas.Dpi);
                _cache.Add(key, bitmap);
                Canvas.Invalidate();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ZenInk: fallo al renderizar tile {key}: {ex}");
        }
        finally
        {
            _inFlight.Remove(key);
        }
    }

    private void EnsureTextLayer(int pageIndex)
    {
        var key = new TextLayerKey(pageIndex, RotationOf(pageIndex));
        if (_documentId < 0 || _textLayers.ContainsKey(key) || !_textInFlight.Add(key)) return;
        _ = LoadTextLayerAsync(key, _documentId, _documentGeneration);
    }

    private async Task LoadTextLayerAsync(TextLayerKey key, int documentId, int generation)
    {
        try
        {
            var layer = await _queue.RequestTextLayerAsync(documentId, key.PageIndex, key.Rotation);
            if (generation != _documentGeneration) return;

            RememberTextLayer(key, layer);
            Canvas.Invalidate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ZenInk: fallo al extraer texto de la página {key.PageIndex}: {ex}");
        }
        finally
        {
            _textInFlight.Remove(key);
        }
    }

    private void RememberTextLayer(TextLayerKey key, PageTextLayer layer)
    {
        _textLayers[key] = layer;
        _textLru.Remove(key);
        _textLru.AddLast(key);

        while (_textLru.Count > MaxTextLayers)
        {
            var oldest = _textLru.First!.Value;
            _textLru.RemoveFirst();
            if (oldest.PageIndex != _selectionPage)
            {
                _textLayers.Remove(oldest);
            }
        }
    }

    // --- find -----------------------------------------------------------

    /// <summary>Shows the find bar and puts the caret in it, keeping any term already typed.</summary>
    public void OpenFind()
    {
        FindBar.Visibility = Visibility.Visible;

        // Closing the bar drops the results but keeps the term, so reopening
        // has to look for it again rather than show an empty count.
        if (FindInput.Text.Length > 0 && _searchQuery != FindInput.Text)
        {
            _searchQuery = FindInput.Text;
            RestartSearch();
        }

        FindInput.Focus(FocusState.Programmatic);
        FindInput.SelectAll();
    }

    /// <summary>Opens the find bar already looking for a given term.</summary>
    public void FindText(string term)
    {
        FindInput.Text = term;
        OpenFind();
    }

    public void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        ResetSearch();
        Canvas.Invalidate();
        Canvas.Focus(FocusState.Programmatic);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsFindOpen => FindBar.Visibility == Visibility.Visible;

    private void OnFindCloseClicked(object sender, RoutedEventArgs e) => CloseFind();

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = FindInput.Text;
        RestartSearch();
    }

    private void OnFindOptionChanged(object sender, RoutedEventArgs e)
    {
        _searchOptions = new TextSearchOptions(
            FindMatchCase.IsChecked == true,
            FindWholeWord.IsChecked == true);
        RestartSearch();
    }

    private void OnFindInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                // Shift walks backwards, the way a browser's find bar does.
                bool back = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                StepFind(back ? -1 : 1);
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                CloseFind();
                e.Handled = true;
                break;
        }
    }

    private void OnFindNextClicked(object sender, RoutedEventArgs e) => StepFind(1);

    private void OnFindPreviousClicked(object sender, RoutedEventArgs e) => StepFind(-1);

    /// <summary>Moves to the next or previous hit, wrapping round the document.</summary>
    public void StepFind(int direction)
    {
        if (_hits.Count == 0) return;

        int next = _searchCursor < 0
            ? (direction >= 0 ? 0 : _hits.Count - 1)
            : (_searchCursor + direction + _hits.Count) % _hits.Count;

        GoToHit(next);
    }

    /// <summary>
    /// Drops the search entirely — no term, no hits, no highlights. The text
    /// box keeps what was typed, because reopening the bar on the same term is
    /// the common case; the *query* has to go, or a later turn would rebuild
    /// highlights for a search the reader had closed.
    /// </summary>
    private void ResetSearch()
    {
        _searchGeneration++;
        _searchRunning = false;
        _searchScanned = 0;
        _searchCursor = -1;
        _searchQuery = string.Empty;
        _hits.Clear();
        _hitsByPage.Clear();
        UpdateFindChrome();
    }

    private void RestartSearch()
    {
        _searchGeneration++;
        _searchRunning = false;
        _searchScanned = 0;
        _searchCursor = -1;
        _hits.Clear();
        _hitsByPage.Clear();

        Canvas.Invalidate();

        if (_searchQuery.Length == 0 || _documentId < 0 || PageCount == 0)
        {
            UpdateFindChrome();
            return;
        }

        _searchRunning = true;
        UpdateFindChrome();
        _ = ScanAsync(_searchGeneration, _documentGeneration, _searchQuery, _searchOptions);
    }

    /// <summary>
    /// Walks the document a sheet at a time, in page order, so hits arrive
    /// already sorted and the count can climb while the reader waits. One sheet
    /// is in flight at a time on purpose: text extraction sits below tiles in
    /// the queue, and flooding it would still leave a dense sheet's tiles
    /// queued behind a hundred text requests.
    /// </summary>
    private async Task ScanAsync(int generation, int documentGeneration, string query, TextSearchOptions options)
    {
        int startPage = CurrentPageIndex;

        for (int pageIndex = 0; pageIndex < PageCount; pageIndex++)
        {
            if (generation != _searchGeneration || documentGeneration != _documentGeneration) return;

            var key = new TextLayerKey(pageIndex, RotationOf(pageIndex));
            PageTextLayer layer;

            if (_textLayers.TryGetValue(key, out var cached))
            {
                layer = cached;
            }
            else
            {
                try
                {
                    layer = await _queue.RequestTextLayerAsync(_documentId, key.PageIndex, key.Rotation);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ZenInk: fallo al buscar en la hoja {pageIndex}: {ex}");
                    continue;
                }

                if (generation != _searchGeneration || documentGeneration != _documentGeneration) return;
                RememberTextLayer(key, layer);
            }

            var matches = layer.Find(query, options);
            if (matches.Count > 0)
            {
                _hitsByPage[pageIndex] = (_hits.Count, matches.Count);
                foreach (var match in matches)
                {
                    _hits.Add(new SearchHit(pageIndex, match, layer.BuildRuns(match.StartIndex, match.EndIndex)));
                }

                // Land on the first hit at or after the sheet in view, so a
                // search starts where the reader is rather than at page one.
                if (_searchCursor < 0 && pageIndex >= startPage)
                {
                    GoToHit(_hitsByPage[pageIndex].Start);
                }
            }

            _searchScanned = pageIndex + 1;
            UpdateFindChrome();
            Canvas.Invalidate();
        }

        if (generation != _searchGeneration) return;

        _searchRunning = false;

        // Nothing after the reader's sheet: wrap to the first hit in the file.
        if (_searchCursor < 0 && _hits.Count > 0)
        {
            GoToHit(0);
        }

        UpdateFindChrome();
    }

    /// <summary>Selects a hit and brings it into view, centred, without changing the zoom.</summary>
    private void GoToHit(int index)
    {
        if (index < 0 || index >= _hits.Count) return;

        _searchCursor = index;
        var hit = _hits[index];

        // In single-sheet mode — and in a two-up spread — the hit may be on a
        // sheet that is not currently laid out at all.
        if (_layout?.RowOfPage(hit.PageIndex) is null)
        {
            GoToPage(hit.PageIndex);
        }

        if (_layout is { } layout)
        {
            foreach (var page in layout.Pages)
            {
                if (page.Index != hit.PageIndex) continue;
                CentreOn(page, hit.Runs);
                break;
            }
        }

        UpdateFindChrome();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CentreOn(PageBox page, IReadOnlyList<TextRun> runs)
    {
        if (runs.Count == 0) return;

        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        foreach (var run in runs)
        {
            left = Math.Min(left, run.Left);
            top = Math.Min(top, run.Top);
            right = Math.Max(right, run.Right);
            bottom = Math.Max(bottom, run.Bottom);
        }

        var centre = new Vector2(
            page.XPt + (left + right) / 2f,
            page.YPt + (top + bottom) / 2f);

        _origin = centre - new Vector2(
            (float)(Canvas.ActualWidth / _scale / 2.0),
            (float)(Canvas.ActualHeight / _scale / 2.0));

        ClampOrigin();
    }

    /// <summary>
    /// Keeps the find bar honest while a scan is still running: the count is a
    /// running total, not a final one, so it says so rather than looking like a
    /// document with fewer hits than it has.
    /// </summary>
    private void UpdateFindChrome()
    {
        bool any = _hits.Count > 0;

        FindNextButton.IsEnabled = any;
        FindPreviousButton.IsEnabled = any;

        if (_searchQuery.Length == 0)
        {
            FindCount.Text = string.Empty;
            return;
        }

        if (!any)
        {
            FindCount.Text = _searchRunning ? "Buscando…" : "Sin resultados";
            return;
        }

        string position = _searchCursor >= 0 ? $"{_searchCursor + 1}/{_hits.Count}" : $"{_hits.Count}";
        FindCount.Text = _searchRunning && _searchScanned < PageCount ? $"{position}…" : position;
    }
}
