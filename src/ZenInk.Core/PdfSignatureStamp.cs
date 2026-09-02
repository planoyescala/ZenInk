using System.Globalization;
using System.Text;

namespace ZenInk.Core;

/// <summary>
/// The drawing that a visible signature puts on the sheet: who signed, their
/// identity number, when, and why.
///
/// It is built as PDF drawing operators rather than as one of ZenInk's own
/// marks, and that is not an inconsistency with the rule that a mark's shape
/// comes from <see cref="AnnotationOutline"/>. This is not a mark. It belongs
/// to the signature's own form field — appearing and disappearing with it, and
/// covered by the signature that verifies it — so it has to live in the field's
/// appearance stream, which is a PDF object with its own space and its own
/// resources.
/// </summary>
public static class PdfSignatureStamp
{
    /// <summary>Space left between the border and the text, in points.</summary>
    public const float Padding = 4f;

    /// <summary>Line to line, as a fraction of the type size.</summary>
    public const float Leading = 1.25f;

    /// <summary>Below this the stamp is unreadable, so the text is dropped rather than smeared.</summary>
    private const float SmallestType = 3.5f;

    /// <summary>
    /// The type size a box gets, and the lines that survive at it.
    ///
    /// A box dragged out small does not get illegible text; it gets fewer
    /// lines. What goes first is what matters least — the place, then the
    /// reason, then the date — and the name is the last thing to go, because a
    /// stamp that does not say who signed says nothing at all.
    ///
    /// It is public because the same stamp is drawn in three places — the
    /// signature's own appearance, the canvas, and the file — and a stamp that
    /// dropped a different line in each of them would be three stamps.
    /// </summary>
    public static (float Size, IReadOnlyList<(string Text, bool Strong)> Lines) Fit(
        IReadOnlyList<(string Text, bool Strong)> lines, float width, float height)
    {
        if (lines.Count == 0) return (0f, lines);

        var shown = new List<(string Text, bool Strong)>(lines);
        float size = FitSize(shown, width, height);

        while (size < SmallestType && shown.Count > 1)
        {
            int last = shown.FindLastIndex(line => !line.Strong);
            shown.RemoveAt(last >= 0 ? last : shown.Count - 1);
            size = FitSize(shown, width, height);
        }

        return size < SmallestType ? (0f, []) : (size, shown);
    }

    /// <summary>The lines the stamp shows, in order, and which of them stands out.</summary>
    public static IReadOnlyList<(string Text, bool Strong)> Lines(
        PdfSignatureAppearance appearance, string reason, string location, DateTimeOffset when)
    {
        var lines = new List<(string, bool)>();

        // A field that was left empty is a line nobody asked for.
        if (appearance.Heading.Length > 0) lines.Add((appearance.Heading, false));
        if (appearance.Name.Length > 0) lines.Add((appearance.Name, true));
        if (appearance.Id.Length > 0) lines.Add(($"DNI: {appearance.Id}", false));

        if (appearance.ShowDate)
        {
            lines.Add(($"Fecha: {when.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}", false));
        }

        if (reason.Length > 0) lines.Add(($"Motivo: {reason}", false));
        if (location.Length > 0) lines.Add(($"Lugar: {location}", false));

        return lines;
    }

    /// <summary>
    /// The appearance stream's contents for a box <paramref name="width"/> by
    /// <paramref name="height"/> points, drawn from the bottom-left as PDF space
    /// expects.
    ///
    /// The type size is chosen to fit rather than fixed: a signature box is
    /// dragged out by hand and can come out any size, and a stamp whose text
    /// runs past its own border looks like a bug in the document rather than a
    /// box drawn small.
    /// </summary>
    public static string Draw(IReadOnlyList<(string Text, bool Strong)> lines, float width, float height)
    {
        var content = new StringBuilder();

        // A thin frame, so the stamp reads as something placed on the drawing
        // rather than as ink belonging to it.
        content.Append(Number(0.30f)).Append(' ').Append(Number(0.42f)).Append(' ')
               .Append(Number(0.62f)).Append(" RG\n");
        content.Append(Number(0.8f)).Append(" w\n");
        content.Append(Number(0.4f)).Append(' ').Append(Number(0.4f)).Append(' ')
               .Append(Number(width - 0.8f)).Append(' ').Append(Number(height - 0.8f)).Append(" re S\n");

        if (lines.Count == 0) return content.ToString();

        var (size, shown) = Fit(lines, width, height);
        if (size <= 0f) return content.ToString();

        lines = shown;

        float leading = size * Leading;
        float baseline = height - Padding - size;

        content.Append("0.12 0.12 0.14 rg\nBT\n");
        foreach (var (text, strong) in lines)
        {
            if (baseline < Padding * 0.5f) break;

            content.Append('/').Append(strong ? "F2" : "F1").Append(' ')
                   .Append(Number(size)).Append(" Tf\n");
            content.Append("1 0 0 1 ").Append(Number(Padding)).Append(' ')
                   .Append(Number(baseline)).Append(" Tm\n");
            content.Append('(').Append(Escape(text)).Append(") Tj\n");

            baseline -= leading;
        }
        content.Append("ET\n");

        return content.ToString();
    }

    /// <summary>
    /// The largest type that fits both ways: every line inside the width, and
    /// all the lines inside the height.
    ///
    /// The width is measured with the font's own advances rather than an
    /// average. Counting characters and multiplying got a name in bold capitals
    /// badly wrong — the letters that make up a Spanish surname in caps are far
    /// wider than the mean — and the name ran out past its own frame.
    ///
    /// No ceiling on the size: the box was dragged out by hand, and what you
    /// drag is what you get.
    /// </summary>
    public static float FitSize(IReadOnlyList<(string Text, bool Strong)> lines, float width, float height)
    {
        if (lines.Count == 0) return 0f;

        float byHeight = (height - Padding * 2f) / (lines.Count * 1.25f);

        float room = width - Padding * 2f;
        float byWidth = float.MaxValue;
        foreach (var (text, strong) in lines)
        {
            byWidth = Math.Min(byWidth, Helvetica.SizeThatFits(text, strong, room));
        }

        return Math.Min(byHeight, byWidth);
    }

    /// <summary>
    /// The form's own matrix, so the stamp reads upright on a sheet whose
    /// /Rotate turns it. The page's turn is applied when the sheet is shown, so
    /// the stamp has to be turned the other way in the page's own space to come
    /// out level.
    /// </summary>
    public static string Matrix(int quarterTurns) => (quarterTurns & 3) switch
    {
        1 => "0 -1 1 0 0 0",
        2 => "-1 0 0 -1 0 0",
        3 => "0 1 -1 0 0 0",
        _ => "1 0 0 1 0 0",
    };

    /// <summary>Turned a quarter either way, the box the text is laid out in swaps its sides.</summary>
    public static (float Width, float Height) LayoutBox(float rectWidth, float rectHeight, int quarterTurns) =>
        (quarterTurns & 1) == 1 ? (rectHeight, rectWidth) : (rectWidth, rectHeight);

    private static string Number(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Escapes what a PDF literal string cannot hold raw. The text goes out as
    /// Latin-1, which is what WinAnsiEncoding reads, so Spanish names keep their
    /// accents and their eñes.
    /// </summary>
    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", "").Replace("\n", " ");
}
