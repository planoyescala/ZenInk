using System.Globalization;
using System.Numerics;
using System.Text;

namespace ZenInk.Core;

/// <summary>
/// The mark as ZenInk needs it back, written into a private key of the PDF
/// annotation dictionary.
///
/// The standard annotation carries the mark for everyone else — a reviewer
/// opening the drawing in Acrobat or Bluebeam sees ink, a shape, a highlight, a
/// note. But the standard shapes cannot say "this is an arrow", or "this cloud
/// came from these four corners", so the exact mark rides alongside in one
/// string. Anything that does not understand the key ignores it, and the file
/// stays a plain PDF.
/// </summary>
public static class AnnotationPayload
{
    /// <summary>The private key. Not a standard name, so nothing else will touch it.</summary>
    public const string Key = "ZenInk";

    private const string Version = "z3";

    /// <summary>Before marks could carry words of their own, and with them a type size.</summary>
    private const string Version2 = "z2";

    /// <summary>The first version, before marks could turn or carry a fill.</summary>
    private const string Version1 = "z1";

    /// <summary>The mark as one line of text, geometry included.</summary>
    public static string Write(Annotation annotation)
    {
        var sb = new StringBuilder(96 + annotation.Points.Count * 12);
        sb.Append(Version).Append('|');
        sb.Append(annotation.Kind.ToString().ToLowerInvariant()).Append('|');
        sb.Append(annotation.Style.Color.Packed.ToString("X6", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(Number(annotation.Style.WidthPt)).Append('|');
        sb.Append(annotation.Created.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)).Append('|');
        sb.Append(Number(annotation.RotationDeg)).Append('|');
        sb.Append(annotation.Style.Fill is { } fill ? fill.Packed.ToString("X6", CultureInfo.InvariantCulture) : "-").Append('|');
        sb.Append(Number(annotation.Style.FillOpacity)).Append('|');
        sb.Append(Number(annotation.Style.FontSizePt)).Append('|');

        for (int i = 0; i < annotation.Points.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Number(annotation.Points[i].X)).Append(',').Append(Number(annotation.Points[i].Y));
        }

        sb.Append('|').Append(Escape(annotation.Author));
        sb.Append('|').Append(annotation.Text);
        return sb.ToString();
    }

    /// <summary>
    /// Reads a mark back, or returns false for anything this version does not
    /// understand. A payload that fails to parse is not an error to report: it
    /// means the annotation belongs to someone else, or to a later ZenInk, and
    /// the right thing is to leave it alone.
    /// </summary>
    public static bool TryRead(string payload, out Annotation annotation)
    {
        annotation = null!;
        if (string.IsNullOrEmpty(payload)) return false;

        // The text is last and unsplit, so a comment may contain the separator.
        string[] fields = payload.Split('|', 12);
        if (fields.Length < 7) return false;

        bool first = fields[0] == Version1;
        bool second = fields[0] == Version2;
        if (!first && !second && fields[0] != Version) return false;

        if (!Enum.TryParse<AnnotationKind>(fields[1], ignoreCase: true, out var kind)) return false;
        if (!uint.TryParse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed)) return false;
        if (!float.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float width)) return false;
        if (!long.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long created)) return false;

        float rotation = 0f;
        AnnotationColor? fill = null;
        float fillOpacity = 0.35f;
        float fontSize = 12f;
        int pointsField = 5;

        if (!first)
        {
            if (fields.Length < 10) return false;

            float.TryParse(fields[5], NumberStyles.Float, CultureInfo.InvariantCulture, out rotation);
            if (fields[6] != "-"
                && uint.TryParse(fields[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packedFill))
            {
                fill = AnnotationColor.FromPacked(packedFill);
            }
            float.TryParse(fields[7], NumberStyles.Float, CultureInfo.InvariantCulture, out fillOpacity);
            pointsField = 8;

            if (!second)
            {
                if (fields.Length < 11) return false;
                float.TryParse(fields[8], NumberStyles.Float, CultureInfo.InvariantCulture, out fontSize);
                pointsField = 9;
            }
        }

        var points = ReadPoints(fields[pointsField]);
        if (points.Count == 0) return false;

        var style = new AnnotationStyle(AnnotationColor.FromPacked(packed), width, fill, fillOpacity, fontSize);
        annotation = new Annotation(
            kind,
            points,
            style,
            text: fields.Length > pointsField + 2 ? fields[pointsField + 2] : string.Empty,
            author: fields.Length > pointsField + 1 ? Unescape(fields[pointsField + 1]) : string.Empty,
            id: null,
            created: DateTimeOffset.FromUnixTimeSeconds(created),
            rotationDeg: rotation);
        return true;
    }

    private static List<Vector2> ReadPoints(string field)
    {
        var points = new List<Vector2>();
        foreach (var pair in field.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int comma = pair.IndexOf(',');
            if (comma <= 0) continue;

            if (!float.TryParse(pair.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) continue;
            if (!float.TryParse(pair.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) continue;

            points.Add(new Vector2(x, y));
        }
        return points;
    }

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string value) => value.Replace("|", "%7C");

    private static string Unescape(string value) => value.Replace("%7C", "|");
}

/// <summary>
/// The appearance stream: the drawing the annotation shows in every other
/// reader.
///
/// It is written by hand for the marks that are only a line — ink, a straight
/// segment, an arrow — because their look has to be exact and they need no
/// transparency. Shapes that can be filled go into the file a different way, as
/// path objects inside the annotation, since a fill that hides the drawing
/// underneath is no use on a plan and only an object can carry the alpha that
/// stops it. Highlights, underlines and notes are left without an appearance on
/// purpose: every reader draws those itself, and does it right.
/// </summary>
public static class AnnotationAppearance
{
    /// <summary>Whether this kind gets an appearance stream written by hand.</summary>
    public static bool NeedsAppearance(AnnotationKind kind) =>
        kind is AnnotationKind.Ink or AnnotationKind.Line
            or AnnotationKind.Arrow or AnnotationKind.Polyline;

    /// <summary>
    /// Builds the stream for a mark whose outline is already in PDF page space,
    /// or returns null for the kinds that go into the file another way.
    /// </summary>
    public static string? Build(Annotation mark, IReadOnlyList<PathStep> pdfOutline, IReadOnlyList<Vector2> pdfPoints)
    {
        if (!NeedsAppearance(mark.Kind) || pdfOutline.Count == 0) return null;

        var sb = new StringBuilder(160);
        sb.Append("q\n");
        sb.Append(Colour(mark.Style.Color)).Append(" RG\n");
        sb.Append(Colour(mark.Style.Color)).Append(" rg\n");
        sb.Append(Number(mark.Style.WidthPt)).Append(" w\n");
        // Round caps and joins: a drawn line has no reason to end square, and a
        // mitre on a hand stroke's tight corner spikes far past the ink.
        sb.Append("1 J\n1 j\n");

        AppendPath(sb, pdfOutline);
        sb.Append("S\n");

        if (mark.Kind == AnnotationKind.Arrow && pdfPoints.Count >= 2)
        {
            var (tip, left, right) = AnnotationGeometry.ArrowHead(
                pdfPoints[0], pdfPoints[^1], mark.Style.WidthPt);

            Point(sb, tip).Append(" m\n");
            Point(sb, left).Append(" l\n");
            Point(sb, right).Append(" l\n");
            sb.Append("h\nf\n");
        }

        sb.Append("Q\n");
        return sb.ToString();
    }

    /// <summary>Writes a path out as PDF operators.</summary>
    public static void AppendPath(StringBuilder sb, IReadOnlyList<PathStep> path)
    {
        foreach (var step in path)
        {
            switch (step.Verb)
            {
                case PathVerb.Move:
                    Point(sb, step.A).Append(" m\n");
                    break;
                case PathVerb.Line:
                    Point(sb, step.A).Append(" l\n");
                    break;
                case PathVerb.Cubic:
                    Point(sb, step.A).Append(' ');
                    Point(sb, step.B).Append(' ');
                    Point(sb, step.C).Append(" c\n");
                    break;
                case PathVerb.Close:
                    sb.Append("h\n");
                    break;
            }
        }
    }

    private static StringBuilder Point(StringBuilder sb, Vector2 point) =>
        sb.Append(Number(point.X)).Append(' ').Append(Number(point.Y));

    private static string Colour(AnnotationColor color) =>
        $"{Number(color.R / 255f)} {Number(color.G / 255f)} {Number(color.B / 255f)}";

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
