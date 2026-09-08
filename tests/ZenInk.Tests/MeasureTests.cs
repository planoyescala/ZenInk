//-----------------------------------------------------------------------------------------
// <copyright file="MeasureTests.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Globalization;
using System.Numerics;
using ZenInk.Core;
using static ZenInk.Tests.TestRunner;

/// <summary>
/// Measuring on a calibrated sheet.
///
/// What is pinned here is the one thing a measurement has to be: the number and
/// the line it is drawn on cannot disagree. Everything else — where the label
/// sits, which unit it is in — is a preference; a length that says 12,4 m over
/// a line that is 9 m long is a drawing signed off on a wrong figure.
/// </summary>
namespace ZenInk.Tests;

public static class MeasureTests
{
    /// <summary>Numbers are compared in a fixed culture, since the display uses the reader's.</summary>
    private static readonly CultureInfo Fixed = CultureInfo.InvariantCulture;

    public static void Run()
    {
        Calibration();
        Geometry();
        Marks();
        Sheets();
        Snapping();
        Squaring();
    }

    /// <summary>
    /// Holding a measurement to the horizontal or the vertical.
    ///
    /// The interesting part is not the arithmetic, it is that the axis is
    /// chosen rather than asked for: a reader dragging along a wall should not
    /// have to say which wall it is.
    /// </summary>
    private static void Squaring()
    {
        Section("Measuring — held square");

        var from = new Vector2(100, 100);

        // Mostly across: the drag keeps the length it drew and loses the drift.
        var across = AnnotationGeometry.OnAxis(from, new Vector2(300, 112));
        Check("a drag along a wall comes out horizontal", across == new Vector2(300, 100));

        // Mostly down: the same, the other way round.
        var down = AnnotationGeometry.OnAxis(from, new Vector2(88, 400));
        Check("a drag down a wall comes out vertical", down == new Vector2(100, 400));

        // Which axis is decided by the movement, so it survives being drawn
        // backwards — a wall measured right to left is the same wall.
        var back = AnnotationGeometry.OnAxis(from, new Vector2(-150, 94));
        Check("drawn backwards it is still the horizontal", back == new Vector2(-150, 100));

        // The length that ends up on the sheet is the one along the axis, not
        // the one the hand drew. That difference is the whole point: a drag
        // 200 long and 12 off reads 200,36 free and 200 square.
        Check(
            "the number that comes out is the run, not the hypotenuse",
            Math.Abs(AnnotationGeometry.TotalLength([from, across]) - 200) < 0.0001);

        // An exact diagonal has no nearer axis. It has to pick one and keep
        // picking the same one, or a mark would flicker between them.
        var tie = AnnotationGeometry.OnAxis(from, new Vector2(200, 200));
        Check("an exact diagonal settles on the horizontal", tie == new Vector2(200, 100));
        Check("and settles there every time", AnnotationGeometry.OnAxis(from, new Vector2(200, 200)) == tie);

        // Standing still is not a direction. It must not jump to an axis, or
        // the first sample of a drag would fly off before the hand moved.
        Check("a point on the anchor stays on it", AnnotationGeometry.OnAxis(from, from) == from);

        // Each vertex hangs off the one before it, which is what makes a run of
        // segments a staircase instead of a single line through the first
        // corner. This is the shape of a room measured round its walls.
        var corner = AnnotationGeometry.OnAxis(new Vector2(300, 100), new Vector2(292, 260));
        Check("the next vertex squares off the previous one", corner == new Vector2(300, 260));
    }

    /// <summary>
    /// Pulling a measurement onto the drawing's own lines. It works on the
    /// pixels of the tile under the pointer, so these are rasters: white paper
    /// with black lines on it, which is what a plan is.
    /// </summary>
    private static void Snapping()
    {
        Section("Medir — pegarse al dibujo");

        const int W = 64, H = 64;

        // A wall running down the middle.
        var wall = Paper(W, H);
        for (int y = 0; y < H; y++) Ink(wall, W, 32, y);

        Check("a point beside a line is pulled onto it",
            InkSnap.Nearest(wall, W, H, new Vector2(28, 20), 8) is { } onto
            && Math.Abs(onto.X - 32) < 0.01f && Math.Abs(onto.Y - 20) < 0.01f);

        Check("and a point on bare paper is left where it is",
            InkSnap.Nearest(Paper(W, H), W, H, new Vector2(10, 10), 8) is null);

        Check("a line further than the reach does not pull",
            InkSnap.Nearest(wall, W, H, new Vector2(10, 20), 8) is null);

        // Two walls meeting: the corner at (20, 40), one arm going right and
        // one going up.
        var corner = Paper(W, H);
        for (int x = 20; x < 60; x++) Ink(corner, W, x, 40);
        for (int y = 8; y <= 40; y++) Ink(corner, W, 20, y);

        Check("a click near a corner lands on the corner, not on a wall",
            InkSnap.Snap(corner, W, H, new Vector2(24, 36), 10) is { } vertex
            && Vector2.Distance(vertex, new Vector2(20, 40)) < 2f,
            $"{InkSnap.Snap(corner, W, H, new Vector2(24, 36), 10)}");

        // And the same drawing, clicked halfway along one of the walls: there
        // is no corner there, so it must not invent one.
        Check("a click along a wall stays on the wall",
            InkSnap.Snap(corner, W, H, new Vector2(45, 37), 6) is { } along
            && Math.Abs(along.Y - 40) < 0.01f && Math.Abs(along.X - 45) < 2f,
            $"{InkSnap.Snap(corner, W, H, new Vector2(45, 37), 6)}");

        // A diagonal spreads along both axes exactly as a corner does, and the
        // corner of its box is a point on bare paper. Snapping there would put
        // the end of a measurement where the drawing has nothing.
        var diagonal = Paper(W, H);
        for (int i = 0; i < 48; i++) Ink(diagonal, W, 8 + i, 8 + i);

        Check("a point beside a diagonal lands on the nearest bit of it, not along it",
            InkSnap.Snap(diagonal, W, H, new Vector2(30, 34), 12) is { } onLine
            && Math.Abs(onLine.X - onLine.Y) < 1.5f
            && Vector2.Distance(onLine, new Vector2(30, 34)) < 4f,
            $"{InkSnap.Snap(diagonal, W, H, new Vector2(30, 34), 12)}");

        // Pale hatching is not a line: a plan is full of it, and snapping to it
        // would take the measurement off the wall it was aimed at.
        var hatched = Paper(W, H);
        for (int y = 0; y < H; y++) Grey(hatched, W, 24, y, 200);
        for (int y = 0; y < H; y++) Ink(hatched, W, 40, y);

        Check("hatching is not ink to snap to",
            InkSnap.Nearest(hatched, W, H, new Vector2(30, 20), 12) is { } skipped
            && Math.Abs(skipped.X - 40) < 0.01f);
    }

    private static byte[] Paper(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        Array.Fill(bgra, (byte)255);
        return bgra;
    }

    private static void Ink(byte[] bgra, int width, int x, int y) => Grey(bgra, width, x, y, 0);

    private static void Grey(byte[] bgra, int width, int x, int y, byte tone)
    {
        int at = ((y * width) + x) * 4;
        bgra[at] = bgra[at + 1] = bgra[at + 2] = tone;
    }

    private static void Calibration()
    {
        Section("Medir — calibrar la hoja");

        // A drag of 100 points said to be 5 metres.
        var scale = SheetScale.From(100, 5, MeasureUnit.Metre);
        Check("a drag over a known distance gives a scale", scale is not null);
        CheckClose("and one point is that fraction of it", scale!.Value.UnitsPerPoint, 0.05, 1e-9);
        CheckClose("so twice the drag is twice the length", scale.Value.Length(200), 10, 1e-9);

        Check("a drag too short to calibrate from is refused",
            SheetScale.From(SheetScale.ShortestCalibrationPt - 1, 5, MeasureUnit.Metre) is null);
        Check("and so is a length of nothing", SheetScale.From(100, 0, MeasureUnit.Metre) is null);

        // 1:100 means one millimetre of paper is a hundred of building. A point
        // is 25,4/72 mm, so it is that many millimetres times a hundred.
        var hundred = SheetScale.FromRatio(100, MeasureUnit.Metre);
        Check("a drawing ratio gives a scale too", hundred is not null);
        CheckClose("a metre stick on paper is a millimetre at 1:100",
            hundred!.Value.Length(72 / 25.4 * 10), 1.0, 1e-9);

        CheckClose("and the ratio comes back out", hundred.Value.Ratio, 100, 1e-6);
        Check("written as a reader writes it", hundred.Value.RatioLabel == "1:100");

        // Calibrating off a scale bar never lands exactly on the round number,
        // and a drawing labelled 1:99,5 reads as the program being wrong. A
        // hundred points of paper at 1:100 is 3,53 m of building, so this is a
        // drag that missed the mark by a hair.
        var measured = SheetScale.From(100, 3.51, MeasureUnit.Metre);
        Check("a ratio near a usual one is written as that one", measured!.Value.RatioLabel == "1:100");

        var enlarged = SheetScale.FromRatio(0.5, MeasureUnit.Millimetre);
        Check("a detail drawn larger than life is written the other way round",
            enlarged!.Value.RatioLabel == "2:1");

        // The unit is the sheet's, and the number is written to suit it.
        var metres = new SheetScale(0.05, MeasureUnit.Metre);
        Check("a length carries its unit", metres.FormatLength(100, Fixed) == "5 m");
        Check("and an area the square of it", metres.FormatArea(100 * 100, Fixed) == "25 m²");
        Check("an angle has no unit of the sheet's in it", SheetScale.FormatAngle(90, Fixed) == "90°");
    }

    private static void Geometry()
    {
        Section("Medir — la geometría de la que sale el número");

        Vector2[] line = [new(0, 0), new(30, 40)];
        CheckClose("a distance is the length of its segment", AnnotationGeometry.TotalLength(line), 50, 1e-6);

        Vector2[] run = [new(0, 0), new(0, 10), new(10, 10)];
        CheckClose("a run of segments adds up", AnnotationGeometry.TotalLength(run), 20, 1e-6);
        CheckClose("and closing it adds the way back",
            AnnotationGeometry.TotalLength(run, closed: true), 20 + Math.Sqrt(200), 1e-6);

        Vector2[] square = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        CheckClose("an area is what its outline encloses", AnnotationGeometry.PolygonArea(square), 100, 1e-6);

        Vector2[] backwards = [new(0, 10), new(10, 10), new(10, 0), new(0, 0)];
        CheckClose("and it does not matter which way round it was traced",
            AnnotationGeometry.PolygonArea(backwards), 100, 1e-6);

        // An L-shaped room: the shoelace has to hold for something that is not
        // a box, which is what every real room is.
        Vector2[] room = [new(0, 0), new(20, 0), new(20, 10), new(10, 10), new(10, 20), new(0, 20)];
        CheckClose("including a shape that is not a box", AnnotationGeometry.PolygonArea(room), 300, 1e-6);

        CheckClose("a right angle measures ninety",
            AnnotationGeometry.CornerAngle(new(10, 0), new(0, 0), new(0, 10)), 90, 1e-6);

        // The angle drawn between the arms, never the rest of the circle.
        CheckClose("and a wide corner is the angle you can see, not its reflex",
            AnnotationGeometry.CornerAngle(new(10, 0), new(0, 0), new(-10, -1)), 174.29, 0.05);
    }

    private static void Marks()
    {
        Section("Medir — la marca y su número");

        var scale = new SheetScale(0.05, MeasureUnit.Metre);
        var style = AnnotationStyle.Default;

        var distance = new Annotation(
            AnnotationKind.Distance, [new Vector2(0, 0), new Vector2(100, 0)], style, scale: scale);

        Check("a measurement's words are its number", distance.Text.Length > 0 && distance.Text.Contains('m'));
        Check("and it carries the scale it was taken at", distance.Scale is not null);

        // The number is never stored apart from the geometry: moving an end
        // makes a new mark, and a new mark recomputes.
        var moved = distance.WithPoints([new Vector2(0, 0), new Vector2(200, 0)]);
        Check("moving an end moves the number with it", moved.Text != distance.Text);

        // Text handed in is ignored for a measurement, which is what stops a
        // number and a line ever coming apart.
        var forced = new Annotation(
            AnnotationKind.Distance, [new Vector2(0, 0), new Vector2(100, 0)], style, text: "cien kilómetros", scale: scale);
        Check("a measurement cannot be told to say something else", forced.Text == distance.Text);

        var uncalibrated = new Annotation(AnnotationKind.Distance, [new Vector2(0, 0), new Vector2(100, 0)], style);
        Check("without a calibration there is no number to show", uncalibrated.Text.Length == 0);

        var recalibrated = uncalibrated.WithScale(scale);
        Check("and calibrating the sheet later gives it one", recalibrated.Text == distance.Text);

        var angle = new Annotation(
            AnnotationKind.Angle, [new Vector2(10, 0), new Vector2(0, 0), new Vector2(0, 10)], style);
        Check("an angle needs no calibration at all", angle.Text.Contains('°'));

        // Every measurement is a shape as well as a number, and the shape is
        // the one the canvas, the paper and the file all draw.
        Check("a distance is drawn as a dimension line, ticks and all", distance.Outline.Count > 2);
        Check("an angle is drawn with the arc between its arms", angle.Outline.Count > 3);
        Check("an area is a closed shape",
            new Annotation(AnnotationKind.Area, [new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10)], style, scale: scale)
                .Outline[^1].Verb == PathVerb.Close);

        // Round trip through the file's private key.
        string payload = AnnotationPayload.Write(distance);
        Check("a measurement survives being written and read back",
            AnnotationPayload.TryRead(payload, out var back)
            && back.Kind == AnnotationKind.Distance
            && back.Text == distance.Text
            && back.Scale is { } read
            && Math.Abs(read.UnitsPerPoint - 0.05) < 1e-9
            && read.Unit == MeasureUnit.Metre);

        // And a mark from before measuring existed still reads, since a
        // drawing marked up last month has to open.
        Check("a mark written by an older ZenInk still reads",
            AnnotationPayload.TryRead("z3|rectangle|D82020|2|1700000000|0|-|0.35|12|10,10 40,30|Manuel|nota", out var old)
            && old.Kind == AnnotationKind.Rectangle
            && old.Text == "nota"
            && old.Scale is null);
    }

    private static void Sheets()
    {
        Section("Medir — la escala vive con la hoja");

        var store = new AnnotationStore();
        var scale = new SheetScale(0.05, MeasureUnit.Metre);

        store.Add(0, new Annotation(
            AnnotationKind.Distance, [new Vector2(0, 0), new Vector2(100, 0)], AnnotationStyle.Default));

        Check("a sheet starts with no scale", store.ScaleOf(0) is null);

        store.SetScale(0, scale);
        Check("calibrating gives it one", store.IsCalibrated(0));
        Check("and the measurements already on it get their number",
            store.ForPage(0)[0].Text.Length > 0);

        store.SetScale(0, new SheetScale(0.1, MeasureUnit.Metre));
        string doubled = store.ForPage(0)[0].Text;
        store.Undo();
        Check("recalibrating and taking it back is one step",
            store.ForPage(0)[0].Text != doubled && store.ScaleOf(0)!.Value.UnitsPerPoint == 0.05);

        // The rule that matters once sheets can be moved: what hangs off a
        // sheet travels with it, and never lands on its neighbour.
        var plan = PagePlan.Identity([new PdfPageSize(842, 595), new PdfPageSize(842, 595)], "plano.pdf");
        store.LoadPlan(plan);
        store.Rearrange(plan.Reorder([1, 0]));

        Check("moving a sheet takes its scale with it", store.ScaleOf(1)?.UnitsPerPoint == 0.05);
        Check("and does not leave one behind on the sheet it left", store.ScaleOf(0) is null);

        // Reopening: the calibration comes back from the measurements
        // themselves, since that is where it was written.
        var reopened = new AnnotationStore();
        var measured = new Annotation(
            AnnotationKind.Distance, [new Vector2(0, 0), new Vector2(100, 0)], AnnotationStyle.Default, scale: scale);

        reopened.Load(new Dictionary<int, IReadOnlyList<Annotation>> { [2] = new[] { measured } });
        Check("a sheet reopened comes back calibrated by its own measurements",
            reopened.ScaleOf(2)?.UnitsPerPoint == 0.05);
        Check("and a sheet that was never measured on does not", reopened.ScaleOf(0) is null);
    }
}
