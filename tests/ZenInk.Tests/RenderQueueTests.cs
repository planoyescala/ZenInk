using ZenInk_App.Rendering;
using static ZenInk.Tests.TestRunner;

namespace ZenInk.Tests;

/// <summary>
/// One queue owns PDFium for the whole process, because its library
/// init/teardown is global and not reference counted. These checks pin the
/// behaviour that depends on: documents in different tabs must not be able to
/// disturb each other, however one of them is closed or fails.
/// </summary>
public static class RenderQueueTests
{
    public static async Task RunAsync()
    {
        Section("Render queue — documents are isolated from each other");

        // Ink in opposite corners, so a tile from one document can never be
        // mistaken for a tile from the other.
        string pathA = TestPdf.WriteRectangle("zenink-doc-a", "0 450 100 150");
        string pathB = TestPdf.WriteRectangle("zenink-doc-b", "300 0 100 150");

        var queue = PdfRenderQueue.Shared;
        var docA = await queue.OpenDocumentAsync(pathA);
        var docB = await queue.OpenDocumentAsync(pathB);

        Check("each document gets its own id", docA.DocumentId != docB.DocumentId);
        Check("both report their page count", docA.Pages.Count == 1 && docB.Pages.Count == 1);

        var tileA = await queue.RequestTileAsync(docA.DocumentId, new TileKey(0, 0, 0, 0), 512);
        var tileB = await queue.RequestTileAsync(docB.DocumentId, new TileKey(0, 0, 0, 0), 512);

        Check("document A renders its own content", tileA is { } a && Bitmap.IsDark(a.Bgra, a.Width, 50, 50));
        Check("document B is blank where A has ink", tileB is { } b && !Bitmap.IsDark(b.Bgra, b.Width, 50, 50));
        Check("document B renders its own content", tileB is { } c && Bitmap.IsDark(c.Bgra, c.Width, 350, 480));

        await queue.CloseDocumentAsync(docA.DocumentId);

        var afterClose = await queue.RequestTileAsync(docB.DocumentId, new TileKey(0, 0, 0, 0), 512);
        Check("closing one document leaves the other rendering",
            afterClose is { } d && Bitmap.IsDark(d.Bgra, d.Width, 350, 480));

        var textAfterClose = await queue.RequestTextLayerAsync(docB.DocumentId, 0);
        Check("closing one document leaves the other extracting text", textAfterClose is not null);

        bool threw = false;
        try
        {
            await queue.RequestTileAsync(docA.DocumentId, new TileKey(0, 0, 1, 1), 512);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Check("a request against a closed document fails cleanly", threw);

        var afterFailure = await queue.RequestTileAsync(docB.DocumentId, new TileKey(0, 0, 0, 0), 512);
        Check("that failure does not disturb the surviving document",
            afterFailure is { } e && Bitmap.IsDark(e.Bgra, e.Width, 350, 480));

        queue.ReleasePages(docB.DocumentId);
        var afterRelease = await queue.RequestTileAsync(docB.DocumentId, new TileKey(0, 0, 0, 0), 512);
        Check("a backgrounded document re-renders after its pages are released",
            afterRelease is { } f && Bitmap.IsDark(f.Bgra, f.Width, 350, 480));

        Section("Render queue — many documents at once");

        var many = new List<PdfDocumentInfo>();
        for (int i = 0; i < 8; i++)
        {
            many.Add(await queue.OpenDocumentAsync(pathA));
        }

        bool allRendered = true;
        foreach (var document in many)
        {
            var tile = await queue.RequestTileAsync(document.DocumentId, new TileKey(0, 0, 0, 0), 512);
            if (tile is not { } t || !Bitmap.IsDark(t.Bgra, t.Width, 50, 50)) allRendered = false;
        }
        Check("eight concurrent documents all render", allRendered);

        foreach (var document in many)
        {
            await queue.CloseDocumentAsync(document.DocumentId);
        }

        var survivor = await queue.RequestTileAsync(docB.DocumentId, new TileKey(0, 0, 0, 0), 512);
        Check("the original document outlives closing all eight",
            survivor is { } g && Bitmap.IsDark(g.Bgra, g.Width, 350, 480));

        await queue.CloseDocumentAsync(docB.DocumentId);
        File.Delete(pathA);
        File.Delete(pathB);

        await ThinLinesAsync();
        await PagePreviewsAsync();
    }

    /// <summary>
    /// Sheet thumbnails come from a whole-page render sized to a box. It has to
    /// preserve the sheet's proportions, or the strip shows every drawing
    /// stretched.
    /// </summary>
    private static async Task PagePreviewsAsync()
    {
        Section("Render queue — sheet previews");

        string path = TestPdf.WriteRectangle("zenink-preview", "0 450 100 150");
        var queue = PdfRenderQueue.Shared;
        var document = await queue.OpenDocumentAsync(path);

        const int maxEdge = 220;
        var preview = await queue.RequestPagePreviewAsync(document.DocumentId, 0, maxEdge);

        if (preview is not { } image)
        {
            Check("a preview is produced", false);
            await queue.CloseDocumentAsync(document.DocumentId);
            File.Delete(path);
            return;
        }

        Check($"the long edge fits the box ({image.Width}x{image.Height})",
            Math.Max(image.Width, image.Height) == maxEdge);

        double sourceRatio = TestPdf.PageHeight / (double)TestPdf.PageWidth;
        double previewRatio = image.Height / (double)image.Width;
        CheckClose("proportions are preserved", previewRatio, sourceRatio, 0.02);

        Check("the buffer matches the reported size", image.Bgra.Length == image.Width * image.Height * 4);

        // Same corner mark as the tiling checks: ink top-left, blank bottom-right.
        Check("the preview draws the page's content",
            Bitmap.IsDark(image.Bgra, image.Width, image.Width / 8, image.Height / 8));
        Check("the preview leaves blank areas blank",
            !Bitmap.IsDark(image.Bgra, image.Width, image.Width * 7 / 8, image.Height * 7 / 8));

        // Previews must not disturb the tiles they are queued behind.
        var tile = await queue.RequestTileAsync(document.DocumentId, new TileKey(0, 0, 0, 0), 512);
        Check("tiles still render after a preview", tile is { } t && Bitmap.IsDark(t.Bgra, t.Width, 50, 50));

        await queue.CloseDocumentAsync(document.DocumentId);
        File.Delete(path);
    }

    /// <summary>
    /// The line-weight toggle must actually thin the strokes, and restoring it
    /// must bring the authored widths back — which it does by reparsing the
    /// page rather than remembering per-object widths.
    /// </summary>
    private static async Task ThinLinesAsync()
    {
        Section("Render queue — line weights");

        const float strokeWidth = 8f;
        string path = TestPdf.WriteThickLines("zenink-thick", strokeWidth);
        var queue = PdfRenderQueue.Shared;
        var document = await queue.OpenDocumentAsync(path);

        static int InkOf(TileBitmapData? tile)
        {
            if (tile is not { } t) return -1;
            int ink = 0;
            for (int y = 0; y < t.Height; y++)
            {
                for (int x = 0; x < t.Width; x++)
                {
                    if (Bitmap.IsDark(t.Bgra, t.Width, x, y)) ink++;
                }
            }
            return ink;
        }

        var key = new TileKey(0, 0, 0, 0);

        int authored = InkOf(await queue.RequestTileAsync(document.DocumentId, key, 512));
        Check("a thick-stroked page renders ink", authored > 0, $"ink={authored}");

        queue.SetThinLines(document.DocumentId, true);
        int hairline = InkOf(await queue.RequestTileAsync(document.DocumentId, key, 512));

        Check($"hairline mode thins the strokes ({authored} -> {hairline} px)",
            hairline > 0 && hairline < authored / 2);

        queue.SetThinLines(document.DocumentId, false);
        int restored = InkOf(await queue.RequestTileAsync(document.DocumentId, key, 512));

        Check($"restoring brings the authored widths back ({restored} px)",
            Math.Abs(restored - authored) <= Math.Max(2, authored / 100));

        await queue.CloseDocumentAsync(document.DocumentId);
        File.Delete(path);
    }
}
