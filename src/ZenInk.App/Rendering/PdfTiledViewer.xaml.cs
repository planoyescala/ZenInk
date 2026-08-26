using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.DirectX;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace ZenInk_App.Rendering;

/// <summary>
/// Pans and zooms a PDF page rendered as a grid of cached tiles. Coordinates
/// are tracked in PDF points (<see cref="_origin"/> = the page point at the
/// viewport's top-left corner, top-left/y-down for on-screen convenience) so
/// the math is independent of which discrete zoom level is currently backing
/// the tiles on screen.
///
/// Draw() renders progressively: it first paints whatever coarser-level tiles
/// are already cached (so panning into new territory shows a soft, previously
/// seen version immediately) and then overpaints with exact tiles at the
/// current level, requesting from <see cref="PdfRenderQueue"/> whichever ones
/// are missing.
/// </summary>
public sealed partial class PdfTiledViewer : UserControl
{
    private const double MinScale = 0.05;
    private const double MaxScale = 12.0;
    private const int FallbackLevels = 6;
    private const long CacheBudgetBytes = 256L * 1024 * 1024;

    private readonly PdfRenderQueue _queue = new();
    private readonly TileCache _cache = new(CacheBudgetBytes);
    private readonly HashSet<TileKey> _inFlight = new();

    private PdfDocumentInfo? _document;
    private int _pageIndex;
    private double _scale = 1.0;
    private Vector2 _origin;
    private bool _isPanning;
    private Point _lastPointerPosition;

    public PdfTiledViewer()
    {
        InitializeComponent();
        Unloaded += (_, _) => _queue.Dispose();
    }

    public async Task OpenAsync(string path)
    {
        _document = await _queue.OpenDocumentAsync(path);
        _pageIndex = 0;
        _cache.Clear();
        _inFlight.Clear();
        FitToWidth();
        Canvas.Invalidate();
    }

    private void FitToWidth()
    {
        if (_document is not { } doc || Canvas.ActualWidth <= 0)
        {
            return;
        }

        _scale = Math.Clamp(Canvas.ActualWidth / doc.WidthPt, MinScale, MaxScale);
        _origin = Vector2.Zero;
    }

    private void OnCanvasSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        if (_document is not null && e.PreviousSize.Width == 0)
        {
            FitToWidth();
        }
        Canvas.Invalidate();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_document is not { } doc) return;

        var point = e.GetCurrentPoint(Canvas);
        int wheelDelta = point.Properties.MouseWheelDelta;
        double factor = wheelDelta > 0 ? 1.15 : 1 / 1.15;

        double newScale = Math.Clamp(_scale * factor, MinScale, MaxScale);
        if (newScale == _scale) return;

        var cursorPx = new Vector2((float)point.Position.X, (float)point.Position.Y);
        var pageUnderCursor = _origin + cursorPx / (float)_scale;

        _scale = newScale;
        _origin = pageUnderCursor - cursorPx / (float)_scale;
        ClampOrigin(doc);

        Canvas.Invalidate();
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_document is null) return;
        var point = e.GetCurrentPoint(Canvas);
        if (!point.Properties.IsLeftButtonPressed) return;

        _isPanning = true;
        _lastPointerPosition = point.Position;
        Canvas.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning || _document is not { } doc) return;

        var position = e.GetCurrentPoint(Canvas).Position;
        var deltaPx = new Vector2((float)(position.X - _lastPointerPosition.X), (float)(position.Y - _lastPointerPosition.Y));
        _origin -= deltaPx / (float)_scale;
        _lastPointerPosition = position;
        ClampOrigin(doc);

        Canvas.Invalidate();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        Canvas.ReleasePointerCapture(e.Pointer);
    }

    private void ClampOrigin(PdfDocumentInfo doc)
    {
        double viewWidthPt = Canvas.ActualWidth / _scale;
        double viewHeightPt = Canvas.ActualHeight / _scale;

        // Allow a bit of overscroll (half a viewport) rather than hard-clamping
        // exactly to the page edge, which feels stiff during fast pans.
        float maxX = (float)Math.Max(doc.WidthPt - viewWidthPt * 0.5, -viewWidthPt * 0.5);
        float minX = (float)(-viewWidthPt * 0.5);
        float maxY = (float)Math.Max(doc.HeightPt - viewHeightPt * 0.5, -viewHeightPt * 0.5);
        float minY = (float)(-viewHeightPt * 0.5);

        _origin = new Vector2(Math.Clamp(_origin.X, minX, maxX), Math.Clamp(_origin.Y, minY, maxY));
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_document is not { } doc) return;

        int level = ZoomLevels.LevelForScale(_scale);
        var ds = args.DrawingSession;

        for (int fallback = Math.Max(0, level - FallbackLevels); fallback < level; fallback++)
        {
            DrawLevel(ds, doc, fallback, requestMissing: false);
        }

        DrawLevel(ds, doc, level, requestMissing: true);
    }

    private void DrawLevel(CanvasDrawingSession ds, PdfDocumentInfo doc, int level, bool requestMissing)
    {
        int ts = ZoomLevels.TileSize;
        double levelScale = ZoomLevels.ScaleForLevel(level);
        double tilePtSize = ts / levelScale;

        double viewWidthPt = Canvas.ActualWidth / _scale;
        double viewHeightPt = Canvas.ActualHeight / _scale;

        int maxCol = Math.Max(0, (int)Math.Ceiling(doc.WidthPt * levelScale / ts) - 1);
        int maxRow = Math.Max(0, (int)Math.Ceiling(doc.HeightPt * levelScale / ts) - 1);

        int colStart = Math.Clamp((int)Math.Floor(_origin.X / tilePtSize), 0, maxCol);
        int colEnd = Math.Clamp((int)Math.Ceiling((_origin.X + viewWidthPt) / tilePtSize), 0, maxCol);
        int rowStart = Math.Clamp((int)Math.Floor(_origin.Y / tilePtSize), 0, maxRow);
        int rowEnd = Math.Clamp((int)Math.Ceiling((_origin.Y + viewHeightPt) / tilePtSize), 0, maxRow);

        for (int row = rowStart; row <= rowEnd; row++)
        {
            for (int col = colStart; col <= colEnd; col++)
            {
                var key = new TileKey(_pageIndex, level, col, row);
                double screenX = (col * tilePtSize - _origin.X) * _scale;
                double screenY = (row * tilePtSize - _origin.Y) * _scale;
                double screenSize = tilePtSize * _scale;
                var destRect = new Rect(screenX, screenY, screenSize, screenSize);

                if (_cache.TryGet(key, out var bitmap))
                {
                    ds.DrawImage(bitmap, destRect);
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
                    DirectXPixelFormat.B8G8R8A8UIntNormalized);
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
