using ZenInk_App.Rendering;
using static ZenInk.Tests.TestRunner;

namespace ZenInk.Tests;

/// <summary>
/// The text layer is only useful if its glyph boxes sit exactly where the ink
/// is drawn — a drift here is what makes a selection highlight the wrong words.
/// These checks compare the engine's own extracted boxes against the pixels
/// PDFium actually paints, under every page rotation.
/// </summary>
public static class TextLayerTests
{
    public static async Task RunAsync()
    {
        Section("Text layer — glyph boxes must track the painted ink");

        foreach (int rotate in new[] { 0, 90, 180, 270 })
        {
            string path = TestPdf.WriteText(rotate);
            var info = await PdfRenderQueue.Shared.OpenDocumentAsync(path);
            var layer = await PdfRenderQueue.Shared.RequestTextLayerAsync(info.DocumentId, 0);

            string extracted = layer.GetText(0, layer.Count - 1).Trim();
            Check($"/Rotate {rotate}: text extracts as written", extracted.Contains("ZENINK"),
                $"got \"{extracted}\"");

            var boxed = UnionOfGlyphBoxes(layer);
            if (boxed is not { } box)
            {
                Check($"/Rotate {rotate}: glyphs carry boxes", false, "no glyph had an area");
                await PdfRenderQueue.Shared.CloseDocumentAsync(info.DocumentId);
                File.Delete(path);
                continue;
            }

            var size = info.Pages[0];
            int width = (int)Math.Round(size.WidthPt);
            int height = (int)Math.Round(size.HeightPt);

            // Ground truth: where PDFium actually puts the ink.
            var pixels = RenderPage(path, width, height);
            if (Bitmap.InkBounds(pixels, width, height) is not { } ink)
            {
                Check($"/Rotate {rotate}: the page has visible text", false);
                await PdfRenderQueue.Shared.CloseDocumentAsync(info.DocumentId);
                File.Delete(path);
                continue;
            }

            // Boxes include side bearings and the full ascent, so they should
            // contain the ink with a little room, never sit apart from it.
            const float tolerance = 12f;
            bool contains = box.Left <= ink.Left + tolerance
                && box.Top <= ink.Top + tolerance
                && box.Right >= ink.Right - tolerance
                && box.Bottom >= ink.Bottom - tolerance;

            bool snug = Math.Abs(box.Left - ink.Left) < 30
                && Math.Abs(box.Top - ink.Top) < 30
                && Math.Abs(box.Right - ink.Right) < 30
                && Math.Abs(box.Bottom - ink.Bottom) < 30;

            Check($"/Rotate {rotate}: glyph boxes enclose the ink", contains,
                $"boxes L{box.Left:0} T{box.Top:0} R{box.Right:0} B{box.Bottom:0} vs ink L{ink.Left} T{ink.Top} R{ink.Right} B{ink.Bottom}");
            Check($"/Rotate {rotate}: glyph boxes hug the ink", snug);

            await PdfRenderQueue.Shared.CloseDocumentAsync(info.DocumentId);
            File.Delete(path);
        }

        Selection();
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

    private static byte[] RenderPage(string path, int width, int height)
    {
        var document = PDFiumCore.fpdfview.FPDF_LoadDocument(path, null)!;
        var page = PDFiumCore.fpdfview.FPDF_LoadPage(document, 0)!;
        try
        {
            return Bitmap.Render(page, 1.0, 0, 0, width, height);
        }
        finally
        {
            PDFiumCore.fpdfview.FPDF_ClosePage(page);
            PDFiumCore.fpdfview.FPDF_CloseDocument(document);
        }
    }

    private static void Selection()
    {
        Section("Text layer — selection ranges");

        var glyphs = new[]
        {
            new TextChar('A', 10, 10, 20, 24),
            new TextChar('B', 20, 10, 30, 24),
            new TextChar('\n', 0, 0, 0, 0),
            new TextChar('C', 10, 30, 20, 44),
        };
        var layer = new PageTextLayer(0, glyphs);

        Check("a hit inside a glyph selects it", layer.HitTest(15, 15, 24f) == 0);
        Check("a hit inside the next glyph selects that one", layer.HitTest(25, 15, 24f) == 1);
        Check("a hit below snaps to the lower line", layer.HitTest(15, 35, 24f) == 3);
        Check("a hit far away selects nothing", layer.HitTest(9000, 9000, 24f) == -1);

        Check("text reads in document order regardless of drag direction",
            layer.GetText(3, 0) == layer.GetText(0, 3));
        Check("selected text includes the line break", layer.GetText(0, 3).Contains('\n'));

        var runs = layer.BuildRuns(0, 3);
        Check("adjacent glyphs on a line merge into one run", runs.Count == 2,
            $"got {runs.Count} runs");
        Check("the merged run spans both glyphs",
            runs.Count > 0 && Math.Abs(runs[0].Left - 10) < 0.01f && Math.Abs(runs[0].Right - 30) < 0.01f);
        Check("zero-area glyphs contribute no highlight",
            runs.All(r => r.Right > r.Left && r.Bottom > r.Top));
    }
}
