using ZenInk.Core;
using static ZenInk.Tests.TestRunner;

namespace ZenInk.Tests;

/// <summary>
/// Turning a sheet has to work in two places that must agree: on screen, where
/// the quarter-turn rides PDFium's own rotation parameter, and in the file,
/// where it becomes the page's /Rotate. These checks pin both, and the seam
/// between them — that what the reader saw is what gets written.
/// </summary>
public static class RotationTests
{
    /// <summary>Where the corner mark should land, as a fraction of the rendered page, per quarter-turn.</summary>
    private static (double X, double Y) ExpectedCorner(int turns) => turns switch
    {
        0 => (0.12, 0.12),   // top-left
        1 => (0.88, 0.12),   // turned clockwise -> top-right
        2 => (0.88, 0.88),   // bottom-right
        _ => (0.12, 0.88),   // bottom-left
    };

    public static async Task RunAsync()
    {
        await OnScreenAsync();
        await TilesAsync();
        await TextAsync();
        await SavingAsync();
    }

    /// <summary>The viewer's own turn must move the ink the same way /Rotate does.</summary>
    private static async Task OnScreenAsync()
    {
        Section("Rotation — the sheet turns on screen");

        string path = TestPdf.WriteRectangle("zenink-rotate-view", "0 450 100 150");
        var queue = PdfRenderQueue.Shared;
        var document = await queue.OpenDocumentAsync(path);

        foreach (int turns in new[] { 0, 1, 2, 3 })
        {
            // Sizing the box to the sheet's long edge renders it at scale 1.
            var preview = await queue.RequestPagePreviewAsync(
                document.DocumentId, 0, TestPdf.PageHeight, turns);

            if (preview is not { } image)
            {
                Check($"turn {turns}: the sheet renders", false);
                continue;
            }

            bool swapped = (turns & 1) == 1;
            Check($"turn {turns}: the rendered sheet swaps its axes ({image.Width}x{image.Height})",
                swapped
                    ? image.Width == TestPdf.PageHeight && image.Height == TestPdf.PageWidth
                    : image.Width == TestPdf.PageWidth && image.Height == TestPdf.PageHeight);

            var (fx, fy) = ExpectedCorner(turns);
            Check($"turn {turns}: ink lands in the expected corner",
                Bitmap.IsDark(image.Bgra, image.Width, (int)(image.Width * fx), (int)(image.Height * fy)));
            Check($"turn {turns}: the opposite corner stays blank",
                !Bitmap.IsDark(image.Bgra, image.Width, (int)(image.Width * (1 - fx)), (int)(image.Height * (1 - fy))));
        }

        await queue.CloseDocumentAsync(document.DocumentId);
        File.Delete(path);
    }

    /// <summary>
    /// A turned sheet is still drawn tile by tile, so its tiles must stitch as
    /// seamlessly as an upright one's. This compares them against a full render
    /// at the same scale, which is what the un-turned case already guarantees.
    /// </summary>
    private static async Task TilesAsync()
    {
        Section("Rotation — turned sheets still tile seamlessly");

        string path = TestPdf.WriteRectangle("zenink-rotate-tiles", "40 380 180 120");
        var queue = PdfRenderQueue.Shared;
        var document = await queue.OpenDocumentAsync(path);

        const int tileSize = 256;

        foreach (int turns in new[] { 1, 2, 3 })
        {
            bool swapped = (turns & 1) == 1;
            int width = swapped ? TestPdf.PageHeight : TestPdf.PageWidth;
            int height = swapped ? TestPdf.PageWidth : TestPdf.PageHeight;

            var full = await queue.RequestPagePreviewAsync(document.DocumentId, 0, Math.Max(width, height), turns);
            if (full is not { } reference)
            {
                Check($"turn {turns}: a reference render is produced", false);
                continue;
            }

            int mismatched = 0;
            int columns = (int)Math.Ceiling(width / (double)tileSize);
            int rows = (int)Math.Ceiling(height / (double)tileSize);

            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    var tile = await queue.RequestTileAsync(
                        document.DocumentId, new TileKey(0, 0, column, row, turns), tileSize);
                    if (tile is not { } t) continue;

                    for (int y = 0; y < tileSize; y++)
                    {
                        int pageY = row * tileSize + y;
                        if (pageY >= height) break;

                        for (int x = 0; x < tileSize; x++)
                        {
                            int pageX = column * tileSize + x;
                            if (pageX >= width) break;

                            if (Bitmap.IsDark(t.Bgra, t.Width, x, y)
                                != Bitmap.IsDark(reference.Bgra, reference.Width, pageX, pageY))
                            {
                                mismatched++;
                            }
                        }
                    }
                }
            }

            Check($"turn {turns}: {columns}x{rows} tiles match the full render",
                mismatched == 0, $"{mismatched} pixels differ");
        }

        // A tile of the same sheet at a different turn is a different picture,
        // which is why the turn belongs in the cache key.
        Check("the turn is part of the tile's identity",
            new TileKey(0, 0, 0, 0, 1) != new TileKey(0, 0, 0, 0, 0));

        await queue.CloseDocumentAsync(document.DocumentId);
        File.Delete(path);
    }

    /// <summary>
    /// Selection and search highlights are drawn from the text layer, so its
    /// boxes have to turn with the ink. If they did not, a search would light
    /// up the wrong part of a turned sheet.
    /// </summary>
    private static async Task TextAsync()
    {
        Section("Rotation — glyph boxes turn with the ink");

        string path = TestPdf.WriteText(0);
        var queue = PdfRenderQueue.Shared;
        var document = await queue.OpenDocumentAsync(path);

        foreach (int turns in new[] { 0, 1, 2, 3 })
        {
            var layer = await queue.RequestTextLayerAsync(document.DocumentId, 0, turns);

            bool swapped = (turns & 1) == 1;
            int width = swapped ? TestPdf.PageHeight : TestPdf.PageWidth;
            int height = swapped ? TestPdf.PageWidth : TestPdf.PageHeight;

            var render = await queue.RequestPagePreviewAsync(document.DocumentId, 0, Math.Max(width, height), turns);
            if (render is not { } image || Bitmap.InkBounds(image.Bgra, image.Width, image.Height) is not { } ink)
            {
                Check($"turn {turns}: the turned sheet has visible text", false);
                continue;
            }

            var box = UnionOfGlyphBoxes(layer);
            if (box is not { } boxes)
            {
                Check($"turn {turns}: glyphs carry boxes", false);
                continue;
            }

            // Boxes include side bearings and the full ascent, so they contain
            // the ink with a little room; they must never sit apart from it.
            const float tolerance = 12f;
            bool contains = boxes.Left <= ink.Left + tolerance
                && boxes.Top <= ink.Top + tolerance
                && boxes.Right >= ink.Right - tolerance
                && boxes.Bottom >= ink.Bottom - tolerance;

            bool snug = Math.Abs(boxes.Left - ink.Left) < 30
                && Math.Abs(boxes.Top - ink.Top) < 30
                && Math.Abs(boxes.Right - ink.Right) < 30
                && Math.Abs(boxes.Bottom - ink.Bottom) < 30;

            Check($"turn {turns}: glyph boxes enclose the turned ink", contains,
                $"boxes L{boxes.Left:0} T{boxes.Top:0} R{boxes.Right:0} B{boxes.Bottom:0} vs ink L{ink.Left} T{ink.Top} R{ink.Right} B{ink.Bottom}");
            Check($"turn {turns}: glyph boxes hug the turned ink", snug);
            Check($"turn {turns}: text still reads as written",
                layer.GetText(0, layer.Count - 1).Contains("ZENINK"));
        }

        await queue.CloseDocumentAsync(document.DocumentId);
        File.Delete(path);
    }

    private static (float Left, float Top, float Right, float Bottom)? UnionOfGlyphBoxes(PageTextLayer layer)
    {
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        bool any = false;

        foreach (var glyph in layer.Chars)
        {
            if (!glyph.HasArea) continue;
            any = true;
            left = Math.Min(left, glyph.Left);
            top = Math.Min(top, glyph.Top);
            right = Math.Max(right, glyph.Right);
            bottom = Math.Max(bottom, glyph.Bottom);
        }

        return any ? (left, top, right, bottom) : null;
    }

    private static async Task SavingAsync()
    {
        Section("Rotation — writing the turn into the file");

        var queue = PdfRenderQueue.Shared;

        string source = TestPdf.WriteRectangle("zenink-rotate-save", "0 450 100 150");
        string copy = Path.Combine(Path.GetTempPath(), "zenink-rotate-save-copy.pdf");
        long originalLength = new FileInfo(source).Length;

        await queue.SaveChangesCopyAsync(source, copy, [1]);

        Check("a copy is written", File.Exists(copy));
        Check("the original is left alone", new FileInfo(source).Length == originalLength);

        var saved = await queue.OpenDocumentAsync(copy);
        Check("the saved sheet reports its turned size",
            Math.Abs(saved.Pages[0].WidthPt - TestPdf.PageHeight) < 0.5f
            && Math.Abs(saved.Pages[0].HeightPt - TestPdf.PageWidth) < 0.5f,
            $"got {saved.Pages[0].WidthPt}x{saved.Pages[0].HeightPt}");

        // Reopened with no viewer turn at all: the file itself now carries it.
        var render = await queue.RequestPagePreviewAsync(saved.DocumentId, 0, TestPdf.PageHeight);
        Check("the saved sheet draws its ink where the reader last saw it",
            render is { } r && Bitmap.IsDark(r.Bgra, r.Width, (int)(r.Width * 0.88), (int)(r.Height * 0.12)));

        await queue.CloseDocumentAsync(saved.DocumentId);

        // Turning twice more must land at 180 relative to the original, not
        // reset to it: the saved turn adds to whatever /Rotate the page had.
        string twice = Path.Combine(Path.GetTempPath(), "zenink-rotate-save-twice.pdf");
        await queue.SaveChangesCopyAsync(copy, twice, [1]);
        var again = await queue.OpenDocumentAsync(twice);
        Check("a further turn adds to the one already in the file",
            Math.Abs(again.Pages[0].WidthPt - TestPdf.PageWidth) < 0.5f,
            $"got {again.Pages[0].WidthPt}x{again.Pages[0].HeightPt}");
        await queue.CloseDocumentAsync(again.DocumentId);

        File.Delete(copy);
        File.Delete(twice);
        File.Delete(source);

        await SavingInPlaceAsync();
        await SavingIgnoresHairlinesAsync();
    }

    private static async Task SavingInPlaceAsync()
    {
        Section("Rotation — saving over the drawing itself");

        var queue = PdfRenderQueue.Shared;
        string path = TestPdf.WriteRectangle("zenink-rotate-inplace", "0 450 100 150");

        var document = await queue.OpenDocumentAsync(path);
        var outcome = await queue.ApplyChangesInPlaceAsync(document.DocumentId, path, [1]);

        Check("the save reports success", outcome.Saved, outcome.Error);
        Check("the document comes back reopened, under a new id",
            outcome.Document.DocumentId != document.DocumentId);
        Check("and reads its new size from the file",
            Math.Abs(outcome.Document.Pages[0].WidthPt - TestPdf.PageHeight) < 0.5f,
            $"got {outcome.Document.Pages[0].WidthPt}x{outcome.Document.Pages[0].HeightPt}");

        // The point of reopening: the tab must keep working afterwards.
        var tile = await queue.RequestTileAsync(outcome.Document.DocumentId, new TileKey(0, 0, 0, 0), 512);
        Check("the reopened document still renders", tile is { } t && Bitmap.IsDark(t.Bgra, t.Width, 500, 50));

        await queue.CloseDocumentAsync(outcome.Document.DocumentId);
        File.Delete(path);
    }

    /// <summary>
    /// The line-weight toggle works by rewriting stroke widths on the parsed
    /// page. Saving from that same handle would make the reader's display
    /// setting permanent, so a save must go through a handle of its own.
    /// </summary>
    private static async Task SavingIgnoresHairlinesAsync()
    {
        Section("Rotation — a display setting must not reach the file");

        var queue = PdfRenderQueue.Shared;
        string path = TestPdf.WriteThickLines("zenink-rotate-hairline", 8f);
        string copy = Path.Combine(Path.GetTempPath(), "zenink-rotate-hairline-copy.pdf");

        var document = await queue.OpenDocumentAsync(path);
        int authored = InkOf(await queue.RequestTileAsync(document.DocumentId, new TileKey(0, 0, 0, 0), 512));

        queue.SetThinLines(document.DocumentId, true);
        int hairline = InkOf(await queue.RequestTileAsync(document.DocumentId, new TileKey(0, 0, 0, 0), 512));
        Check($"hairline mode is in force while saving ({authored} -> {hairline} px)",
            hairline > 0 && hairline < authored / 2);

        await queue.SaveChangesCopyAsync(path, copy, [0]);

        var saved = await queue.OpenDocumentAsync(copy);
        int savedInk = InkOf(await queue.RequestTileAsync(saved.DocumentId, new TileKey(0, 0, 0, 0), 512));
        Check($"the saved file keeps the authored stroke widths ({savedInk} px)",
            Math.Abs(savedInk - authored) <= Math.Max(2, authored / 100));

        await queue.CloseDocumentAsync(saved.DocumentId);
        await queue.CloseDocumentAsync(document.DocumentId);
        File.Delete(copy);
        File.Delete(path);
    }

    private static int InkOf(TileBitmapData? tile)
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
}
