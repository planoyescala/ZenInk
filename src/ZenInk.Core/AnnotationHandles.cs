//-----------------------------------------------------------------------------------------
// <copyright file="AnnotationHandles.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

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

    /// <summary>
    /// One of the mark's own points. A shape placed corner by corner is
    /// corrected corner by corner — stretching its box moves every vertex at
    /// once, which on a measured room means redrawing it rather than fixing
    /// the one corner that missed.
    /// </summary>
    Vertex,
}

/// <summary>
/// A grip, and which of the mark's points it is when it is one of those.
/// <see cref="Index"/> is −1 for the grips that belong to the frame.
/// </summary>
public readonly record struct MarkGrip(MarkHandle Which, int Index = -1)
{
    public static MarkGrip None => new(MarkHandle.None);

    public bool Exists => Which != MarkHandle.None;

    public static implicit operator MarkHandle(MarkGrip grip) => grip.Which;
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

    /// <summary>
    /// Above this many points a mark is gripped by its frame instead. A traced
    /// outline with forty corners would be forty squares to pick between, and
    /// the one under the pointer would be a guess.
    /// </summary>
    private const int MostVertexGrips = 24;

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
    public static IReadOnlyList<(MarkGrip Grip, Vector2 At)> For(Annotation mark, float rotateOffsetPt = RotateOffsetPt)
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
            return [(new MarkGrip(MarkHandle.Rotate), At(midX, box.Top - rotateOffsetPt))];
        }

        // A shape whose points are its corners is gripped by those corners,
        // and by nothing else. The frame's eight grips would say the shape can
        // only change as a whole, which for a room traced round its walls — or
        // for a measurement of one — is the wrong offer.
        if (HasVertexGrips(mark))
        {
            var grips = new List<(MarkGrip, Vector2)>(mark.Points.Count + 1);
            for (int i = 0; i < mark.Points.Count; i++)
            {
                grips.Add((new MarkGrip(MarkHandle.Vertex, i), AnnotationGeometry.Rotate(mark.Points[i], centre, degrees)));
            }

            grips.Add((new MarkGrip(MarkHandle.Rotate), At(midX, box.Top - rotateOffsetPt)));
            return grips;
        }

        return
        [
            (new MarkGrip(MarkHandle.TopLeft), At(box.Left, box.Top)),
            (new MarkGrip(MarkHandle.Top), At(midX, box.Top)),
            (new MarkGrip(MarkHandle.TopRight), At(box.Right, box.Top)),
            (new MarkGrip(MarkHandle.Right), At(box.Right, midY)),
            (new MarkGrip(MarkHandle.BottomRight), At(box.Right, box.Bottom)),
            (new MarkGrip(MarkHandle.Bottom), At(midX, box.Bottom)),
            (new MarkGrip(MarkHandle.BottomLeft), At(box.Left, box.Bottom)),
            (new MarkGrip(MarkHandle.Left), At(box.Left, midY)),
            (new MarkGrip(MarkHandle.Rotate), At(midX, box.Top - rotateOffsetPt)),
        ];
    }

    /// <summary>
    /// Which marks are gripped point by point: the ones placed that way, and
    /// the two-point ones whose points are their ends. Freehand is not among
    /// them — a stroke has hundreds of points, and a grip on each would be a
    /// wall of squares over the drawing.
    /// </summary>
    public static bool HasVertexGrips(Annotation mark) =>
        CanResize(mark)
        && mark.Points.Count <= MostVertexGrips
        && (Annotation.TakesVertices(mark.Kind)
            || mark.Kind is AnnotationKind.Line or AnnotationKind.Arrow or AnnotationKind.Distance);

    /// <summary>The grip nearest a point, within <paramref name="tolerancePt"/>, or none.</summary>
    public static MarkGrip At(
        Annotation mark, Vector2 sheetPoint, float tolerancePt, float rotateOffsetPt = RotateOffsetPt)
    {
        var best = MarkGrip.None;
        float nearest = tolerancePt;

        foreach (var (grip, at) in For(mark, rotateOffsetPt))
        {
            float distance = Vector2.Distance(at, sheetPoint);
            if (distance > nearest) continue;

            nearest = distance;
            best = grip;
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
    public static Annotation Drag(Annotation mark, MarkGrip grip, Vector2 sheetPoint)
    {
        var handle = grip.Which;
        if (handle == MarkHandle.None || !CanResize(mark)) return mark;

        if (handle == MarkHandle.Rotate)
        {
            return mark.WithRotation(RotationFor(mark, sheetPoint));
        }

        // One corner, and only that one. The pointer is where the grip is
        // seen — with the mark's turn in it — so it comes back out of the turn
        // before it becomes one of the mark's own points.
        if (handle == MarkHandle.Vertex)
        {
            if (grip.Index < 0 || grip.Index >= mark.Points.Count) return mark;

            var moved = mark.Points.ToArray();
            moved[grip.Index] = AnnotationGeometry.Rotate(sheetPoint, mark.Centre, -mark.RotationDeg);
            return mark.WithPoints(moved);
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
