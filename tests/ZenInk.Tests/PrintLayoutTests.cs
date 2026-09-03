//-----------------------------------------------------------------------------------------
// <copyright file="PrintLayoutTests.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using ZenInk.Core;
using static ZenInk.Tests.TestRunner;

namespace ZenInk.Tests;

/// <summary>
/// What lands on each sheet of paper. A print of a set of A0s is slow and
/// expensive to redo, so the arithmetic that decides it is pinned here rather
/// than discovered at the plotter.
/// </summary>
public static class PrintLayoutTests
{
    // ISO sizes in points, which is what the engine works in.
    private static readonly PdfPageSize A4 = new(595f, 842f);
    private static readonly PdfPageSize A3 = new(842f, 1191f);
    private static readonly PdfPageSize A0 = new(2384f, 3370f);

    /// <summary>A landscape A0 drawing, the shape most plans arrive in.</summary>
    private static readonly PdfPageSize LandscapeA0 = new(3370f, 2384f);

    public static void Run()
    {
        Orientation();
        Fitting();
        ActualSize();
        Poster();
        Margins();
        Ranges();
    }

    /// <summary>
    /// The bands a print job is assembled from. An A0 at plotter resolution is
    /// far too big for one bitmap, so it goes out a strip at a time — and the
    /// strips have to line up exactly, or every print carries seams.
    /// </summary>
    public static async Task RunRenderAsync()
    {
        Section("Print — bands must stitch into the whole page");

        string path = TestPdf.WriteRectangle("zenink-print-band", "40 380 180 120");
        var queue = PdfRenderQueue.Shared;
        var document = await queue.OpenDocumentAsync(path);

        // A scale that is not a whole number, as a real paper fit rarely is.
        const double scale = 1.37;
        int fullWidth = (int)Math.Ceiling(TestPdf.PageWidth * scale);
        int fullHeight = (int)Math.Ceiling(TestPdf.PageHeight * scale);

        var whole = await queue.RequestPrintBandAsync(
            document.DocumentId, 0, 0, scale, 0, 0, fullWidth, fullHeight, monochrome: false);

        if (whole is not { } reference)
        {
            Check("a full-page band renders", false);
            await queue.CloseDocumentAsync(document.DocumentId);
            File.Delete(path);
            return;
        }

        Check($"the band is the size asked for ({reference.Width}x{reference.Height})",
            reference.Width == fullWidth && reference.Height == fullHeight);
        Check("the buffer matches the reported size",
            reference.Bgra.Length == reference.Width * reference.Height * 4);

        const int bandHeight = 137;   // deliberately not a divisor of the page
        int mismatched = 0;

        for (int top = 0; top < fullHeight; top += bandHeight)
        {
            int height = Math.Min(bandHeight, fullHeight - top);
            var band = await queue.RequestPrintBandAsync(
                document.DocumentId, 0, 0, scale, 0, top, fullWidth, height, monochrome: false);
            if (band is not { } strip) continue;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < fullWidth; x++)
                {
                    if (Bitmap.IsDark(strip.Bgra, strip.Width, x, y)
                        != Bitmap.IsDark(reference.Bgra, reference.Width, x, top + y))
                    {
                        mismatched++;
                    }
                }
            }
        }

        Check("every band matches its slice of the whole page", mismatched == 0,
            $"{mismatched} pixels differ");

        // Offsetting horizontally too, since a poster splits both ways.
        var quarter = await queue.RequestPrintBandAsync(
            document.DocumentId, 0, 0, scale, 60, 40, 120, 90, monochrome: false);
        int offsetMismatched = 0;
        if (quarter is { } q)
        {
            for (int y = 0; y < q.Height; y++)
            {
                for (int x = 0; x < q.Width; x++)
                {
                    if (Bitmap.IsDark(q.Bgra, q.Width, x, y)
                        != Bitmap.IsDark(reference.Bgra, reference.Width, 60 + x, 40 + y))
                    {
                        offsetMismatched++;
                    }
                }
            }
        }
        Check("a band offset on both axes lands on the right part of the page",
            quarter is not null && offsetMismatched == 0, $"{offsetMismatched} pixels differ");

        // A turned sheet prints turned: the paper follows what the reader saw.
        var turned = await queue.RequestPrintBandAsync(
            document.DocumentId, 0, 1, 1.0, 0, 0, TestPdf.PageHeight, TestPdf.PageWidth, monochrome: false);
        Check("a band of a turned sheet swaps its axes",
            turned is { } t && t.Width == TestPdf.PageHeight && t.Height == TestPdf.PageWidth);

        var mono = await queue.RequestPrintBandAsync(
            document.DocumentId, 0, 0, scale, 0, 0, fullWidth, 60, monochrome: true);
        bool allGrey = true;
        if (mono is { } m)
        {
            for (int i = 0; i + 3 < m.Bgra.Length; i += 4)
            {
                if (m.Bgra[i] != m.Bgra[i + 1] || m.Bgra[i + 1] != m.Bgra[i + 2]) allGrey = false;
            }
        }
        Check("monochrome leaves no colour behind", mono is not null && allGrey);

        await queue.CloseDocumentAsync(document.DocumentId);
        File.Delete(path);
    }

    private static void Orientation()
    {
        Section("Print — turning the paper to the drawing");

        var auto = new PrintSettings();

        var forLandscape = PrintLayout.OrientPaper(LandscapeA0, A4, auto);
        Check("a landscape drawing turns portrait paper landscape",
            forLandscape.WidthPt > forLandscape.HeightPt,
            $"got {forLandscape.WidthPt}x{forLandscape.HeightPt}");

        var forPortrait = PrintLayout.OrientPaper(A0, A4, auto);
        Check("a portrait drawing leaves portrait paper alone",
            Math.Abs(forPortrait.WidthPt - A4.WidthPt) < 0.01f);

        var forced = PrintLayout.OrientPaper(LandscapeA0, A4, PrintOrientation.Portrait);
        Check("asking for portrait overrides the drawing's own shape",
            forced.HeightPt > forced.WidthPt);

        var forcedLandscape = PrintLayout.OrientPaper(A0, A4, PrintOrientation.Landscape);
        Check("and so does asking for landscape",
            forcedLandscape.WidthPt > forcedLandscape.HeightPt);

        // Once Windows has been told the orientation, the paper is a fact.
        var asGiven = PrintLayout.OrientPaper(LandscapeA0, A4, PrintOrientation.AsGiven);
        Check("as-given leaves the paper exactly as handed over",
            Math.Abs(asGiven.WidthPt - A4.WidthPt) < 0.01f
            && Math.Abs(asGiven.HeightPt - A4.HeightPt) < 0.01f);

        // Turning the paper is what stops an A0 plan being printed at half the
        // size it could be, so the difference is worth pinning as a number.
        double turned = PrintLayout.ScaleFor(LandscapeA0, forLandscape, new PrintSettings());
        double unturned = PrintLayout.ScaleFor(LandscapeA0, A4, new PrintSettings());
        Check($"turning the paper prints the drawing larger ({unturned:0.000} -> {turned:0.000})",
            turned > unturned * 1.3);
    }

    private static void Fitting()
    {
        Section("Print — fitting a drawing to the paper");

        var settings = new PrintSettings();
        var paper = PrintLayout.OrientPaper(LandscapeA0, A3, settings);
        var pieces = PrintLayout.Build([LandscapeA0], [0], paper, settings);

        Check("a fitted drawing takes one sheet of paper", pieces.Count == 1);
        if (pieces.Count != 1) return;

        var piece = pieces[0];
        Check("the whole drawing goes on it",
            Math.Abs(piece.Source.Width - LandscapeA0.WidthPt) < 0.01f
            && Math.Abs(piece.Source.Height - LandscapeA0.HeightPt) < 0.01f);
        Check("it is not reported as a poster", !piece.IsPoster);

        // Proportions must survive, or every drawing comes out stretched.
        double sourceRatio = LandscapeA0.HeightPt / (double)LandscapeA0.WidthPt;
        double destRatio = piece.Destination.Height / (double)piece.Destination.Width;
        CheckClose("proportions are kept", destRatio, sourceRatio, 0.001);

        Check("it stays inside the paper",
            piece.Destination.Right <= paper.WidthPt + 0.01f
            && piece.Destination.Bottom <= paper.HeightPt + 0.01f);

        // Centred: the margin left of the drawing equals the margin right of it.
        float leftGap = piece.Destination.X;
        float rightGap = paper.WidthPt - piece.Destination.Right;
        CheckClose("and is centred on it", leftGap, rightGap, 0.01);
    }

    private static void ActualSize()
    {
        Section("Print — one point of drawing to one point of paper");

        var settings = new PrintSettings { ScaleMode = PrintScaleMode.ActualSize };

        Check("actual size is exactly one to one",
            Math.Abs(PrintLayout.ScaleFor(A4, A4, settings) - 1.0) < 1e-9);

        // A small drawing on big paper: whole thing, at its own size, centred.
        var pieces = PrintLayout.Build([A4], [0], A3, settings);
        Check("a drawing smaller than the paper prints whole", pieces.Count == 1);
        if (pieces.Count == 1)
        {
            CheckClose("at its own size", pieces[0].Destination.Width, A4.WidthPt, 0.01);
            Check("without becoming a poster", !pieces[0].IsPoster);
        }

        // A big drawing on small paper, posters off: cropped to the middle.
        var cropped = PrintLayout.Build([LandscapeA0], [0], A4, settings);
        Check("an oversized drawing still makes one sheet when posters are off", cropped.Count == 1);
        if (cropped.Count == 1)
        {
            var source = cropped[0].Source;
            Check("and the paper takes the middle of it, not a corner",
                source.X > 0.01f && source.Y > 0.01f,
                $"source at {source.X:0}, {source.Y:0}");
            CheckClose("losing as much on the left as on the right",
                source.X, LandscapeA0.WidthPt - source.Right, 0.01);
        }

        var custom = new PrintSettings { ScaleMode = PrintScaleMode.Custom, CustomScalePercent = 50 };
        CheckClose("a custom percentage is taken literally",
            PrintLayout.ScaleFor(A4, A3, custom), 0.5, 1e-9);

        DrawingScale();
    }

    /// <summary>
    /// Printing to a drawing scale, which is the one mode that has to know what
    /// the drawing already is.
    /// </summary>
    private static void DrawingScale()
    {
        Section("Print — printed at the scale the drawing is read at");

        var plotted = SheetScale.FromRatio(200, MeasureUnit.Metre)!.Value;
        var scales = new Dictionary<int, SheetScale> { [0] = plotted };

        var asDrawn = new PrintSettings
        {
            ScaleMode = PrintScaleMode.Drawing,
            DrawingRatio = 200,
            Scales = scales,
        };

        CheckClose("a 1:200 drawing printed at 1:200 comes out untouched",
            PrintLayout.ScaleFor(A4, A3, asDrawn, plotted), 1.0, 1e-6);

        CheckClose("printed at 1:100 it comes out twice the size",
            PrintLayout.ScaleFor(A4, A3, asDrawn with { DrawingRatio = 100 }, plotted), 2.0, 1e-6);

        CheckClose("and at 1:500 it comes out smaller by the same reasoning",
            PrintLayout.ScaleFor(A4, A3, asDrawn with { DrawingRatio = 500 }, plotted), 0.4, 1e-6);

        // A sheet nobody calibrated cannot be printed to a scale, and saying so
        // by fitting the paper is better than printing a number that is wrong.
        double uncalibrated = PrintLayout.ScaleFor(A4, A3, asDrawn, null);
        CheckClose("an uncalibrated sheet falls back to fitting the paper",
            uncalibrated, PrintLayout.ScaleFor(A4, A3, new PrintSettings()), 1e-9);

        // And the per-sheet scales reach the layout: the same job, two sheets,
        // only one of them calibrated.
        var pieces = PrintLayout.Build([A4, A4], [0, 1], A3, asDrawn with { DrawingRatio = 100 });

        // The factor is not carried on the piece; it is the destination over
        // the source, which is what the printer actually does with it.
        double Factor(PrintPiece piece) => piece.Destination.Width / piece.Source.Width;

        Check("the calibrated sheet and the other one print at different scales",
            pieces.Count == 2 && Math.Abs(Factor(pieces[0]) - Factor(pieces[1])) > 0.01,
            pieces.Count == 2 ? $"{Factor(pieces[0]):0.###} vs {Factor(pieces[1]):0.###}" : $"{pieces.Count} piezas");
    }

    private static void Poster()
    {
        Section("Print — an oversized drawing across several sheets");

        var settings = new PrintSettings
        {
            ScaleMode = PrintScaleMode.ActualSize,
            Poster = true,
            PosterOverlapPt = 28.35f,
            Orientation = PrintOrientation.Auto,
        };

        var paper = PrintLayout.OrientPaper(LandscapeA0, A3, settings);
        var pieces = PrintLayout.Build([LandscapeA0], [0], paper, settings);

        Check($"an A0 at actual size needs several A3 sheets ({pieces.Count})", pieces.Count > 1);
        if (pieces.Count <= 1) return;

        int columns = pieces[0].ColumnCount;
        int rows = pieces[0].RowCount;
        Check("the sheets form a full grid", pieces.Count == columns * rows,
            $"{pieces.Count} sheets for a {columns}x{rows} grid");
        Check("every sheet knows it is part of a poster", pieces.All(p => p.IsPoster));

        Check("sheets come out in reading order",
            pieces.Select((p, i) => p.Row == i / columns && p.Column == i % columns).All(x => x));

        // Coverage is the whole point: no part of the drawing may fall between
        // two sheets.
        float rightmost = pieces.Max(p => p.Source.Right);
        float lowest = pieces.Max(p => p.Source.Bottom);
        CheckClose("the sheets reach the right edge of the drawing", rightmost, LandscapeA0.WidthPt, 0.5);
        CheckClose("and the bottom edge", lowest, LandscapeA0.HeightPt, 0.5);

        Check("no sheet runs past the drawing",
            pieces.All(p => p.Source.Right <= LandscapeA0.WidthPt + 0.01f
                && p.Source.Bottom <= LandscapeA0.HeightPt + 0.01f));

        // Neighbours must share a strip, or there is nothing to align on.
        var first = pieces.First(p => p is { Row: 0, Column: 0 });
        var second = pieces.FirstOrDefault(p => p is { Row: 0, Column: 1 });
        if (columns > 1)
        {
            Check("neighbouring sheets overlap rather than butt together",
                second.Source.X < first.Source.Right - 0.01f,
                $"second starts at {second.Source.X:0}, first ends at {first.Source.Right:0}");
        }

        Check("every sheet prints at the same scale",
            pieces.All(p => Math.Abs(p.Destination.Width / p.Source.Width - 1.0) < 0.01));

        // Poster sheets sit flush in the corner: centring each one would open a
        // gap at every seam.
        Check("poster sheets are flush, not centred",
            pieces.All(p => Math.Abs(p.Destination.X - PrintLayout.PrintableBox(paper, settings.Margins).X) < 0.01f));

        var noPoster = PrintLayout.Build([LandscapeA0], [0], paper, settings with { Poster = false });
        Check("turning posters off brings it back to one sheet", noPoster.Count == 1);
    }

    private static void Margins()
    {
        Section("Print — the printer's unprintable border");

        var settings = new PrintSettings { Margins = PrintMarginsPt.Uniform(36f) };
        var box = PrintLayout.PrintableBox(A4, settings.Margins);

        CheckClose("the printable box loses the margin on both sides", box.Width, A4.WidthPt - 72f, 0.01);
        CheckClose("and on top and bottom", box.Height, A4.HeightPt - 72f, 0.01);
        CheckClose("and starts at the margin", box.X, 36f, 0.01);

        var pieces = PrintLayout.Build([A4], [0], A4, settings);
        Check("a fitted drawing respects the margin", pieces.Count == 1 && pieces[0].Destination.X >= 36f - 0.01f);

        Check("a margin bigger than the paper prints nothing rather than nonsense",
            PrintLayout.Build([A4], [0], A4, new PrintSettings { Margins = PrintMarginsPt.Uniform(500f) }).Count == 0);
    }

    private static void Ranges()
    {
        Section("Print — which sheets");

        var sheets = new List<PdfPageSize> { A4, LandscapeA0, A4 };
        var settings = new PrintSettings();

        var all = PrintLayout.Build(sheets, [0, 1, 2], A4, settings);
        Check("every sheet asked for makes a piece", all.Count == 3);
        Check("and they keep the order asked for",
            all[0].PageIndex == 0 && all[1].PageIndex == 1 && all[2].PageIndex == 2);

        var some = PrintLayout.Build(sheets, [2, 0], A4, settings);
        Check("a reordered range prints in that order",
            some.Count == 2 && some[0].PageIndex == 2 && some[1].PageIndex == 0);

        Check("a page outside the document is skipped, not drawn blank",
            PrintLayout.Build(sheets, [0, 99, -3], A4, settings).Count == 1);

        Check("no pages means no paper", PrintLayout.Build(sheets, [], A4, settings).Count == 0);

        // Mixed orientations in one set: each sheet gets the paper turned to suit.
        var mixed = PrintLayout.Build(sheets, [0, 1], A4, settings);
        Check("each drawing turns the paper for itself",
            mixed.Count == 2
            && mixed[0].Destination.Width < mixed[0].Destination.Height
            && mixed[1].Destination.Width > mixed[1].Destination.Height);
    }
}
