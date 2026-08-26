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
    }
}
