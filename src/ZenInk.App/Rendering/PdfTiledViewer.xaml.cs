using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.System;
using Windows.UI;

namespace ZenInk_App.Rendering;

public enum ViewerTool
{
    Pan,
    SelectText,
}

public enum ViewerLayoutMode
{
    /// <summary>Every sheet stacked in one scrollable strip.</summary>
    Continuous,

    /// <summary>One sheet at a time; scrolling stays on it and pages change explicitly.</summary>
    SinglePage,
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

    private static readonly Color SelectionFill = Color.FromArgb(70, 0, 103, 192);

    private readonly PdfRenderQueue _queue = PdfRenderQueue.Shared;
    private readonly TileCache _cache = new(CacheBudgetBytes);
    private readonly HashSet<TileKey> _inFlight = new();
    private readonly Dictionary<int, PageTextLayer> _textLayers = new();
    private readonly LinkedList<int> _textLru = new();
    private readonly HashSet<int> _textInFlight = new();

    private DocumentLayout? _layout;
    private int _documentId = -1;
    private bool _isActive = true;
    private IReadOnlyList<PdfPageSize> _pageSizes = [];
    private ViewerLayoutMode _layoutMode = ViewerLayoutMode.Continuous;
    private int _currentPageIndex;
    private double _scale = 1.0;
    private Vector2 _origin;
    private bool _pendingFit;
    private bool _isPanning;
    private bool _isSelecting;
    private Point _lastPointerPosition;
    private ViewerTool _tool = ViewerTool.Pan;
    private bool _thinLines;

    /// <summary>Bumped on every open, so results for a previous document are discarded.</summary>
    private int _documentGeneration;

    private int _selectionPage = -1;
    private int _selectionAnchor = -1;
    private int _selectionFocus = -1;

    public PdfTiledViewer()
    {
        InitializeComponent();
        UpdateCursor();
    }

    /// <summary>Raised when the visible page, zoom level or selection changes.</summary>
    public event EventHandler? ViewChanged;

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
            ? DocumentLayout.SinglePage(_pageSizes, _currentPageIndex)
            : DocumentLayout.Continuous(_pageSizes);

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
        _documentGeneration++;
        _cache.Clear();
        _inFlight.Clear();
        _textLayers.Clear();
        _textLru.Clear();
        _textInFlight.Clear();
        ClearSelection();

        _pageSizes = info.Pages;
        _currentPageIndex = 0;
        _layout = _layoutMode == ViewerLayoutMode.SinglePage
            ? DocumentLayout.SinglePage(_pageSizes, 0)
            : DocumentLayout.Continuous(_pageSizes);
        _origin = Vector2.Zero;
        _pendingFit = true;

        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void FitToWidth()
    {
        _pendingFit = true;
        Canvas.Invalidate();
    }

    public string? GetSelectedText()
    {
        if (!HasSelection) return null;
        if (!_textLayers.TryGetValue(_selectionPage, out var layer)) return null;

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

        if (_documentId >= 0)
        {
            _ = _queue.CloseDocumentAsync(_documentId);
            _documentId = -1;
        }

        _layout = null;
        _pageSizes = [];
        Canvas.RemoveFromVisualTree();
    }

    private void UpdateCursor() => ProtectedCursor = InputSystemCursor.Create(
        _tool == ViewerTool.SelectText ? InputSystemCursorShape.IBeam : InputSystemCursorShape.Hand);

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
        if (_layout is null || _tool != ViewerTool.SelectText) return;

        int pageIndex = CurrentPageIndex;
        if (_textLayers.TryGetValue(pageIndex, out var layer) && layer.Count > 0)
        {
            _selectionPage = pageIndex;
            _selectionAnchor = 0;
            _selectionFocus = layer.Count - 1;
            Canvas.Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }

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
    /// Applies a deferred fit-to-width once the canvas actually has a size.
    /// Fitting straight after the document loads is unreliable — the canvas may
    /// still be zero-sized or mid-layout, which is what produced inconsistent
    /// side margins before.
    /// </summary>
    private void ApplyPendingFit()
    {
        if (!_pendingFit || _layout is not { } layout) return;
        if (Canvas.ActualWidth <= 0 || Canvas.ActualHeight <= 0 || layout.WidthPt <= 0) return;

        _scale = Math.Clamp(Canvas.ActualWidth / layout.WidthPt, MinScale, MaxScale);
        _origin = Vector2.Zero;
        _pendingFit = false;
        ClampOrigin();
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
    }

    private void ScrollBy(Vector2 deltaPt)
    {
        _origin += deltaPt;
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
            page = candidate;
            localX = (float)(docX - candidate.XPt);
            localY = (float)(docY - candidate.YPt);
            return true;
        }

        return false;
    }

    // --- input ----------------------------------------------------------

    private void OnCanvasSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
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

        // Middle-drag always pans, whichever tool is active.
        if (left && _tool == ViewerTool.SelectText)
        {
            BeginSelection(point.Position);
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

        if (!_isPanning) return;

        var deltaDips = new Vector2(
            (float)(position.X - _lastPointerPosition.X),
            (float)(position.Y - _lastPointerPosition.Y));
        _lastPointerPosition = position;

        ScrollBy(-deltaDips / (float)_scale);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning && !_isSelecting) return;
        _isPanning = false;
        _isSelecting = false;
        Canvas.ReleasePointerCapture(e.Pointer);
    }

    private void BeginSelection(Point position)
    {
        if (!TryHitPage(position, out var page, out float localX, out float localY)) return;
        if (!_textLayers.TryGetValue(page.Index, out var layer)) return;

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
        if (!_textLayers.TryGetValue(_selectionPage, out var layer)) return;

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
                int floor = Math.Max(ZoomLevels.MinLevel, level - FallbackLevels);
                for (int fallback = floor; fallback < level; fallback++)
                {
                    DrawPageTiles(ds, page, fallback, view, requestMissing: false);
                }

                DrawPageTiles(ds, page, level, view, requestMissing: true);
                DrawSelection(ds, page);
            }

            if (_tool == ViewerTool.SelectText)
            {
                EnsureTextLayer(page.Index);
            }
        }
    }

    private Rect ToScreenRect(PageBox page) => new(
        (page.XPt - _origin.X) * _scale,
        (page.YPt - _origin.Y) * _scale,
        Math.Max(0, page.WidthPt * _scale),
        Math.Max(0, page.HeightPt * _scale));

    private void DrawPageTiles(CanvasDrawingSession ds, PageBox page, int level, Rect view, bool requestMissing)
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

        int colStart = (int)Math.Floor(left / tilePt);
        int colEnd = (int)Math.Floor((right - 1e-6) / tilePt);
        int rowStart = (int)Math.Floor(top / tilePt);
        int rowEnd = (int)Math.Floor((bottom - 1e-6) / tilePt);

        for (int row = rowStart; row <= rowEnd; row++)
        {
            for (int col = colStart; col <= colEnd; col++)
            {
                var key = new TileKey(page.Index, level, col, row);
                var dest = new Rect(
                    (page.XPt + col * tilePt - _origin.X) * _scale,
                    (page.YPt + row * tilePt - _origin.Y) * _scale,
                    tilePt * _scale,
                    tilePt * _scale);

                if (_cache.TryGet(key, out var bitmap))
                {
                    ds.DrawImage(bitmap, dest);
                }
                else if (requestMissing)
                {
                    RequestTile(key);
                }
            }
        }
    }

    private void DrawSelection(CanvasDrawingSession ds, PageBox page)
    {
        if (!HasSelection || page.Index != _selectionPage) return;
        if (!_textLayers.TryGetValue(_selectionPage, out var layer)) return;

        foreach (var run in layer.BuildRuns(_selectionAnchor, _selectionFocus))
        {
            var rect = new Rect(
                (page.XPt + run.Left - _origin.X) * _scale,
                (page.YPt + run.Top - _origin.Y) * _scale,
                Math.Max(0, (run.Right - run.Left) * _scale),
                Math.Max(0, (run.Bottom - run.Top) * _scale));
            ds.FillRectangle(rect, SelectionFill);
        }
    }

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
        if (_documentId < 0 || _textLayers.ContainsKey(pageIndex) || !_textInFlight.Add(pageIndex)) return;
        _ = LoadTextLayerAsync(pageIndex, _documentId, _documentGeneration);
    }

    private async Task LoadTextLayerAsync(int pageIndex, int documentId, int generation)
    {
        try
        {
            var layer = await _queue.RequestTextLayerAsync(documentId, pageIndex);
            if (generation != _documentGeneration) return;

            _textLayers[pageIndex] = layer;
            _textLru.Remove(pageIndex);
            _textLru.AddLast(pageIndex);

            while (_textLru.Count > MaxTextLayers)
            {
                int oldest = _textLru.First!.Value;
                _textLru.RemoveFirst();
                if (oldest != _selectionPage)
                {
                    _textLayers.Remove(oldest);
                }
            }

            Canvas.Invalidate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ZenInk: fallo al extraer texto de la página {pageIndex}: {ex}");
        }
        finally
        {
            _textInFlight.Remove(pageIndex);
        }
    }
}
