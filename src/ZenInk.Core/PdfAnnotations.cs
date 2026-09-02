using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

using PDFiumCore;

namespace ZenInk.Core;

/// <summary>
/// Two PDFium entry points PDFiumCore's generated wrapper cannot express.
///
/// Both take an array of points, and the wrapper models FS_POINTF as a single
/// managed object around one native struct — there is no way to hand it a
/// contiguous block of them. Declaring the two functions directly is the whole
/// workaround; everything else goes through PDFiumCore as usual.
/// </summary>
internal static unsafe class PdfiumInk
{
    [DllImport("pdfium")]
    public static extern int FPDFAnnot_AddInkStroke(IntPtr annotation, float* points, nuint count);

    [DllImport("pdfium")]
    public static extern uint FPDFAnnot_GetInkListCount(IntPtr annotation);

    [DllImport("pdfium")]
    public static extern uint FPDFAnnot_GetInkListPath(IntPtr annotation, uint pathIndex, float* buffer, uint length);
}

/// <summary>
/// The affine map between PDF page space (y up, the page's own units, the
/// origin wherever its box puts it) and sheet space (y down, points, origin at
/// the top-left corner of the page as the file renders it).
///
/// It is asked of PDFium rather than built by hand. The page's /Rotate, a crop
/// box that does not start at the origin, and units that are not points all
/// land in the same matrix, and reconstructing that from the page dictionary is
/// exactly the sort of arithmetic that has already cost this project a day. The
/// same trick — probe the library at three separated points and solve — is what
/// keeps the text layer aligned with the ink.
/// </summary>
public readonly struct SheetTransform
{
    /// <summary>
    /// Probes are taken against a device box this many times the page's size in
    /// points, because PDFium reports device coordinates as whole numbers. At 64
    /// the quantum is a hundredth of a point, well under the width of anything
    /// drawn on a drawing.
    /// </summary>
    private const int Precision = 64;

    private readonly float _a, _b, _c, _d, _e, _f;
    private readonly float _ia, _ib, _ic, _id, _ie, _if;

    private SheetTransform(float a, float b, float c, float d, float e, float f)
    {
        _a = a; _b = b; _c = c; _d = d; _e = e; _f = f;

        float determinant = a * d - b * c;
        if (MathF.Abs(determinant) < 1e-9f) determinant = 1f;

        _ia = d / determinant;
        _ib = -b / determinant;
        _ic = -c / determinant;
        _id = a / determinant;
        _ie = (b * f - d * e) / determinant;
        _if = (c * e - a * f) / determinant;
    }

    /// <summary>Derives the map for a loaded page. PDFium thread only.</summary>
    public static SheetTransform ForPage(FpdfPageT page)
    {
        float widthPt = fpdfview.FPDF_GetPageWidthF(page);
        float heightPt = fpdfview.FPDF_GetPageHeightF(page);

        int deviceWidth = Math.Max(1, (int)MathF.Round(widthPt * Precision));
        int deviceHeight = Math.Max(1, (int)MathF.Round(heightPt * Precision));

        var origin = Probe(page, deviceWidth, deviceHeight, 0, 0);
        var alongX = Probe(page, deviceWidth, deviceHeight, widthPt, 0);
        var alongY = Probe(page, deviceWidth, deviceHeight, 0, heightPt);

        float spanX = widthPt <= 0 ? 1f : widthPt;
        float spanY = heightPt <= 0 ? 1f : heightPt;

        return new SheetTransform(
            a: (alongX.X - origin.X) / spanX / Precision,
            b: (alongY.X - origin.X) / spanY / Precision,
            c: (alongX.Y - origin.Y) / spanX / Precision,
            d: (alongY.Y - origin.Y) / spanY / Precision,
            e: origin.X / Precision,
            f: origin.Y / Precision);
    }

    private static Vector2 Probe(FpdfPageT page, int deviceWidth, int deviceHeight, double pageX, double pageY)
    {
        int x = 0, y = 0;
        fpdfview.FPDF_PageToDevice(page, 0, 0, deviceWidth, deviceHeight, 0, pageX, pageY, ref x, ref y);
        return new Vector2(x, y);
    }

    public Vector2 ToSheet(Vector2 pdfPoint) => new(
        _a * pdfPoint.X + _b * pdfPoint.Y + _e,
        _c * pdfPoint.X + _d * pdfPoint.Y + _f);

    public Vector2 ToPdf(Vector2 sheetPoint) => new(
        _ia * sheetPoint.X + _ib * sheetPoint.Y + _ie,
        _ic * sheetPoint.X + _id * sheetPoint.Y + _if);
}

/// <summary>
/// Marks, as PDF annotations. Everything here calls PDFium, so everything here
/// runs on <see cref="PdfRenderQueue"/>'s thread and nowhere else.
///
/// ZenInk's own marks are recognised by a private key in the annotation
/// dictionary. That is what separates them from annotations made elsewhere,
/// which this code reads past and never rewrites: someone else's review
/// comments are not ours to edit, and rewriting them on every save would churn
/// a file for no reason.
/// </summary>
public static class PdfAnnotations
{
    private const int SubtypeText = 1;
    private const int SubtypeHighlight = 9;
    private const int SubtypeStamp = 13;
    private const int SubtypeInk = 15;

    /// <summary>Path draw modes, as FPDFPath_SetDrawMode counts them.</summary>
    private const int FillModeNone = 0;
    private const int FillModeWinding = 2;

    /// <summary>
    /// The face written marks go into the file with. One of the fourteen every
    /// PDF reader has, so nothing has to be embedded and the words show up
    /// wherever the drawing is opened. On screen it is Arial, which shares
    /// Helvetica's metrics.
    /// </summary>
    private const string StandardFont = "Helvetica";

    /// <summary>
    /// How far off the measured line the number sits, so it never lands on the
    /// very ink it is about.
    /// </summary>
    private const float MeasureLabelOffsetPt = 4f;

    /// <summary>Annotation flag bit 3: print this annotation. Without it, most readers will not.</summary>
    private const int FlagPrint = 4;

    /// <summary>Annotation flag bit 2: do not display. Used on the viewer's own handle only.</summary>
    private const int FlagHidden = 2;

    /// <summary>Normal appearance, the only one these marks have.</summary>
    private const int AppearanceNormal = 0;

    /// <summary>
    /// Reads back the marks ZenInk itself wrote, in sheet space. Annotations
    /// from other tools are skipped: PDFium draws those into the tiles, which
    /// is the right way to show a mark this program cannot edit.
    /// </summary>
    public static List<Annotation> Read(FpdfPageT page)
    {
        var transform = SheetTransform.ForPage(page);
        var marks = new List<Annotation>();

        int count = fpdf_annot.FPDFPageGetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            var annotation = fpdf_annot.FPDFPageGetAnnot(page, i);
            if (annotation is null) continue;

            try
            {
                string payload = GetString(annotation, AnnotationPayload.Key);
                if (!AnnotationPayload.TryRead(payload, out var mark)) continue;

                marks.Add(ToSheet(mark, transform, GetString(annotation, "NM")));
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(annotation);
            }
        }

        return marks;
    }

    /// <summary>
    /// Hides ZenInk's own marks on a page that is being read, so they are not
    /// drawn twice.
    ///
    /// The viewer paints its marks itself — that is what makes them selectable
    /// and editable — while PDFium draws everyone else's into the tiles. After a
    /// save the marks are in the file too, so without this they would appear
    /// both ways at once. It is safe because it happens on the viewer's own
    /// handle, and the viewer's handle is never the one that writes a file.
    /// </summary>
    public static void HideOwned(FpdfPageT page)
    {
        int count = fpdf_annot.FPDFPageGetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            var annotation = fpdf_annot.FPDFPageGetAnnot(page, i);
            if (annotation is null) continue;

            try
            {
                if (GetString(annotation, AnnotationPayload.Key).Length > 0)
                {
                    fpdf_annot.FPDFAnnotSetFlags(annotation, FlagHidden);
                }
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(annotation);
            }
        }
    }

    /// <summary>
    /// Makes the page's ZenInk marks match <paramref name="marks"/>: the old
    /// ones go, the current ones are written. Returns the names written, which
    /// is what the save path checks the reopened file against.
    ///
    /// The transform is taken before any turn is applied to the page, because
    /// the marks are held in the sheet space of the page as it was read.
    /// Rotating a sheet moves the paper, not the ink on it.
    /// </summary>
    public static List<string> Write(FpdfDocumentT document, FpdfPageT page, IReadOnlyList<Annotation> marks)
    {
        var transform = SheetTransform.ForPage(page);

        RemoveOwned(page);

        var names = new List<string>(marks.Count);
        foreach (var mark in marks)
        {
            names.Add(WriteOne(document, page, mark, transform));
        }
        return names;
    }

    /// <summary>The names of the ZenInk marks a page carries, for verifying a written file.</summary>
    public static List<string> OwnedNames(FpdfPageT page)
    {
        var names = new List<string>();

        int count = fpdf_annot.FPDFPageGetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            var annotation = fpdf_annot.FPDFPageGetAnnot(page, i);
            if (annotation is null) continue;

            try
            {
                if (GetString(annotation, AnnotationPayload.Key).Length > 0)
                {
                    names.Add(GetString(annotation, "NM"));
                }
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(annotation);
            }
        }

        return names;
    }

    private static void RemoveOwned(FpdfPageT page)
    {
        // Backwards: removing an annotation renumbers the ones after it.
        for (int i = fpdf_annot.FPDFPageGetAnnotCount(page) - 1; i >= 0; i--)
        {
            var annotation = fpdf_annot.FPDFPageGetAnnot(page, i);
            if (annotation is null) continue;

            bool owned;
            try
            {
                owned = GetString(annotation, AnnotationPayload.Key).Length > 0;
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(annotation);
            }

            if (owned)
            {
                fpdf_annot.FPDFPageRemoveAnnot(page, i);
            }
        }
    }

    private static unsafe string WriteOne(
        FpdfDocumentT document, FpdfPageT page, Annotation mark, SheetTransform transform)
    {
        // Three views of the same mark in PDF space: the raw points, which are
        // what the private key stores; the turned points, which is where an
        // arrow's head goes; and the outline, which is the shape itself.
        var pdfPoints = ToPdfPoints(mark.Points, transform);
        var turnedPoints = ToPdfPoints([.. Enumerable.Range(0, mark.Points.Count).Select(mark.TurnedPoint)], transform);
        var outline = ToPdfPath(mark.Outline, transform);
        var inPdfSpace = mark.WithPoints(pdfPoints);

        string name = mark.Id.ToString("N");
        int subtype = SubtypeOf(mark.Kind);

        var annotation = fpdf_annot.FPDFPageCreateAnnot(page, subtype)
            ?? throw new InvalidOperationException("PDFium no pudo crear la anotación.");

        try
        {
            // Colour before appearance: PDFium refuses to touch /C once an
            // appearance stream exists, since the stream is then what decides
            // how the annotation looks.
            fpdf_annot.FPDFAnnotSetColor(
                annotation,
                FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color,
                mark.Style.Color.R, mark.Style.Color.G, mark.Style.Color.B, 255);

            if (mark.Style.Fill is { } fill)
            {
                fpdf_annot.FPDFAnnotSetColor(
                    annotation,
                    FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_InteriorColor,
                    fill.R, fill.G, fill.B, mark.Style.FillAlpha);
            }

            fpdf_annot.FPDFAnnotSetBorder(annotation, 0f, 0f, mark.Style.WidthPt);
            fpdf_annot.FPDFAnnotSetRect(annotation, PdfRect(mark, transform));
            fpdf_annot.FPDFAnnotSetFlags(annotation, FlagPrint);

            SetString(annotation, AnnotationPayload.Key, AnnotationPayload.Write(inPdfSpace));
            SetString(annotation, "NM", name);
            SetString(annotation, "Contents", mark.Text);
            SetString(annotation, "M", PdfDate(mark.Created));
            SetString(annotation, "CreationDate", PdfDate(mark.Created));
            if (mark.Author.Length > 0)
            {
                SetString(annotation, "T", mark.Author);
            }

            if (subtype == SubtypeInk)
            {
                AddInkStroke(annotation, turnedPoints);
            }
            else if (mark.Kind == AnnotationKind.FreeText)
            {
                AddWrittenText(document, annotation, mark, transform);
            }
            else if (subtype == SubtypeStamp)
            {
                AddShapeObject(annotation, outline, mark.Style);

                // The number goes in beside the shape, not only in /Contents.
                // A measurement whose figure lives in a tooltip is a line
                // somebody drew as far as the person receiving the drawing is
                // concerned.
                if (Measures.Is(mark.Kind) && mark.Text.Length > 0)
                {
                    AddLabel(document, annotation, mark, transform);
                }
                else if (mark.Kind == AnnotationKind.Stamp && mark.Text.Length > 0)
                {
                    AddStampText(document, annotation, mark, transform);
                }
            }
            else if (subtype == SubtypeHighlight)
            {
                AddQuadPoints(annotation, outline);
            }

            if (AnnotationAppearance.Build(mark, outline, turnedPoints) is { } appearance)
            {
                SetWideString(
                    appearance,
                    buffer => fpdf_annot.FPDFAnnotSetAP(annotation, AppearanceNormal, ref buffer[0]));
            }
        }
        finally
        {
            fpdf_annot.FPDFPageCloseAnnot(annotation);
        }

        return name;
    }

    /// <summary>
    /// Puts the shape into the annotation as a path object.
    ///
    /// This is what buys a fill that lets the drawing show through it. A PDF
    /// annotation's own transparency lives in /CA, which PDFium cannot write,
    /// but a page object carries its own alpha and PDFium builds the
    /// annotation's appearance — graphics state and all — from the objects it
    /// is given. Checked end to end: the ink underneath still reads through a
    /// filled box.
    /// </summary>
    private static void AddShapeObject(FpdfAnnotationT annotation, IReadOnlyList<PathStep> outline, AnnotationStyle style)
    {
        if (outline.Count == 0) return;

        FpdfPageobjectT? path = null;

        void Finish()
        {
            if (path is not { } built) return;

            if (style.Fill is { } fill)
            {
                fpdf_edit.FPDFPageObjSetFillColor(built, fill.R, fill.G, fill.B, style.FillAlpha);
            }

            fpdf_edit.FPDFPageObjSetStrokeColor(built, style.Color.R, style.Color.G, style.Color.B, 255);
            fpdf_edit.FPDFPageObjSetStrokeWidth(built, style.WidthPt);
            fpdf_edit.FPDFPathSetDrawMode(built, style.Fill is null ? FillModeNone : FillModeWinding, 1);

            fpdf_annot.FPDFAnnotAppendObject(annotation, built);
            path = null;
        }

        for (int i = 0; i < outline.Count; i++)
        {
            var step = outline[i];
            switch (step.Verb)
            {
                // A figure of its own. Most marks are one shape, but a measured
                // distance carries a tick across each end and an angle carries
                // the arc between its arms, and those are separate strokes: an
                // object per figure is what stops them being joined by a line
                // that was never drawn.
                case PathVerb.Move:
                    Finish();
                    path = fpdf_edit.FPDFPageObjCreateNewPath(step.A.X, step.A.Y);
                    break;

                case PathVerb.Line when path is { } open:
                    fpdf_edit.FPDFPathLineTo(open, step.A.X, step.A.Y);
                    break;

                case PathVerb.Cubic when path is { } curving:
                    fpdf_edit.FPDFPathBezierTo(curving, step.A.X, step.A.Y, step.B.X, step.B.Y, step.C.X, step.C.Y);
                    break;

                case PathVerb.Close when path is { } closing:
                    fpdf_edit.FPDFPathClose(closing);
                    break;
            }
        }

        Finish();
    }

    /// <summary>
    /// Puts the mark's words into the annotation, a text object per line.
    ///
    /// PDF has no idea of a paragraph, so the lines are placed one by one on
    /// the baselines the engine worked out — the same ones the canvas draws on,
    /// which is what keeps the words in the same place on screen and on paper.
    /// Both the mark's own turn and the page's come out of the transform rather
    /// than being reasoned about: a point one unit along the line of type says
    /// which way the writing runs, whatever is between here and the page.
    /// </summary>
    private static void AddWrittenText(
        FpdfDocumentT document, FpdfAnnotationT annotation, Annotation mark, SheetTransform transform)
    {
        var lines = AnnotationText.Lines(mark.Text);
        var anchor = mark.Points[0];

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length == 0) continue;

            var baseline = new Vector2(anchor.X, anchor.Y + AnnotationText.BaselineOffset(i, mark.Style.FontSizePt));
            var along = baseline + new Vector2(1f, 0f);

            var start = transform.ToPdf(AnnotationGeometry.Rotate(baseline, anchor, mark.RotationDeg));
            var next = transform.ToPdf(AnnotationGeometry.Rotate(along, anchor, mark.RotationDeg));

            var direction = next - start;
            float scale = direction.Length();
            if (scale <= 0.0001f) continue;
            direction /= scale;

            var text = fpdf_edit.FPDFPageObjNewTextObj(document, StandardFont, mark.Style.FontSizePt * scale);
            if (text is null) continue;

            SetTextObject(text, lines[i]);
            fpdf_edit.FPDFPageObjSetFillColor(text, mark.Style.Color.R, mark.Style.Color.G, mark.Style.Color.B, 255);
            fpdf_edit.FPDFPageObjTransform(
                text, direction.X, direction.Y, -direction.Y, direction.X, start.X, start.Y);

            fpdf_annot.FPDFAnnotAppendObject(annotation, text);
        }
    }

    /// <summary>
    /// Puts a measurement's number on the sheet, beside what it measures.
    ///
    /// Same placement as on screen — <see cref="Measures.LabelAnchor"/> decides
    /// it for both — so the drawing that leaves here reads the way it read to
    /// the person who took the measurement.
    /// </summary>
    private static void AddLabel(
        FpdfDocumentT document, FpdfAnnotationT annotation, Annotation mark, SheetTransform transform)
    {
        var anchor = Measures.LabelAnchor(mark.Kind, mark.Points);
        var baseline = anchor + new Vector2(MeasureLabelOffsetPt, -MeasureLabelOffsetPt);

        // A point one unit along the writing says which way it runs once the
        // sheet's own turn is in; nothing here has to know what that turn was.
        var start = transform.ToPdf(AnnotationGeometry.Rotate(baseline, anchor, mark.RotationDeg));
        var next = transform.ToPdf(AnnotationGeometry.Rotate(baseline + new Vector2(1f, 0f), anchor, mark.RotationDeg));

        var direction = next - start;
        float scale = direction.Length();
        if (scale <= 0.0001f) return;
        direction /= scale;

        var text = fpdf_edit.FPDFPageObjNewTextObj(document, StandardFont, mark.Style.FontSizePt * scale);
        if (text is null) return;

        SetTextObject(text, mark.Text);
        fpdf_edit.FPDFPageObjSetFillColor(text, mark.Style.Color.R, mark.Style.Color.G, mark.Style.Color.B, 255);
        fpdf_edit.FPDFPageObjTransform(text, direction.X, direction.Y, -direction.Y, direction.X, start.X, start.Y);

        fpdf_annot.FPDFAnnotAppendObject(annotation, text);
    }

    /// <summary>
    /// A stamp's lines inside its box, laid out by the same fitting the
    /// signature's own appearance uses. That shared fitting is the whole reason
    /// a review stamp looks like the drawing said it would: the canvas asks the
    /// same question and gets the same answer.
    /// </summary>
    private static void AddStampText(
        FpdfDocumentT document, FpdfAnnotationT annotation, Annotation mark, SheetTransform transform)
    {
        var box = mark.Box();
        var lines = AnnotationText.Lines(mark.Text)
            .Where(line => line.Length > 0)
            .Select(line => (Text: line, Strong: false))
            .ToArray();

        var (size, shown) = PdfSignatureStamp.Fit(lines, box.Width, box.Height);
        if (size <= 0f) return;

        var centre = mark.Centre;
        float baseline = box.Top + PdfSignatureStamp.Padding + size;

        foreach (var (line, _) in shown)
        {
            if (baseline > box.Bottom) break;

            var at = new Vector2(box.Left + PdfSignatureStamp.Padding, baseline);
            var start = transform.ToPdf(AnnotationGeometry.Rotate(at, centre, mark.RotationDeg));
            var next = transform.ToPdf(
                AnnotationGeometry.Rotate(at + new Vector2(1f, 0f), centre, mark.RotationDeg));

            var direction = next - start;
            float scale = direction.Length();
            if (scale > 0.0001f)
            {
                direction /= scale;

                var text = fpdf_edit.FPDFPageObjNewTextObj(document, StandardFont, size * scale);
                if (text is not null)
                {
                    SetTextObject(text, line);
                    fpdf_edit.FPDFPageObjSetFillColor(
                        text, mark.Style.Color.R, mark.Style.Color.G, mark.Style.Color.B, 255);
                    fpdf_edit.FPDFPageObjTransform(
                        text, direction.X, direction.Y, -direction.Y, direction.X, start.X, start.Y);

                    fpdf_annot.FPDFAnnotAppendObject(annotation, text);
                }
            }

            baseline += size * PdfSignatureStamp.Leading;
        }
    }

    private static void SetTextObject(FpdfPageobjectT text, string line)
    {
        var buffer = new ushort[line.Length + 1];
        for (int i = 0; i < line.Length; i++)
        {
            buffer[i] = line[i];
        }
        fpdf_edit.FPDFTextSetText(text, ref buffer[0]);
    }

    /// <summary>
    /// The boxes a highlight covers, one quad each.
    ///
    /// A highlight follows text, so a phrase that wraps is several boxes in one
    /// annotation — which is exactly what /QuadPoints is for, and what makes
    /// another reader show it as one highlight rather than a row of them. They
    /// come from the outline's own corners, so a highlight over a label on a
    /// turned sheet still sits along the words.
    /// </summary>
    private static void AddQuadPoints(FpdfAnnotationT annotation, IReadOnlyList<PathStep> outline)
    {
        foreach (var figure in AnnotationGeometry.Figures(outline))
        {
            if (figure.Count < 4) continue;

            fpdf_annot.FPDFAnnotAppendAttachmentPoints(annotation, new FS_QUADPOINTSF
            {
                X1 = figure[0].X,
                Y1 = figure[0].Y,
                X2 = figure[1].X,
                Y2 = figure[1].Y,
                X3 = figure[3].X,
                Y3 = figure[3].Y,
                X4 = figure[2].X,
                Y4 = figure[2].Y,
            });
        }
    }

    /// <summary>
    /// Writes the stroke into /InkList, so a reader that regenerates
    /// appearances still has the geometry. An arrow's head is not part of it:
    /// the head is a filled shape, and the ink list is the path it hangs off.
    /// </summary>
    private static unsafe void AddInkStroke(FpdfAnnotationT annotation, IReadOnlyList<Vector2> points)
    {
        if (points.Count < 2) return;

        IntPtr buffer = Marshal.AllocHGlobal(sizeof(float) * 2 * points.Count);
        try
        {
            float* coordinates = (float*)buffer;
            for (int i = 0; i < points.Count; i++)
            {
                coordinates[i * 2] = points[i].X;
                coordinates[i * 2 + 1] = points[i].Y;
            }

            PdfiumInk.FPDFAnnot_AddInkStroke(annotation.__Instance, coordinates, (nuint)points.Count);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int SubtypeOf(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Highlight => SubtypeHighlight,
        AnnotationKind.Note => SubtypeText,

        // Anything that can be filled goes in as a stamp holding its own path
        // object. A /Square could carry a box and a /Circle an ellipse, but
        // neither can carry a cloud or a polygon — PDFium cannot write their
        // vertices — and none of them can carry a fill you can see through.
        // One shape, one way in, and the same drawing for every reader.
        // A measurement goes the same way, and for the same reason twice over:
        // an area needs a fill you can see through, and every measurement
        // carries its number as well as its shape. Only a stamp can hold both.
        AnnotationKind.Rectangle or AnnotationKind.Ellipse
            or AnnotationKind.Polygon or AnnotationKind.Cloud
            or AnnotationKind.FreeText or AnnotationKind.Stamp
            or AnnotationKind.Distance or AnnotationKind.Perimeter
            or AnnotationKind.Area or AnnotationKind.Angle => SubtypeStamp,

        // Ink, and also the line, the arrow and the polyline: all of them are a
        // stroked path, which is what an ink annotation is.
        _ => SubtypeInk,
    };

    private static Vector2[] ToPdfPoints(IReadOnlyList<Vector2> points, SheetTransform transform)
    {
        var converted = new Vector2[points.Count];
        for (int i = 0; i < converted.Length; i++)
        {
            converted[i] = transform.ToPdf(points[i]);
        }
        return converted;
    }

    /// <summary>The same outline, moved from sheet space into the page's own.</summary>
    private static PathStep[] ToPdfPath(IReadOnlyList<PathStep> outline, SheetTransform transform)
    {
        var converted = new PathStep[outline.Count];
        for (int i = 0; i < converted.Length; i++)
        {
            var step = outline[i];
            converted[i] = step.Verb switch
            {
                PathVerb.Move => PathStep.Move(transform.ToPdf(step.A)),
                PathVerb.Line => PathStep.Line(transform.ToPdf(step.A)),
                PathVerb.Cubic => PathStep.Cubic(
                    transform.ToPdf(step.A), transform.ToPdf(step.B), transform.ToPdf(step.C)),
                _ => PathStep.Close(),
            };
        }
        return converted;
    }

    /// <summary>
    /// The mark's box in PDF space. Derived from the sheet-space bounds — which
    /// already allow for the stroke width and the arrow head — rather than from
    /// the points, because this rectangle also clips the appearance stream.
    /// </summary>
    private static FS_RECTF_ PdfRect(Annotation mark, SheetTransform transform)
    {
        var bounds = mark.Bounds;
        Span<Vector2> corners =
        [
            transform.ToPdf(new Vector2(bounds.Left, bounds.Top)),
            transform.ToPdf(new Vector2(bounds.Right, bounds.Top)),
            transform.ToPdf(new Vector2(bounds.Left, bounds.Bottom)),
            transform.ToPdf(new Vector2(bounds.Right, bounds.Bottom)),
        ];

        float left = corners[0].X, right = left, bottom = corners[0].Y, top = bottom;
        for (int i = 1; i < corners.Length; i++)
        {
            left = MathF.Min(left, corners[i].X);
            right = MathF.Max(right, corners[i].X);
            bottom = MathF.Min(bottom, corners[i].Y);
            top = MathF.Max(top, corners[i].Y);
        }

        return new FS_RECTF_ { Left = left, Bottom = bottom, Right = right, Top = top };
    }

    private static Annotation ToSheet(Annotation pdfSpaceMark, SheetTransform transform, string name)
    {
        var points = new Vector2[pdfSpaceMark.Points.Count];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = transform.ToSheet(pdfSpaceMark.Points[i]);
        }

        // A note's anchor is the top-left of its icon in sheet space; in PDF
        // space that same corner is the top-left too, so the point maps
        // straight across. Everything else is a plain path.
        // The mark's own turn comes back as it was written. It is an angle in
        // sheet space on both sides of the trip: the shape that went into the
        // file was built with the turn already applied, so the number here is
        // only for editing it again, never for drawing it.
        return new Annotation(
            pdfSpaceMark.Kind,
            points,
            pdfSpaceMark.Style,
            pdfSpaceMark.Text,
            pdfSpaceMark.Author,
            ParseName(name),
            pdfSpaceMark.Created,
            pdfSpaceMark.RotationDeg,
            // The calibration comes back with the measurement. Without it the
            // mark would arrive as a line with no number — and the sheet it is
            // on would come back uncalibrated, since its measurements are where
            // that is remembered.
            pdfSpaceMark.Scale);
    }

    /// <summary>
    /// Recovers the mark's identity from /NM. Keeping it means a mark that is
    /// saved, reopened and moved is still the same mark, which matters the
    /// moment two ZenInk windows have the same drawing open.
    /// </summary>
    private static Guid? ParseName(string name) =>
        Guid.TryParseExact(name, "N", out var id) ? id : null;

    private static string PdfDate(DateTimeOffset when)
    {
        var offset = when.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        return string.Format(
            CultureInfo.InvariantCulture,
            "D:{0:yyyyMMddHHmmss}{1}{2:00}'{3:00}'",
            when.DateTime,
            sign,
            Math.Abs(offset.Hours),
            Math.Abs(offset.Minutes));
    }

    // --- string marshalling ---------------------------------------------
    //
    // PDFium takes and returns UTF-16 through FPDF_WIDESTRING, which PDFiumCore
    // surfaces as a ref to the first unit of a buffer. Both helpers keep the
    // array alive for the length of the call, which is all the pinning a ref to
    // an array element needs.

    private static void SetString(FpdfAnnotationT annotation, string key, string value) =>
        SetWideString(value, buffer => fpdf_annot.FPDFAnnotSetStringValue(annotation, key, ref buffer[0]));

    private static void SetWideString(string value, Func<ushort[], int> call)
    {
        var buffer = new ushort[value.Length + 1];
        for (int i = 0; i < value.Length; i++)
        {
            buffer[i] = value[i];
        }
        call(buffer);
    }

    private static string GetString(FpdfAnnotationT annotation, string key)
    {
        ushort probe = 0;
        ulong size = fpdf_annot.FPDFAnnotGetStringValue(annotation, key, ref probe, 0);
        if (size <= 2) return string.Empty;

        var buffer = new ushort[size / 2];
        fpdf_annot.FPDFAnnotGetStringValue(annotation, key, ref buffer[0], size);

        var text = new StringBuilder(buffer.Length);
        foreach (ushort unit in buffer)
        {
            if (unit == 0) break;
            text.Append((char)unit);
        }
        return text.ToString();
    }
}
