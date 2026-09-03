//-----------------------------------------------------------------------------------------
// <copyright file="Annotation.cs" company="plano y escala">
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

/// <summary>
/// What a mark is. The kind decides how <see cref="Annotation.Points"/> is
/// read, how it is drawn, and which PDF annotation subtype carries it into the
/// file.
/// </summary>
public enum AnnotationKind
{
    /// <summary>Freehand: every point of the stroke, in order.</summary>
    Ink,

    /// <summary>A straight segment: two points.</summary>
    Line,

    /// <summary>A straight segment with a head on the second point.</summary>
    Arrow,

    /// <summary>An open run of straight segments through every point.</summary>
    Polyline,

    /// <summary>An outlined box: two opposite corners.</summary>
    Rectangle,

    /// <summary>An outlined ellipse inscribed in the box of two opposite corners.</summary>
    Ellipse,

    /// <summary>A closed run of straight segments through every point.</summary>
    Polygon,

    /// <summary>
    /// A revision cloud: the same outline as a polygon, drawn as a run of
    /// bumps. Two points make the cloud round a box, which is how one is drawn
    /// nine times out of ten.
    /// </summary>
    Cloud,

    /// <summary>A translucent wash over the text it was dragged across.</summary>
    Highlight,

    /// <summary>
    /// Words written straight onto the drawing, anchored at one point. Unlike a
    /// note, which is an icon you open, this one is read where it sits.
    /// </summary>
    FreeText,

    /// <summary>A comment anchored at one point; the text is the annotation.</summary>
    Note,

    /// <summary>
    /// A straight segment that says how long it is. The same two points as a
    /// line; what makes it a measurement is that its text is worked out from
    /// them and the sheet's scale rather than typed.
    /// </summary>
    Distance,

    /// <summary>A run of segments, closed back to the start, that says how far round it is.</summary>
    Perimeter,

    /// <summary>A closed run of segments that says how much it encloses.</summary>
    Area,

    /// <summary>
    /// Three points — an arm, the corner, the other arm — that say what the
    /// angle between them is. The one measurement with no scale in it.
    /// </summary>
    Angle,

    /// <summary>
    /// A box with a few lines of text in it, sized to fit: the shape a
    /// signature block takes on a drawing.
    ///
    /// It is a mark and not a signature, and the difference is the whole point
    /// of it. Nothing is sealed, nothing is verified, and anyone can move it or
    /// rub it out — which is exactly what is wanted on a drawing that is being
    /// reviewed rather than issued.
    /// </summary>
    Stamp,
}

/// <summary>A mark's colour, as the PDF carries it: three channels, no alpha.</summary>
public readonly record struct AnnotationColor(byte R, byte G, byte B)
{
    public static AnnotationColor Red => new(216, 32, 32);

    public uint Packed => ((uint)R << 16) | ((uint)G << 8) | B;

    public static AnnotationColor FromPacked(uint value) =>
        new((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
}

/// <summary>
/// How a mark is drawn: the line, and the fill inside it.
///
/// <see cref="Fill"/> is absent when the shape is a bare outline. The fill's
/// opacity is its own value because that is what a fill is for on a drawing —
/// a solid block would bury the very lines the mark is pointing at.
/// </summary>
public readonly record struct AnnotationStyle(
    AnnotationColor Color,
    float WidthPt,
    AnnotationColor? Fill = null,
    float FillOpacity = 0.35f,
    float FontSizePt = 12f)
{
    public static AnnotationStyle Default => new(AnnotationColor.Red, 2f);

    /// <summary>The fill's alpha as PDF and Win2D want it, 0–255.</summary>
    public byte FillAlpha => (byte)Math.Clamp((int)MathF.Round(FillOpacity * 255f), 0, 255);
}

/// <summary>
/// One mark on one sheet.
///
/// Coordinates are "sheet space": PDF points, y down, origin at the top-left of
/// the page <em>as the file renders it</em> — the file's own /Rotate applied,
/// the reader's turn not. That is deliberate. A turn is a way of looking at the
/// sheet, so ink drawn before a turn has to stay on the same lines afterwards;
/// keeping the reader's turn out of the stored geometry is what makes that true
/// by construction rather than by fixing up coordinates on every turn.
///
/// <see cref="RotationDeg"/> is the mark's own turn, about the middle of its
/// points. It is kept apart from the points so that a box stays a box: turning
/// one and turning it back has to leave the same two corners, not four drifting
/// ones.
///
/// Marks are immutable: editing one produces another with the same
/// <see cref="Id"/>, which is what lets undo hold the previous version without
/// copying defensively.
/// </summary>
public sealed class Annotation
{
    /// <summary>The note icon's box, in points. Fixed on the sheet, like a PDF note.</summary>
    public const float NoteSizePt = 18f;

    /// <summary>How close a click has to be to count as hitting a mark, on top of its own width.</summary>
    public const float HitSlopPt = 3f;

    public Annotation(
        AnnotationKind kind,
        IReadOnlyList<Vector2> points,
        AnnotationStyle style,
        string text = "",
        string author = "",
        Guid? id = null,
        DateTimeOffset? created = null,
        float rotationDeg = 0f,
        SheetScale? scale = null)
    {
        Kind = kind;
        Points = points;
        Style = style;
        Author = author ?? string.Empty;
        Id = id ?? Guid.NewGuid();
        Created = created ?? DateTimeOffset.Now;
        RotationDeg = Normalise(rotationDeg);
        Scale = Measures.Is(kind) ? scale : null;

        // A measurement's words are its number, and they are worked out here
        // rather than passed in. That is the whole of what keeps a measurement
        // honest: dragging one of its ends makes a new mark, and a new mark
        // recomputes — there is no path by which the line says one thing and
        // the number another.
        Text = Measures.Is(kind) ? Measures.Label(kind, points, Scale) : text ?? string.Empty;

        Outline = AnnotationOutline.Build(kind, points, style, RotationDeg, Text);
        Bounds = ComputeBounds();
    }

    public Guid Id { get; }

    public AnnotationKind Kind { get; }

    /// <summary>The mark's geometry in sheet space; what it means depends on <see cref="Kind"/>.</summary>
    public IReadOnlyList<Vector2> Points { get; }

    public AnnotationStyle Style { get; }

    /// <summary>The mark's own turn, in degrees clockwise about the middle of its points.</summary>
    public float RotationDeg { get; }

    /// <summary>The comment's text, or a measurement's number.</summary>
    public string Text { get; }

    /// <summary>
    /// The sheet's scale as it stood when this measurement was made, and null
    /// for everything else.
    ///
    /// It rides with the mark rather than only with the sheet so that the
    /// number survives the file: a drawing reopened somewhere else brings its
    /// measurements back with the calibration that produced them, instead of
    /// with whatever the reader last calibrated.
    /// </summary>
    public SheetScale? Scale { get; }

    public string Author { get; }

    public DateTimeOffset Created { get; }

    /// <summary>
    /// The shape itself, in sheet space, with the mark's turn already applied:
    /// the one description of the geometry that the canvas, the paper and the
    /// file all draw from.
    /// </summary>
    public IReadOnlyList<PathStep> Outline { get; }

    /// <summary>The box the mark covers, stroke width and arrow head included.</summary>
    public RectPt Bounds { get; }

    /// <summary>True when the mark covers what is under it, and so is picked up from inside.</summary>
    public bool IsFilled =>
        Style.Fill is not null || Kind is AnnotationKind.Highlight or AnnotationKind.Note;

    /// <summary>True for the kinds that can carry a fill at all.</summary>
    public static bool TakesFill(AnnotationKind kind) =>
        kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse
            or AnnotationKind.Polygon or AnnotationKind.Cloud or AnnotationKind.Area;

    /// <summary>
    /// True for the kinds drawn with a line of their own. A highlight is a wash
    /// and a note is an icon: a width control over either would do nothing.
    /// </summary>
    public static bool TakesWidth(AnnotationKind kind) =>
        kind is not (AnnotationKind.Highlight or AnnotationKind.Note or AnnotationKind.FreeText);

    /// <summary>True for the kinds whose points are a run of vertices the reader places one by one.</summary>
    public static bool TakesVertices(AnnotationKind kind) =>
        kind is AnnotationKind.Polyline or AnnotationKind.Polygon or AnnotationKind.Cloud
            or AnnotationKind.Perimeter or AnnotationKind.Area or AnnotationKind.Angle;

    /// <summary>
    /// True for the marks that can still be changed once they are made.
    ///
    /// A highlight cannot: it belongs to the words underneath it, and moving it
    /// or recolouring it would only take it off them or make it lie about what
    /// was picked out. It can still be deleted — that is what taking a
    /// highlight back means.
    /// </summary>
    public static bool CanBeChanged(AnnotationKind kind) => kind != AnnotationKind.Highlight;

    /// <summary>
    /// True for the kinds that carry words on the sheet itself — the ones typed
    /// in, and the measurements, whose number has to be readable on an A0 held
    /// at arm's length.
    /// </summary>
    public static bool TakesFontSize(AnnotationKind kind) =>
        kind == AnnotationKind.FreeText || Measures.Is(kind);

    /// <summary>True for the kinds that are typed into rather than drawn.</summary>
    public static bool TakesText(AnnotationKind kind) =>
        kind is AnnotationKind.Note or AnnotationKind.FreeText or AnnotationKind.Stamp;

    /// <summary>The middle of the mark's own points, which is what it turns about.</summary>
    public Vector2 Centre => AnnotationGeometry.Centre(Points);

    /// <summary>
    /// The box the mark was drawn in, before its own turn — what the grips sit
    /// on. For the kinds anchored at a single point it is the box that point
    /// implies: an icon, or the words themselves.
    /// </summary>
    public RectPt FrameBox => Kind switch
    {
        AnnotationKind.Note => new RectPt(Points[0].X, Points[0].Y, NoteSizePt, NoteSizePt),
        AnnotationKind.FreeText => AnnotationText.Box(Points[0], Text, Style.FontSizePt),
        _ => AnnotationGeometry.Union(null, Points),
    };

    public Annotation MovedBy(Vector2 delta)
    {
        var moved = new Vector2[Points.Count];
        for (int i = 0; i < moved.Length; i++)
        {
            moved[i] = Points[i] + delta;
        }
        return With(points: moved);
    }

    /// <summary>
    /// Stretches the mark about <paramref name="anchor"/> — the corner opposite
    /// the one being dragged. The turn is left alone: a box being made wider is
    /// wider along its own axes, not along the sheet's.
    /// </summary>
    public Annotation Scaled(Vector2 anchor, float scaleX, float scaleY)
    {
        var scaled = new Vector2[Points.Count];
        for (int i = 0; i < scaled.Length; i++)
        {
            scaled[i] = new Vector2(
                anchor.X + (Points[i].X - anchor.X) * scaleX,
                anchor.Y + (Points[i].Y - anchor.Y) * scaleY);
        }
        return With(points: scaled);
    }

    public Annotation WithPoints(IReadOnlyList<Vector2> points) => With(points: points);

    public Annotation WithStyle(AnnotationStyle style) => With(style: style);

    public Annotation WithText(string text) => With(text: text);

    public Annotation WithRotation(float degrees) => With(rotationDeg: degrees);

    /// <summary>
    /// The same mark calibrated differently — the sheet's scale having been set
    /// or changed after it was drawn. The number is recomputed, because it is
    /// never anything but the geometry seen through the scale.
    /// </summary>
    public Annotation WithScale(SheetScale? scale) => With(scale: scale, rescaled: true);

    private Annotation With(
        IReadOnlyList<Vector2>? points = null,
        AnnotationStyle? style = null,
        string? text = null,
        float? rotationDeg = null,
        SheetScale? scale = null,
        bool rescaled = false) =>
        new(Kind,
            points ?? Points,
            style ?? Style,
            text ?? Text,
            Author,
            Id,
            Created,
            rotationDeg ?? RotationDeg,
            rescaled ? scale : Scale);

    /// <summary>
    /// Whether a click at <paramref name="point"/> lands on this mark. An
    /// outlined shape is hit near its line and not inside it: a box drawn round
    /// a detail must not swallow the clicks meant for the drawing underneath. A
    /// filled one is hit anywhere inside, because there it is the thing on top.
    /// </summary>
    public bool HitTest(Vector2 point, float extraTolerancePt = 0f)
    {
        float tolerance = Style.WidthPt / 2f + HitSlopPt + extraTolerancePt;
        if (!Bounds.Inflated(tolerance).Contains(point)) return false;

        if (Kind == AnnotationKind.Note) return Bounds.Contains(point);

        // Words are caught anywhere on them, not by their edge: nobody aims at
        // the boundary of a piece of writing.
        if (Kind == AnnotationKind.FreeText) return AnnotationGeometry.InsidePath(Outline, point);

        if (Kind == AnnotationKind.Highlight)
        {
            return AnnotationGeometry.InsidePath(Outline, point);
        }

        if (IsFilled && AnnotationGeometry.InsidePath(Outline, point)) return true;

        if (Kind == AnnotationKind.Arrow)
        {
            var head = AnnotationGeometry.ArrowHead(TurnedPoint(0), TurnedPoint(Points.Count - 1), Style.WidthPt);
            if (AnnotationGeometry.InsideTriangle(point, head.Tip, head.Left, head.Right)) return true;
        }

        return AnnotationGeometry.NearPath(Outline, point, tolerance);
    }

    /// <summary>The box of a two-corner mark, before its own turn.</summary>
    public RectPt Box() => Points.Count >= 2
        ? RectPt.FromCorners(Points[0], Points[1])
        : new RectPt(0, 0, 0, 0);

    /// <summary>One of the mark's points with the mark's own turn applied.</summary>
    public Vector2 TurnedPoint(int index)
    {
        if (index < 0 || index >= Points.Count) return Vector2.Zero;
        return AnnotationGeometry.Rotate(Points[index], Centre, RotationDeg);
    }

    private static float Normalise(float degrees)
    {
        float turned = degrees % 360f;
        return turned < 0 ? turned + 360f : turned;
    }

    private RectPt ComputeBounds()
    {
        if (Points.Count == 0) return new RectPt(0, 0, 0, 0);

        if (Kind == AnnotationKind.Note) return FrameBox;

        var bounds = AnnotationGeometry.BoundsOf(Outline);

        // The head hangs off the end of the segment, and the box is what clips
        // the appearance stream in every other reader.
        if (Kind == AnnotationKind.Arrow && Points.Count >= 2)
        {
            var head = AnnotationGeometry.ArrowHead(TurnedPoint(0), TurnedPoint(Points.Count - 1), Style.WidthPt);
            bounds = AnnotationGeometry.Union(bounds, [head.Tip, head.Left, head.Right]);
        }

        // A highlight is the area it covers, exactly; everything else is drawn
        // with a line that has width, and half of it falls outside the path.
        return Kind == AnnotationKind.Highlight ? bounds : bounds.Inflated(Style.WidthPt / 2f + 0.5f);
    }
}
