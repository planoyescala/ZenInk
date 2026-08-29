using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PDFiumCore;

namespace ZenInk.Core;

public readonly record struct PdfPageSize(float WidthPt, float HeightPt);

public sealed record PdfDocumentInfo(int DocumentId, IReadOnlyList<PdfPageSize> Pages);

public readonly record struct TileBitmapData(byte[] Bgra, int Width, int Height);

/// <summary>
/// Outcome of writing page rotations back into a PDF.
///
/// <see cref="Document"/> is the document to use from here on: saving in place
/// has to close the file and reopen it, so the id changes even though the tab
/// did not. It is filled in whether or not the write succeeded, so a failed
/// save leaves the reader looking at a working document rather than a dead tab.
/// </summary>
public sealed record PdfSaveOutcome(PdfDocumentInfo Document, string? Error)
{
    public bool Saved => Error is null;
}

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

    private const int PageObjectTypePath = 2;
    private const int PageObjectTypeForm = 5;

    /// <summary>FPDF_INCREMENTAL — appends the change, leaving the original bytes in place.</summary>
    private const ulong SaveIncremental = 1;

    /// <summary>FPDF_NO_INCREMENTAL — writes the whole document afresh.</summary>
    private const ulong SaveFullRewrite = 2;

    /// <summary>FLAT_NORMALDISPLAY — burn the annotations in as they are seen on screen.</summary>
    private const int FlattenForDisplay = 0;

    /// <summary>FLATTEN_FAIL, the one answer that means nothing was done.</summary>
    private const int FlattenFail = 0;

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

    private readonly object _previewGate = new();
    private readonly Dictionary<PreviewRequest, PreviewJob> _pendingPreviews = new();
    private readonly List<PreviewRequest> _previewOrder = new();

    /// <summary>
    /// Print bands are served FIFO, not LIFO like tiles: a print job asks for
    /// its bands in the order they go on the paper and wants all of them, so
    /// there is nothing to gain by serving the newest first.
    /// </summary>
    private readonly object _printGate = new();
    private readonly Queue<(PrintBandRequest Request, TaskCompletionSource<TileBitmapData?> Completion)> _printOrder = new();

    private volatile bool _disposed;
    private int _nextDocumentId;

    private readonly Dictionary<int, OpenDocument> _documents = new();

    /// <summary>Documents currently drawing every stroke as a hairline. Worker thread only.</summary>
    private readonly HashSet<int> _thinLineDocuments = [];

    private readonly record struct TileRequest(int DocumentId, TileKey Key);

    private readonly record struct TextRequest(int DocumentId, int PageIndex, int Rotation);

    private readonly record struct PreviewRequest(int DocumentId, int PageIndex, int Rotation);

    /// <summary>
    /// A strip of a page rendered straight at the paper's resolution. An A0 at
    /// 300 dpi is 139 megapixels — half a gigabyte as one bitmap — so the print
    /// path asks for it a band at a time.
    /// </summary>
    private readonly record struct PrintBandRequest(
        int DocumentId,
        int PageIndex,
        int Rotation,
        double Scale,
        int StartX,
        int StartY,
        int Width,
        int Height,
        bool Monochrome);

    private sealed record TileJob(int TileSize, TaskCompletionSource<TileBitmapData?> Completion);

    private sealed record PreviewJob(int MaxEdge, TaskCompletionSource<TileBitmapData?> Completion);

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

    /// <summary>
    /// Draws every stroke at hairline width, or restores the document's own
    /// widths. Restoring works by dropping the parsed pages: reloading reparses
    /// them from the content stream, which brings the original widths back
    /// without having to remember them per object.
    /// </summary>
    public void SetThinLines(int documentId, bool enabled)
    {
        DropPendingWork(documentId);
        EnqueueControl(() =>
        {
            if (enabled)
            {
                _thinLineDocuments.Add(documentId);
            }
            else
            {
                _thinLineDocuments.Remove(documentId);
            }

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
    ///
    /// <paramref name="rotation"/> is the viewer's quarter-turn on top of the
    /// page's own /Rotate: the boxes come back in the space the tiles are
    /// drawn in, so a rotated sheet needs no correction downstream.
    /// </summary>
    public Task<PageTextLayer> RequestTextLayerAsync(int documentId, int pageIndex, int rotation = 0)
    {
        var request = new TextRequest(documentId, pageIndex, rotation & 3);
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

    /// <summary>
    /// Renders a whole page small enough to fit a box of <paramref name="maxEdge"/>
    /// pixels, for the sheet thumbnails. Lowest priority of all: a dense sheet
    /// costs the same object traversal whatever the output size, so previews
    /// must never come before the tiles or the text of what is on screen.
    /// </summary>
    public Task<TileBitmapData?> RequestPagePreviewAsync(int documentId, int pageIndex, int maxEdge, int rotation = 0)
    {
        var request = new PreviewRequest(documentId, pageIndex, rotation & 3);
        lock (_previewGate)
        {
            if (_pendingPreviews.TryGetValue(request, out var existing))
            {
                return existing.Completion.Task;
            }

            var job = new PreviewJob(maxEdge, new TaskCompletionSource<TileBitmapData?>(TaskCreationOptions.RunContinuationsAsynchronously));
            _pendingPreviews[request] = job;
            _previewOrder.Add(request);
            _signal.Release();
            return job.Completion.Task;
        }
    }

    /// <summary>
    /// Reads back the marks ZenInk itself wrote into a document, sheet by
    /// sheet, so a drawing reopens with its review still on it.
    ///
    /// A control action, and cheap enough to be one: loading a page builds its
    /// dictionary but does not parse its content, so this is a walk over the
    /// annotation lists and not over the drawing.
    /// </summary>
    public Task<Dictionary<int, IReadOnlyList<Annotation>>> ReadAnnotationsAsync(int documentId)
    {
        var tcs = new TaskCompletionSource<Dictionary<int, IReadOnlyList<Annotation>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        EnqueueControl(() =>
        {
            try
            {
                tcs.TrySetResult(ReadAnnotationsCore(documentId));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>
    /// Writes the given quarter-turns and marks into the file the document was
    /// opened from, then reopens it.
    ///
    /// Reopening is not optional: PDFium holds the source file open, so the new
    /// bytes cannot take its place until the handle is gone. The changes are
    /// applied to a *separate* handle opened for the purpose, which also keeps
    /// the hairline toggle out of the file — that setting works by rewriting
    /// stroke widths on the parsed page, and saving from the viewer's own
    /// handle would make it permanent.
    /// </summary>
    public Task<PdfSaveOutcome> ApplyChangesInPlaceAsync(
        int documentId,
        string path,
        IReadOnlyList<int> quarterTurns,
        IReadOnlyDictionary<int, IReadOnlyList<Annotation>>? annotations = null,
        bool flatten = false)
    {
        var tcs = new TaskCompletionSource<PdfSaveOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueControl(() =>
        {
            string? error = null;
            string? staged = null;

            try
            {
                staged = WriteChangesCore(path, path, quarterTurns, annotations, flatten);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (staged is null)
            {
                // Nothing was touched, so the document in hand is still good.
                try
                {
                    tcs.TrySetResult(new PdfSaveOutcome(DescribeOpenDocument(documentId), error ?? "No se pudo preparar el archivo."));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                return;
            }

            CloseDocumentCore(documentId);

            try
            {
                File.Move(staged, path, overwrite: true);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                TryDelete(staged);
            }

            try
            {
                tcs.TrySetResult(new PdfSaveOutcome(OpenDocumentCore(path), error));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>
    /// Writes a copy carrying the turns and marks to
    /// <paramref name="targetPath"/>, leaving the source untouched. No document
    /// handle is involved, so the tab the reader is looking at carries on
    /// undisturbed.
    /// </summary>
    public Task SaveChangesCopyAsync(
        string sourcePath,
        string targetPath,
        IReadOnlyList<int> quarterTurns,
        IReadOnlyDictionary<int, IReadOnlyList<Annotation>>? annotations = null)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueControl(() =>
        {
            string? staged = null;
            try
            {
                staged = WriteChangesCore(sourcePath, targetPath, quarterTurns, annotations);
                File.Move(staged, targetPath, overwrite: true);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                if (staged is not null)
                {
                    TryDelete(staged);
                }
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>
    /// Renders one band of a page at the paper's own resolution.
    /// <paramref name="scale"/> is output pixels per PDF point;
    /// <paramref name="startX"/> and <paramref name="startY"/> locate the band
    /// within the scaled page, the same way a tile does.
    ///
    /// Served below tiles on purpose: the reader panning the drawing must never
    /// wait behind a print job's bands.
    /// </summary>
    public Task<TileBitmapData?> RequestPrintBandAsync(
        int documentId,
        int pageIndex,
        int rotation,
        double scale,
        int startX,
        int startY,
        int width,
        int height,
        bool monochrome)
    {
        var request = new PrintBandRequest(
            documentId, pageIndex, rotation & 3, scale, startX, startY, width, height, monochrome);

        var tcs = new TaskCompletionSource<TileBitmapData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_printGate)
        {
            _printOrder.Enqueue((request, tcs));
        }
        _signal.Release();
        return tcs.Task;
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

        List<TaskCompletionSource<TileBitmapData?>> bands = [];
        lock (_printGate)
        {
            int count = _printOrder.Count;
            for (int i = 0; i < count; i++)
            {
                var entry = _printOrder.Dequeue();
                if (entry.Request.DocumentId == documentId)
                {
                    bands.Add(entry.Completion);
                }
                else
                {
                    _printOrder.Enqueue(entry);
                }
            }
        }

        foreach (var completion in bands)
        {
            completion.TrySetResult(null);
        }

        List<PreviewJob> previews = [];
        lock (_previewGate)
        {
            for (int i = _previewOrder.Count - 1; i >= 0; i--)
            {
                if (_previewOrder[i].DocumentId != documentId) continue;
                if (_pendingPreviews.Remove(_previewOrder[i], out var job))
                {
                    previews.Add(job);
                }
                _previewOrder.RemoveAt(i);
            }
        }

        foreach (var job in previews)
        {
            job.Completion.TrySetResult(null);
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

                if (DequeueOldestPrintBand() is { } band)
                {
                    try
                    {
                        band.Completion.TrySetResult(RenderPrintBandCore(band.Request));
                    }
                    catch (Exception ex)
                    {
                        band.Completion.TrySetException(ex);
                    }
                    continue;
                }

                if (DequeueNewestText() is { } text)
                {
                    try
                    {
                        text.Completion.TrySetResult(ExtractTextCore(text.Request));
                    }
                    catch (Exception ex)
                    {
                        text.Completion.TrySetException(ex);
                    }
                    continue;
                }

                if (DequeueNewestPreview() is not { } preview) continue;

                try
                {
                    preview.Job.Completion.TrySetResult(RenderPreviewCore(preview.Request, preview.Job.MaxEdge));
                }
                catch (Exception ex)
                {
                    preview.Job.Completion.TrySetException(ex);
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

    private (PrintBandRequest Request, TaskCompletionSource<TileBitmapData?> Completion)? DequeueOldestPrintBand()
    {
        lock (_printGate)
        {
            return _printOrder.Count == 0 ? null : _printOrder.Dequeue();
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

    private (PreviewRequest Request, PreviewJob Job)? DequeueNewestPreview()
    {
        lock (_previewGate)
        {
            if (_previewOrder.Count == 0) return null;
            var request = _previewOrder[^1];
            _previewOrder.RemoveAt(_previewOrder.Count - 1);
            if (!_pendingPreviews.Remove(request, out var job)) return null;
            return (request, job);
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

        return new PdfDocumentInfo(documentId, ReadPageSizes(handle));
    }

    /// <summary>Re-describes a document that is already open, for a save that changed nothing.</summary>
    private PdfDocumentInfo DescribeOpenDocument(int documentId)
    {
        if (!_documents.TryGetValue(documentId, out var document))
        {
            throw new InvalidOperationException($"El documento {documentId} ya no está abierto.");
        }

        return new PdfDocumentInfo(documentId, ReadPageSizes(document.Handle));
    }

    private static IReadOnlyList<PdfPageSize> ReadPageSizes(FpdfDocumentT handle)
    {
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
        return sizes;
    }

    /// <summary>
    /// Builds the changed file next to its destination and returns the staged
    /// path, or throws having written nothing that matters. The caller moves it
    /// into place; splitting the two is what makes a save all-or-nothing over
    /// the reader's drawing.
    ///
    /// Turns and marks travel together because they are one save to the reader,
    /// and because the order between them matters: a mark is held in the sheet
    /// space of the page as it was read, so it has to be written before the page
    /// is turned under it.
    /// </summary>
    private static string WriteChangesCore(
        string sourcePath,
        string targetPath,
        IReadOnlyList<int> quarterTurns,
        IReadOnlyDictionary<int, IReadOnlyList<Annotation>>? annotations,
        bool flatten = false)
    {
        string full = Path.GetFullPath(targetPath);
        string directory = Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
        string staged = Path.Combine(directory, $"{Path.GetFileName(full)}.zenink-{Guid.NewGuid():N}.tmp");

        try
        {
            var handle = fpdfview.FPDF_LoadDocument(sourcePath, null)
                ?? throw new InvalidOperationException(
                    $"No se pudo leer el PDF para guardarlo (PDFium error {fpdfview.FPDF_GetLastError()}).");

            var expected = new PageExpectation[fpdfview.FPDF_GetPageCount(handle)];
            try
            {
                for (int i = 0; i < expected.Length; i++)
                {
                    int turns = i < quarterTurns.Count ? quarterTurns[i] & 3 : 0;
                    var marks = annotations is not null && annotations.TryGetValue(i, out var forPage)
                        ? forPage
                        : [];

                    var page = fpdfview.FPDF_LoadPage(handle, i)
                        ?? throw new InvalidOperationException($"No se pudo cargar la página {i} para guardarla.");
                    try
                    {
                        // Only touch a page's annotations if there is something
                        // to say about them: nothing to write and nothing of
                        // ours already there means the page is left exactly as
                        // it came.
                        var names = marks.Count > 0 || PdfAnnotations.OwnedNames(page).Count > 0
                            ? PdfAnnotations.Write(handle, page, marks)
                            : [];

                        // PDFium reports /Rotate in quarter turns, and the
                        // viewer's turn is relative to whatever the page
                        // already carried.
                        int rotation = (fpdf_edit.FPDFPageGetRotation(page) + turns) & 3;
                        if (turns != 0)
                        {
                            fpdf_edit.FPDFPageSetRotation(page, rotation);
                        }

                        int objects = fpdf_edit.FPDFPageCountObjects(page);

                        if (flatten)
                        {
                            // Burns every annotation into the page itself. What
                            // comes back has no marks left to check by name —
                            // the check is that they are gone and that the
                            // drawing they were on is still there.
                            if (fpdf_flatten.FPDFPageFlatten(page, FlattenForDisplay) == FlattenFail)
                            {
                                throw new InvalidOperationException($"No se pudieron aplanar las marcas de la página {i + 1}.");
                            }

                            names = [];
                        }

                        expected[i] = new PageExpectation(rotation, names, flatten ? objects : 0);
                    }
                    finally
                    {
                        fpdfview.FPDF_ClosePage(page);
                    }
                }

                // An incremental save appends to the original bytes instead of
                // rewriting them, which is the gentler thing to do to someone's
                // drawing. It is not guaranteed to carry the change, so the
                // result is read back before it is trusted.
                WriteDocumentCore(handle, staged, SaveIncremental);
                if (!ChangesMatch(staged, expected))
                {
                    WriteDocumentCore(handle, staged, SaveFullRewrite);
                }
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(handle);
            }

            if (!ChangesMatch(staged, expected))
            {
                throw new InvalidOperationException("El archivo guardado no conserva los cambios; no se ha tocado el original.");
            }

            return staged;
        }
        catch
        {
            TryDelete(staged);
            throw;
        }
    }

    /// <summary>
    /// What a written page has to come back with for the save to be trusted.
    ///
    /// <see cref="LeastObjects"/> is only used by a flatten, where there is
    /// nothing left to check by name: a page whose marks were burnt in must
    /// still carry at least the drawing it had before.
    /// </summary>
    private readonly record struct PageExpectation(
        int Rotation,
        IReadOnlyList<string> AnnotationNames,
        int LeastObjects = 0);

    /// <summary>
    /// Reopens a written file and checks that every page carries the turn and
    /// the marks it was meant to. Cheap next to the write, and it is the only
    /// thing standing between a bad save and the reader's original.
    /// </summary>
    private static bool ChangesMatch(string path, IReadOnlyList<PageExpectation> expected)
    {
        var handle = fpdfview.FPDF_LoadDocument(path, null);
        if (handle is null) return false;

        try
        {
            if (fpdfview.FPDF_GetPageCount(handle) != expected.Count) return false;

            for (int i = 0; i < expected.Count; i++)
            {
                var page = fpdfview.FPDF_LoadPage(handle, i);
                if (page is null) return false;

                try
                {
                    if ((fpdf_edit.FPDFPageGetRotation(page) & 3) != expected[i].Rotation) return false;

                    var written = PdfAnnotations.OwnedNames(page);
                    var wanted = expected[i].AnnotationNames;
                    if (written.Count != wanted.Count) return false;

                    var pending = new HashSet<string>(wanted);
                    foreach (string name in written)
                    {
                        if (!pending.Remove(name)) return false;
                    }

                    // After a flatten the marks are part of the drawing, so the
                    // page must have grown rather than lost anything.
                    if (expected[i].LeastObjects > 0
                        && fpdf_edit.FPDFPageCountObjects(page) < expected[i].LeastObjects)
                    {
                        return false;
                    }
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }

            return true;
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(handle);
        }
    }

    /// <summary>
    /// Reads a document's ZenInk marks, sheet by sheet. Pages already parsed
    /// are reused; the rest are loaded and let go again, so reading the marks
    /// of a hundred-sheet set does not evict the sheets on screen.
    /// </summary>
    private Dictionary<int, IReadOnlyList<Annotation>> ReadAnnotationsCore(int documentId)
    {
        var marks = new Dictionary<int, IReadOnlyList<Annotation>>();
        if (!_documents.TryGetValue(documentId, out var document)) return marks;

        int pageCount = fpdfview.FPDF_GetPageCount(document.Handle);
        for (int i = 0; i < pageCount; i++)
        {
            bool cached = document.Pages.TryGetValue(i, out var page);
            page ??= fpdfview.FPDF_LoadPage(document.Handle, i);
            if (page is null) continue;

            try
            {
                var found = PdfAnnotations.Read(page);
                if (found.Count > 0)
                {
                    marks[i] = found;
                }
            }
            finally
            {
                if (!cached)
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }
        }

        return marks;
    }

    /// <summary>
    /// Streams a document to disk through PDFium's writer callback. The
    /// delegate is held on the stack for the whole call: it is handed to native
    /// code as a bare function pointer, which the collector cannot see.
    /// </summary>
    private static void WriteDocumentCore(FpdfDocumentT handle, string path, ulong flags)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[64 * 1024];

        int WriteBlock(IntPtr _, IntPtr data, ulong size)
        {
            long remaining = (long)size;
            long offset = 0;

            while (remaining > 0)
            {
                int chunk = (int)Math.Min(remaining, buffer.Length);
                Marshal.Copy(data + (int)offset, buffer, 0, chunk);
                stream.Write(buffer, 0, chunk);
                offset += chunk;
                remaining -= chunk;
            }

            return 1;
        }

        var callback = new PDFiumCore.Delegates.Func_int___IntPtr___IntPtr_ulong(WriteBlock);
        var writer = new FPDF_FILEWRITE_ { Version = 1, WriteBlock = callback };

        try
        {
            if (fpdf_save.FPDF_SaveAsCopy(handle, writer, flags) == 0)
            {
                throw new InvalidOperationException("PDFium no pudo escribir el archivo.");
            }
        }
        finally
        {
            GC.KeepAlive(callback);
            writer.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ZenInk: no se pudo borrar el temporal {path}: {ex.Message}");
        }
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

        if (_thinLineDocuments.Contains(documentId))
        {
            ApplyHairlineStrokes(page);
        }

        // ZenInk's own marks are drawn by the viewer, over the tiles, so that
        // they can be picked up and moved. Left visible here they would also be
        // rasterized into the tile underneath and show twice — but only after a
        // save, which is exactly the kind of difference nobody looks for.
        PdfAnnotations.HideOwned(page);

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

    /// <summary>
    /// Forces every stroked path on the page to zero width, which PDFium draws
    /// as a one-pixel hairline. Applied once per page load, since the parsed
    /// objects are what rendering walks.
    /// </summary>
    private static void ApplyHairlineStrokes(FpdfPageT page)
    {
        int count = fpdf_edit.FPDFPageCountObjects(page);
        for (int i = 0; i < count; i++)
        {
            if (fpdf_edit.FPDFPageGetObject(page, i) is { } pageObject)
            {
                ApplyHairlineToObject(pageObject);
            }
        }
    }

    private static void ApplyHairlineToObject(FpdfPageobjectT pageObject)
    {
        int type = fpdf_edit.FPDFPageObjGetType(pageObject);

        if (type == PageObjectTypeForm)
        {
            // Drawings exported from CAD often nest their geometry inside form
            // XObjects, so the walk has to descend rather than stop at the top.
            int inner = fpdf_edit.FPDFFormObjCountObjects(pageObject);
            for (int i = 0; i < inner; i++)
            {
                if (fpdf_edit.FPDFFormObjGetObject(pageObject, (ulong)i) is { } child)
                {
                    ApplyHairlineToObject(child);
                }
            }
        }
        else if (type == PageObjectTypePath)
        {
            fpdf_edit.FPDFPageObjSetStrokeWidth(pageObject, 0f);
        }
    }

    private TileBitmapData RenderTileCore(TileRequest request, int tileSize)
    {
        var key = request.Key;
        var page = LoadPageCore(request.DocumentId, key.PageIndex);
        double levelScale = ZoomLevels.ScaleForLevel(key.Level);

        int rotation = key.Rotation & 3;
        var (scaledWidth, scaledHeight) = RotatedRenderSize(page, levelScale, rotation);

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
            // top-left origin, so no hand-built axis flip is involved. The
            // viewer's own quarter-turn rides the same parameter.
            fpdfview.FPDF_RenderPageBitmap(
                bitmap,
                page,
                -key.Col * tileSize,
                -key.Row * tileSize,
                scaledWidth,
                scaledHeight,
                rotation,
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
    /// The output box a page occupies once the viewer's quarter-turn is applied.
    /// The odd turns swap the axes, and FPDF_RenderPageBitmap is given the size
    /// of the destination, not of the page — passing the unswapped size is what
    /// squashes a rotated sheet into the wrong aspect.
    /// </summary>
    private static (int Width, int Height) RotatedRenderSize(FpdfPageT page, double scale, int rotation)
    {
        float widthPt = fpdfview.FPDF_GetPageWidthF(page);
        float heightPt = fpdfview.FPDF_GetPageHeightF(page);
        if ((rotation & 1) == 1)
        {
            (widthPt, heightPt) = (heightPt, widthPt);
        }

        return (
            Math.Max(1, (int)Math.Ceiling(widthPt * scale)),
            Math.Max(1, (int)Math.Ceiling(heightPt * scale)));
    }

    /// <summary>
    /// Renders one band straight at the paper's resolution. The band is placed
    /// the same way a tile is — the whole scaled page positioned so the wanted
    /// strip lands on the bitmap — so it inherits the property the tile checks
    /// pin: a band is exactly its slice of the full render, with no drift at
    /// the seams.
    /// </summary>
    private TileBitmapData RenderPrintBandCore(PrintBandRequest request)
    {
        var page = LoadPageCore(request.DocumentId, request.PageIndex);
        var (scaledWidth, scaledHeight) = RotatedRenderSize(page, request.Scale, request.Rotation);

        int width = Math.Max(1, request.Width);
        int height = Math.Max(1, request.Height);

        var bitmap = fpdfview.FPDFBitmapCreateEx(width, height, (int)FPDFBitmapFormat.BGRA, IntPtr.Zero, width * 4)
            ?? throw new InvalidOperationException("No se pudo crear el bitmap de impresión.");

        try
        {
            fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFFUL);
            fpdfview.FPDF_RenderPageBitmap(
                bitmap,
                page,
                -request.StartX,
                -request.StartY,
                scaledWidth,
                scaledHeight,
                request.Rotation,
                (int)RenderFlags.RenderAnnotations);

            int stride = fpdfview.FPDFBitmapGetStride(bitmap);
            IntPtr buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);

            var packed = new byte[width * 4 * height];
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(buffer + row * stride, packed, row * width * 4, width * 4);
            }

            if (request.Monochrome)
            {
                ToGrayscale(packed);
            }

            return new TileBitmapData(packed, width, height);
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }

    /// <summary>
    /// Flattens colour to its luminance, in place. Done here rather than left
    /// to the printer driver so that "monocromo" means the same thing whatever
    /// is at the other end — and so the preview shows what will come out.
    /// </summary>
    private static void ToGrayscale(byte[] bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            // Rec. 601 luma: matches how the eye weighs the channels, so a
            // saturated red line does not come out as pale as a yellow one.
            byte grey = (byte)((bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000);
            bgra[i] = grey;
            bgra[i + 1] = grey;
            bgra[i + 2] = grey;
        }
    }

    private TileBitmapData RenderPreviewCore(PreviewRequest request, int maxEdge)
    {
        var page = LoadPageCore(request.DocumentId, request.PageIndex);
        int rotation = request.Rotation & 3;

        float widthPt = fpdfview.FPDF_GetPageWidthF(page);
        float heightPt = fpdfview.FPDF_GetPageHeightF(page);
        if ((rotation & 1) == 1)
        {
            (widthPt, heightPt) = (heightPt, widthPt);
        }

        double scale = maxEdge / (double)Math.Max(Math.Max(widthPt, heightPt), 1f);

        int width = Math.Max(1, (int)Math.Round(widthPt * scale));
        int height = Math.Max(1, (int)Math.Round(heightPt * scale));

        var bitmap = fpdfview.FPDFBitmapCreateEx(width, height, (int)FPDFBitmapFormat.BGRA, IntPtr.Zero, width * 4);
        if (bitmap is null)
        {
            throw new InvalidOperationException("No se pudo crear el bitmap de la miniatura.");
        }

        try
        {
            fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFFUL);
            fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, rotation, (int)RenderFlags.RenderAnnotations);

            int stride = fpdfview.FPDFBitmapGetStride(bitmap);
            IntPtr buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);

            var packed = new byte[width * 4 * height];
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(buffer + row * stride, packed, row * width * 4, width * 4);
            }

            return new TileBitmapData(packed, width, height);
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
    private static PageTransform BuildPageTransform(FpdfPageT page, int rotation)
    {
        // Sub-point precision for the integer device coordinates PDFium returns,
        // and a long sample span so the rounding error stays negligible.
        const int Precision = 64;
        const double Span = 1000.0;

        // Same rotation and same destination box the tiles are rendered into,
        // so whatever PDFium does to the ink it also does to the boxes.
        var (sizeX, sizeY) = RotatedRenderSize(page, Precision, rotation);

        int x0 = 0, y0 = 0, x1 = 0, y1 = 0, x2 = 0, y2 = 0;
        fpdfview.FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, rotation, 0, 0, ref x0, ref y0);
        fpdfview.FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, rotation, Span, 0, ref x1, ref y1);
        fpdfview.FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, rotation, 0, Span, ref x2, ref y2);

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
        var transform = BuildPageTransform(page, request.Rotation & 3);

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
        _thinLineDocuments.Remove(documentId);
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
