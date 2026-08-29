using System.Numerics;

namespace ZenInk.Core;

/// <summary>Which grip of a selected mark is being pulled.</summary>
public enum MarkHandle
{
    None,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,

    /// <summary>The knob that turns the mark on the spot.</summary>
    Rotate,
}

/// <summary>
/// The grips round a selected mark, and what pulling one does to it.
///
/// The frame is the mark's own, not the sheet's: the grips sit on the corners
/// of the box the mark was drawn in, turned with it. That is what makes
/// stretching a turned box widen it along its own edge instead of shearing it
/// across the sheet.
///
/// It lives in the engine because it is arithmetic, and because getting it
/// wrong is the kind of thing that only shows up as a mark quietly sliding
/// away from the drawing it was pointing at.
/// </summary>
public static class AnnotationHandles
{
    /// <summary>How far above the mark the turn knob floats, in points.</summary>
    public const float RotateOffsetPt = 22f;

    /// <summary>Nothing may be squeezed below this, in points, or it could never be caught again.</summary>
    private const float MinimumSizePt = 4f;

    /// <summary>Which marks can be dragged somewhere else at all.</summary>
    public static bool CanMove(Annotation mark) => Annotation.CanBeChanged(mark.Kind);

    /// <summary>
    /// Which marks can be stretched. A note is an icon, not a shape; a written
    /// mark is sized by its type, not by pulling at it.
    /// </summary>
    public static bool CanResize(Annotation mark) =>
        CanMove(mark)
        && mark.Kind is not (AnnotationKind.Note or AnnotationKind.FreeText)
        && mark.Points.Count >= 2;

    /// <summary>
    /// Which marks can be turned. Written marks can, which is what a label
    /// running along a diagonal needs; an icon cannot, since it has no
    /// direction to speak of.
    /// </summary>
    public static bool CanRotate(Annotation mark) =>
        CanMove(mark) && mark.Kind != AnnotationKind.Note;

    /// <summary>
    /// Every grip, in sheet space, already turned with the mark. The offset is
    /// in points, so the caller decides how far the knob floats at the zoom
    /// in force.
    /// </summary>
    public static IReadOnlyList<(MarkHandle Which, Vector2 At)> For(Annotation mark, float rotateOffsetPt = RotateOffsetPt)
    {
        if (!CanRotate(mark)) return [];

        var box = Frame(mark);
        var centre = mark.Centre;
        float degrees = mark.RotationDeg;

        Vector2 At(float x, float y) => AnnotationGeometry.Rotate(new Vector2(x, y), centre, degrees);

        float midX = (box.Left + box.Right) / 2f;
        float midY = (box.Top + box.Bottom) / 2f;

        // A mark that cannot be stretched still gets its knob: the words of a
        // label may want to run along a diagonal even though pulling at them
        // would mean nothing.
        if (!CanResize(mark))
        {
            return [(MarkHandle.Rotate, At(midX, box.Top - rotateOffsetPt))];
        }

        return
        [
            (MarkHandle.TopLeft, At(box.Left, box.Top)),
            (MarkHandle.Top, At(midX, box.Top)),
            (MarkHandle.TopRight, At(box.Right, box.Top)),
            (MarkHandle.Right, At(box.Right, midY)),
            (MarkHandle.BottomRight, At(box.Right, box.Bottom)),
            (MarkHandle.Bottom, At(midX, box.Bottom)),
            (MarkHandle.BottomLeft, At(box.Left, box.Bottom)),
            (MarkHandle.Left, At(box.Left, midY)),
            (MarkHandle.Rotate, At(midX, box.Top - rotateOffsetPt)),
        ];
    }

    /// <summary>The grip nearest a point, within <paramref name="tolerancePt"/>, or none.</summary>
    public static MarkHandle At(
        Annotation mark, Vector2 sheetPoint, float tolerancePt, float rotateOffsetPt = RotateOffsetPt)
    {
        var best = MarkHandle.None;
        float nearest = tolerancePt;

        foreach (var (which, at) in For(mark, rotateOffsetPt))
        {
            float distance = Vector2.Distance(at, sheetPoint);
            if (distance > nearest) continue;

            nearest = distance;
            best = which;
        }

        return best;
    }

    /// <summary>
    /// The mark's own box before its turn — what the grips are placed on. It is
    /// not <see cref="Annotation.Bounds"/>, which is the turned outline and
    /// would grow every time the mark turned.
    /// </summary>
    public static RectPt Frame(Annotation mark) => mark.FrameBox;

    /// <summary>
    /// The mark as it is once a grip has been dragged to
    /// <paramref name="sheetPoint"/>.
    ///
    /// The corner opposite the grip stays where it is on the sheet — including
    /// on a turned mark, where scaling moves the middle the turn happens about
    /// and would otherwise slide the whole thing sideways.
    /// </summary>
    public static Annotation Drag(Annotation mark, MarkHandle handle, Vector2 sheetPoint)
    {
        if (handle == MarkHandle.None || !CanResize(mark)) return mark;

        if (handle == MarkHandle.Rotate)
        {
            return mark.WithRotation(RotationFor(mark, sheetPoint));
        }

        var box = Frame(mark);
        var centre = mark.Centre;

        // Work in the mark's own frame: undo its turn, stretch, then let the
        // turn apply again.
        var target = AnnotationGeometry.Rotate(sheetPoint, centre, -mark.RotationDeg);

        (float anchorX, float movingX) = HorizontalGrip(handle) switch
        {
            -1 => (box.Right, box.Left),
            1 => (box.Left, box.Right),
            _ => (box.Left, box.Right),
        };

        (float anchorY, float movingY) = VerticalGrip(handle) switch
        {
            -1 => (box.Bottom, box.Top),
            1 => (box.Top, box.Bottom),
            _ => (box.Top, box.Bottom),
        };

        float scaleX = HorizontalGrip(handle) == 0 ? 1f : Scale(target.X, anchorX, movingX, box.Width);
        float scaleY = VerticalGrip(handle) == 0 ? 1f : Scale(target.Y, anchorY, movingY, box.Height);

        var anchor = new Vector2(anchorX, anchorY);
        var anchorBefore = AnnotationGeometry.Rotate(anchor, centre, mark.RotationDeg);

        var stretched = mark.Scaled(anchor, scaleX, scaleY);
        var anchorAfter = AnnotationGeometry.Rotate(anchor, stretched.Centre, mark.RotationDeg);

        return stretched.MovedBy(anchorBefore - anchorAfter);
    }

    /// <summary>
    /// The turn that puts the knob under the pointer. The knob sits above the
    /// mark's top edge, so a pointer straight above the middle means no turn at
    /// all.
    /// </summary>
    public static float RotationFor(Annotation mark, Vector2 sheetPoint) =>
        AnnotationGeometry.AngleDeg(mark.Centre, sheetPoint) + 90f;

    /// <summary>Rounds a turn to the nearest step, for dragging with Shift held.</summary>
    public static float Snap(float degrees, float stepDeg = 15f) =>
        stepDeg <= 0 ? degrees : MathF.Round(degrees / stepDeg) * stepDeg;

    private static float Scale(float target, float anchor, float moving, float size)
    {
        float span = moving - anchor;
        if (MathF.Abs(span) < 0.001f) return 1f;

        float scale = (target - anchor) / span;

        // Never through zero: a mark dragged inside out would flip, and a mark
        // squeezed to nothing could not be caught again.
        float smallest = size <= 0.001f ? 1f : MinimumSizePt / size;
        return MathF.Max(scale, smallest);
    }

    private static int HorizontalGrip(MarkHandle handle) => handle switch
    {
        MarkHandle.TopLeft or MarkHandle.Left or MarkHandle.BottomLeft => -1,
        MarkHandle.TopRight or MarkHandle.Right or MarkHandle.BottomRight => 1,
        _ => 0,
    };

    private static int VerticalGrip(MarkHandle handle) => handle switch
    {
        MarkHandle.TopLeft or MarkHandle.Top or MarkHandle.TopRight => -1,
        MarkHandle.BottomLeft or MarkHandle.Bottom or MarkHandle.BottomRight => 1,
        _ => 0,
    };
}
