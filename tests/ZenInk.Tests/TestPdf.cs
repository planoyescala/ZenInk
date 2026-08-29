using System.Text;

namespace ZenInk.Tests;

/// <summary>
/// Builds small PDFs by hand so the suite has known-good ground truth without
/// depending on sample files. Content is deliberately asymmetric on both axes,
/// so any flip, rotation or offset error shows up as ink in the wrong corner.
/// </summary>
public static class TestPdf
{
    public const int PageWidth = 400;
    public const int PageHeight = 600;

    /// <summary>A single page with a black rectangle in the top-left of the rendered image.</summary>
    public static string WriteCornerMark(int rotate)
    {
        // PDF space is y-up, so a high y is the visual top.
        string content = "0 0 0 rg\n0 450 100 150 re f\n";
        string rotateEntry = rotate == 0 ? "" : $"/Rotate {rotate}";

        return Write($"zenink-corner-{rotate}", content, rotateEntry, withFont: false);
    }

    /// <summary>A single page with the word ZENINK near the top-left, in a standard font.</summary>
    public static string WriteText(int rotate)
    {
        string content = "BT /F1 36 Tf 30 520 Td (ZENINK) Tj ET\n";
        string rotateEntry = rotate == 0 ? "" : $"/Rotate {rotate}";

        return Write($"zenink-text-{rotate}", content, rotateEntry, withFont: true);
    }

    /// <summary>A single page with a black rectangle at an arbitrary spot, for telling documents apart.</summary>
    public static string WriteRectangle(string name, string rectangle)
    {
        return Write(name, $"0 0 0 rg\n{rectangle} re f\n", "", withFont: false);
    }

    /// <summary>
    /// A single page holding stroked lines wide enough that forcing them to
    /// hairline is unmistakable, unlike the sub-point strokes typical of a
    /// real drawing.
    /// </summary>
    public static string WriteThickLines(string name, float strokeWidth)
    {
        var content = new StringBuilder();
        content.Append("0 0 0 RG\n");
        content.Append($"{strokeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)} w\n");
        for (int i = 0; i < 6; i++)
        {
            int y = 80 + i * 80;
            content.Append($"40 {y} m 360 {y} l S\n");
        }
        return Write(name, content.ToString(), "", withFont: false);
    }

    /// <summary>
    /// A single page carrying an annotation ZenInk did not write — someone
    /// else's review comment. Nothing marks it as ours, so saving must leave it
    /// exactly where it is, and PDFium must go on drawing it into the tiles.
    /// </summary>
    public static string WriteForeignAnnotation(string name) =>
        Write(
            name,
            "0 0 0 rg\n0 450 100 150 re f\n",
            "",
            withFont: false,
            extraPageEntries: "/Annots[5 0 R]",
            extraObjects: ["<</Type/Annot/Subtype/Square/Rect[200 100 350 250]/C[0 0.35 0.78]/F 4/Border[0 0 3]>>"]);

    private static string Write(
        string name,
        string content,
        string rotateEntry,
        bool withFont,
        string extraPageEntries = "",
        IReadOnlyList<string>? extraObjects = null)
    {
        int contentLength = Encoding.ASCII.GetByteCount(content);
        string resources = withFont ? "/Resources<</Font<</F1 5 0 R>>>>" : "";

        var objects = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 {PageWidth} {PageHeight}]{rotateEntry}{resources}{extraPageEntries}/Contents 4 0 R>>",
            $"<</Length {contentLength}>>\nstream\n{content}endstream",
        };

        if (withFont)
        {
            objects.Add("<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        }

        if (extraObjects is not null)
        {
            objects.AddRange(extraObjects);
        }

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.4\n");

        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xrefOffset = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }
        sb.Append($"trailer\n<</Size {objects.Count + 1}/Root 1 0 R>>\nstartxref\n{xrefOffset}\n%%EOF\n");

        string path = Path.Combine(Path.GetTempPath(), $"{name}.pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
        return path;
    }
}
