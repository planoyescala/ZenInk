using System.Numerics;

namespace ZenInk.Core;

/// <summary>What one step of a path does.</summary>
public enum PathVerb
{
    Move,
    Line,
    Cubic,
    Close,
}

/// <summary>
/// One step of an outline. A cubic uses all three points — two controls and an
/// end — and everything else uses only <see cref="A"/>.
/// </summary>
public readonly record struct PathStep(PathVerb Verb, Vector2 A, Vector2 B, Vector2 C)
{
    public static PathStep Move(Vector2 to) => new(PathVerb.Move, to, to, to);

    public static PathStep Line(Vector2 to) => new(PathVerb.Line, to, to, to);

    public static PathStep Cubic(Vector2 control1, Vector2 control2, Vector2 to) =>
        new(PathVerb.Cubic, control1, control2, to);

    public static PathStep Close() => new(PathVerb.Close, Vector2.Zero, Vector2.Zero, Vector2.Zero);

    /// <summary>Where the pen ends up after this step.</summary>
    public Vector2 To => Verb == PathVerb.Cubic ? C : A;
}

/// <summary>
/// Where the lines of a written mark sit.
///
/// The engine has no font, so the width of a line is an estimate — but the
/// <em>baselines</em> are not: they are worked out here and used by both the
/// canvas and the file, so the words land in the same place on screen as they
/// do in the PDF. Only the box around them is approximate, and it is drawn
/// generously so it always holds the text it encloses.
/// </summary>
public static class AnnotationText
{
    /// <summary>Average advance of Helvetica in mixed case, as a fraction of the type size.</summary>
    private const float AverageAdvance = 0.55f;

    /// <summary>Line to line, as a fraction of the type size.</summary>
    public const float LineHeight = 1.25f;

    /// <summary>First baseline below the top of the box, as a fraction of the type size.</summary>
    public const float FirstBaseline = 0.95f;

    /// <summary>An empty text still needs a box big enough to be caught and typed into.</summary>
    private const float MinimumWidthPt = 36f;

    public static IReadOnlyList<string> Lines(string text) =>
        string.IsNullOrEmpty(text) ? [string.Empty] : text.Replace("\r\n", "\n").Split('\n');

    /// <summary>The baseline of line <paramref name="index"/>, below the anchor.</summary>
    public static float BaselineOffset(int index, float fontSizePt) =>
        (FirstBaseline + index * LineHeight) * fontSizePt;

    /// <summary>The box the words occupy, anchored at their top-left corner.</summary>
    public static RectPt Box(Vector2 anchor, string text, float fontSizePt)
    {
        var lines = Lines(text);
        int longest = 0;
        foreach (var line in lines)
        {
            longest = Math.Max(longest, line.Length);
        }

        float width = MathF.Max(MinimumWidthPt, longest * AverageAdvance * fontSizePt);
        float height = MathF.Max(1, lines.Count) * LineHeight * fontSizePt;
        return new RectPt(anchor.X, anchor.Y, width, height);
    }
}

/// <summary>
/// Turns a mark into a path.
///
/// This is the single description of every mark's shape, in sheet space and
/// with the mark's own turn applied. The canvas strokes it, the printer strokes
/// it, and the file gets the same path as PDF operators — so a cloud's bumps
/// cannot come out one way on screen and another on paper, which is exactly the
/// sort of drift that would be found by the person receiving the drawing.
/// </summary>
public static class AnnotationOutline
{
    /// <summary>Bézier constant for a quarter turn of a circle.</summary>
    internal const float Kappa = 0.5523f;

    public static IReadOnlyList<PathStep> Build(
        AnnotationKind kind,
        IReadOnlyList<Vector2> points,
        AnnotationStyle style,
        float rotationDeg,
        string text = "")
    {
        if (points.Count == 0) return [];

        var centre = AnnotationGeometry.Centre(points);
        Vector2 Turn(Vector2 point) => AnnotationGeometry.Rotate(point, centre, rotationDeg);

        switch (kind)
        {
            case AnnotationKind.Note:
                return [PathStep.Move(points[0])];

            // The box round the words: what the reader clicks on, and what
            // becomes the annotation's rectangle in the file.
            case AnnotationKind.FreeText:
                var written = AnnotationText.Box(points[0], text, style.FontSizePt);
                return Closed(
                [
                    new Vector2(written.Left, written.Top),
                    new Vector2(written.Right, written.Top),
                    new Vector2(written.Right, written.Bottom),
                    new Vector2(written.Left, written.Bottom),
                ], Turn);

            case AnnotationKind.Ink:
            case AnnotationKind.Line:
            case AnnotationKind.Polyline:
            case AnnotationKind.Arrow:
                return Open(points, Turn);

            case AnnotationKind.Rectangle:
                return Closed(Corners(points), Turn);

            // A highlight follows the text, so it is a run of boxes and not
            // one: a phrase that wraps is highlighted line by line, the way a
            // marker pen leaves it.
            case AnnotationKind.Highlight:
                return Quads(points, Turn);

            case AnnotationKind.Polygon:
                return Closed(points, Turn);

            case AnnotationKind.Ellipse:
                return Ellipse(points, Turn);

            case AnnotationKind.Cloud:
                return AnnotationGeometry.Cloud(
                    [.. Corners(points).Select(Turn)],
                    BumpRadius(style.WidthPt));

            default:
                return Open(points, Turn);
        }
    }

    /// <summary>
    /// How big a cloud's bumps are. Tied to the line's width so a heavy cloud
    /// does not come out as a fuzzy edge, with a floor that keeps a fine one
    /// recognisable as a cloud rather than a wobble.
    /// </summary>
    public static float BumpRadius(float widthPt) => MathF.Max(5f, widthPt * 3.5f);

    /// <summary>
    /// The vertices of a shape drawn with two corners; a shape drawn vertex by
    /// vertex is already its own outline.
    /// </summary>
    private static IReadOnlyList<Vector2> Corners(IReadOnlyList<Vector2> points)
    {
        if (points.Count != 2) return points;

        var box = RectPt.FromCorners(points[0], points[1]);
        return
        [
            new Vector2(box.Left, box.Top),
            new Vector2(box.Right, box.Top),
            new Vector2(box.Right, box.Bottom),
            new Vector2(box.Left, box.Bottom),
        ];
    }

    /// <summary>
    /// A box per pair of opposite corners, as one path of several closed
    /// figures. An odd point at the end is ignored: half a box is not a box.
    /// </summary>
    private static List<PathStep> Quads(IReadOnlyList<Vector2> points, Func<Vector2, Vector2> turn)
    {
        var steps = new List<PathStep>(points.Count * 3);

        for (int i = 0; i + 1 < points.Count; i += 2)
        {
            var box = RectPt.FromCorners(points[i], points[i + 1]);
            steps.Add(PathStep.Move(turn(new Vector2(box.Left, box.Top))));
            steps.Add(PathStep.Line(turn(new Vector2(box.Right, box.Top))));
            steps.Add(PathStep.Line(turn(new Vector2(box.Right, box.Bottom))));
            steps.Add(PathStep.Line(turn(new Vector2(box.Left, box.Bottom))));
            steps.Add(PathStep.Close());
        }

        return steps;
    }

    private static List<PathStep> Open(IReadOnlyList<Vector2> points, Func<Vector2, Vector2> turn)
    {
        var steps = new List<PathStep>(points.Count) { PathStep.Move(turn(points[0])) };
        for (int i = 1; i < points.Count; i++)
        {
            steps.Add(PathStep.Line(turn(points[i])));
        }
        return steps;
    }

    private static List<PathStep> Closed(IReadOnlyList<Vector2> points, Func<Vector2, Vector2> turn)
    {
        var steps = Open(points, turn);
        steps.Add(PathStep.Close());
        return steps;
    }

    private static List<PathStep> Ellipse(IReadOnlyList<Vector2> points, Func<Vector2, Vector2> turn)
    {
        var box = RectPt.FromCorners(points[0], points.Count > 1 ? points[1] : points[0]);
        float rx = box.Width / 2f;
        float ry = box.Height / 2f;
        float cx = box.Left + rx;
        float cy = box.Top + ry;
        float kx = rx * Kappa;
        float ky = ry * Kappa;

        // Four quarter arcs, clockwise from the right-hand point. Every control
        // point goes through the same turn as the ends, which is what keeps a
        // turned ellipse an ellipse and not a lopsided egg.
        return
        [
            PathStep.Move(turn(new Vector2(cx + rx, cy))),
            PathStep.Cubic(turn(new Vector2(cx + rx, cy + ky)), turn(new Vector2(cx + kx, cy + ry)), turn(new Vector2(cx, cy + ry))),
            PathStep.Cubic(turn(new Vector2(cx - kx, cy + ry)), turn(new Vector2(cx - rx, cy + ky)), turn(new Vector2(cx - rx, cy))),
            PathStep.Cubic(turn(new Vector2(cx - rx, cy - ky)), turn(new Vector2(cx - kx, cy - ry)), turn(new Vector2(cx, cy - ry))),
            PathStep.Cubic(turn(new Vector2(cx + kx, cy - ry)), turn(new Vector2(cx + rx, cy - ky)), turn(new Vector2(cx + rx, cy))),
            PathStep.Close(),
        ];
    }
}
