using System.Numerics;

namespace ZenInk.Core;

/// <summary>
/// The arithmetic behind the marks: distances for hit-testing, the arrow head,
/// the bumps of a revision cloud, thinning a captured stroke, and the
/// quarter-turn that separates sheet space from what the reader has on screen.
///
/// All of it is pure, which is the point: the same numbers decide where a mark
/// is drawn on the canvas, where it lands on paper, and what goes into the file.
/// </summary>
public static class AnnotationGeometry
{
    /// <summary>How finely a curve is chopped up when it has to be treated as straight lines.</summary>
    private const int FlattenSteps = 10;

    public static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float lengthSquared = ab.LengthSquared();
        if (lengthSquared <= float.Epsilon) return Vector2.Distance(point, a);

        float t = Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0f, 1f);
        return Vector2.Distance(point, a + ab * t);
    }

    /// <summary>The middle of a set of points, by their box — what a mark turns about.</summary>
    public static Vector2 Centre(IReadOnlyList<Vector2> points)
    {
        if (points.Count == 0) return Vector2.Zero;

        float left = points[0].X, right = left, top = points[0].Y, bottom = top;
        for (int i = 1; i < points.Count; i++)
        {
            left = MathF.Min(left, points[i].X);
            right = MathF.Max(right, points[i].X);
            top = MathF.Min(top, points[i].Y);
            bottom = MathF.Max(bottom, points[i].Y);
        }
        return new Vector2((left + right) / 2f, (top + bottom) / 2f);
    }

    /// <summary>Turns a point about another, clockwise in degrees — y runs down here.</summary>
    public static Vector2 Rotate(Vector2 point, Vector2 about, float degrees)
    {
        if (degrees == 0f) return point;

        float radians = degrees * MathF.PI / 180f;
        float cos = MathF.Cos(radians), sin = MathF.Sin(radians);
        var offset = point - about;

        return new Vector2(
            about.X + offset.X * cos - offset.Y * sin,
            about.Y + offset.X * sin + offset.Y * cos);
    }

    /// <summary>The angle from one point to another, in degrees clockwise from the x axis.</summary>
    public static float AngleDeg(Vector2 from, Vector2 to)
    {
        var offset = to - from;
        return MathF.Atan2(offset.Y, offset.X) * 180f / MathF.PI;
    }

    // --- measuring -------------------------------------------------------

    /// <summary>
    /// The run of segments end to end, on the paper. What a measurement turns
    /// into a real length by way of the sheet's scale.
    /// </summary>
    public static double TotalLength(IReadOnlyList<Vector2> points, bool closed = false)
    {
        double total = 0;
        for (int i = 1; i < points.Count; i++)
        {
            total += (points[i] - points[i - 1]).Length();
        }

        if (closed && points.Count > 2)
        {
            total += (points[0] - points[^1]).Length();
        }

        return total;
    }

    /// <summary>
    /// The area a closed run of points encloses, on the paper. The shoelace
    /// sum, taken as an absolute: which way round the reader traced the room is
    /// not something they should have to think about.
    ///
    /// A shape that crosses itself is not defended against, because the answer
    /// to one is not a number: it is a shape to be redrawn, and it is visible
    /// as such.
    /// </summary>
    public static double PolygonArea(IReadOnlyList<Vector2> points)
    {
        if (points.Count < 3) return 0;

        double twice = 0;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            twice += ((double)points[j].X * points[i].Y) - ((double)points[i].X * points[j].Y);
        }

        return Math.Abs(twice) / 2.0;
    }

    /// <summary>
    /// The angle at <paramref name="corner"/> between the two arms, from 0 to
    /// 180 degrees. Always the angle that is drawn: a reader measuring a corner
    /// wants what they can see between the two lines, never its reflex twin.
    /// </summary>
    public static double CornerAngle(Vector2 from, Vector2 corner, Vector2 to)
    {
        var a = from - corner;
        var b = to - corner;
        double lengths = (double)a.Length() * b.Length();
        if (lengths <= 0) return 0;

        double cosine = Math.Clamp((((double)a.X * b.X) + ((double)a.Y * b.Y)) / lengths, -1.0, 1.0);
        return Math.Acos(cosine) * 180.0 / Math.PI;
    }

    // --- paths -----------------------------------------------------------

    /// <summary>
    /// A path as straight segments, one list per figure. Curves are chopped up,
    /// which is all that hit-testing and bounding need — and it means one
    /// routine serves every kind of mark instead of one per shape.
    ///
    /// Figures are kept apart because a highlight is several boxes in one mark:
    /// run together, the jump from one box to the next would be tested as an
    /// edge, and a click in the gap between two lines of text would land on it.
    /// </summary>
    public static List<List<Vector2>> Figures(IReadOnlyList<PathStep> path)
    {
        var figures = new List<List<Vector2>>();
        var points = new List<Vector2>();
        var current = Vector2.Zero;
        var start = Vector2.Zero;

        foreach (var step in path)
        {
            switch (step.Verb)
            {
                case PathVerb.Move:
                    if (points.Count > 0)
                    {
                        figures.Add(points);
                        points = [];
                    }
                    current = start = step.A;
                    points.Add(current);
                    break;

                case PathVerb.Line:
                    current = step.A;
                    points.Add(current);
                    break;

                case PathVerb.Cubic:
                    for (int i = 1; i <= FlattenSteps; i++)
                    {
                        points.Add(CubicAt(current, step.A, step.B, step.C, i / (float)FlattenSteps));
                    }
                    current = step.C;
                    break;

                case PathVerb.Close:
                    points.Add(start);
                    current = start;
                    break;
            }
        }

        if (points.Count > 0) figures.Add(points);
        return figures;
    }

    /// <summary>Every point of the path, figures run together. For bounds, where the joins do not matter.</summary>
    public static List<Vector2> Flatten(IReadOnlyList<PathStep> path)
    {
        var all = new List<Vector2>(path.Count * 2);
        foreach (var figure in Figures(path))
        {
            all.AddRange(figure);
        }
        return all;
    }

    private static Vector2 CubicAt(Vector2 from, Vector2 c1, Vector2 c2, Vector2 to, float t)
    {
        float u = 1f - t;
        return (u * u * u * from) + (3f * u * u * t * c1) + (3f * u * t * t * c2) + (t * t * t * to);
    }

    /// <summary>Whether a point is within <paramref name="tolerance"/> of the path itself.</summary>
    public static bool NearPath(IReadOnlyList<PathStep> path, Vector2 point, float tolerance)
    {
        foreach (var figure in Figures(path))
        {
            if (figure.Count == 1)
            {
                if (Vector2.Distance(point, figure[0]) <= tolerance) return true;
                continue;
            }

            for (int i = 1; i < figure.Count; i++)
            {
                if (DistanceToSegment(point, figure[i - 1], figure[i]) <= tolerance) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a point falls inside a closed path — the even-odd ray test, run
    /// figure by figure, so a mark made of several boxes is inside any one of
    /// them and outside the gaps between.
    /// </summary>
    public static bool InsidePath(IReadOnlyList<PathStep> path, Vector2 point)
    {
        bool inside = false;
        foreach (var figure in Figures(path))
        {
            if (InsidePolygon(figure, point)) inside = !inside;
        }
        return inside;
    }

    public static bool InsidePolygon(IReadOnlyList<Vector2> polygon, Vector2 point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];

            if (a.Y > point.Y != b.Y > point.Y
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    public static bool InsideTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c) =>
        InsidePolygon([a, b, c], point);

    /// <summary>The box a path covers, curves included.</summary>
    public static RectPt BoundsOf(IReadOnlyList<PathStep> path) => Union(null, Flatten(path));

    /// <summary>Grows a box to take in some points; a null box starts from them.</summary>
    public static RectPt Union(RectPt? box, IReadOnlyList<Vector2> points)
    {
        if (points.Count == 0) return box ?? new RectPt(0, 0, 0, 0);

        float left = box?.Left ?? points[0].X;
        float right = box?.Right ?? points[0].X;
        float top = box?.Top ?? points[0].Y;
        float bottom = box?.Bottom ?? points[0].Y;

        foreach (var point in points)
        {
            left = MathF.Min(left, point.X);
            right = MathF.Max(right, point.X);
            top = MathF.Min(top, point.Y);
            bottom = MathF.Max(bottom, point.Y);
        }

        return new RectPt(left, top, right - left, bottom - top);
    }

    // --- shapes ----------------------------------------------------------

    /// <summary>
    /// How far back from the tip an arrow head reaches. Tied to the stroke
    /// width so a heavy arrow does not end in a pinhead, with a floor so a
    /// hairline one still reads as an arrow.
    /// </summary>
    public static float ArrowHeadLength(float widthPt) => MathF.Max(7f, widthPt * 4.5f);

    /// <summary>
    /// The three corners of the head at <paramref name="tip"/>, pointing away
    /// from <paramref name="from"/>: tip first, then the two barbs.
    /// </summary>
    public static (Vector2 Tip, Vector2 Left, Vector2 Right) ArrowHead(Vector2 from, Vector2 tip, float widthPt)
    {
        var direction = tip - from;
        float length = direction.Length();
        if (length <= float.Epsilon)
        {
            direction = new Vector2(1f, 0f);
        }
        else
        {
            direction /= length;
        }

        float head = ArrowHeadLength(widthPt);
        var perpendicular = new Vector2(-direction.Y, direction.X) * (head * 0.42f);
        var basePoint = tip - direction * head;

        return (tip, basePoint + perpendicular, basePoint - perpendicular);
    }

    /// <summary>
    /// A revision cloud round a closed outline: each edge is walked in steps of
    /// about two radii, and every step becomes a half-circle bulging away from
    /// the inside.
    ///
    /// Which way is "away" is decided per edge by asking whether the bump's
    /// crown would land inside the outline, rather than by the winding of the
    /// points. The reader draws the box either way round, and a cloud whose
    /// bumps face inwards is not a cloud.
    /// </summary>
    public static List<PathStep> Cloud(IReadOnlyList<Vector2> outline, float bumpRadius)
    {
        if (outline.Count < 2 || bumpRadius <= 0.01f)
        {
            var fallback = new List<PathStep>();
            if (outline.Count > 0) fallback.Add(PathStep.Move(outline[0]));
            for (int i = 1; i < outline.Count; i++) fallback.Add(PathStep.Line(outline[i]));
            if (outline.Count > 2) fallback.Add(PathStep.Close());
            return fallback;
        }

        var steps = new List<PathStep> { PathStep.Move(outline[0]) };

        for (int edge = 0; edge < outline.Count; edge++)
        {
            var from = outline[edge];
            var to = outline[(edge + 1) % outline.Count];

            var along = to - from;
            float length = along.Length();
            if (length <= 0.01f) continue;

            along /= length;
            var normal = new Vector2(-along.Y, along.X);

            // Outward is whichever side of this edge is not the inside.
            var probe = (from + to) / 2f + normal * bumpRadius;
            if (InsidePolygon(outline, probe)) normal = -normal;

            int bumps = Math.Max(1, (int)MathF.Round(length / (bumpRadius * 2f)));
            float step = length / bumps;

            for (int i = 0; i < bumps; i++)
            {
                var a = from + along * (step * i);
                var b = from + along * (step * (i + 1));
                Bump(steps, a, b, normal);
            }
        }

        steps.Add(PathStep.Close());
        return steps;
    }

    /// <summary>One bump: the half-circle from a to b that bulges along the normal.</summary>
    private static void Bump(List<PathStep> steps, Vector2 a, Vector2 b, Vector2 normal)
    {
        var middle = (a + b) / 2f;
        float radius = Vector2.Distance(a, b) / 2f;
        if (radius <= 0.01f)
        {
            steps.Add(PathStep.Line(b));
            return;
        }

        var crown = middle + normal * radius;

        // Two quarter turns: a to the crown, the crown to b. On a circle the
        // tangent at one point is the position vector a quarter turn further
        // on, so travelling a -> crown -> b the tangents are the normal, the
        // way on to b, and the normal reversed.
        var onToB = (b - middle) / radius;
        float pull = radius * AnnotationOutline.Kappa;

        steps.Add(PathStep.Cubic(a + normal * pull, crown - onToB * pull, crown));
        steps.Add(PathStep.Cubic(crown + onToB * pull, b + normal * pull, b));
    }

    /// <summary>
    /// Drops the points a stroke does not need — Ramer–Douglas–Peucker, keeping
    /// every point that sits more than <paramref name="tolerancePt"/> off the
    /// line its neighbours would draw.
    ///
    /// A pen reports hundreds of samples for a gesture that lasts half a
    /// second. All of them would be written into the file, drawn on every
    /// frame, and walked on every hit test, for a curve the eye cannot tell
    /// from the thinned one.
    /// </summary>
    public static IReadOnlyList<Vector2> Simplify(IReadOnlyList<Vector2> points, float tolerancePt)
    {
        if (points.Count <= 2) return points;

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;

        var pending = new Stack<(int Start, int End)>();
        pending.Push((0, points.Count - 1));

        while (pending.Count > 0)
        {
            var (start, end) = pending.Pop();
            if (end <= start + 1) continue;

            float worst = 0f;
            int worstIndex = -1;
            for (int i = start + 1; i < end; i++)
            {
                float distance = DistanceToSegment(points[i], points[start], points[end]);
                if (distance > worst)
                {
                    worst = distance;
                    worstIndex = i;
                }
            }

            if (worstIndex < 0 || worst <= tolerancePt) continue;

            keep[worstIndex] = true;
            pending.Push((start, worstIndex));
            pending.Push((worstIndex, end));
        }

        var kept = new List<Vector2>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i]) kept.Add(points[i]);
        }
        return kept;
    }
}

/// <summary>
/// The reader's quarter-turn, as a mapping between sheet space — the page the
/// way the file draws it — and the page as it currently sits on screen.
///
/// The numbers match what PDFium does with the same turn: a sheet W×H drawn
/// with one clockwise quarter-turn comes out H×W, with the sheet's top edge
/// running down its right side.
/// </summary>
public static class SheetTurn
{
    /// <summary>The box a W×H sheet occupies once turned.</summary>
    public static (float Width, float Height) Size(float widthPt, float heightPt, int quarterTurns) =>
        (quarterTurns & 1) == 1 ? (heightPt, widthPt) : (widthPt, heightPt);

    /// <summary>Sheet space to the turned page's own space.</summary>
    public static Vector2 ToDisplay(Vector2 point, float widthPt, float heightPt, int quarterTurns) =>
        (quarterTurns & 3) switch
        {
            1 => new Vector2(heightPt - point.Y, point.X),
            2 => new Vector2(widthPt - point.X, heightPt - point.Y),
            3 => new Vector2(point.Y, widthPt - point.X),
            _ => point,
        };

    /// <summary>The turned page's own space back to sheet space.</summary>
    public static Vector2 ToSheet(Vector2 point, float widthPt, float heightPt, int quarterTurns) =>
        (quarterTurns & 3) switch
        {
            1 => new Vector2(point.Y, heightPt - point.X),
            2 => new Vector2(widthPt - point.X, heightPt - point.Y),
            3 => new Vector2(widthPt - point.Y, point.X),
            _ => point,
        };

    /// <summary>Turns a whole box, which stays axis-aligned under quarter turns.</summary>
    public static RectPt ToDisplay(RectPt box, float widthPt, float heightPt, int quarterTurns) =>
        RectPt.FromCorners(
            ToDisplay(new Vector2(box.Left, box.Top), widthPt, heightPt, quarterTurns),
            ToDisplay(new Vector2(box.Right, box.Bottom), widthPt, heightPt, quarterTurns));
}
