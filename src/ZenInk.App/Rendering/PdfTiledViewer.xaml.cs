using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.System;

namespace ZenInk_App.Rendering;

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

    private readonly PdfRenderQueue _queue = new();
    private readonly TileCache _cache = new(CacheBudgetBytes);
    private readonly HashSet<TileKey> _inFlight = new();

    private DocumentLayout? _layout;
    private double _scale = 1.0;
    private Vector2 _origin;
    private bool _pendingFit;
    private bool _isPanning;
    private Point _lastPointerPosition;

    public PdfTiledViewer()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    /// <summary>Raised when the visible page or zoom level changes.</summary>
    public event EventHandler? ViewChanged;

    public int PageCount => _layout?.PageCount ?? 0;

    public int CurrentPageNumber
    {
        get
        {
            if (_layout is not { } layout) return 0;
            var view = ViewportInDocSpace();
            return layout.DominantPageIndex(view.Top, view.Bottom) + 1;
        }
    }

    public double ZoomPercent => _scale * 100.0;

    public async Task OpenAsync(string path)
    {
        var info = await _queue.OpenDocumentAsync(path);

        _cache.Clear();
        _inFlight.Clear();
        _layout = new DocumentLayout(info.Pages);
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

    private void OnUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _cache.Clear();
        _queue.Dispose();
        Canvas.RemoveFromVisualTree();
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

        if (modifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            ZoomAt(point.Position, delta > 0 ? ZoomStep : 1.0 / ZoomStep);
        }
        else if (modifiers.HasFlag(VirtualKeyModifiers.Shift))
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
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed) return;

        _isPanning = true;
        _lastPointerPosition = point.Position;
        Canvas.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning || _layout is null) return;

        var position = e.GetCurrentPoint(Canvas).Position;
        var deltaDips = new Vector2(
            (float)(position.X - _lastPointerPosition.X),
            (float)(position.Y - _lastPointerPosition.Y));
        _lastPointerPosition = position;

        ScrollBy(-deltaDips / (float)_scale);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        Canvas.ReleasePointerCapture(e.Pointer);
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

    private void RequestTile(TileKey key)
    {
        if (!_inFlight.Add(key)) return;
        _ = LoadTileAsync(key);
    }

    private async Task LoadTileAsync(TileKey key)
    {
        try
        {
            var data = await _queue.RequestTileAsync(key, ZoomLevels.TileSize);
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
}
