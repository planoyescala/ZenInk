using System.Numerics;

using PDFiumCore;
using ZenInk.Core;
using static ZenInk.Tests.TestRunner;

namespace ZenInk.Tests;

/// <summary>
/// Marks on a drawing, from the geometry that decides where they are up to the
/// bytes that carry them into the file.
///
/// The file half is the part worth pinning hardest: a mark that draws
/// beautifully and saves wrong is a review lost, and the reviewer will not find
/// out until someone else opens the drawing.
/// </summary>
public static class AnnotationTests
{
    /// <summary>What a saved and reopened point may drift, in points.</summary>
    private const float PointTolerance = 0.15f;

    public static void Run()
    {
        Shapes();
        Picking();
        Thinning();
        Turning();
        Payload();
        Appearance();
        Outlines();
        WrittenMarks();
        Clouds();
        FillAndTurn();
        Resizing();
        Handles();
        Store();
    }

    public static async Task RunAsync()
    {
        await RoundTripAsync();
        await RoundTripRotatedAsync();
        await SavingWithATurnAsync();
        await LeavingOtherToolsAloneAsync();
        await NotDrawnTwiceAsync();
        await FilledMarkIsSeeThroughAsync();
        await WrittenMarksReachTheFileAsync();
        await MeasurementsReachTheFileAsync();
        await StampsReachTheFileAsync();
        await FlatteningAsync();
    }

    private static Annotation Ink(params (float X, float Y)[] points) =>
        new(AnnotationKind.Ink, [.. points.Select(p => new Vector2(p.X, p.Y))], new AnnotationStyle(AnnotationColor.Red, 2f));

    private static Annotation Of(AnnotationKind kind, float widthPt, params (float X, float Y)[] points) =>
        new(kind, [.. points.Select(p => new Vector2(p.X, p.Y))], new AnnotationStyle(new AnnotationColor(10, 120, 220), widthPt));

    // --- geometry --------------------------------------------------------

    private static void Shapes()
    {
        Section("Marks — the box a mark covers");

        // Half the stroke falls outside the path, plus half a point of slack:
        // the box becomes the PDF's /Rect, and that rectangle clips the
        // appearance, so it must not be drawn tight against the ink.
        var stroke = Of(AnnotationKind.Ink, 4f, (100, 100), (200, 160), (150, 120));
        CheckClose("a stroke's box starts at its leftmost point, less half the width", stroke.Bounds.Left, 97.5, 0.01);
        CheckClose("and ends at its lowest, plus half the width", stroke.Bounds.Bottom, 162.5, 0.01);

        // The head sits behind the tip, not beyond it — but its barbs stand
        // out to either side of the shaft, and the box has to hold them.
        var arrow = Of(AnnotationKind.Arrow, 2f, (100, 100), (200, 100));
        var plain = Of(AnnotationKind.Line, 2f, (100, 100), (200, 100));
        Check("an arrow's box leaves room for the barbs of its head",
            arrow.Bounds.Height > plain.Bounds.Height + 4f,
            $"{arrow.Bounds.Height:0.#} vs {plain.Bounds.Height:0.#}");
        CheckClose("and stops at the tip, which is where the arrow ends", arrow.Bounds.Right, 201.5, 0.01);

        var note = new Annotation(AnnotationKind.Note, [new Vector2(50, 60)], AnnotationStyle.Default, "hola");
        Check("a note's box is the icon, wherever the sheet is zoomed to",
            Math.Abs(note.Bounds.Width - Annotation.NoteSizePt) < 0.01f
            && Math.Abs(note.Bounds.Height - Annotation.NoteSizePt) < 0.01f);

        var box = Of(AnnotationKind.Rectangle, 1f, (300, 200), (100, 100));
        CheckClose("a box drawn right-to-left still reads left to right", box.Box().Left, 100, 0.01);
        CheckClose("and top to bottom", box.Box().Top, 100, 0.01);
    }

    private static void Picking()
    {
        Section("Marks — what a click lands on");

        var stroke = Of(AnnotationKind.Ink, 3f, (100, 100), (200, 100), (200, 200));
        Check("a click on the stroke hits it", stroke.HitTest(new Vector2(150, 101)));
        Check("a click well off it does not", !stroke.HitTest(new Vector2(150, 140)));

        var box = Of(AnnotationKind.Rectangle, 2f, (100, 100), (300, 250));
        Check("a box is picked up by its edge", box.HitTest(new Vector2(200, 100.5f)));
        Check("but not by its middle — the drawing underneath is what is wanted there",
            !box.HitTest(new Vector2(200, 175)));

        var wash = Of(AnnotationKind.Highlight, 1f, (100, 100), (300, 250));
        Check("a highlight is picked up anywhere inside it, because it covers what is there",
            wash.HitTest(new Vector2(200, 175)));

        var ring = Of(AnnotationKind.Ellipse, 2f, (100, 100), (300, 200));
        Check("an ellipse is picked up on its curve", ring.HitTest(new Vector2(200, 100.6f)));
        Check("and not inside it", !ring.HitTest(new Vector2(200, 150)));
        Check("nor outside it", !ring.HitTest(new Vector2(310, 150)));
    }

    private static void Thinning()
    {
        Section("Marks — thinning a captured stroke");

        var straight = new List<Vector2>();
        for (int i = 0; i <= 100; i++)
        {
            straight.Add(new Vector2(i * 3f, 200f));
        }

        var thinned = AnnotationGeometry.Simplify(straight, 0.4f);
        Check("a straight drag comes down to its two ends", thinned.Count == 2, $"kept {thinned.Count}");

        // An L: along, then down. Only the corner carries any information.
        var corner = new List<Vector2>();
        for (int i = 0; i <= 50; i++) corner.Add(new Vector2(i * 3f, 200f));
        for (int i = 1; i <= 50; i++) corner.Add(new Vector2(150f, 200f + i * 4f));

        var kept = AnnotationGeometry.Simplify(corner, 0.4f);
        Check("a corner survives, and nothing else does", kept.Count == 3, $"kept {kept.Count}");
        Check("and it is the corner itself that is kept",
            kept.Any(p => Math.Abs(p.X - 150f) < 0.01f && Math.Abs(p.Y - 200f) < 0.01f));
    }

    private static void Turning()
    {
        Section("Marks — the reader's turn does not move the ink");

        const float w = 400f, h = 600f;
        var point = new Vector2(100f, 500f);

        foreach (int turns in new[] { 0, 1, 2, 3 })
        {
            var onScreen = SheetTurn.ToDisplay(point, w, h, turns);
            var back = SheetTurn.ToSheet(onScreen, w, h, turns);
            Check($"turn {turns}: a point survives the round trip",
                Vector2.Distance(back, point) < 0.001f, $"got {back}");

            var (tw, th) = SheetTurn.Size(w, h, turns);
            Check($"turn {turns}: the turned point lands inside the turned sheet",
                onScreen.X >= 0 && onScreen.X <= tw && onScreen.Y >= 0 && onScreen.Y <= th,
                $"{onScreen} in {tw}x{th}");
        }

        // The same numbers PDFium produces for a page carrying that /Rotate:
        // a sheet's top-left corner ends up at its top-right after a quarter
        // turn clockwise.
        var corner = SheetTurn.ToDisplay(new Vector2(0f, 0f), w, h, 1);
        Check("a quarter turn clockwise sends the top-left corner to the top-right",
            Math.Abs(corner.X - h) < 0.001f && Math.Abs(corner.Y) < 0.001f, $"got {corner}");
    }

    private static void Payload()
    {
        Section("Marks — the private key that carries a mark exactly");

        var original = new Annotation(
            AnnotationKind.Arrow,
            [new Vector2(12.25f, 33.5f), new Vector2(400f, 61.75f)],
            new AnnotationStyle(new AnnotationColor(0x2E, 0x8B, 0x57), 3.5f),
            text: "Revisar la sección: ¿el forjado va a 25 cm? | pendiente",
            author: "Manuel");

        string written = AnnotationPayload.Write(original);
        Check("the payload parses back", AnnotationPayload.TryRead(written, out var read), written);

        Check("the kind survives", read.Kind == original.Kind);
        Check("the colour survives", read.Style.Color == original.Style.Color);
        CheckClose("the width survives", read.Style.WidthPt, original.Style.WidthPt, 0.001);
        Check("the author survives", read.Author == original.Author);
        Check("accents and a separator inside the comment survive", read.Text == original.Text, read.Text);
        Check("the points survive", read.Points.Count == 2
            && Vector2.Distance(read.Points[0], original.Points[0]) < 0.01f
            && Vector2.Distance(read.Points[1], original.Points[1]) < 0.01f);

        Check("something that is not ours is not read as ours",
            !AnnotationPayload.TryRead("Bluebeam markup", out _));
        Check("nor is an empty value", !AnnotationPayload.TryRead(string.Empty, out _));

        var typed = new Annotation(
            AnnotationKind.FreeText,
            [new Vector2(50f, 80f)],
            new AnnotationStyle(AnnotationColor.Red, 2f, null, 0.35f, 18f),
            text: "Primera línea\nSegunda línea");

        Check("a written mark parses back", AnnotationPayload.TryRead(AnnotationPayload.Write(typed), out var word));
        CheckClose("its type size survives", word.Style.FontSizePt, 18f, 0.001);
        Check("and so do its line breaks", word.Text == typed.Text, $"{word.Text.Length} caracteres");

        // A mark written by an earlier ZenInk still has to read: the marks are
        // in someone's drawing, and a version bump must not orphan them.
        Check("a mark from the version before this one still reads",
            AnnotationPayload.TryRead("z2|line|D82020|2|1700000000|0|-|0.35|10,10 90,90|Manuel|", out var old));
        Check("with its kind", old.Kind == AnnotationKind.Line);
        Check("and its points", old.Points.Count == 2 && Math.Abs(old.Points[1].X - 90f) < 0.01f);
    }

    private static void Appearance()
    {
        Section("Marks — the appearance every other reader sees");

        var line = Of(AnnotationKind.Line, 2f, (10, 10), (100, 100));
        string? stream = AnnotationAppearance.Build(line, line.Outline, line.Points);
        Check("a line is stroked", stream is not null && stream.Contains("S\n"));
        Check("with its own width", stream is not null && stream.Contains("2 w"));

        var arrow = Of(AnnotationKind.Arrow, 2f, (10, 10), (100, 10));
        string? head = AnnotationAppearance.Build(arrow, arrow.Outline, arrow.Points);
        Check("an arrow's head is a filled shape", head is not null && head.Contains("h\nf\n"));

        // The shapes that can be filled go in as a path object instead: an
        // appearance stream written this way cannot carry the transparency a
        // fill needs.
        var ring = Of(AnnotationKind.Ellipse, 1f, (0, 0), (100, 50));
        Check("a shape that can be filled is not written as a stream",
            AnnotationAppearance.Build(ring, ring.Outline, ring.Points) is null);

        var wash = Of(AnnotationKind.Highlight, 1f, (0, 0), (100, 50));
        Check("a highlight is left to the reader, which knows how to blend it",
            AnnotationAppearance.Build(wash, wash.Outline, wash.Points) is null);

        var note = new Annotation(AnnotationKind.Note, [Vector2.Zero], AnnotationStyle.Default, "x");
        Check("so is a note, which every reader draws as its own icon",
            AnnotationAppearance.Build(note, note.Outline, note.Points) is null);
    }

    private static void Outlines()
    {
        Section("Marks — one outline, drawn the same way everywhere");

        var box = Of(AnnotationKind.Rectangle, 2f, (100, 100), (300, 200));
        Check("a box is four corners and a close",
            box.Outline.Count == 5 && box.Outline[^1].Verb == PathVerb.Close);

        var ring = Of(AnnotationKind.Ellipse, 2f, (100, 100), (300, 200));
        Check("an ellipse is four curves",
            ring.Outline.Count(s => s.Verb == PathVerb.Cubic) == 4);
        // The box, plus half the stroke on each side and the half point of
        // slack the bounds carry so a hairline still has a box.
        CheckClose("and it fills its box", ring.Bounds.Width, 203, 0.3);

        var line = Of(AnnotationKind.Polyline, 2f, (10, 10), (50, 40), (90, 10));
        Check("an open polyline does not close", line.Outline.All(s => s.Verb != PathVerb.Close));

        var shape = Of(AnnotationKind.Polygon, 2f, (10, 10), (90, 10), (50, 80));
        Check("a polygon does", shape.Outline[^1].Verb == PathVerb.Close);

        // A highlight follows text, so it is a box per line of it: one mark,
        // several closed figures.
        var wash = Of(AnnotationKind.Highlight, 1f,
            (100, 100), (300, 112),
            (100, 116), (240, 128));

        Check("a highlight over two lines is two figures",
            AnnotationGeometry.Figures(wash.Outline).Count == 2);
        Check("it is picked up on the first line", wash.HitTest(new Vector2(200, 106)));
        Check("and on the second", wash.HitTest(new Vector2(200, 122)));
        Check("but not in the gap between them", !wash.HitTest(new Vector2(200, 114)),
            "the jump from one box to the next must not be treated as an edge");
        Check("nor past the end of the shorter line", !wash.HitTest(new Vector2(280, 122)));
        CheckClose("and its box takes in both lines", wash.Bounds.Bottom, 128, 0.01);
    }

    private static void WrittenMarks()
    {
        Section("Marks — words written on the drawing");

        var one = new Annotation(
            AnnotationKind.FreeText,
            [new Vector2(100f, 100f)],
            new AnnotationStyle(AnnotationColor.Red, 2f, null, 0.35f, 20f),
            text: "Cota");

        var two = one.WithText("Cota\nrevisada");
        Check("a second line makes the mark taller", two.Bounds.Height > one.Bounds.Height + 10f,
            $"{one.Bounds.Height:0.#} then {two.Bounds.Height:0.#}");
        // The frame is the words themselves; the bounds add a little slack round
        // them so the rectangle in the file never clips a glyph.
        CheckClose("lines sit a line apart", two.FrameBox.Height / one.FrameBox.Height, 2, 0.01);

        Check("the words are caught anywhere on them", one.HitTest(new Vector2(110, 110)));
        Check("and not well below them", !one.HitTest(new Vector2(110, 180)));

        CheckClose("the first baseline hangs below the anchor",
            AnnotationText.BaselineOffset(0, 20f), 19, 0.01);
        CheckClose("and the second a full line lower",
            AnnotationText.BaselineOffset(1, 20f) - AnnotationText.BaselineOffset(0, 20f), 25, 0.01);

        // Written marks turn — a label along a diagonal is the reason — but
        // there is nothing to stretch: the type size is what sizes them.
        Check("a written mark can be turned", AnnotationHandles.CanRotate(one));
        Check("but not stretched", !AnnotationHandles.CanResize(one));
        Check("so it carries the knob and nothing else",
            AnnotationHandles.For(one) is [({ Which: MarkHandle.Rotate }, _)]);
    }

    private static void Clouds()
    {
        Section("Marks — revision clouds");

        var cloud = Of(AnnotationKind.Cloud, 2f, (100, 100), (300, 200));
        Check("a cloud closes", cloud.Outline[^1].Verb == PathVerb.Close);
        Check("and it is made of curves, not corners",
            cloud.Outline.Count(s => s.Verb == PathVerb.Cubic) >= 16,
            $"{cloud.Outline.Count(s => s.Verb == PathVerb.Cubic)} curves");

        // The bumps belong on the outside. A cloud that bulges inwards is what
        // you get from taking the winding of the points on trust, and the
        // reader draws the box from either corner.
        Check("the cloud reaches beyond the box it was drawn from",
            cloud.Bounds.Left < 100 - 2 && cloud.Bounds.Right > 300 + 2,
            $"({cloud.Bounds.Left:0.#}..{cloud.Bounds.Right:0.#})");

        var reversed = Of(AnnotationKind.Cloud, 2f, (300, 200), (100, 100));
        Check("drawn the other way round it comes out the same size",
            Math.Abs(reversed.Bounds.Width - cloud.Bounds.Width) < 0.5f
            && Math.Abs(reversed.Bounds.Height - cloud.Bounds.Height) < 0.5f,
            $"{reversed.Bounds.Width:0.#}x{reversed.Bounds.Height:0.#} vs {cloud.Bounds.Width:0.#}x{cloud.Bounds.Height:0.#}");

        Check("the middle of a cloud is not on its line",
            !cloud.HitTest(new Vector2(200, 150)));
        Check("but its edge is", cloud.HitTest(new Vector2(200, 100)));
    }

    private static void FillAndTurn()
    {
        Section("Marks — fill, and turning a mark on the spot");

        var hollow = Of(AnnotationKind.Rectangle, 2f, (100, 100), (300, 200));
        Check("an outlined box is not picked up from inside", !hollow.HitTest(new Vector2(200, 150)));

        var filled = hollow.WithStyle(hollow.Style with { Fill = new AnnotationColor(255, 214, 0), FillOpacity = 0.4f });
        Check("a filled one is", filled.HitTest(new Vector2(200, 150)));
        Check("the fill's opacity survives as an alpha", filled.Style.FillAlpha == 102,
            $"got {filled.Style.FillAlpha}");

        var turned = hollow.WithRotation(90f);
        Check("turning a box keeps its two corners", turned.Points.Count == 2
            && Math.Abs(turned.Points[0].X - 100f) < 0.01f);
        CheckClose("and swaps what it covers", turned.Bounds.Width, hollow.Bounds.Height, 0.01);

        // A corner of the turned box, worked out by hand: the box is 200x100
        // about (200,150), so a quarter turn puts its left edge at y = 50.
        Check("the turned box lands where the geometry says",
            turned.HitTest(new Vector2(200, 50)), $"bounds {turned.Bounds}");
        Check("and no longer where it was", !turned.HitTest(new Vector2(110, 100)));

        var backAgain = turned.WithRotation(0f);
        Check("turning it back leaves the mark it started as",
            Math.Abs(backAgain.Bounds.Width - hollow.Bounds.Width) < 0.01f);
    }

    private static void Resizing()
    {
        Section("Marks — stretching one by a corner");

        var box = Of(AnnotationKind.Rectangle, 2f, (100, 100), (300, 200));
        var anchor = new Vector2(100, 100);
        var wider = box.Scaled(anchor, 2f, 1f);

        CheckClose("the anchored corner stays put", wider.Points[0].X, 100, 0.01);
        CheckClose("and the dragged one moves by the factor", wider.Points[1].X, 500, 0.01);
        CheckClose("the other axis is left alone", wider.Points[1].Y, 200, 0.01);

        var stroke = Of(AnnotationKind.Ink, 2f, (10, 10), (20, 30), (30, 10));
        var scaled = stroke.Scaled(new Vector2(10, 10), 2f, 2f);
        Check("every point of a stroke moves with it",
            Math.Abs(scaled.Points[1].X - 30f) < 0.01f && Math.Abs(scaled.Points[2].X - 50f) < 0.01f);
    }

    private static void Handles()
    {
        Section("Marks — the grips round a selected mark");

        var box = Of(AnnotationKind.Rectangle, 2f, (100, 100), (300, 200));
        var grips = AnnotationHandles.For(box);
        Check("there are eight grips and a knob to turn by", grips.Count == 9);

        Check("the top-left grip is on the top-left corner",
            grips.Any(g => g.Grip.Which == MarkHandle.TopLeft && Vector2.Distance(g.At, new Vector2(100, 100)) < 0.01f));
        Check("the turn knob floats above the mark",
            grips.Any(g => g.Grip.Which == MarkHandle.Rotate
                && Math.Abs(g.At.X - 200f) < 0.01f
                && Math.Abs(g.At.Y - (100f - AnnotationHandles.RotateOffsetPt)) < 0.01f));

        Check("a click on a corner finds that grip",
            AnnotationHandles.At(box, new Vector2(301, 199), 6f) == MarkHandle.BottomRight);
        Check("a click in the middle of nowhere finds none",
            AnnotationHandles.At(box, new Vector2(200, 150), 6f) == MarkHandle.None);

        var widened = AnnotationHandles.Drag(box, new MarkGrip(MarkHandle.Right), new Vector2(500, 150));
        CheckClose("dragging the right edge moves that edge", widened.Box().Right, 500, 0.01);
        CheckClose("and leaves the left one where it was", widened.Box().Left, 100, 0.01);
        CheckClose("and does not touch the height", widened.Box().Height, 100, 0.01);

        var squeezed = AnnotationHandles.Drag(box, new MarkGrip(MarkHandle.Right), new Vector2(80, 150));
        Check("a mark cannot be squeezed inside out", squeezed.Box().Width > 0.5f, $"{squeezed.Box().Width}");

        // A turned mark is stretched along its own edge, and the corner
        // opposite the grip has to stay put on the sheet while that happens.
        var turned = box.WithRotation(35f);
        var pulled = AnnotationHandles.Drag(turned, new MarkGrip(MarkHandle.BottomRight),
            AnnotationGeometry.Rotate(new Vector2(400, 260), turned.Centre, 35f));

        // Where the anchored corner sits on the sheet: its own frame's corner,
        // turned about its own middle — both of which the stretch moved.
        static Vector2 TopLeftOnSheet(Annotation mark) => AnnotationGeometry.Rotate(
            new Vector2(AnnotationHandles.Frame(mark).Left, AnnotationHandles.Frame(mark).Top),
            mark.Centre,
            mark.RotationDeg);

        CheckClose("the turn survives the stretch", pulled.RotationDeg, 35f, 0.01);
        Check("and the anchored corner has not moved on the sheet",
            Vector2.Distance(TopLeftOnSheet(turned), TopLeftOnSheet(pulled)) < 0.05f,
            $"moved {Vector2.Distance(TopLeftOnSheet(turned), TopLeftOnSheet(pulled)):0.###} pt");
        CheckClose("while the mark grew along its own axis", pulled.Box().Width, 300, 0.5);

        var spun = AnnotationHandles.Drag(box, new MarkGrip(MarkHandle.Rotate), new Vector2(400, 150));
        CheckClose("dragging the knob to the right turns the mark a quarter", spun.RotationDeg, 90, 0.01);
        CheckClose("and it snaps to the nearest step when asked to", AnnotationHandles.Snap(97f), 90, 0.01);

        var note = new Annotation(AnnotationKind.Note, [new Vector2(10, 10)], AnnotationStyle.Default);
        Check("a note has no grips — it is an icon, not a shape", AnnotationHandles.For(note).Count == 0);

        VertexGrips();
    }

    /// <summary>
    /// Shapes placed corner by corner are corrected corner by corner. The frame
    /// grips move every point at once, which for a room traced round its walls
    /// means redrawing it to fix the one corner that missed.
    /// </summary>
    private static void VertexGrips()
    {
        Section("Marks — the grips on a shape's own corners");

        var room = Of(AnnotationKind.Polygon, 2f, (100, 100), (300, 100), (300, 260), (100, 260));
        var grips = AnnotationHandles.For(room);

        Check("a traced shape is gripped by its corners and turned by its knob", grips.Count == 5);
        Check("one grip per corner, in the order they were placed",
            grips.Take(4).Select((g, i) => g.Grip.Which == MarkHandle.Vertex && g.Grip.Index == i).All(ok => ok));

        Check("a click on a corner finds that corner",
            AnnotationHandles.At(room, new Vector2(299, 262), 6f) is { Which: MarkHandle.Vertex, Index: 2 });

        var fixedUp = AnnotationHandles.Drag(room, new MarkGrip(MarkHandle.Vertex, 2), new Vector2(340, 300));
        CheckClose("dragging it moves that corner", fixedUp.Points[2].X, 340, 0.01);
        Check("and leaves the others where they were",
            Vector2.Distance(fixedUp.Points[0], room.Points[0]) < 0.01f
            && Vector2.Distance(fixedUp.Points[1], room.Points[1]) < 0.01f
            && Vector2.Distance(fixedUp.Points[3], room.Points[3]) < 0.01f);

        // The grips are seen where the mark is seen, so on a turned shape the
        // pointer has to come back out of the turn before it becomes a corner.
        var turnedRoom = room.WithRotation(30f);
        var seen = AnnotationHandles.For(turnedRoom)[1].At;
        var again = AnnotationHandles.Drag(turnedRoom, new MarkGrip(MarkHandle.Vertex, 1), seen);
        Check("dragging a corner of a turned shape to where it already is leaves it there",
            Vector2.Distance(again.Points[1], turnedRoom.Points[1]) < 0.05f,
            $"moved {Vector2.Distance(again.Points[1], turnedRoom.Points[1]):0.###} pt");

        // A stroke has hundreds of points; a grip on each would be a wall of
        // squares over the drawing, so freehand keeps its frame.
        var stroke = Ink((10, 10), (20, 20), (30, 12), (40, 30), (50, 18));
        Check("freehand is gripped by its frame, not by its points",
            !AnnotationHandles.HasVertexGrips(stroke));

        // And a measurement is edited the same way — which is the whole reason
        // this exists: a room measured one corner short is fixed, not redrawn.
        var scale = new SheetScale(0.05, MeasureUnit.Metre);
        var measured = new Annotation(
            AnnotationKind.Distance,
            [new Vector2(0, 0), new Vector2(100, 0)],
            AnnotationStyle.Default,
            scale: scale);

        var stretched = AnnotationHandles.Drag(measured, new MarkGrip(MarkHandle.Vertex, 1), new Vector2(200, 0));
        Check("a measurement dragged by an end keeps its calibration", stretched.Scale is not null);
        Check("and says the new length", stretched.Text != measured.Text && stretched.Text.StartsWith("10"),
            $"{measured.Text} → {stretched.Text}");

        // A highlight belongs to the words it covers. It can be taken back, but
        // not moved off them, stretched past them or recoloured into a lie.
        var wash = Of(AnnotationKind.Highlight, 1f, (100, 100), (300, 120));
        Check("a highlight cannot be changed once made", !Annotation.CanBeChanged(wash.Kind));
        Check("so it cannot be moved", !AnnotationHandles.CanMove(wash));
        Check("nor stretched", !AnnotationHandles.CanResize(wash));
        Check("nor turned", !AnnotationHandles.CanRotate(wash));
        Check("and it carries no grips at all", AnnotationHandles.For(wash).Count == 0);
        Check("everything else can still be changed", Annotation.CanBeChanged(AnnotationKind.Rectangle)
            && Annotation.CanBeChanged(AnnotationKind.FreeText)
            && Annotation.CanBeChanged(AnnotationKind.Note));
    }

    private static void Store()
    {
        Section("Marks — keeping them, and taking them back");

        var store = new AnnotationStore();
        Check("a fresh store has nothing to save", !store.IsDirty);

        var first = Ink((10, 10), (100, 100));
        var second = Of(AnnotationKind.Rectangle, 2f, (50, 50), (200, 200));
        store.Add(3, first);
        store.Add(3, second);

        Check("both marks are on the sheet", store.CountForPage(3) == 2);
        Check("and the document knows it has something to save", store.IsDirty);
        Check("a sheet with no marks reports none", store.CountForPage(0) == 0);

        // Both marks cover this point; the later one is on top.
        Check("the topmost mark wins a click",
            ReferenceEquals(store.HitTest(3, new Vector2(50, 50.5f)), second));

        var moved = first.MovedBy(new Vector2(20, 0));
        store.Replace(3, first, moved);
        Check("moving a mark keeps its identity", moved.Id == first.Id);
        Check("and replaces it rather than adding another", store.CountForPage(3) == 2);

        store.Undo();
        Check("undo puts the mark back where it was",
            store.ForPage(3).Any(m => m.Id == first.Id && Math.Abs(m.Points[0].X - 10f) < 0.01f));

        store.Redo();
        Check("redo moves it again",
            store.ForPage(3).Any(m => m.Id == first.Id && Math.Abs(m.Points[0].X - 30f) < 0.01f));

        store.Remove(3, second);
        Check("a rubbed-out mark is gone", store.CountForPage(3) == 1);
        store.Undo();
        Check("and undo brings it back", store.CountForPage(3) == 2);

        while (store.CanUndo) store.Undo();
        Check("undoing everything empties the sheet", store.CountForPage(3) == 0);
        Check("but the document is still dirty — the file was never like this either", store.IsDirty);

        store.MarkSaved();
        Check("a save settles it", !store.IsDirty);

        store.Load(new Dictionary<int, IReadOnlyList<Annotation>> { [1] = [first] });
        Check("loading from a file is not a change to save", !store.IsDirty);
        Check("and it is not something to undo", !store.CanUndo);
        Check("the loaded mark is there", store.CountForPage(1) == 1);
    }

    // --- the file --------------------------------------------------------

    private static IReadOnlyDictionary<int, IReadOnlyList<Annotation>> OnePage(int page, params Annotation[] marks) =>
        new Dictionary<int, IReadOnlyList<Annotation>> { [page] = marks };

    private static async Task RoundTripAsync()
    {
        Section("Marks — writing them to the file and reading them back");

        var queue = PdfRenderQueue.Shared;
        string source = TestPdf.WriteRectangle("zenink-annot-roundtrip", "0 450 100 150");
        string copy = Path.Combine(Path.GetTempPath(), "zenink-annot-roundtrip-copy.pdf");

        var marks = new[]
        {
            new Annotation(
                AnnotationKind.Ink,
                [new Vector2(40, 60), new Vector2(120, 140), new Vector2(260, 90)],
                new AnnotationStyle(new AnnotationColor(216, 32, 32), 2.5f),
                author: "Manuel"),
            new Annotation(
                AnnotationKind.Rectangle,
                [new Vector2(60, 300), new Vector2(300, 420)],
                new AnnotationStyle(new AnnotationColor(0, 90, 200), 1.5f)),
            new Annotation(
                AnnotationKind.Arrow,
                [new Vector2(80, 500), new Vector2(320, 560)],
                new AnnotationStyle(new AnnotationColor(20, 140, 60), 3f)),
            new Annotation(
                AnnotationKind.Highlight,
                [new Vector2(40, 200), new Vector2(360, 240)],
                new AnnotationStyle(new AnnotationColor(255, 214, 0), 1f)),
            new Annotation(
                AnnotationKind.Note,
                [new Vector2(340, 40)],
                AnnotationStyle.Default,
                text: "Cota sin comprobar — sección A-A'"),
            // Two lines of text picked out in one go, which is what the
            // highlighter makes: one mark, two quads in the file.
            new Annotation(
                AnnotationKind.Highlight,
                [
                    new Vector2(40, 250), new Vector2(300, 264),
                    new Vector2(40, 268), new Vector2(220, 282),
                ],
                new AnnotationStyle(new AnnotationColor(255, 214, 0), 1f)),
            new Annotation(
                AnnotationKind.Polyline,
                [new Vector2(40, 440), new Vector2(90, 400), new Vector2(150, 455), new Vector2(210, 410)],
                new AnnotationStyle(new AnnotationColor(120, 40, 200), 2f)),
            new Annotation(
                AnnotationKind.Polygon,
                [new Vector2(60, 120), new Vector2(180, 100), new Vector2(210, 175), new Vector2(80, 190)],
                new AnnotationStyle(new AnnotationColor(0, 120, 90), 1.8f, new AnnotationColor(0, 200, 150), 0.3f)),
            new Annotation(
                AnnotationKind.Cloud,
                [new Vector2(230, 480), new Vector2(370, 570)],
                new AnnotationStyle(new AnnotationColor(230, 90, 0), 2f)),
            // A turned mark: the turn is its own, not the sheet's, and it has
            // to come back the same way round.
            new Annotation(
                AnnotationKind.Rectangle,
                [new Vector2(120, 200), new Vector2(260, 240)],
                new AnnotationStyle(new AnnotationColor(90, 90, 90), 1.5f),
                rotationDeg: 30f),
        };

        await queue.SaveChangesCopyAsync(TestPlan.Turns(source, 0), copy, OnePage(0, marks));

        var reopened = await queue.OpenDocumentAsync(copy);
        var readBack = await queue.ReadAnnotationsAsync(reopened.DocumentId);

        Check("the marks come back on the sheet they were made on", readBack.ContainsKey(0));
        var found = readBack.TryGetValue(0, out var list) ? list : [];
        Check($"every mark comes back ({found.Count} of {marks.Length})", found.Count == marks.Length);

        foreach (var original in marks)
        {
            var match = found.FirstOrDefault(m => m.Id == original.Id);
            if (match is null)
            {
                Check($"{original.Kind}: comes back with the same identity", false);
                continue;
            }

            Check($"{original.Kind}: keeps its kind", match.Kind == original.Kind);
            Check($"{original.Kind}: keeps its colour", match.Style.Color == original.Style.Color);
            CheckClose($"{original.Kind}: keeps its width", match.Style.WidthPt, original.Style.WidthPt, 0.01);

            float drift = 0f;
            for (int i = 0; i < Math.Min(match.Points.Count, original.Points.Count); i++)
            {
                drift = Math.Max(drift, Vector2.Distance(match.Points[i], original.Points[i]));
            }

            Check($"{original.Kind}: lands back on the same spot of the sheet",
                match.Points.Count == original.Points.Count && drift < PointTolerance,
                $"drifted {drift:0.###} pt");

            if (original.Text.Length > 0)
            {
                Check($"{original.Kind}: keeps its comment, accents and all", match.Text == original.Text, match.Text);
            }

            if (original.RotationDeg != 0f)
            {
                CheckClose($"{original.Kind}: keeps its own turn", match.RotationDeg, original.RotationDeg, 0.01);
            }

            if (original.Style.Fill is { } fill)
            {
                Check($"{original.Kind}: keeps its fill", match.Style.Fill == fill);
                CheckClose($"{original.Kind}: keeps how see-through the fill is",
                    match.Style.FillOpacity, original.Style.FillOpacity, 0.01);
            }
        }

        await queue.CloseDocumentAsync(reopened.DocumentId);
        File.Delete(copy);
        File.Delete(source);
    }

    /// <summary>
    /// The same round trip on a sheet the file itself turns. Sheet space is
    /// defined by the page as it renders, so a mark at the top-left of a
    /// /Rotate 90 page has to come back at the top-left of it.
    /// </summary>
    private static async Task RoundTripRotatedAsync()
    {
        Section("Marks — on a sheet the file already turns");

        var queue = PdfRenderQueue.Shared;

        foreach (int rotate in new[] { 90, 180, 270 })
        {
            string source = TestPdf.WriteCornerMark(rotate);
            string copy = Path.Combine(Path.GetTempPath(), $"zenink-annot-rot-{rotate}.pdf");

            var mark = new Annotation(
                AnnotationKind.Line,
                [new Vector2(30, 40), new Vector2(200, 120)],
                new AnnotationStyle(AnnotationColor.Red, 2f));

            await queue.SaveChangesCopyAsync(TestPlan.Turns(source, 0), copy, OnePage(0, mark));

            var reopened = await queue.OpenDocumentAsync(copy);
            var readBack = await queue.ReadAnnotationsAsync(reopened.DocumentId);
            var found = readBack.TryGetValue(0, out var list) && list.Count == 1 ? list[0] : null;

            if (found is null)
            {
                Check($"/Rotate {rotate}: the mark comes back", false);
            }
            else
            {
                float drift = Math.Max(
                    Vector2.Distance(found.Points[0], mark.Points[0]),
                    Vector2.Distance(found.Points[1], mark.Points[1]));
                Check($"/Rotate {rotate}: the mark comes back on the same part of the sheet",
                    drift < PointTolerance, $"drifted {drift:0.###} pt");
            }

            await queue.CloseDocumentAsync(reopened.DocumentId);
            File.Delete(copy);
            File.Delete(source);
        }
    }

    /// <summary>
    /// Turning a sheet and marking it in the same sitting. The turn moves the
    /// paper; the mark has to stay on the drawing, which means it comes back
    /// turned with it.
    /// </summary>
    private static async Task SavingWithATurnAsync()
    {
        Section("Marks — saved together with a turn");

        var queue = PdfRenderQueue.Shared;
        string source = TestPdf.WriteRectangle("zenink-annot-turn", "0 450 100 150");
        string copy = Path.Combine(Path.GetTempPath(), "zenink-annot-turn-copy.pdf");

        var anchor = new Vector2(40, 60);
        var mark = new Annotation(
            AnnotationKind.Ink,
            [anchor, new Vector2(140, 160)],
            new AnnotationStyle(AnnotationColor.Red, 2f));

        await queue.SaveChangesCopyAsync(TestPlan.Turns(source, 1), copy, OnePage(0, mark));

        var reopened = await queue.OpenDocumentAsync(copy);
        var readBack = await queue.ReadAnnotationsAsync(reopened.DocumentId);
        var found = readBack.TryGetValue(0, out var list) && list.Count == 1 ? list[0] : null;

        var expected = SheetTurn.ToDisplay(anchor, TestPdf.PageWidth, TestPdf.PageHeight, 1);
        Check("the sheet comes back turned",
            Math.Abs(reopened.Pages[0].WidthPt - TestPdf.PageHeight) < 0.5f);
        Check("and the mark has turned with the drawing under it",
            found is not null && Vector2.Distance(found.Points[0], expected) < PointTolerance,
            found is null ? "no mark" : $"expected {expected}, got {found.Points[0]}");

        await queue.CloseDocumentAsync(reopened.DocumentId);
        File.Delete(copy);
        File.Delete(source);
    }

    /// <summary>
    /// A drawing that arrives with someone else's markup on it. ZenInk neither
    /// claims it nor disturbs it: it is not ours to rewrite, and PDFium already
    /// knows how to draw it.
    /// </summary>
    private static async Task LeavingOtherToolsAloneAsync()
    {
        Section("Marks — someone else's markup is left where it is");

        var queue = PdfRenderQueue.Shared;
        string source = TestPdf.WriteForeignAnnotation("zenink-annot-foreign");
        string copy = Path.Combine(Path.GetTempPath(), "zenink-annot-foreign-copy.pdf");

        var opened = await queue.OpenDocumentAsync(source);
        var beforeSave = await queue.ReadAnnotationsAsync(opened.DocumentId);
        Check("an annotation from another tool is not read as one of ours", beforeSave.Count == 0);

        var render = await queue.RequestPagePreviewAsync(opened.DocumentId, 0, TestPdf.PageHeight);
        Check("and it is drawn into the sheet, so the reviewer can see it",
            render is { } image && CountBlue(image) > 200,
            render is { } r ? $"{CountBlue(r)} blue pixels" : "no render");
        await queue.CloseDocumentAsync(opened.DocumentId);

        var ours = new Annotation(
            AnnotationKind.Ink,
            [new Vector2(40, 60), new Vector2(140, 160)],
            new AnnotationStyle(AnnotationColor.Red, 2f));

        await queue.SaveChangesCopyAsync(TestPlan.Turns(source, 0), copy, OnePage(0, ours));

        var reopened = await queue.OpenDocumentAsync(copy);
        var readBack = await queue.ReadAnnotationsAsync(reopened.DocumentId);
        Check("our mark is written", readBack.TryGetValue(0, out var mine) && mine.Count == 1);

        var afterSave = await queue.RequestPagePreviewAsync(reopened.DocumentId, 0, TestPdf.PageHeight);
        Check("and the other tool's annotation is still on the sheet",
            afterSave is { } after && CountBlue(after) > 200,
            afterSave is { } a ? $"{CountBlue(a)} blue pixels" : "no render");

        await queue.CloseDocumentAsync(reopened.DocumentId);
        File.Delete(copy);
        File.Delete(source);
    }

    /// <summary>
    /// Once a mark is in the file, PDFium would happily draw it into the tiles
    /// as well — and the viewer is already drawing it on top. This is the check
    /// that it does not appear twice.
    /// </summary>
    private static async Task NotDrawnTwiceAsync()
    {
        Section("Marks — a saved mark is not drawn into the sheet as well");

        var queue = PdfRenderQueue.Shared;
        string source = TestPdf.WriteRectangle("zenink-annot-twice", "0 450 100 150");
        string copy = Path.Combine(Path.GetTempPath(), "zenink-annot-twice-copy.pdf");

        var mark = new Annotation(
            AnnotationKind.Ink,
            [new Vector2(40, 300), new Vector2(360, 320)],
            new AnnotationStyle(new AnnotationColor(230, 20, 20), 6f));

        await queue.SaveChangesCopyAsync(TestPlan.Turns(source, 0), copy, OnePage(0, mark));

        var reopened = await queue.OpenDocumentAsync(copy);
        var render = await queue.RequestPagePreviewAsync(reopened.DocumentId, 0, TestPdf.PageHeight);

        Check("the sheet still renders", render is not null);
        Check("but ZenInk's own mark is not in it — the viewer draws that itself",
            render is { } image && CountRed(image) == 0,
            render is { } r ? $"{CountRed(r)} red pixels" : "no render");

        await queue.CloseDocumentAsync(reopened.DocumentId);
        File.Delete(copy);
        File.Delete(source);
    }

    /// <summary>
    /// The check behind the whole reason filled shapes go into the file as path
    /// objects: a fill has to let the drawing under it show through.
    ///
    /// A PDF annotation's transparency lives in /CA, which PDFium cannot write.
    /// An object inside the annotation carries its own alpha instead, and
    /// PDFium builds the appearance — graphics state included — around it. This
    /// renders the saved file the way another reader would and looks at the
    /// pixels.
    /// </summary>
    private static async Task FilledMarkIsSeeThroughAsync()
    {
        Section("Marks — a fill that does not bury the drawing");

        var queue = PdfRenderQueue.Shared;

        // Black block over the top-left of the sheet: sheet y runs down, so
        // PDF "0 450 100 150" is the corner the mark will sit on.
        string source = TestPdf.WriteRectangle("zenink-annot-fill", "0 450 100 150");
        string copy = Path.Combine(Path.GetTempPath(), "zenink-annot-fill-copy.pdf");

        var filled = new Annotation(
            AnnotationKind.Rectangle,
            [new Vector2(10, 10), new Vector2(200, 140)],
            new AnnotationStyle(new AnnotationColor(220, 30, 30), 2f, new AnnotationColor(220, 30, 30), 0.35f));

        await queue.SaveChangesCopyAsync(TestPlan.Turns(source, 0), copy, OnePage(0, filled));

        // Read straight from PDFium, not through the queue: the queue hides
        // ZenInk's own marks, and what is wanted here is the other reader's view.
        var document = fpdfview.FPDF_LoadDocument(copy, null);
        Check("the saved file opens", document is not null);

        if (document is not null)
        {
            var page = fpdfview.FPDF_LoadPage(document, 0);
            var image = Bitmap.RenderWithAnnotations(page, TestPdf.PageWidth, TestPdf.PageHeight);

            var overInk = Bitmap.Pixel(image, TestPdf.PageWidth, 50, 60);
            var overPaper = Bitmap.Pixel(image, TestPdf.PageWidth, 150, 60);
            var untouched = Bitmap.Pixel(image, TestPdf.PageWidth, 300, 400);

            Check("over the drawing the fill stays dark — the ink still reads through it",
                overInk.R < 160 && overInk.R > overInk.B + 25,
                $"B={overInk.B} G={overInk.G} R={overInk.R}");

            Check("over the paper it is a wash, not a block of colour",
                overPaper.R > 200 && overPaper.B < 220 && overPaper.B > 120,
                $"B={overPaper.B} G={overPaper.G} R={overPaper.R}");

            Check("and the rest of the sheet is untouched",
                untouched is { B: > 240, G: > 240, R: > 240 });

            fpdfview.FPDF_ClosePage(page);
            fpdfview.FPDF_CloseDocument(document);
        }

        File.Delete(copy);
        File.Delete(source);
    }

    /// <summary>
    /// Words typed onto a drawing have to leave ZenInk as words: text objects in
    /// a standard face, so that whoever opens the file sees them without any
    /// font travelling along.
    /// </summary>
    private static async Task WrittenMarksReachTheFileAsync()
    {
        Section("Marks — written words go into the file as text");

        var queue = PdfRenderQueue.Shared;
        string source = TestPdf.WriteRectangle("zenink-annot-text", "0 450 100 150");
        string copy = Path.Combine(Path.GetTempPath(), "zenink-annot-text-copy.pdf");

        var written = new Annotation(
            AnnotationKind.FreeText,
            [new Vector2(60, 300)],
            new AnnotationStyle(new AnnotationColor(220, 30, 30), 2f, null, 0.35f, 22f),
            text: "Sección A-A'\nrevisar año 1½");

        await queue.SaveChangesCopyAsync(TestPlan.Turns(source, 0), copy, OnePage(0, written));

        var reopened = await queue.OpenDocumentAsync(copy);
        var readBack = await queue.ReadAnnotationsAsync(reopened.DocumentId);
        var match = readBack.TryGetValue(0, out var list) && list.Count == 1 ? list[0] : null;

        Check("the mark comes back", match is not null);
        if (match is not null)
        {
            Check("as a written one", match.Kind == AnnotationKind.FreeText);
            Check("with its words, accents and line break", match.Text == written.Text,
                match.Text.Replace("\n", "\\n"));
            CheckClose("and its type size", match.Style.FontSizePt, 22f, 0.01);
            Check("in the same place on the sheet",
                Vector2.Distance(match.Points[0], written.Points[0]) < PointTolerance);
        }
        await queue.CloseDocumentAsync(reopened.DocumentId);

        // What another reader sees.
        var document = fpdfview.FPDF_LoadDocument(copy, null);
        if (document is not null)
        {
            var page = fpdfview.FPDF_LoadPage(document, 0);
            var image = Bitmap.RenderWithAnnotations(page, TestPdf.PageWidth, TestPdf.PageHeight);

            int red = 0;
            for (int i = 0; i + 3 < image.Length; i += 4)
            {
                if (image[i + 2] > 150 && image[i + 1] < 110 && image[i] < 110) red++;
            }

            Check("and the words are drawn for anyone opening the drawing", red > 150, $"{red} píxeles");

            fpdfview.FPDF_ClosePage(page);
            fpdfview.FPDF_CloseDocument(document);
        }

        File.Delete(copy);
        File.Delete(source);
    }

    /// <summary>
    /// A measurement through the file and back.
    ///
    /// It is the one mark whose text nobody typed, so the round trip has to
    /// carry the calibration as well as the geometry: a length that comes back
    /// without its scale is a line, and one that comes back with a different
    /// scale is a lie. What another reader sees matters here more than for any
    /// other mark — the number is the whole content.
    /// </summary>
    private static async Task MeasurementsReachTheFileAsync()
    {
        Section("Marks — a measurement goes into the file with its number");

        var queue = PdfRenderQueue.Shared;
        string source = TestPdf.WriteRectangle("zenink-annot-measure", "0 450 100 150");
        string copy = Path.Combine(Path.GetTempPath(), "zenink-annot-measure-copy.pdf");

        // A hundred points of paper called five metres: the sheet is at 1:141,
        // and the line drawn is two hundred points, so it is ten metres long.
        var scale = SheetScale.From(100, 5, MeasureUnit.Metre)!.Value;
        var measured = new Annotation(
            AnnotationKind.Distance,
            [new Vector2(60, 300), new Vector2(260, 300)],
            new AnnotationStyle(new AnnotationColor(220, 30, 30), 2f, null, 0.35f, 16f),
            scale: scale);

        var area = new Annotation(
            AnnotationKind.Area,
            [new Vector2(60, 360), new Vector2(160, 360), new Vector2(160, 460), new Vector2(60, 460)],
            new AnnotationStyle(new AnnotationColor(30, 120, 220), 2f, new AnnotationColor(30, 120, 220)),
            scale: scale);

        await queue.SaveChangesCopyAsync(TestPlan.Turns(source, 0), copy, OnePage(0, measured, area));

        var reopened = await queue.OpenDocumentAsync(copy);
        var readBack = await queue.ReadAnnotationsAsync(reopened.DocumentId);
        var list = readBack.TryGetValue(0, out var found) ? found : [];

        Check("both measurements come back", list.Count == 2);

        var distance = list.FirstOrDefault(mark => mark.Kind == AnnotationKind.Distance);
        Check("the distance comes back as a distance", distance is not null);
        if (distance is not null)
        {
            Check("with the scale it was taken at",
                distance.Scale is { } read
                && Math.Abs(read.UnitsPerPoint - scale.UnitsPerPoint) < 1e-9
                && read.Unit == MeasureUnit.Metre);

            Check("and so with the same number", distance.Text == measured.Text, distance.Text);
            Check("which is the length of the line it is drawn on", measured.Text.StartsWith("10"), measured.Text);
        }

        Check("the area comes back as an area",
            list.FirstOrDefault(mark => mark.Kind == AnnotationKind.Area) is { } back
            && back.Text == area.Text);

        await queue.CloseDocumentAsync(reopened.DocumentId);

        // And what anyone else opening the drawing sees: the number, drawn.
        var document = fpdfview.FPDF_LoadDocument(copy, null);
        if (document is not null)
        {
            var page = fpdfview.FPDF_LoadPage(document, 0);
            var image = Bitmap.RenderWithAnnotations(page, TestPdf.PageWidth, TestPdf.PageHeight);

            int red = 0;
            for (int i = 0; i + 3 < image.Length; i += 4)
            {
                if (image[i + 2] > 150 && image[i + 1] < 110 && image[i] < 110) red++;
            }

            Check("and the number is drawn for whoever opens the drawing", red > 150, $"{red} píxeles");

            fpdfview.FPDF_ClosePage(page);
            fpdfview.FPDF_CloseDocument(document);
        }

        File.Delete(copy);
        File.Delete(source);
    }

    /// <summary>
    /// A stamp — the box that says who reviewed a drawing — through the file
    /// and back.
    ///
    /// It is deliberately a mark and not a signature: nothing is sealed and
    /// anybody can take it off. What has to survive is its words and its box,
    /// and its lines have to be drawn for whoever opens the drawing next.
    /// </summary>
    private static async Task StampsReachTheFileAsync()
    {
        Section("Marks — a stamp goes into the file as a box with words in it");

        var queue = PdfRenderQueue.Shared;
        string source = TestPdf.WriteRectangle("zenink-annot-stamp", "0 450 100 150");
        string copy = Path.Combine(Path.GetTempPath(), "zenink-annot-stamp-copy.pdf");

        var stamp = new Annotation(
            AnnotationKind.Stamp,
            [new Vector2(40, 200), new Vector2(240, 280)],
            new AnnotationStyle(new AnnotationColor(20, 20, 24), 1f),
            text: "Revisado por\nMANUEL MONTERO\nFecha: 02/09/2026");

        Check("its shape is its box", stamp.Outline.Count == 5);

        // What a WinUI text box hands back for a line break is a lone carriage
        // return. Read as one line, a stamp typed in the panel goes into the
        // file as one long line — right on screen, wrong in the drawing that
        // was sent on. Found by putting a stamp on a real plan and zooming in.
        Check("a line break typed in the panel is still a line break",
            AnnotationText.Lines("Revisado por\rMANUEL MONTERO").Count == 2);
        CheckClose("which is what was dragged out", stamp.Box().Width, 200, 0.01);

        await queue.SaveChangesCopyAsync(TestPlan.Turns(source, 0), copy, OnePage(0, stamp));

        var reopened = await queue.OpenDocumentAsync(copy);
        var readBack = await queue.ReadAnnotationsAsync(reopened.DocumentId);
        var match = readBack.TryGetValue(0, out var list) && list.Count == 1 ? list[0] : null;

        Check("the stamp comes back", match is not null);
        if (match is not null)
        {
            Check("as a stamp", match.Kind == AnnotationKind.Stamp);
            Check("with its lines", match.Text == stamp.Text, match.Text.Replace("\n", "\\n"));
            Check("and its box", Math.Abs(match.Box().Width - 200) < PointTolerance * 4);
        }
        await queue.CloseDocumentAsync(reopened.DocumentId);

        // And what anybody else sees: the words, drawn on the sheet.
        var document = fpdfview.FPDF_LoadDocument(copy, null);
        if (document is not null)
        {
            var page = fpdfview.FPDF_LoadPage(document, 0);
            var image = Bitmap.RenderWithAnnotations(page, TestPdf.PageWidth, TestPdf.PageHeight);

            int dark = 0;
            for (int i = 0; i + 3 < image.Length; i += 4)
            {
                if (image[i] < 90 && image[i + 1] < 90 && image[i + 2] < 90) dark++;
            }

            // The rectangle the fixture draws is black too, so this counts only
            // what is inside the stamp's own box.
            int inStamp = 0;
            for (int y = 205; y < 275; y++)
            {
                for (int x = 45; x < 235; x++)
                {
                    int at = ((y * TestPdf.PageWidth) + x) * 4;
                    if (at + 3 < image.Length && image[at] < 120 && image[at + 1] < 120 && image[at + 2] < 120) inStamp++;
                }
            }

            Check("and the stamp is drawn where it was put", inStamp > 200, $"{inStamp} píxeles de {dark}");

            fpdfview.FPDF_ClosePage(page);
            fpdfview.FPDF_CloseDocument(document);
        }

        File.Delete(copy);
        File.Delete(source);
    }

    /// <summary>
    /// Flattening is the one change that cannot be taken back, so it is the one
    /// that most needs pinning: the marks must end up part of the drawing, and
    /// the drawing must still be there afterwards.
    /// </summary>
    private static async Task FlatteningAsync()
    {
        Section("Marks — burning them into the drawing");

        var queue = PdfRenderQueue.Shared;
        string path = Path.Combine(Path.GetTempPath(), "zenink-annot-flat.pdf");
        File.Copy(TestPdf.WriteRectangle("zenink-annot-flat-src", "0 450 100 150"), path, overwrite: true);

        var marks = new[]
        {
            new Annotation(
                AnnotationKind.Ink,
                [new Vector2(40, 300), new Vector2(360, 320)],
                new AnnotationStyle(new AnnotationColor(230, 20, 20), 6f)),
            new Annotation(
                AnnotationKind.FreeText,
                [new Vector2(60, 380)],
                new AnnotationStyle(new AnnotationColor(230, 20, 20), 2f, null, 0.35f, 20f),
                text: "Aprobado"),
        };

        var document = await queue.OpenDocumentAsync(path);
        var outcome = await queue.ApplyChangesInPlaceAsync(
            document.DocumentId, path, TestPlan.Turns(path, 0), OnePage(0, marks), flatten: true);

        Check("the flatten reports success", outcome.Saved, outcome.Error);

        var afterMarks = await queue.ReadAnnotationsAsync(outcome.Document.DocumentId);
        Check("nothing of ours is left to edit", afterMarks.Count == 0);
        await queue.CloseDocumentAsync(outcome.Document.DocumentId);

        // The point of flattening: the marks are the drawing now, so they show
        // even for a reader that never draws annotations.
        var reopened = fpdfview.FPDF_LoadDocument(path, null);
        Check("the file still opens", reopened is not null);

        if (reopened is not null)
        {
            var page = fpdfview.FPDF_LoadPage(reopened, 0);
            var plain = Bitmap.Render(page, 1.0, 0, 0, TestPdf.PageWidth, TestPdf.PageHeight);

            int red = 0, dark = 0;
            for (int i = 0; i + 3 < plain.Length; i += 4)
            {
                if (plain[i + 2] > 150 && plain[i + 1] < 110 && plain[i] < 110) red++;
                if (plain[i] < 60 && plain[i + 1] < 60 && plain[i + 2] < 60) dark++;
            }

            Check("the marks are drawn without asking for annotations at all", red > 500, $"{red} píxeles rojos");
            Check("and the drawing underneath survived", dark > 10000, $"{dark} píxeles negros");

            Check("no annotations are left on the page at all",
                fpdf_annot.FPDFPageGetAnnotCount(page) == 0);

            fpdfview.FPDF_ClosePage(page);
            fpdfview.FPDF_CloseDocument(reopened);
        }

        File.Delete(path);

        await FlatteningToACopyAsync(marks);
    }

    /// <summary>
    /// The same burn, aimed at a copy. This is the way out of the change that
    /// cannot be taken back: the copy is flattened and the original is left with
    /// its marks still editable, so both checks matter equally.
    /// </summary>
    private static async Task FlatteningToACopyAsync(Annotation[] marks)
    {
        var queue = PdfRenderQueue.Shared;
        string source = Path.Combine(Path.GetTempPath(), "zenink-annot-flat-orig.pdf");
        string copy = Path.Combine(Path.GetTempPath(), "zenink-annot-flat-copy.pdf");
        File.Copy(TestPdf.WriteRectangle("zenink-annot-flat-src2", "0 450 100 150"), source, overwrite: true);

        byte[] before = File.ReadAllBytes(source);
        await queue.SaveChangesCopyAsync(TestPlan.Turns(source, 0), copy, OnePage(0, marks), flatten: true);

        Check("flatten to a copy: the original is not touched at all",
            File.ReadAllBytes(source).SequenceEqual(before));

        var flattened = fpdfview.FPDF_LoadDocument(copy, null);
        Check("flatten to a copy: the copy opens", flattened is not null);

        if (flattened is not null)
        {
            var page = fpdfview.FPDF_LoadPage(flattened, 0);
            var plain = Bitmap.Render(page, 1.0, 0, 0, TestPdf.PageWidth, TestPdf.PageHeight);

            int red = 0;
            for (int i = 0; i + 3 < plain.Length; i += 4)
            {
                if (plain[i + 2] > 150 && plain[i + 1] < 110 && plain[i] < 110) red++;
            }

            Check("flatten to a copy: the marks are the drawing in the copy", red > 500, $"{red} píxeles rojos");
            Check("flatten to a copy: nothing is left as an annotation",
                fpdf_annot.FPDFPageGetAnnotCount(page) == 0);

            fpdfview.FPDF_ClosePage(page);
            fpdfview.FPDF_CloseDocument(flattened);
        }

        File.Delete(source);
        File.Delete(copy);
    }

    private static int CountRed(TileBitmapData image) =>
        Count(image, (b, g, r) => r > 150 && g < 100 && b < 100);

    private static int CountBlue(TileBitmapData image) =>
        Count(image, (b, g, r) => b > 130 && r < 110 && g < 150);

    private static int Count(TileBitmapData image, Func<byte, byte, byte, bool> predicate)
    {
        int found = 0;
        for (int i = 0; i + 3 < image.Bgra.Length; i += 4)
        {
            if (predicate(image.Bgra[i], image.Bgra[i + 1], image.Bgra[i + 2])) found++;
        }
        return found;
    }
}
