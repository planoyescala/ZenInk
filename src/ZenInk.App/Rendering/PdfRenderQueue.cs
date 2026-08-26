using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PDFiumCore;

namespace ZenInk_App.Rendering;

public readonly record struct PdfPageSize(float WidthPt, float HeightPt);

public sealed record PdfDocumentInfo(IReadOnlyList<PdfPageSize> Pages);

public readonly record struct TileBitmapData(byte[] Bgra, int Width, int Height);

/// <summary>
/// Owns every call into PDFium on a single dedicated thread — PDFium's C API is
/// not thread-safe, so all document/page/bitmap handles live and die on this one
/// thread. Everything else talks to it through <see cref="OpenDocumentAsync"/> and
/// <see cref="RequestTileAsync"/>.
///
/// Tile requests are served most-recent-first (LIFO) and coalesced by key, so
/// during fast pan/zoom the queue always renders what's currently on screen
/// instead of working through a backlog of tiles that have already scrolled away.
/// </summary>
public sealed class PdfRenderQueue : IDisposable
{
    /// <summary>Parsed pages held open. Each one retains its parsed content, so this is capped.</summary>
    private const int MaxLoadedPages = 8;

    private readonly Thread _worker;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentQueue<Action> _controlActions = new();
    private readonly object _tileGate = new();
    private readonly Dictionary<TileKey, TileJob> _pendingTiles = new();
    private readonly List<TileKey> _tileOrder = new();
    private readonly object _textGate = new();
    private readonly Dictionary<int, TaskCompletionSource<PageTextLayer>> _pendingText = new();
    private readonly List<int> _textOrder = new();
    private volatile bool _disposed;

    private FpdfDocumentT? _document;
    private readonly Dictionary<int, FpdfPageT> _pages = new();
    private readonly LinkedList<int> _pageLru = new();

    private sealed record TileJob(TileKey Key, int TileSize, TaskCompletionSource<TileBitmapData?> Completion);

    public PdfRenderQueue()
    {
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "ZenInk PDFium",
        };
        _worker.Start();
    }

    public Task<PdfDocumentInfo> OpenDocumentAsync(string path)
    {
        var tcs = new TaskCompletionSource<PdfDocumentInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        _controlActions.Enqueue(() =>
        {
            try
            {
                tcs.TrySetResult(OpenDocumentCore(path));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        _signal.Release();
        return tcs.Task;
    }

    public Task<TileBitmapData?> RequestTileAsync(TileKey key, int tileSize)
    {
        lock (_tileGate)
        {
            if (_pendingTiles.TryGetValue(key, out var existing))
            {
                _tileOrder.Remove(key);
                _tileOrder.Add(key);
                return existing.Completion.Task;
            }

            var job = new TileJob(key, tileSize, new TaskCompletionSource<TileBitmapData?>(TaskCreationOptions.RunContinuationsAsynchronously));
            _pendingTiles[key] = job;
            _tileOrder.Add(key);
            _signal.Release();
            return job.Completion.Task;
        }
    }

    /// <summary>
    /// Extracts a page's glyph boxes. Served at lower priority than tiles, so
    /// text extraction on a dense sheet can never delay the tiles the user is
    /// currently looking at.
    /// </summary>
    public Task<PageTextLayer> RequestTextLayerAsync(int pageIndex)
    {
        lock (_textGate)
        {
            if (_pendingText.TryGetValue(pageIndex, out var existing))
            {
                return existing.Task;
            }

            var tcs = new TaskCompletionSource<PageTextLayer>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingText[pageIndex] = tcs;
            _textOrder.Add(pageIndex);
            _signal.Release();
            return tcs.Task;
        }
    }

    private void WorkerLoop()
    {
        fpdfview.FPDF_InitLibrary();
        try
        {
            while (!_disposed)
            {
                _signal.Wait();
                if (_disposed) break;

                if (_controlActions.TryDequeue(out var action))
                {
                    action();
                    continue;
                }

                TileJob? job = DequeueNewestTile();
                if (job is not null)
                {
                    try
                    {
                        job.Completion.TrySetResult(RenderTileCore(job.Key, job.TileSize));
                    }
                    catch (Exception ex)
                    {
                        job.Completion.TrySetException(ex);
                    }
                    continue;
                }

                if (DequeueNewestText() is not { } textJob) continue;

                try
                {
                    textJob.Completion.TrySetResult(ExtractTextCore(textJob.PageIndex));
                }
                catch (Exception ex)
                {
                    textJob.Completion.TrySetException(ex);
                }
            }
        }
        finally
        {
            CloseDocumentCore();
            fpdfview.FPDF_DestroyLibrary();
        }
    }

    private TileJob? DequeueNewestTile()
    {
        lock (_tileGate)
        {
            if (_tileOrder.Count == 0) return null;
            var key = _tileOrder[^1];
            _tileOrder.RemoveAt(_tileOrder.Count - 1);
            _pendingTiles.Remove(key, out var job);
            return job;
        }
    }

    private (int PageIndex, TaskCompletionSource<PageTextLayer> Completion)? DequeueNewestText()
    {
        lock (_textGate)
        {
            if (_textOrder.Count == 0) return null;
            int pageIndex = _textOrder[^1];
            _textOrder.RemoveAt(_textOrder.Count - 1);
            if (!_pendingText.Remove(pageIndex, out var tcs)) return null;
            return (pageIndex, tcs);
        }
    }

    // --- PDFium thread-affine work below: only ever called from WorkerLoop ---

    private PdfDocumentInfo OpenDocumentCore(string path)
    {
        CloseDocumentCore();

        var doc = fpdfview.FPDF_LoadDocument(path, null);
        if (doc is null)
        {
            throw new InvalidOperationException($"No se pudo abrir el PDF (PDFium error {fpdfview.FPDF_GetLastError()}).");
        }

        _document = doc;

        int pageCount = fpdfview.FPDF_GetPageCount(doc);
        var sizes = new List<PdfPageSize>(pageCount);
        for (int i = 0; i < pageCount; i++)
        {
            double width = 0, height = 0;
            // Reports the size after the page's /Rotate is applied, matching
            // what FPDF_RenderPageBitmap will actually rasterize.
            if (fpdfview.FPDF_GetPageSizeByIndex(doc, i, ref width, ref height) == 0)
            {
                width = 612;
                height = 792;
            }
            sizes.Add(new PdfPageSize((float)width, (float)height));
        }

        return new PdfDocumentInfo(sizes);
    }

    private FpdfPageT LoadPageCore(int pageIndex)
    {
        if (_document is null)
        {
            throw new InvalidOperationException("No hay ningún documento abierto.");
        }

        if (_pages.TryGetValue(pageIndex, out var cached))
        {
            _pageLru.Remove(pageIndex);
            _pageLru.AddLast(pageIndex);
            return cached;
        }

        var page = fpdfview.FPDF_LoadPage(_document, pageIndex);
        if (page is null)
        {
            throw new InvalidOperationException($"No se pudo cargar la página {pageIndex}.");
        }

        _pages[pageIndex] = page;
        _pageLru.AddLast(pageIndex);

        while (_pageLru.Count > MaxLoadedPages)
        {
            int oldest = _pageLru.First!.Value;
            _pageLru.RemoveFirst();
            if (_pages.Remove(oldest, out var stale))
            {
                fpdfview.FPDF_ClosePage(stale);
            }
        }

        return page;
    }

    private TileBitmapData RenderTileCore(TileKey key, int tileSize)
    {
        var page = LoadPageCore(key.PageIndex);
        double levelScale = ZoomLevels.ScaleForLevel(key.Level);

        int scaledWidth = Math.Max(1, (int)Math.Ceiling(fpdfview.FPDF_GetPageWidthF(page) * levelScale));
        int scaledHeight = Math.Max(1, (int)Math.Ceiling(fpdfview.FPDF_GetPageHeightF(page) * levelScale));

        var bitmap = fpdfview.FPDFBitmapCreateEx(tileSize, tileSize, (int)FPDFBitmapFormat.BGRA, IntPtr.Zero, tileSize * 4);
        if (bitmap is null)
        {
            throw new InvalidOperationException("No se pudo crear el bitmap del tile.");
        }

        try
        {
            fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, tileSize, tileSize, 0xFFFFFFFFUL);

            // Place the whole scaled page so that the requested tile lands on the
            // bitmap, and let PDFium clip away everything outside it. Unlike the
            // matrix variant, this applies the page's own /Rotate and uses a
            // top-left origin, so no hand-built axis flip is involved.
            fpdfview.FPDF_RenderPageBitmap(
                bitmap,
                page,
                -key.Col * tileSize,
                -key.Row * tileSize,
                scaledWidth,
                scaledHeight,
                0,
                (int)RenderFlags.RenderAnnotations);

            int stride = fpdfview.FPDFBitmapGetStride(bitmap);
            IntPtr buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);

            // Copy into a tightly packed BGRA array (row length == tileSize * 4)
            // regardless of PDFium's actual stride, so downstream consumers never
            // need to know about it.
            var packed = new byte[tileSize * 4 * tileSize];
            for (int row = 0; row < tileSize; row++)
            {
                Marshal.Copy(buffer + row * stride, packed, row * tileSize * 4, tileSize * 4);
            }

            return new TileBitmapData(packed, tileSize, tileSize);
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }

    /// <summary>
    /// Affine map from PDF page space (y up, unrotated) to page-local render
    /// space (y down, top-left origin, /Rotate applied).
    /// </summary>
    private readonly record struct PageTransform(float A, float B, float C, float D, float E, float F)
    {
        public (float X, float Y) Apply(double px, double py) =>
            ((float)(A * px + C * py + E), (float)(B * px + D * py + F));
    }

    /// <summary>
    /// Derives the page transform by asking PDFium itself to map three widely
    /// separated points, rather than reconstructing the rotation maths by hand.
    /// That keeps the text layer aligned with whatever FPDF_RenderPageBitmap
    /// draws, including any page rotation, by construction.
    /// </summary>
    private static PageTransform BuildPageTransform(FpdfPageT page)
    {
        // Sub-point precision for the integer device coordinates PDFium returns,
        // and a long sample span so the rounding error stays negligible.
        const int Precision = 64;
        const double Span = 1000.0;

        int sizeX = Math.Max(1, (int)Math.Round(fpdfview.FPDF_GetPageWidthF(page) * Precision));
        int sizeY = Math.Max(1, (int)Math.Round(fpdfview.FPDF_GetPageHeightF(page) * Precision));

        int x0 = 0, y0 = 0, x1 = 0, y1 = 0, x2 = 0, y2 = 0;
        fpdfview.FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, 0, 0, 0, ref x0, ref y0);
        fpdfview.FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, 0, Span, 0, ref x1, ref y1);
        fpdfview.FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, 0, 0, Span, ref x2, ref y2);

        return new PageTransform(
            (float)((x1 - x0) / Span / Precision),
            (float)((y1 - y0) / Span / Precision),
            (float)((x2 - x0) / Span / Precision),
            (float)((y2 - y0) / Span / Precision),
            x0 / (float)Precision,
            y0 / (float)Precision);
    }

    private PageTextLayer ExtractTextCore(int pageIndex)
    {
        var page = LoadPageCore(pageIndex);
        var transform = BuildPageTransform(page);

        var textPage = fpdf_text.FPDFTextLoadPage(page);
        if (textPage is null)
        {
            return new PageTextLayer(pageIndex, []);
        }

        try
        {
            int count = Math.Max(0, fpdf_text.FPDFTextCountChars(textPage));
            var chars = new TextChar[count];

            for (int i = 0; i < count; i++)
            {
                char value = (char)fpdf_text.FPDFTextGetUnicode(textPage, i);

                double left = 0, right = 0, bottom = 0, top = 0;
                if (fpdf_text.FPDFTextGetCharBox(textPage, i, ref left, ref right, ref bottom, ref top) == 0)
                {
                    chars[i] = new TextChar(value, 0, 0, 0, 0);
                    continue;
                }

                // Transform all four corners: under a 90/270 degree page
                // rotation the axes swap, so taking only two corners would
                // produce an inverted box.
                var c1 = transform.Apply(left, bottom);
                var c2 = transform.Apply(right, bottom);
                var c3 = transform.Apply(left, top);
                var c4 = transform.Apply(right, top);

                chars[i] = new TextChar(
                    value,
                    Math.Min(Math.Min(c1.X, c2.X), Math.Min(c3.X, c4.X)),
                    Math.Min(Math.Min(c1.Y, c2.Y), Math.Min(c3.Y, c4.Y)),
                    Math.Max(Math.Max(c1.X, c2.X), Math.Max(c3.X, c4.X)),
                    Math.Max(Math.Max(c1.Y, c2.Y), Math.Max(c3.Y, c4.Y)));
            }

            return new PageTextLayer(pageIndex, chars);
        }
        finally
        {
            fpdf_text.FPDFTextClosePage(textPage);
        }
    }

    private void CloseDocumentCore()
    {
        foreach (var page in _pages.Values)
        {
            fpdfview.FPDF_ClosePage(page);
        }
        _pages.Clear();
        _pageLru.Clear();

        if (_document is not null)
        {
            fpdfview.FPDF_CloseDocument(_document);
            _document = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _signal.Release();
        _worker.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
    }
}
