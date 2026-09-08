//-----------------------------------------------------------------------------------------
// <copyright file="AnnotationRenderer.cs" company="plano y escala">
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

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

using ZenInk.Core;

namespace ZenInk_App.Rendering;

/// <summary>
/// Where a sheet sits on whatever is being drawn on — the canvas, or a sheet of
/// paper — and the turn between the marks' own space and that surface.
///
/// The marks are held in sheet space, which is the page as the file draws it.
/// This is the one place that becomes a position on screen, so the viewer and
/// the printer cannot drift apart about where a mark goes.
/// </summary>
internal readonly struct SheetPlacement(
    float sheetWidthPt,
    float sheetHeightPt,
    int quarterTurns,
    double originX,
    double originY,
    double scale)
{
    /// <summary>Drawing units per PDF point.</summary>
    public float Scale { get; } = (float)scale;

    public Vector2 ToScreen(Vector2 sheetPoint)
    {
        var turned = SheetTurn.ToDisplay(sheetPoint, sheetWidthPt, sheetHeightPt, quarterTurns);
        return new Vector2(
            (float)(originX + turned.X * scale),
            (float)(originY + turned.Y * scale));
    }

    /// <summary>A whole box, which stays axis-aligned through a quarter turn.</summary>
    public Rect ToScreen(RectPt box)
    {
        var a = ToScreen(new Vector2(box.Left, box.Top));
        var b = ToScreen(new Vector2(box.Right, box.Bottom));
        return new Rect(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }
}

/// <summary>
/// Draws the marks. The same code paints the canvas and the paper, because
/// "what you see is what prints" is not something to reimplement twice — and
/// what it draws is the outline the engine hands over, which is the same one
/// that goes into the file.
/// </summary>
internal static class AnnotationRenderer
{
    /// <summary>
    /// The thinnest a mark is ever drawn, in DIPs. A 0.5 pt line on a sheet
    /// zoomed out to fit would otherwise disappear, and a mark you cannot see
    /// is a mark you delete by accident.
    /// </summary>
    private const float MinimumStrokeDips = 1.1f;

    /// <summary>How small a measurement's number may get on screen before it stops shrinking.</summary>
    private const float SmallestLabelDips = 11f;

    /// <summary>The grips are a fixed size on screen: they are interface, not part of the drawing.</summary>
    private const float HandleSizeDips = 7f;

    private static readonly Color SelectionColor = Color.FromArgb(255, 0, 103, 192);

    private static readonly Color HandleFill = Color.FromArgb(255, 255, 255, 255);

    private static readonly CanvasStrokeStyle RoundStroke = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round,
    };

    private static readonly CanvasStrokeStyle SelectionStroke = new()
    {
        DashStyle = CanvasDashStyle.Dash,
        DashOffset = 2f,
    };

    public static void Draw(CanvasDrawingSession ds, Annotation mark, SheetPlacement placement, bool monochrome = false)
    {
        var colour = Tone(mark.Style.Color, monochrome);
        float width = Math.Max(MinimumStrokeDips, mark.Style.WidthPt * placement.Scale);

        if (mark.Kind == AnnotationKind.Note)
        {
            DrawNote(ds, mark, placement, colour);
            return;
        }

        if (mark.Kind == AnnotationKind.FreeText)
        {
            DrawWritten(ds, mark, placement, colour, monochrome);
            return;
        }

        if (mark.Outline.Count == 0) return;

        using var geometry = BuildGeometry(ds, mark.Outline, placement);

        // A highlight is a wash and nothing else: an outline round it would
        // read as a box someone drew.
        //
        // It is drawn by taking the darker of the two at every pixel, which is
        // what puts it *behind* the words rather than over them: against white
        // paper the colour wins, against black type the type wins. That is also
        // exactly what the PDF's own highlight does — it multiplies — so the
        // screen and the file agree without a blend layer of our own.
        if (mark.Kind == AnnotationKind.Highlight)
        {
            var blend = ds.Blend;
            ds.Blend = CanvasBlend.Min;
            ds.FillGeometry(geometry, colour);
            ds.Blend = blend;
            return;
        }

        if (mark.Style.Fill is { } fill)
        {
            var tone = Tone(fill, monochrome);
            ds.FillGeometry(geometry, Color.FromArgb(mark.Style.FillAlpha, tone.R, tone.G, tone.B));
        }

        ds.DrawGeometry(geometry, colour, width, RoundStroke);

        if (mark.Kind == AnnotationKind.Arrow && mark.Points.Count >= 2)
        {
            DrawArrowHead(ds, mark, placement, colour);
        }

        // The number, in the same place the file puts it: both ask
        // Measures.LabelAnchor, so the drawing sent on reads as it did here.
        if (Measures.Is(mark.Kind) && mark.Text.Length > 0)
        {
            DrawLabel(ds, mark, placement, colour);
        }

        if (mark.Kind == AnnotationKind.Stamp)
        {
            DrawStamp(ds, mark, placement, colour);
        }
    }

    /// <summary>
    /// The lines inside a stamp, laid out by the same fitting a signature's own
    /// appearance uses — so the box that says who reviewed a drawing reads the
    /// same here, on paper, and in the file.
    /// </summary>
    private static void DrawStamp(
        CanvasDrawingSession ds, Annotation mark, SheetPlacement placement, Color colour)
    {
        if (mark.Text.Length == 0) return;

        var box = mark.Box();
        var (size, lines) = PdfSignatureStamp.Fit(StampLines(mark.Text), box.Width, box.Height);
        if (size <= 0f || size * placement.Scale < 3f) return;

        using var format = new CanvasTextFormat
        {
            FontFamily = "Arial",
            FontSize = size * placement.Scale,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        var centre = mark.Centre;
        float baseline = box.Top + PdfSignatureStamp.Padding + size;

        foreach (var (text, _) in lines)
        {
            if (baseline > box.Bottom) break;

            var at = new Vector2(box.Left + PdfSignatureStamp.Padding, baseline);
            var start = placement.ToScreen(AnnotationGeometry.Rotate(at, centre, mark.RotationDeg));
            var along = placement.ToScreen(
                AnnotationGeometry.Rotate(at + new Vector2(1f, 0f), centre, mark.RotationDeg));

            var direction = along - start;
            if (direction.LengthSquared() > float.Epsilon)
            {
                using var layout = new CanvasTextLayout(ds, text, format, 0f, 0f);

                var previous = ds.Transform;
                ds.Transform =
                    Matrix3x2.CreateRotation(MathF.Atan2(direction.Y, direction.X)) *
                    Matrix3x2.CreateTranslation(start);

                ds.DrawTextLayout(layout, new Vector2(0f, -layout.LineMetrics[0].Baseline), colour);
                ds.Transform = previous;
            }

            baseline += size * PdfSignatureStamp.Leading;
        }
    }

    /// <summary>A stamp's text as the fitting wants it. Nothing is bold: this is a mark, not a signature.</summary>
    private static IReadOnlyList<(string Text, bool Strong)> StampLines(string text) =>
        [.. AnnotationText.Lines(text).Where(line => line.Length > 0).Select(line => (line, false))];

    /// <summary>
    /// What a measurement says, beside what it measured.
    ///
    /// On a plate under a sheet of paper the number would be lost among the
    /// drawing's own dimensions, so it goes on a plate of its own — the sheet
    /// showing through it, and the mark's colour around it, which is what says
    /// the number belongs to the line it sits on rather than to the drawing.
    /// </summary>
    private static void DrawLabel(
        CanvasDrawingSession ds, Annotation mark, SheetPlacement placement, Color colour)
    {
        // A measurement is its number, so the number does not shrink away with
        // the drawing: below this it stops at a size that can still be read.
        // Written marks may vanish at a distance — they are notes on the sheet —
        // but a length nobody can read is a line, and a line is not what the
        // reader asked for. The floor is in dips, so it never reaches the
        // captured or printed picture, whose scale is far above it.
        float size = MathF.Max(mark.Style.FontSizePt * placement.Scale, SmallestLabelDips);

        var anchor = Measures.LabelAnchor(mark.Kind, mark.Points);
        var at = placement.ToScreen(AnnotationGeometry.Rotate(anchor, mark.Centre, mark.RotationDeg));

        using var format = new CanvasTextFormat
        {
            FontFamily = "Arial",
            FontSize = size,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        using var layout = new CanvasTextLayout(ds, mark.Text, format, 0f, 0f);
        float padding = size * 0.3f;

        // Above and to the right of the anchor, which keeps it off the line
        // itself whichever way that line runs.
        var box = new Rect(
            at.X + padding,
            at.Y - (float)layout.LayoutBounds.Height - padding,
            layout.LayoutBounds.Width + (padding * 2),
            layout.LayoutBounds.Height + padding);

        ds.FillRoundedRectangle(box, padding, padding, Color.FromArgb(215, 255, 255, 255));
        ds.DrawRoundedRectangle(box, padding, padding, Color.FromArgb(160, colour.R, colour.G, colour.B), 1f);
        ds.DrawTextLayout(layout, new Vector2((float)box.X + padding, (float)box.Y), colour);
    }

    /// <summary>
    /// The mark the reader has hold of: its own frame, and the grips to stretch
    /// or turn it by. The frame follows the mark's turn rather than the sheet's
    /// axes, which is what tells the reader which way "wider" will go.
    /// </summary>
    public static void DrawSelection(
        CanvasDrawingSession ds, Annotation mark, SheetPlacement placement, float rotateOffsetPt)
    {
        // A mark with nothing to grab gets a plain ring round it; the rest get
        // their own frame, even when the only grip is the one that turns them.
        if (!AnnotationHandles.CanRotate(mark))
        {
            var box = placement.ToScreen(mark.Bounds);
            box = new Rect(box.X - 4, box.Y - 4, box.Width + 8, box.Height + 8);
            ds.DrawRectangle(box, Color.FromArgb(70, 0, 103, 192), 3f);
            ds.DrawRectangle(box, SelectionColor, 1.2f, SelectionStroke);
            return;
        }

        var frame = AnnotationHandles.Frame(mark);
        var centre = mark.Centre;

        Vector2 Corner(float x, float y) =>
            placement.ToScreen(AnnotationGeometry.Rotate(new Vector2(x, y), centre, mark.RotationDeg));

        var topLeft = Corner(frame.Left, frame.Top);
        var topRight = Corner(frame.Right, frame.Top);
        var bottomRight = Corner(frame.Right, frame.Bottom);
        var bottomLeft = Corner(frame.Left, frame.Bottom);

        using (var outline = Quad(ds, topLeft, topRight, bottomRight, bottomLeft))
        {
            ds.DrawGeometry(outline, Color.FromArgb(70, 0, 103, 192), 3f);
            ds.DrawGeometry(outline, SelectionColor, 1.2f, SelectionStroke);
        }

        foreach (var (grip, at) in AnnotationHandles.For(mark, rotateOffsetPt))
        {
            var screen = placement.ToScreen(at);

            if (grip.Which == MarkHandle.Rotate)
            {
                // A stalk from the top edge, so the knob reads as belonging to
                // the mark rather than floating over the drawing.
                var top = placement.ToScreen(AnnotationGeometry.Rotate(
                    new Vector2((frame.Left + frame.Right) / 2f, frame.Top), centre, mark.RotationDeg));

                ds.DrawLine(top, screen, SelectionColor, 1.2f);
                ds.FillCircle(screen, HandleSizeDips / 2f + 1f, HandleFill);
                ds.DrawCircle(screen, HandleSizeDips / 2f + 1f, SelectionColor, 1.4f);
                continue;
            }

            // A corner of the shape itself is a diamond, and a corner of the
            // frame a square. They do two different things — one moves a point,
            // the other stretches everything — and a reader should not have to
            // drag one to find out which.
            if (grip.Which == MarkHandle.Vertex)
            {
                using var diamond = Diamond(ds, screen, HandleSizeDips / 2f + 1f);
                ds.FillGeometry(diamond, HandleFill);
                ds.DrawGeometry(diamond, SelectionColor, 1.4f);
                continue;
            }

            var handle = new Rect(
                screen.X - HandleSizeDips / 2f,
                screen.Y - HandleSizeDips / 2f,
                HandleSizeDips,
                HandleSizeDips);

            ds.FillRectangle(handle, HandleFill);
            ds.DrawRectangle(handle, SelectionColor, 1.4f);
        }
    }

    /// <summary>A square on its corner, for the grips that move one point of a shape.</summary>
    private static CanvasGeometry Diamond(CanvasDrawingSession ds, Vector2 at, float radius)
    {
        using var builder = new CanvasPathBuilder(ds);
        builder.BeginFigure(new Vector2(at.X, at.Y - radius));
        builder.AddLine(new Vector2(at.X + radius, at.Y));
        builder.AddLine(new Vector2(at.X, at.Y + radius));
        builder.AddLine(new Vector2(at.X - radius, at.Y));
        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
    }

    /// <summary>Builds a Win2D path from the engine's outline, placed on the surface.</summary>
    private static CanvasGeometry BuildGeometry(
        CanvasDrawingSession ds, IReadOnlyList<PathStep> outline, SheetPlacement placement)
    {
        using var builder = new CanvasPathBuilder(ds);
        bool open = false;
        var loop = CanvasFigureLoop.Open;

        foreach (var step in outline)
        {
            switch (step.Verb)
            {
                case PathVerb.Move:
                    if (open) builder.EndFigure(loop);
                    builder.BeginFigure(placement.ToScreen(step.A));
                    open = true;
                    loop = CanvasFigureLoop.Open;
                    break;

                case PathVerb.Line:
                    if (open) builder.AddLine(placement.ToScreen(step.A));
                    break;

                case PathVerb.Cubic:
                    if (open)
                    {
                        builder.AddCubicBezier(
                            placement.ToScreen(step.A), placement.ToScreen(step.B), placement.ToScreen(step.C));
                    }
                    break;

                case PathVerb.Close:
                    loop = CanvasFigureLoop.Closed;
                    break;
            }
        }

        if (open) builder.EndFigure(loop);
        return CanvasGeometry.CreatePath(builder);
    }

    private static CanvasGeometry Quad(CanvasDrawingSession ds, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        using var builder = new CanvasPathBuilder(ds);
        builder.BeginFigure(a);
        builder.AddLine(b);
        builder.AddLine(c);
        builder.AddLine(d);
        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
    }

    private static void DrawArrowHead(
        CanvasDrawingSession ds, Annotation mark, SheetPlacement placement, Color colour)
    {
        // Worked out in sheet space and then placed, so the head points the
        // same way whatever turn the sheet — or the mark — is on.
        var (tip, left, right) = AnnotationGeometry.ArrowHead(
            mark.TurnedPoint(0), mark.TurnedPoint(mark.Points.Count - 1), mark.Style.WidthPt);

        using var builder = new CanvasPathBuilder(ds);
        builder.BeginFigure(placement.ToScreen(tip));
        builder.AddLine(placement.ToScreen(left));
        builder.AddLine(placement.ToScreen(right));
        builder.EndFigure(CanvasFigureLoop.Closed);

        using var geometry = CanvasGeometry.CreatePath(builder);
        ds.FillGeometry(geometry, colour);
    }

    /// <summary>
    /// Words written on the sheet.
    ///
    /// Each line is placed on the baseline the engine worked out — the very
    /// same number the file is written from — and the type is asked to sit on
    /// it, rather than being dropped into a box and hoping. Arial stands in for
    /// Helvetica, whose metrics it shares; the file carries Helvetica, which
    /// every reader has without anything being embedded.
    /// </summary>
    private static void DrawWritten(
        CanvasDrawingSession ds, Annotation mark, SheetPlacement placement, Color colour, bool monochrome)
    {
        var anchor = mark.Points[0];
        float size = mark.Style.FontSizePt * placement.Scale;

        // The ground the words sit on, under everything else. Drawn from the
        // same box the outline reports, so what is painted here, what goes in
        // the file and what the reader clicks on are one rectangle and not
        // three that have to be kept agreeing.
        if (mark.Style.Fill is { } ground)
        {
            var tone = Tone(ground, monochrome);
            ds.FillRectangle(
                placement.ToScreen(mark.FrameBox),
                Color.FromArgb(mark.Style.FillAlpha, tone.R, tone.G, tone.B));
        }

        // A mark with nothing typed into it yet still has to be visible, or the
        // reader has clicked and apparently nothing happened.
        if (mark.Text.Length == 0)
        {
            var empty = placement.ToScreen(mark.FrameBox);
            ds.DrawRectangle(empty, Color.FromArgb(120, colour.R, colour.G, colour.B), 1f, SelectionStroke);
            return;
        }

        if (size < 2f) return;

        using var format = new CanvasTextFormat
        {
            FontFamily = "Arial",
            FontSize = size,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        var lines = AnnotationText.Lines(mark.Text);
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length == 0) continue;

            var baseline = new Vector2(anchor.X, anchor.Y + AnnotationText.BaselineOffset(i, mark.Style.FontSizePt));
            var along = baseline + new Vector2(1f, 0f);

            var start = placement.ToScreen(AnnotationGeometry.Rotate(baseline, anchor, mark.RotationDeg));
            var next = placement.ToScreen(AnnotationGeometry.Rotate(along, anchor, mark.RotationDeg));

            var direction = next - start;
            if (direction.LengthSquared() <= float.Epsilon) continue;

            using var layout = new CanvasTextLayout(ds, lines[i], format, 0f, 0f);

            var previous = ds.Transform;
            ds.Transform =
                Matrix3x2.CreateRotation(MathF.Atan2(direction.Y, direction.X)) *
                Matrix3x2.CreateTranslation(start);

            ds.DrawTextLayout(layout, new Vector2(0f, -layout.LineMetrics[0].Baseline), colour);
            ds.Transform = previous;
        }
    }

    /// <summary>
    /// A comment's marker: the same speech bubble a PDF reader draws for a text
    /// annotation, so a reviewer recognises it for what it is. The text itself
    /// lives in the panel, not on the sheet — a drawing has no room for it.
    /// </summary>
    private static void DrawNote(CanvasDrawingSession ds, Annotation mark, SheetPlacement placement, Color colour)
    {
        var box = placement.ToScreen(mark.Bounds);
        float radius = (float)Math.Min(box.Width, box.Height) * 0.22f;

        var body = new Rect(box.X, box.Y, box.Width, box.Height * 0.78);
        ds.FillRoundedRectangle(body, radius, radius, colour);
        ds.DrawRoundedRectangle(body, radius, radius, Color.FromArgb(90, 255, 255, 255), 1f);

        using var builder = new CanvasPathBuilder(ds);
        builder.BeginFigure(new Vector2((float)(box.X + box.Width * 0.28), (float)(box.Y + box.Height * 0.74)));
        builder.AddLine(new Vector2((float)(box.X + box.Width * 0.28), (float)(box.Y + box.Height)));
        builder.AddLine(new Vector2((float)(box.X + box.Width * 0.62), (float)(box.Y + box.Height * 0.74)));
        builder.EndFigure(CanvasFigureLoop.Closed);
        using var tail = CanvasGeometry.CreatePath(builder);
        ds.FillGeometry(tail, colour);

        // Two lines of "writing", which is what tells the marker apart from a
        // plain blob at the zoom a whole sheet is read at.
        float inset = (float)box.Width * 0.22f;
        float lineWidth = Math.Max(1f, (float)box.Height * 0.07f);
        var ink = Color.FromArgb(210, 255, 255, 255);
        for (int i = 0; i < 2; i++)
        {
            float y = (float)(box.Y + box.Height * (0.28 + i * 0.24));
            ds.DrawLine(
                new Vector2((float)box.X + inset, y),
                new Vector2((float)(box.X + box.Width) - inset, y),
                ink,
                lineWidth);
        }
    }

    /// <summary>
    /// The colour to draw in. On a monochrome print the mark's own colour would
    /// come out of the printer as some grey the reviewer never chose, so it is
    /// converted here — by luminance, which keeps a yellow highlight light and
    /// a blue line dark.
    /// </summary>
    private static Color Tone(AnnotationColor colour, bool monochrome)
    {
        if (!monochrome) return Color.FromArgb(255, colour.R, colour.G, colour.B);

        byte grey = (byte)Math.Clamp(0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B, 0, 255);
        return Color.FromArgb(255, grey, grey, grey);
    }
}
