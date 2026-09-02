using ZenInk.Core;
using static ZenInk.Tests.TestRunner;

namespace ZenInk.Tests;

/// <summary>
/// Comparing two revisions of a drawing. What is pinned here is what a reader
/// would otherwise have to trust: that the revision lands where it belongs on
/// the sheet, that ink the two drawings share never comes out as a change, and
/// that the changes reported are the ones that are there.
///
/// The last of those matters most. A comparison that misses a change is worse
/// than no comparison at all, because it was believed.
/// </summary>
public static class CompareTests
{
    private static readonly PdfPageSize A0 = new(2384f, 3370f);
    private static readonly PdfPageSize A1 = new(1684f, 2384f);
    private static readonly PdfPageSize LandscapeA0 = new(3370f, 2384f);

    public static void Run()
    {
        Alignment();
        Composition();
        Detection();
    }

    private static void Alignment()
    {
        Section("Comparar — la revisión se coloca sobre la hoja");

        var same = SheetAlignment.For(A0, A0, 0, CompareFit.Fit);
        Check("the same paper needs no transform at all",
            same is { QuarterTurns: 0, ScaleX: 1.0, ScaleY: 1.0, OffsetXPt: 0.0, OffsetYPt: 0.0 });

        // Two plots of one drawing whose paper differs by a rounding: scaling
        // by 0.9998 would resample every pixel and fringe every line.
        var rounded = SheetAlignment.For(A0, new PdfPageSize(2383f, 3369f), 0, CompareFit.Fit);
        Check("a paper size that differs by a rounding is taken as the same", rounded.ScaleX == 1.0);

        var smaller = SheetAlignment.For(A0, A1, 0, CompareFit.Fit);
        CheckClose("an A1 plot fits onto the A0 sheet", smaller.ScaleX, 3370.0 / 2384.0, 0.001);
        Check("and it is scaled the same on both axes", smaller.ScaleX == smaller.ScaleY);
        CheckClose("centred across", smaller.OffsetXPt, (2384.0 - (1684.0 * smaller.ScaleX)) / 2.0, 0.5);
        CheckClose("centred down", smaller.OffsetYPt, (3370.0 - (2384.0 * smaller.ScaleY)) / 2.0, 0.5);

        // A set re-issued through another plotter driver arrives stored the
        // other way round. The reader should never have to notice.
        var sideways = SheetAlignment.For(LandscapeA0, A0, 0, CompareFit.Fit);
        Check("a revision stored upright is turned onto a landscape sheet", sideways.QuarterTurns == 1);
        CheckClose("and then it needs no scaling", sideways.ScaleX, 1.0, 0.001);

        // The reader turned the sheet, so it lies landscape while the file
        // still holds it upright — and the revision has to be turned with it.
        // An upright overlay on a sideways sheet compares nothing.
        var turned = SheetAlignment.For(LandscapeA0, A0, 1, CompareFit.Fit);
        Check("the reader's quarter turn travels to the revision", turned.QuarterTurns == 1);
        CheckClose("and the two still lie on each other exactly", turned.ScaleX, 1.0, 0.001);

        var stretched = SheetAlignment.For(A0, A1, 0, CompareFit.Stretch);
        CheckClose("stretching fills the sheet across", 1684.0 * stretched.ScaleX, 2384.0, 0.5);
        CheckClose("stretching fills the sheet down", 2384.0 * stretched.ScaleY, 3370.0, 0.5);
        CheckClose("and leaves nothing to centre across", stretched.OffsetXPt, 0.0, 0.001);
        CheckClose("and leaves nothing to centre down", stretched.OffsetYPt, 0.0, 0.001);

        var nudged = same.Nudged(12.0, -4.0);
        Check("a nudge moves the revision and nothing else",
            nudged.OffsetXPt == 12.0 && nudged.OffsetYPt == -4.0 && nudged.ScaleX == same.ScaleX);
    }

    private static void Composition()
    {
        Section("Comparar — la tinta compartida no puede salir de color");

        var palette = ComparePalette.Default;

        // A drawing compared against itself: every line is in both, so nothing
        // may come out coloured. This is the check that would catch a
        // comparison that fringes everything.
        var ink = Sheet(16, 16, (4, 4, 12, 12));
        var both = RevisionInk.Compose(ink, ink, 16, 16, 0, palette);

        Check("ink in both drawings comes out neutral", IsNeutral(both, 16, 8, 8));
        Check("and it is grey, not black", Value(both, 16, 8, 8).R is > 60 and < 220);
        Check("bare paper stays white", Value(both, 16, 2, 2) is { R: 255, G: 255, B: 255 });

        var onlySheet = RevisionInk.Compose(ink, Blank(16, 16), 16, 16, 0, palette);
        var onlyRevision = RevisionInk.Compose(Blank(16, 16), ink, 16, 16, 0, palette);

        Check("ink only in the open document takes the document's colour",
            Value(onlySheet, 16, 8, 8) is var s && s.B > s.R + 40);
        Check("ink only in the revision takes the revision's colour",
            Value(onlyRevision, 16, 8, 8) is var r && r.R > r.B + 40);

        var swapped = RevisionInk.Compose(ink, Blank(16, 16), 16, 16, 0, palette.Swapped());
        Check("swapping the palette swaps which file is which",
            Value(swapped, 16, 8, 8) is var w && w.R > w.B + 40);

        // Two renders of one line land on the pixel grid a fraction
        // differently. Taken literally that is a line removed and another
        // added — and every line on the sheet would be fringed.
        var left = Sheet(16, 16, (6, 2, 7, 14));
        var right = Sheet(16, 16, (7, 2, 8, 14));
        var shifted = RevisionInk.Compose(left, right, 16, 16, 0, palette);

        Check("a line that lands one pixel over is not a change", IsNeutral(shifted, 16, 6, 8));
        Check("and it is still drawn", Value(shifted, 16, 6, 8).R < 250);

        var moved = Sheet(16, 16, (11, 2, 12, 14));
        var apart = RevisionInk.Compose(left, moved, 16, 16, 0, palette);
        Check("a line that really moved is two changes",
            Value(apart, 16, 6, 8) is var a && a.B > a.R + 40
            && Value(apart, 16, 11, 8) is var b && b.R > b.B + 40);

        var cropped = RevisionInk.Compose(Blank(16, 16), Blank(16, 16), 16, 16, 1, palette);
        Check("the margin the spread needed is cropped back off", cropped.Length == 14 * 14 * 4);
    }

    private static void Detection()
    {
        Section("Comparar — dónde están los cambios");

        const double pointsPerPixel = 1.5;

        var common = Sheet(120, 120, (10, 10, 40, 40));
        Check("a drawing compared against itself reports nothing",
            RevisionInk.FindChanges(common, common, 120, 120, pointsPerPixel).Count == 0);

        var added = Sheet(120, 120, (10, 10, 40, 40), (70, 80, 90, 100));
        var changes = RevisionInk.FindChanges(common, added, 120, 120, pointsPerPixel);

        Check("what only one revision has is one change", changes.Count == 1);
        if (changes.Count == 1)
        {
            CheckClose("found across the sheet", changes[0].Box.X, 70 * pointsPerPixel, 3.0);
            CheckClose("found down the sheet", changes[0].Box.Y, 80 * pointsPerPixel, 3.0);
            CheckClose("and it is the size of what changed", changes[0].Box.Width, 20 * pointsPerPixel, 4.0);
        }

        // Far apart: two things to walk through, and in reading order.
        var twoPlaces = Sheet(120, 120, (10, 10, 40, 40), (80, 5, 100, 25), (10, 90, 30, 110));
        var separate = RevisionInk.FindChanges(common, twoPlaces, 120, 120, pointsPerPixel);
        Check("two changes far apart stay two", separate.Count == 2);
        if (separate.Count == 2)
        {
            Check("and they come back in reading order", separate[0].Box.Y < separate[1].Box.Y);
        }

        // Close together: a revised dimension arrives as its line, its arrow
        // and each of its digits, and the reader wants the dimension.
        var scattered = Sheet(120, 120, (10, 10, 40, 40), (80, 80, 84, 84), (88, 82, 92, 86), (95, 80, 99, 84));
        var folded = RevisionInk.FindChanges(common, scattered, 120, 120, pointsPerPixel);
        Check("pieces of one change are folded into one", folded.Count == 1);
        if (folded.Count == 1)
        {
            CheckClose("and the box covers all of them", folded[0].Box.Width, 19 * pointsPerPixel, 4.0);
        }
    }

    /// <summary>
    /// The same thing again through PDFium, because everything above is
    /// arithmetic on buffers somebody could hand it. This is the path the
    /// reader actually sees: two files, one composed tile.
    /// </summary>
    public static async Task RunRenderAsync()
    {
        Section("Comparar — dos archivos, un dibujo");

        // Ink the two share, ink only the first has, ink only the second has.
        // PDF space is y-up, so a high y is the top of the picture.
        const string shared = "40 480 120 80";
        string basePath = TestPdf.WriteRectangles("zenink-rev-a", shared, "40 220 120 80");
        string revisionPath = TestPdf.WriteRectangles("zenink-rev-b", shared, "220 220 120 80");

        var queue = PdfRenderQueue.Shared;
        var sheet = await queue.OpenDocumentAsync(basePath);
        var revision = await queue.OpenDocumentAsync(revisionPath);

        var alignment = SheetAlignment.For(sheet.Pages[0], revision.Pages[0], 0, CompareFit.Fit);
        Check("two plots of the same paper align without a transform",
            alignment is { QuarterTurns: 0, ScaleX: 1.0, OffsetXPt: 0.0 });

        var overlay = new OverlaySheet(revision.DocumentId, 0, alignment, ComparePalette.Default);
        var tile = await queue.RequestComparisonTileAsync(sheet.DocumentId, new TileKey(0, 0, 0, 0), 512, overlay);

        if (tile is not { } composed)
        {
            Check("the comparison renders a tile", false);
            return;
        }

        // Device space, y down: the shared block is 40..160 across and 40..120
        // down; the two changed blocks are both at 300..380 down.
        Check("what both drawings have is neutral", IsNeutral(composed.Bgra, composed.Width, 100, 80));
        Check("what only the open document has is its colour",
            Value(composed.Bgra, composed.Width, 100, 340) is var s && s.B > s.R + 40);
        Check("what only the revision has is its colour",
            Value(composed.Bgra, composed.Width, 280, 340) is var r && r.R > r.B + 40);
        Check("bare paper stays white",
            Value(composed.Bgra, composed.Width, 380, 480) is { R: 255, G: 255, B: 255 });

        // Off the edge of the paper there is nothing at all, and a comparison
        // must not paint the gap between two sheets.
        Check("beyond the sheet there is nothing to compare",
            Value(composed.Bgra, composed.Width, 450, 100) is { R: 255, G: 255, B: 255 });

        // A capture and a print of a comparison go down the band path. They are
        // only the same picture as the screen because both compose the same
        // way, so that is checked rather than assumed.
        var band = await queue.RequestPrintBandAsync(
            sheet.DocumentId, 0, 0, 1.0, 100, 320, 220, 60, monochrome: false, overlay);

        if (band is { } strip)
        {
            bool matches = true;
            for (int y = 0; y < strip.Height && matches; y++)
            {
                for (int x = 0; x < strip.Width; x++)
                {
                    if (Value(strip.Bgra, strip.Width, x, y) != Value(composed.Bgra, composed.Width, 100 + x, 320 + y))
                    {
                        matches = false;
                        break;
                    }
                }
            }
            Check("a captured comparison is the same picture as the screen's", matches);
        }
        else
        {
            Check("the comparison renders a band", false);
        }

        // And the sweep that says how many changes there are, over the two
        // pages rendered onto one grid.
        double detect = RevisionInk.DetectionDpi / 72.0;
        int width = (int)Math.Ceiling(TestPdf.PageWidth * detect);
        int height = (int)Math.Ceiling(TestPdf.PageHeight * detect);

        var sheetSweep = await queue.RequestPrintBandAsync(
            sheet.DocumentId, 0, 0, detect, 0, 0, width, height, monochrome: false);
        var revisionSweep = await queue.RequestBandAsync(
            revision.DocumentId, 0, alignment.QuarterTurns,
            detect * alignment.ScaleX, detect * alignment.ScaleY,
            -(int)Math.Round(alignment.OffsetXPt * detect),
            -(int)Math.Round(alignment.OffsetYPt * detect),
            width, height, monochrome: false);

        if (sheetSweep is { } a && revisionSweep is { } b)
        {
            var found = RevisionInk.FindChanges(a.Bgra, b.Bgra, width, height, 72.0 / RevisionInk.DetectionDpi);
            Check($"the sweep finds both changes and not the shared block ({found.Count})", found.Count == 2);

            if (found.Count == 2)
            {
                CheckClose("the first is where the open document's own block is", found[0].Box.X, 40, 6);
                CheckClose("the second is where the revision's is", found[1].Box.X, 220, 6);
                CheckClose("and both are on the row they were drawn on", found[0].Box.Y, 300, 6);
            }
        }
        else
        {
            Check("the sweep renders both pages", false);
        }

        await queue.CloseDocumentAsync(sheet.DocumentId);
        await queue.CloseDocumentAsync(revision.DocumentId);
        File.Delete(basePath);
        File.Delete(revisionPath);
    }

    /// <summary>A white BGRA buffer with black boxes on it, given as left, top, right, bottom.</summary>
    private static byte[] Sheet(int width, int height, params (int Left, int Top, int Right, int Bottom)[] boxes)
    {
        var bgra = Blank(width, height);
        foreach (var box in boxes)
        {
            for (int y = box.Top; y < box.Bottom; y++)
            {
                for (int x = box.Left; x < box.Right; x++)
                {
                    int at = ((y * width) + x) * 4;
                    bgra[at] = bgra[at + 1] = bgra[at + 2] = 0;
                }
            }
        }
        return bgra;
    }

    private static byte[] Blank(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        Array.Fill(bgra, (byte)255);
        return bgra;
    }

    private static (byte B, byte G, byte R) Value(byte[] bgra, int width, int x, int y)
    {
        int at = ((y * width) + x) * 4;
        return (bgra[at], bgra[at + 1], bgra[at + 2]);
    }

    /// <summary>Grey: no channel pulling away from the others, which is what "both drawings have this" looks like.</summary>
    private static bool IsNeutral(byte[] bgra, int width, int x, int y)
    {
        var (b, g, r) = Value(bgra, width, x, y);
        return Math.Abs(r - g) <= 2 && Math.Abs(g - b) <= 2;
    }
}
