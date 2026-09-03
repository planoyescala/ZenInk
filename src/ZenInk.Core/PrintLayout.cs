//-----------------------------------------------------------------------------------------
// <copyright file="PrintLayout.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

namespace ZenInk.Core;

/// <summary>A rectangle in PDF points, y down.</summary>
public readonly record struct RectPt(float X, float Y, float Width, float Height)
{
    public float Left => X;

    public float Top => Y;

    public float Right => X + Width;

    public float Bottom => Y + Height;

    /// <summary>The box two opposite corners span, whichever way round they came.</summary>
    public static RectPt FromCorners(System.Numerics.Vector2 a, System.Numerics.Vector2 b) => new(
        MathF.Min(a.X, b.X),
        MathF.Min(a.Y, b.Y),
        MathF.Abs(b.X - a.X),
        MathF.Abs(b.Y - a.Y));

    /// <summary>Grows the box on every side; a negative amount shrinks it.</summary>
    public RectPt Inflated(float by) => new(X - by, Y - by, Width + by * 2f, Height + by * 2f);

    public bool Contains(System.Numerics.Vector2 point) =>
        point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    public bool Intersects(RectPt other) =>
        other.Left <= Right && other.Right >= Left && other.Top <= Bottom && other.Bottom >= Top;
}

/// <summary>Unprintable border, in points, on each edge of the paper.</summary>
public readonly record struct PrintMarginsPt(float Left, float Top, float Right, float Bottom)
{
    public static PrintMarginsPt Uniform(float points) => new(points, points, points, points);

    public float Horizontal => Left + Right;

    public float Vertical => Top + Bottom;
}

/// <summary>How the drawing is sized onto the paper.</summary>
public enum PrintScaleMode
{
    /// <summary>As large as fits, proportions kept. What you want to just see the sheet.</summary>
    FitToPaper,

    /// <summary>One PDF point to one point of paper, so a scale bar on the drawing stays true.</summary>
    ActualSize,

    /// <summary>An explicit percentage, for the times the drawing was authored oversized.</summary>
    Custom,

    /// <summary>
    /// Printed to a drawing scale — 1:100, 1:50 — so a rule held against the
    /// paper reads the building. It needs the sheet to be calibrated, because
    /// it is the only mode that has to know what the drawing already is: the
    /// factor is what takes it from the ratio it was plotted at to the one
    /// being asked for. A sheet with no calibration falls back to fitting the
    /// paper, which is the honest answer to a question that cannot be
    /// answered.
    /// </summary>
    Drawing,
}

public enum PrintOrientation
{
    /// <summary>Turn the paper to match the drawing, which for a plan is nearly always right.</summary>
    Auto,

    Portrait,

    Landscape,

    /// <summary>
    /// Take the paper exactly as handed over. This is what the print path uses:
    /// once Windows has been told the orientation, the paper in the printer is
    /// a fact, not a preference, and turning it again here would lay the
    /// drawing out for a sheet that does not exist.
    /// </summary>
    AsGiven,
}

/// <summary>
/// Everything the reader chose in the print dialog, other than which printer.
/// </summary>
public sealed record PrintSettings
{
    public PrintScaleMode ScaleMode { get; init; } = PrintScaleMode.FitToPaper;

    /// <summary>Only consulted for <see cref="PrintScaleMode.Custom"/>.</summary>
    public double CustomScalePercent { get; init; } = 100.0;

    /// <summary>
    /// The drawing scale asked for, as the number after the colon: 100 for
    /// 1:100. Only consulted for <see cref="PrintScaleMode.Drawing"/>.
    /// </summary>
    public double DrawingRatio { get; init; } = 100.0;

    /// <summary>
    /// What each sheet is drawn to, for the sheets that have been calibrated.
    /// Per sheet and not per job: a set can hold a site plan at 1:500 and a
    /// detail at 1:20, and printing both "at 1:100" is two different factors.
    /// </summary>
    public IReadOnlyDictionary<int, SheetScale>? Scales { get; init; }

    public PrintOrientation Orientation { get; init; } = PrintOrientation.Auto;

    /// <summary>
    /// Spill a drawing too big for the paper across several sheets, to be taped
    /// together. Without it an oversized drawing is cropped to what fits.
    /// </summary>
    public bool Poster { get; init; }

    /// <summary>Duplicated strip along each seam, so the sheets can be aligned. 1 cm by default.</summary>
    public float PosterOverlapPt { get; init; } = 28.35f;

    public bool Monochrome { get; init; }

    public PrintMarginsPt Margins { get; init; } = PrintMarginsPt.Uniform(0f);
}

/// <summary>
/// One sheet of paper: which part of which drawing goes on it, and where.
/// <see cref="Source"/> is in the page's own points after rotation, the same
/// space the tiles use; <see cref="Destination"/> is in points from the paper's
/// top-left corner, margins already applied.
/// </summary>
public readonly record struct PrintPiece(
    int PageIndex,
    RectPt Source,
    RectPt Destination,
    int Column,
    int Row,
    int ColumnCount,
    int RowCount)
{
    /// <summary>True when this drawing needed more than one sheet of paper.</summary>
    public bool IsPoster => ColumnCount > 1 || RowCount > 1;
}

/// <summary>
/// Turns "these sheets, this paper, this scale" into the list of pieces of
/// paper that come out of the printer.
///
/// This is pure geometry on purpose. Printing a set of A0s is expensive in
/// paper and slow to redo, so the part that decides what lands where is kept
/// away from the printing machinery and can be checked without a printer.
/// </summary>
public static class PrintLayout
{
    /// <summary>Below this, a scale is treated as nothing and produces no pieces.</summary>
    private const double MinScale = 1e-4;

    /// <summary>
    /// Chooses which way round the paper goes. Auto turns the paper to match
    /// the drawing's proportions, which is what stops a landscape plan from
    /// being printed onto portrait paper at half the size it could be.
    /// </summary>
    public static PdfPageSize OrientPaper(PdfPageSize sheet, PdfPageSize paper, PrintSettings settings) =>
        OrientPaper(sheet, paper, settings.Orientation);

    public static PdfPageSize OrientPaper(PdfPageSize sheet, PdfPageSize paper, PrintOrientation orientation)
    {
        if (orientation == PrintOrientation.AsGiven) return paper;

        bool paperIsLandscape = paper.WidthPt > paper.HeightPt;

        bool wantLandscape = orientation switch
        {
            PrintOrientation.Portrait => false,
            PrintOrientation.Landscape => true,
            _ => sheet.WidthPt > sheet.HeightPt,
        };

        return wantLandscape == paperIsLandscape
            ? paper
            : new PdfPageSize(paper.HeightPt, paper.WidthPt);
    }

    /// <summary>
    /// The scale a single drawing would print at, in paper points per PDF
    /// point. Fit is capped at the paper; the other modes are taken as asked,
    /// and whether the result fits is a separate question.
    /// </summary>
    public static double ScaleFor(
        PdfPageSize sheet, PdfPageSize paper, PrintSettings settings, SheetScale? calibration = null)
    {
        var printable = PrintableBox(paper, settings.Margins);
        if (printable.Width <= 0 || printable.Height <= 0) return 0;
        if (sheet.WidthPt <= 0 || sheet.HeightPt <= 0) return 0;

        double fit = Math.Min(printable.Width / sheet.WidthPt, printable.Height / sheet.HeightPt);

        return settings.ScaleMode switch
        {
            PrintScaleMode.ActualSize => 1.0,
            PrintScaleMode.Custom => Math.Max(0, settings.CustomScalePercent) / 100.0,

            // From the ratio the sheet was plotted at to the one asked for. A
            // drawing already at 1:200 printed at 1:100 comes out twice the
            // size, which is exactly what it means and usually needs the next
            // paper up — the poster option is there for that.
            PrintScaleMode.Drawing =>
                calibration is { } scale && settings.DrawingRatio > 0 && scale.Ratio > 0
                    ? scale.Ratio / settings.DrawingRatio
                    : fit,

            _ => fit,
        };
    }

    /// <summary>
    /// Every sheet of paper the job will produce, in order. One drawing makes
    /// one piece unless it is too big for the paper and posters are on, in
    /// which case it makes a grid of them, read left to right and top to bottom.
    /// </summary>
    public static IReadOnlyList<PrintPiece> Build(
        IReadOnlyList<PdfPageSize> sheets,
        IReadOnlyList<int> pageIndices,
        PdfPageSize paper,
        PrintSettings settings)
    {
        var pieces = new List<PrintPiece>();

        foreach (int pageIndex in pageIndices)
        {
            if (pageIndex < 0 || pageIndex >= sheets.Count) continue;

            var sheet = sheets[pageIndex];
            var oriented = OrientPaper(sheet, paper, settings.Orientation);
            var printable = PrintableBox(oriented, settings.Margins);

            SheetScale? calibration =
                settings.Scales is { } scales && scales.TryGetValue(pageIndex, out var known) ? known : null;

            double scale = ScaleFor(sheet, oriented, settings, calibration);

            if (scale < MinScale || printable.Width <= 0 || printable.Height <= 0) continue;

            // How much of the drawing, in its own points, one sheet of paper holds.
            double windowW = printable.Width / scale;
            double windowH = printable.Height / scale;

            bool fits = sheet.WidthPt <= windowW + 0.01 && sheet.HeightPt <= windowH + 0.01;

            if (fits || !settings.Poster)
            {
                pieces.Add(SinglePiece(pageIndex, sheet, printable, scale, windowW, windowH));
                continue;
            }

            AddPosterPieces(pieces, pageIndex, sheet, printable, scale, windowW, windowH, settings.PosterOverlapPt);
        }

        return pieces;
    }

    /// <summary>
    /// One sheet of paper for the whole drawing. When it does not fit and
    /// posters are off, the drawing is centred and the paper takes the middle
    /// of it — losing the edges evenly rather than one corner.
    /// </summary>
    private static PrintPiece SinglePiece(
        int pageIndex, PdfPageSize sheet, RectPt printable, double scale, double windowW, double windowH)
    {
        float sourceW = (float)Math.Min(sheet.WidthPt, windowW);
        float sourceH = (float)Math.Min(sheet.HeightPt, windowH);
        var source = new RectPt(
            (sheet.WidthPt - sourceW) / 2f,
            (sheet.HeightPt - sourceH) / 2f,
            sourceW,
            sourceH);

        // Centred on the printable box, which is what makes a fitted drawing
        // sit square on the paper instead of hugging one corner.
        float destW = (float)(sourceW * scale);
        float destH = (float)(sourceH * scale);
        var destination = new RectPt(
            printable.X + (printable.Width - destW) / 2f,
            printable.Y + (printable.Height - destH) / 2f,
            destW,
            destH);

        return new PrintPiece(pageIndex, source, destination, 0, 0, 1, 1);
    }

    /// <summary>
    /// Splits an oversized drawing into a grid of sheets. Each seam carries an
    /// overlap duplicated on both neighbours, so there is something to line up
    /// and trim against when they are taped together.
    /// </summary>
    private static void AddPosterPieces(
        List<PrintPiece> pieces,
        int pageIndex,
        PdfPageSize sheet,
        RectPt printable,
        double scale,
        double windowW,
        double windowH,
        float overlapPt)
    {
        // The overlap is given on paper; in the drawing's own points it is
        // whatever that comes to at this scale.
        double overlap = Math.Max(0, overlapPt) / scale;
        double strideX = Math.Max(windowW - overlap, windowW * 0.1);
        double strideY = Math.Max(windowH - overlap, windowH * 0.1);

        int columns = Math.Max(1, (int)Math.Ceiling((sheet.WidthPt - overlap) / strideX - 1e-6));
        int rows = Math.Max(1, (int)Math.Ceiling((sheet.HeightPt - overlap) / strideY - 1e-6));

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                double left = column * strideX;
                double top = row * strideY;

                // The last column and row are pulled back against the edge, so
                // the set ends flush instead of printing a sliver.
                if (column == columns - 1) left = Math.Max(0, sheet.WidthPt - windowW);
                if (row == rows - 1) top = Math.Max(0, sheet.HeightPt - windowH);

                float sourceW = (float)Math.Min(windowW, sheet.WidthPt - left);
                float sourceH = (float)Math.Min(windowH, sheet.HeightPt - top);
                if (sourceW <= 0 || sourceH <= 0) continue;

                var source = new RectPt((float)left, (float)top, sourceW, sourceH);

                // A poster piece sits at the corner of the printable box, not
                // centred: centring each sheet would put a gap at every seam.
                var destination = new RectPt(
                    printable.X,
                    printable.Y,
                    (float)(sourceW * scale),
                    (float)(sourceH * scale));

                pieces.Add(new PrintPiece(pageIndex, source, destination, column, row, columns, rows));
            }
        }
    }

    /// <summary>The area of the paper that can actually carry ink.</summary>
    public static RectPt PrintableBox(PdfPageSize paper, PrintMarginsPt margins) => new(
        margins.Left,
        margins.Top,
        Math.Max(0, paper.WidthPt - margins.Horizontal),
        Math.Max(0, paper.HeightPt - margins.Vertical));
}
