using ZenInk.Core;

namespace ZenInk.Tests;

/// <summary>
/// Builds the arrangements a save is given. Most checks want "the document as
/// it came, with these turns on it", which is what every save looked like
/// before sheets could be moved.
/// </summary>
public static class TestPlan
{
    /// <summary>One sheet per turn given, in the document's own order.</summary>
    public static PagePlan Turns(string path, params int[] quarterTurns)
    {
        var sizes = new PdfPageSize[quarterTurns.Length];
        Array.Fill(sizes, new PdfPageSize(TestPdf.PageWidth, TestPdf.PageHeight));

        var plan = PagePlan.Identity(sizes, path);
        for (int i = 0; i < quarterTurns.Length; i++)
        {
            if ((quarterTurns[i] & 3) != 0)
            {
                plan = plan.Rotate([i], quarterTurns[i]).Plan;
            }
        }
        return plan;
    }

    /// <summary>The document as it came, however many sheets it has.</summary>
    public static PagePlan Of(string path, int pageCount) => Turns(path, new int[pageCount]);
}
