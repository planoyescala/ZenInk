using ZenInk.Core;
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
        Searching();
    }

    /// <summary>
    /// Lays a string out as glyph boxes, one line per newline. Line breaks
    /// carry no box, exactly as PDFium reports them.
    /// </summary>
    private static PageTextLayer LayerOf(string text)
    {
        var glyphs = new TextChar[text.Length];
        float x = 10f, top = 10f;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                glyphs[i] = new TextChar('\n', 0, 0, 0, 0);
                x = 10f;
                top += 20f;
                continue;
            }

            glyphs[i] = new TextChar(text[i], x, top, x + 10f, top + 14f);
            x += 10f;
        }

        return new PageTextLayer(0, glyphs);
    }

    /// <summary>
    /// The find bar's matching. A drawing's text arrives broken by line wraps
    /// and written however the draughtsman felt that day, so these pin the two
    /// things that decide whether a search is useful on a real sheet: accents
    /// and case fold away unless asked otherwise, and a caption split across
    /// lines still answers a query typed as one.
    /// </summary>
    private static void Searching()
    {
        Section("Text layer — finding text");

        var loose = new TextSearchOptions();
        var exact = new TextSearchOptions(MatchCase: true);
        var word = new TextSearchOptions(WholeWord: true);

        var layer = LayerOf("SECCION A-A y seccion b-b");

        Check("a search finds every occurrence", layer.Find("seccion", loose).Count == 2);
        Check("case folds by default", layer.Find("SeCcIoN", loose).Count == 2);
        Check("matching case narrows to the exact spelling", layer.Find("SECCION", exact).Count == 1);
        Check("nothing found returns no matches", layer.Find("planta", loose).Count == 0);
        Check("an empty query finds nothing", layer.Find("", loose).Count == 0);

        var accented = LayerOf("SECCIÓN transversal");
        Check("accents fold away in a loose search", accented.Find("seccion", loose).Count == 1);
        Check("an accented query still finds the accented text", accented.Find("Sección", loose).Count == 1);
        Check("matching case keeps the accent significant", accented.Find("SECCION", exact).Count == 0);

        var hit = layer.Find("A-A", loose);
        Check("a match reports an inclusive glyph range",
            hit.Count == 1 && hit[0].EndIndex - hit[0].StartIndex == 2,
            hit.Count == 1 ? $"got {hit[0].StartIndex}..{hit[0].EndIndex}" : $"got {hit.Count} matches");
        Check("the range points at the matched glyphs",
            hit.Count == 1 && layer.GetText(hit[0].StartIndex, hit[0].EndIndex) == "A-A");

        var wrapped = LayerOf("PLANTA\nBAJA");
        Check("a caption split across lines matches as one phrase",
            wrapped.Find("PLANTA BAJA", loose).Count == 1);

        var wrappedHit = wrapped.Find("PLANTA BAJA", loose);
        Check("a wrapped match highlights on both lines",
            wrappedHit.Count == 1 && wrapped.BuildRuns(wrappedHit[0].StartIndex, wrappedHit[0].EndIndex).Count == 2);

        var spaced = LayerOf("PLANTA   BAJA");
        Check("runs of spaces collapse on both sides of the comparison",
            spaced.Find("PLANTA BAJA", loose).Count == 1);

        var partial = LayerOf("PLANTA y REPLANTEO");
        Check("a substring matches by default", partial.Find("PLANT", loose).Count == 2);
        Check("whole word rejects the one inside another word", partial.Find("PLANTA", word).Count == 1);
        Check("punctuation counts as a boundary", LayerOf("E-04, E-05").Find("E-04", word).Count == 1);

        var repeated = LayerOf("aaaa");
        Check("overlapping candidates are not double counted", repeated.Find("aa", loose).Count == 2);

        Check("matches come back in reading order",
            layer.Find("e", loose).Zip(layer.Find("e", loose).Skip(1)).All(p => p.First.StartIndex < p.Second.StartIndex));
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
