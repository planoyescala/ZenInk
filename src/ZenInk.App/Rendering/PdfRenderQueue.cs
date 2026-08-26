using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PDFiumCore;

namespace ZenInk_App.Rendering;

public readonly record struct PdfPageSize(float WidthPt, float HeightPt);

public sealed record PdfDocumentInfo(int DocumentId, IReadOnlyList<PdfPageSize> Pages);

public readonly record struct TileBitmapData(byte[] Bgra, int Width, int Height);

/// <summary>
/// Owns every call into PDFium on a single dedicated thread — PDFium's C API is
/// not thread-safe, so all document/page/bitmap handles live and die on this one
/// thread. Everything else talks to it through the Request* methods.
///
/// There is exactly one of these per process, and every open document shares
/// it. That is not an optimisation: FPDF_InitLibrary and FPDF_DestroyLibrary
/// are process-global and not reference counted, so a second queue tearing down
/// on close would pull the library out from under any document still open in
/// another tab.
///
/// Tile requests are served most-recent-first (LIFO) and coalesced by key, so
/// during fast pan/zoom the queue always renders what's currently on screen
/// instead of working through a backlog of tiles that have already scrolled
/// away. Text extraction sits below tiles, so it can never delay them.
/// </summary>
public sealed class PdfRenderQueue : IDisposable
{
    /// <summary>Parsed pages held open per document. Each retains its content, so this is capped.</summary>
    private const int MaxLoadedPagesPerDocument = 6;

    private static readonly Lazy<PdfRenderQueue> LazyShared = new(() => new PdfRenderQueue());

    public static PdfRenderQueue Shared => LazyShared.Value;

    private readonly Thread _worker;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentQueue<Action> _controlActions = new();

    private readonly object _tileGate = new();
    private readonly Dictionary<TileRequest, TileJob> _pendingTiles = new();
    private readonly List<TileRequest> _tileOrder = new();

    private readonly object _textGate = new();
    private readonly Dictionary<TextRequest, TaskCompletionSource<PageTextLayer>> _pendingText = new();
    private readonly List<TextRequest> _textOrder = new();

    private volatile bool _disposed;
    private int _nextDocumentId;

    private readonly Dictionary<int, OpenDocument> _documents = new();

    private readonly record struct TileRequest(int DocumentId, TileKey Key);

    private readonly record struct TextRequest(int DocumentId, int PageIndex);

    private sealed record TileJob(int TileSize, TaskCompletionSource<TileBitmapData?> Completion);

    private sealed class OpenDocument(FpdfDocumentT handle)
    {
        public FpdfDocumentT Handle { get; } = handle;

        public Dictionary<int, FpdfPageT> Pages { get; } = new();

        public LinkedList<int> PageLru { get; } = new();
    }

    private PdfRenderQueue()
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
        EnqueueControl(() =>
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
        return tcs.Task;
    }

    public Task CloseDocumentAsync(int documentId)
    {
        DropPendingWork(documentId);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueControl(() =>
        {
            CloseDocumentCore(documentId);
            tcs.TrySetResult();
        });
        return tcs.Task;
    }

    /// <summary>
    /// Frees a document's parsed pages without closing it. Used when a tab goes
    /// to the background, since a large sheet's parsed content dwarfs the rest
    /// of what a document costs to keep open.
    /// </summary>
    public void ReleasePages(int documentId)
    {
        DropPendingWork(documentId);
        EnqueueControl(() =>
        {
            if (_documents.TryGetValue(documentId, out var document))
            {
                ClosePages(document);
            }
        });
    }

    public Task<TileBitmapData?> RequestTileAsync(int documentId, TileKey key, int tileSize)
    {
        var request = new TileRequest(documentId, key);
        lock (_tileGate)
        {
            if (_pendingTiles.TryGetValue(request, out var existing))
            {
                _tileOrder.Remove(request);
                _tileOrder.Add(request);
                return existing.Completion.Task;
            }

            var job = new TileJob(tileSize, new TaskCompletionSource<TileBitmapData?>(TaskCreationOptions.RunContinuationsAsynchronously));
            _pendingTiles[request] = job;
            _tileOrder.Add(request);
            _signal.Release();
            return job.Completion.Task;
        }
    }

    /// <summary>
    /// Extracts a page's glyph boxes. Served at lower priority than tiles, so
    /// text extraction on a dense sheet can never delay the tiles the user is
    /// currently looking at.
    /// </summary>
    public Task<PageTextLayer> RequestTextLayerAsync(int documentId, int pageIndex)
    {
        var request = new TextRequest(documentId, pageIndex);
        lock (_textGate)
        {
            if (_pendingText.TryGetValue(request, out var existing))
            {
                return existing.Task;
            }

            var tcs = new TaskCompletionSource<PageTextLayer>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingText[request] = tcs;
            _textOrder.Add(request);
            _signal.Release();
            return tcs.Task;
        }
    }

    private void EnqueueControl(Action action)
    {
        _controlActions.Enqueue(action);
        _signal.Release();
    }

    /// <summary>
    /// Abandons queued work for a document. Requests resolve to null rather
    /// than faulting, since a caller that no longer wants the tile is the
    /// normal reason for this.
    /// </summary>
    private void DropPendingWork(int documentId)
    {
        List<TileJob> tiles = [];
        lock (_tileGate)
        {
            for (int i = _tileOrder.Count - 1; i >= 0; i--)
            {
                if (_tileOrder[i].DocumentId != documentId) continue;
                if (_pendingTiles.Remove(_tileOrder[i], out var job))
                {
                    tiles.Add(job);
                }
                _tileOrder.RemoveAt(i);
            }
        }

        foreach (var job in tiles)
        {
            job.Completion.TrySetResult(null);
        }

        List<TaskCompletionSource<PageTextLayer>> texts = [];
        lock (_textGate)
        {
            for (int i = _textOrder.Count - 1; i >= 0; i--)
            {
                if (_textOrder[i].DocumentId != documentId) continue;
                if (_pendingText.Remove(_textOrder[i], out var tcs))
                {
                    texts.Add(tcs);
                }
                _textOrder.RemoveAt(i);
            }
        }

        foreach (var tcs in texts)
        {
            tcs.TrySetResult(new PageTextLayer(0, []));
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

                if (DequeueNewestTile() is { } tile)
                {
                    try
                    {
                        tile.Job.Completion.TrySetResult(RenderTileCore(tile.Request, tile.Job.TileSize));
                    }
                    catch (Exception ex)
                    {
                        tile.Job.Completion.TrySetException(ex);
                    }
                    continue;
                }

                if (DequeueNewestText() is not { } text) continue;

                try
                {
                    text.Completion.TrySetResult(ExtractTextCore(text.Request));
                }
                catch (Exception ex)
                {
                    text.Completion.TrySetException(ex);
                }
            }
        }
        finally
        {
            foreach (int documentId in _documents.Keys.ToList())
            {
                CloseDocumentCore(documentId);
            }
            fpdfview.FPDF_DestroyLibrary();
        }
    }

    private (TileRequest Request, TileJob Job)? DequeueNewestTile()
    {
        lock (_tileGate)
        {
            if (_tileOrder.Count == 0) return null;
            var request = _tileOrder[^1];
            _tileOrder.RemoveAt(_tileOrder.Count - 1);
            if (!_pendingTiles.Remove(request, out var job)) return null;
            return (request, job);
        }
    }

    private (TextRequest Request, TaskCompletionSource<PageTextLayer> Completion)? DequeueNewestText()
    {
        lock (_textGate)
        {
            if (_textOrder.Count == 0) return null;
            var request = _textOrder[^1];
            _textOrder.RemoveAt(_textOrder.Count - 1);
            if (!_pendingText.Remove(request, out var tcs)) return null;
            return (request, tcs);
        }
    }

    // --- PDFium thread-affine work below: only ever called from WorkerLoop ---

    private PdfDocumentInfo OpenDocumentCore(string path)
    {
        var handle = fpdfview.FPDF_LoadDocument(path, null);
        if (handle is null)
        {
            throw new InvalidOperationException($"No se pudo abrir el PDF (PDFium error {fpdfview.FPDF_GetLastError()}).");
        }

        int documentId = ++_nextDocumentId;
        _documents[documentId] = new OpenDocument(handle);

        int pageCount = fpdfview.FPDF_GetPageCount(handle);
        var sizes = new List<PdfPageSize>(pageCount);
        for (int i = 0; i < pageCount; i++)
        {
            double width = 0, height = 0;
            // Reports the size after the page's /Rotate is applied, matching
            // what FPDF_RenderPageBitmap will actually rasterize.
            if (fpdfview.FPDF_GetPageSizeByIndex(handle, i, ref width, ref height) == 0)
            {
                width = 612;
                height = 792;
            }
            sizes.Add(new PdfPageSize((float)width, (float)height));
        }

        return new PdfDocumentInfo(documentId, sizes);
    }

    private FpdfPageT LoadPageCore(int documentId, int pageIndex)
    {
        if (!_documents.TryGetValue(documentId, out var document))
        {
            throw new InvalidOperationException($"El documento {documentId} ya no está abierto.");
        }

        if (document.Pages.TryGetValue(pageIndex, out var cached))
        {
            document.PageLru.Remove(pageIndex);
            document.PageLru.AddLast(pageIndex);
            return cached;
        }

        var page = fpdfview.FPDF_LoadPage(document.Handle, pageIndex);
        if (page is null)
        {
            throw new InvalidOperationException($"No se pudo cargar la página {pageIndex}.");
        }

        document.Pages[pageIndex] = page;
        document.PageLru.AddLast(pageIndex);

        while (document.PageLru.Count > MaxLoadedPagesPerDocument)
        {
            int oldest = document.PageLru.First!.Value;
            document.PageLru.RemoveFirst();
            if (document.Pages.Remove(oldest, out var stale))
            {
                fpdfview.FPDF_ClosePage(stale);
            }
        }

        return page;
    }

    private TileBitmapData RenderTileCore(TileRequest request, int tileSize)
    {
        var key = request.Key;
        var page = LoadPageCore(request.DocumentId, key.PageIndex);
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

    private PageTextLayer ExtractTextCore(TextRequest request)
    {
        int pageIndex = request.PageIndex;
        var page = LoadPageCore(request.DocumentId, pageIndex);
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

    private static void ClosePages(OpenDocument document)
    {
        foreach (var page in document.Pages.Values)
        {
            fpdfview.FPDF_ClosePage(page);
        }
        document.Pages.Clear();
        document.PageLru.Clear();
    }

    private void CloseDocumentCore(int documentId)
    {
        if (!_documents.Remove(documentId, out var document)) return;

        ClosePages(document);
        fpdfview.FPDF_CloseDocument(document.Handle);
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
