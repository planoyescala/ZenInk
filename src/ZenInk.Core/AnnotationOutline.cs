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

    /// <summary>
    /// The text as lines. A lone carriage return counts too: that is what a
    /// WinUI text box hands back for a line break, so words typed into the
    /// panel would otherwise come out of the file as one long line — right in
    /// the box on screen, wrong in the drawing that was sent on.
    /// </summary>
    public static IReadOnlyList<string> Lines(string text) =>
        string.IsNullOrEmpty(text)
            ? [string.Empty]
            : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

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

            // A stamp is its box. What is inside it is text, placed by the same
            // fitting the signature's own appearance uses, and text is not
            // outline: the reader picks a stamp up by its frame.
            case AnnotationKind.Rectangle:
            case AnnotationKind.Stamp:
                return Closed(Corners(points), Turn);

            // A highlight follows the text, so it is a run of boxes and not
            // one: a phrase that wraps is highlighted line by line, the way a
            // marker pen leaves it.
            case AnnotationKind.Highlight:
                return Quads(points, Turn);

            case AnnotationKind.Polygon:
                return Closed(points, Turn);

            // A measured distance is a dimension line: the segment with a tick
            // across each end, which is what tells it from a line somebody drew
            // and what says where the measurement starts and stops.
            case AnnotationKind.Distance:
                return Dimension(points, style.WidthPt, Turn);

            // Both are closed: a perimeter goes round and back to where it
            // started, and an area is the ground it encloses.
            case AnnotationKind.Perimeter:
            case AnnotationKind.Area:
                return Closed(points, Turn);

            // Two arms and the arc between them, so the corner being measured
            // is the one the reader can see marked rather than one of the two
            // the arms could mean.
            case AnnotationKind.Angle:
                return Corner(points, Turn);

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

    /// <summary>
    /// A dimension line: the measured segment, and a tick across each end. The
    /// ticks are as long as an arrow head would be, so a measurement and an
    /// arrow drawn with the same pen weigh the same on the sheet.
    /// </summary>
    private static List<PathStep> Dimension(
        IReadOnlyList<Vector2> points, float widthPt, Func<Vector2, Vector2> turn)
    {
        var steps = Open(points, turn);
        if (points.Count < 2) return steps;

        var along = points[^1] - points[0];
        if (along.LengthSquared() <= 0) return steps;

        var across = Vector2.Normalize(new Vector2(-along.Y, along.X))
            * (AnnotationGeometry.ArrowHeadLength(widthPt) / 2f);

        foreach (var end in (Vector2[])[points[0], points[^1]])
        {
            steps.Add(PathStep.Move(turn(end - across)));
            steps.Add(PathStep.Line(turn(end + across)));
        }

        return steps;
    }

    /// <summary>
    /// The two arms of an angle and the arc across them. The arc's radius is a
    /// share of the shorter arm, so a wide corner and a tight one are both
    /// marked inside the lines they belong to.
    /// </summary>
    private static List<PathStep> Corner(IReadOnlyList<Vector2> points, Func<Vector2, Vector2> turn)
    {
        var steps = Open(points, turn);
        if (points.Count < 3) return steps;

        var corner = points[1];
        var first = points[0] - corner;
        var second = points[2] - corner;
        float reach = MathF.Min(first.Length(), second.Length());
        if (reach <= 0) return steps;

        float radius = reach * 0.3f;
        float from = AnnotationGeometry.AngleDeg(corner, points[0]);
        float sweep = (float)AnnotationGeometry.CornerAngle(points[0], corner, points[2]);

        // Round the short way: the angle marked has to be the one the arms
        // enclose, not the rest of the circle.
        float turned = ((AnnotationGeometry.AngleDeg(corner, points[2]) - from + 540f) % 360f) - 180f;
        float direction = turned >= 0 ? 1f : -1f;

        const int Steps = 10;
        for (int i = 0; i <= Steps; i++)
        {
            float degrees = from + (direction * sweep * i / Steps);
            float radians = degrees * MathF.PI / 180f;
            var at = corner + new Vector2(MathF.Cos(radians), MathF.Sin(radians)) * radius;
            steps.Add(i == 0 ? PathStep.Move(turn(at)) : PathStep.Line(turn(at)));
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
