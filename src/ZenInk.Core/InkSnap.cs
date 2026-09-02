using System.Numerics;

namespace ZenInk.Core;

/// <summary>
/// Pulls a point onto the drawing's own ink.
///
/// It works on the pixels the reader is looking at rather than on the PDF's
/// path objects, and that is a decision rather than a shortcut. A dense A0 is
/// three million objects; walking them to find the nearest line would cost what
/// a full render costs — seconds — for every position of the pointer, and it
/// would fight the tiles for the one thread PDFium allows. The tile under the
/// pointer is already rasterized, already correct, and already there.
///
/// What that costs is precision: a snap can only be as fine as a pixel at the
/// zoom in force. That is the same limit the reader is aiming with, and it is
/// the same bargain they make on paper — you lean in to measure a corner.
/// </summary>
public static class InkSnap
{
    /// <summary>
    /// How dark a pixel has to be, out of 255, to count as a line rather than
    /// as paper. Plans are drawn on white with grey grids and pale hatches over
    /// it; this sits above the hatching and below anything that reads as a
    /// line.
    /// </summary>
    public const int InkThreshold = 96;

    /// <summary>
    /// Where a point should go: onto a corner if the ink around it makes one,
    /// otherwise onto the nearest ink, and nowhere at all if there is none.
    ///
    /// The corner wins because it is what a reader aiming near two walls meant,
    /// and because the nearest ink to that same click is a point somewhere
    /// along one of the two — which is exactly the error a measurement should
    /// not carry.
    /// </summary>
    public static Vector2? Snap(
        ReadOnlySpan<byte> bgra, int width, int height, Vector2 at, int radius, int threshold = InkThreshold)
    {
        var nearest = Nearest(bgra, width, height, at, radius, threshold);
        if (nearest is not { } ink) return null;

        if (Corner(bgra, width, height, at, radius, threshold) is not { } corner) return ink;

        // The corner wins only when it is about as close as the ink itself —
        // which is what "the reader was aiming at the corner" looks like in
        // numbers. Aiming halfway along a wall, the corner is far and the wall
        // is under the pointer, and dragging the point twenty pixels down the
        // wall to reach a corner nobody was pointing at would be the snap
        // making the measurement up.
        float toInk = Vector2.Distance(ink, at);
        float toCorner = Vector2.Distance(corner, at);

        return toCorner <= Math.Max((toInk * 1.6f) + 2f, 4f) ? corner : ink;
    }

    /// <summary>
    /// The nearest ink to <paramref name="at"/> within <paramref name="radius"/>
    /// pixels, or null when there is nothing but paper around it.
    ///
    /// Nearest, and among equals the darkest: a hairline beside a heavy wall
    /// should not win just because the scan reached it first, and the wall is
    /// what the reader was aiming at.
    /// </summary>
    public static Vector2? Nearest(
        ReadOnlySpan<byte> bgra, int width, int height, Vector2 at, int radius, int threshold = InkThreshold)
    {
        if (width <= 0 || height <= 0 || radius <= 0) return null;

        int cx = (int)MathF.Round(at.X);
        int cy = (int)MathF.Round(at.Y);

        float nearest = float.MaxValue;
        int darkest = 256;
        Vector2? found = null;

        for (int y = Math.Max(0, cy - radius); y <= Math.Min(height - 1, cy + radius); y++)
        {
            int row = y * width;
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(width - 1, cx + radius); x++)
            {
                int at4 = (row + x) * 4;
                if (at4 + 2 >= bgra.Length) continue;

                int luma = ((bgra[at4 + 2] * 299) + (bgra[at4 + 1] * 587) + (bgra[at4] * 114)) / 1000;
                if (luma > threshold) continue;

                float dx = x - at.X;
                float dy = y - at.Y;
                float distance = (dx * dx) + (dy * dy);
                if (distance > radius * radius) continue;

                // A hair nearer wins outright; at the same distance, the darker
                // pixel is the one the reader meant.
                if (distance > nearest + 0.01f) continue;
                if (distance > nearest - 0.01f && luma >= darkest) continue;

                nearest = distance;
                darkest = luma;
                found = new Vector2(x, y);
            }
        }

        return found;
    }

    /// <summary>
    /// Where a corner is, when the ink around a point makes one.
    ///
    /// Two walls meeting is what a reader is nearly always aiming at, and the
    /// nearest ink to a click near a corner is a point along one of the two
    /// walls rather than the corner itself. This looks at the ink inside a
    /// small box and, if it spreads along both axes rather than lying in a
    /// line, returns where the two directions cross.
    ///
    /// It returns null rather than guessing: a snap that invents a corner in
    /// the middle of a straight wall is worse than no snap, because the number
    /// that comes out of it looks just as confident.
    /// </summary>
    public static Vector2? Corner(
        ReadOnlySpan<byte> bgra, int width, int height, Vector2 at, int radius, int threshold = InkThreshold)
    {
        if (width <= 0 || height <= 0 || radius <= 1) return null;

        int left = Math.Max(0, (int)at.X - radius);
        int right = Math.Min(width - 1, (int)at.X + radius);
        int top = Math.Max(0, (int)at.Y - radius);
        int bottom = Math.Min(height - 1, (int)at.Y + radius);

        // The ink's spread along each axis, and where it sits. Two runs that
        // both span most of the box are a crossing; one that spans it alone is
        // a straight line, and the corner is somebody else's business.
        int inkLeft = int.MaxValue, inkRight = int.MinValue;
        int inkTop = int.MaxValue, inkBottom = int.MinValue;
        long sumX = 0, sumY = 0, count = 0;

        for (int y = top; y <= bottom; y++)
        {
            int row = y * width;
            for (int x = left; x <= right; x++)
            {
                int at4 = (row + x) * 4;
                if (at4 + 2 >= bgra.Length) continue;

                int luma = ((bgra[at4 + 2] * 299) + (bgra[at4 + 1] * 587) + (bgra[at4] * 114)) / 1000;
                if (luma > threshold) continue;

                inkLeft = Math.Min(inkLeft, x);
                inkRight = Math.Max(inkRight, x);
                inkTop = Math.Min(inkTop, y);
                inkBottom = Math.Max(inkBottom, y);
                sumX += x;
                sumY += y;
                count++;
            }
        }

        if (count < 3 || inkLeft > inkRight) return null;

        int spreadX = inkRight - inkLeft;
        int spreadY = inkBottom - inkTop;
        int reach = radius;

        // Both directions have to be there for it to be a corner at all.
        if (spreadX < reach || spreadY < reach) return null;

        // Where the ink's own middle is not: a crossing's arms pull the middle
        // towards the corner, so the corner is the far end of each run from the
        // emptier side. Taken as the box's own corner nearest that middle,
        // which is exactly the vertex where the two arms meet.
        double middleX = (double)sumX / count;
        double middleY = (double)sumY / count;

        float cornerX = middleX - inkLeft < inkRight - middleX ? inkLeft : inkRight;
        float cornerY = middleY - inkTop < inkBottom - middleY ? inkTop : inkBottom;
        var corner = new Vector2(cornerX, cornerY);

        // And there has to be ink at it. A line running diagonally spreads
        // along both axes exactly as a corner does, and its box's corner is a
        // point in empty space — which would be a measurement taken from
        // somewhere the drawing has nothing.
        return Nearest(bgra, width, height, corner, 2, threshold) is not null ? corner : null;
    }
}
