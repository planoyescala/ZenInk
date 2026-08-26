using ZenInk_App.Rendering;
using static ZenInk.Tests.TestRunner;

namespace ZenInk.Tests;

/// <summary>Pure geometry: how sheets are stacked, and how zoom maps to tile levels.</summary>
public static class LayoutTests
{
    public static void Run()
    {
        Continuous();
        SinglePage();
        EdgeCases();
        Zoom();
    }

    private static readonly List<PdfPageSize> MixedSizes =
    [
        new(400f, 600f),
        new(3370f, 2384f),
        new(400f, 600f),
    ];

    private static void Continuous()
    {
        Section("DocumentLayout — continuous");
        var layout = DocumentLayout.Continuous(MixedSizes);

        Check("lays out every sheet", layout.PageCount == 3);
        Check("reports the document's page count", layout.DocumentPageCount == 3);
        CheckClose("width is the widest sheet", layout.WidthPt, 3370f, 0.01);

        float expectedHeight = 600f + DocumentLayout.PageGapPt + 2384f + DocumentLayout.PageGapPt + 600f;
        CheckClose("height stacks the sheets with gaps", layout.HeightPt, expectedHeight, 0.01);

        var first = layout.Pages[0];
        var middle = layout.Pages[1];
        var last = layout.Pages[2];

        Check("page indices are sequential", first.Index == 0 && middle.Index == 1 && last.Index == 2);
        CheckClose("first sheet starts at the top", first.YPt, 0, 0.01);
        CheckClose("second sheet follows the gap", middle.YPt, 600f + DocumentLayout.PageGapPt, 0.01);
        CheckClose("third sheet follows the second", last.YPt, 600f + DocumentLayout.PageGapPt + 2384f + DocumentLayout.PageGapPt, 0.01);
        CheckClose("narrow sheets are centred", first.XPt, (3370f - 400f) / 2f, 0.01);
        CheckClose("the widest sheet is flush", middle.XPt, 0, 0.01);

        var isolated = layout.PagesInBand(middle.YPt + 10, middle.BottomPt - 10).ToList();
        Check("a band inside one sheet returns only that sheet",
            isolated.Count == 1 && isolated[0].Index == 1);

        var seam = layout.PagesInBand(first.BottomPt - 5, middle.YPt + 5).ToList();
        Check("a band across a seam returns both neighbours",
            seam.Count == 2 && seam[0].Index == 0 && seam[1].Index == 1);

        Check("the dominant sheet is the mostly-visible one",
            layout.DominantPageIndex(middle.YPt + 10, middle.YPt + 500) == 1);
    }

    private static void SinglePage()
    {
        Section("DocumentLayout — single sheet");
        var layout = DocumentLayout.SinglePage(MixedSizes, 1);

        Check("lays out exactly one sheet", layout.PageCount == 1);
        Check("still reports the whole document", layout.DocumentPageCount == 3);
        Check("keeps the real PDF page index, so tiles stay cache-valid", layout.Pages[0].Index == 1);
        Check("the sheet sits at the origin",
            Math.Abs(layout.Pages[0].XPt) < 0.01f && Math.Abs(layout.Pages[0].YPt) < 0.01f);
        Check("document space is exactly that sheet",
            Math.Abs(layout.WidthPt - 3370f) < 0.01f && Math.Abs(layout.HeightPt - 2384f) < 0.01f);

        Check("an out-of-range page clamps to the last",
            DocumentLayout.SinglePage(MixedSizes, 99).Pages[0].Index == 2);
        Check("a negative page clamps to the first",
            DocumentLayout.SinglePage(MixedSizes, -5).Pages[0].Index == 0);
    }

    private static void EdgeCases()
    {
        Section("DocumentLayout — empty documents");
        var empty = DocumentLayout.Continuous([]);
        Check("an empty continuous layout has no sheets", empty.PageCount == 0 && empty.DocumentPageCount == 0);

        var emptySingle = DocumentLayout.SinglePage([], 0);
        Check("an empty single-sheet layout has no sheets", emptySingle.PageCount == 0);
        Check("an empty layout has no extent", emptySingle.WidthPt == 0 && emptySingle.HeightPt == 0);
    }

    private static void Zoom()
    {
        Section("ZoomLevels — tiles must never be upsampled");

        foreach (double scale in new[] { 0.05, 0.37, 1.0, 1.3, 2.0, 5.5, 11.9 })
        {
            double levelScale = ZoomLevels.ScaleForLevel(ZoomLevels.LevelForScale(scale));
            Check($"level for scale {scale} rasterizes at or above it ({levelScale:0.###})",
                levelScale >= scale - 1e-9);
        }

        Check("exact powers of two stay exact",
            Math.Abs(ZoomLevels.ScaleForLevel(ZoomLevels.LevelForScale(2.0)) - 2.0) < 1e-9);
        Check("levels increase with scale",
            ZoomLevels.LevelForScale(0.5) < ZoomLevels.LevelForScale(4.0));
    }
}
