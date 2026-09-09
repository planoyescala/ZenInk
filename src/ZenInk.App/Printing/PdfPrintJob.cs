//-----------------------------------------------------------------------------------------
// <copyright file="PdfPrintJob.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using ZenInk.Core;
using ZenInk_App.Rendering;

namespace ZenInk_App.Printing;

/// <summary>
/// One print job: the drawing, the paper, and what lands on each sheet.
///
/// It opens its <em>own</em> handle on the file rather than borrowing the
/// viewer's, for the same reason saving does: the line-weight toggle works by
/// rewriting stroke widths on the parsed page, and printing through that handle
/// would put a screen-reading aid onto paper. The reader's sheet turns do come
/// along, because those are what they are looking at.
/// </summary>
public sealed class PdfPrintJob : IAsyncDisposable
{
    /// <summary>
    /// Ceiling on one band, in pixels. An A0 at 300 dpi is 139 megapixels; this
    /// keeps any single allocation to about 64 MB however big the sheet is.
    /// </summary>
    private const long MaxBandPixels = 16L * 1024 * 1024;

    /// <summary>PDF points to device-independent pixels.</summary>
    public const double PointsToDips = 96.0 / 72.0;

    private readonly PdfRenderQueue _queue = PdfRenderQueue.Shared;
    private readonly PagePlan _plan;

    /// <summary>The document opened for each of the plan's sources, by source number.</summary>
    private readonly List<int> _documents = [];

    private IReadOnlyList<PdfPageSize> _sheets = [];
    private IReadOnlyList<PrintPiece> _pieces = [];
    private bool _open;

    private PdfPrintJob(PagePlan plan)
    {
        _plan = plan;
        SourcePath = plan.Sources[0].Path;
    }

    public string SourcePath { get; }

    public PrintSettings Settings { get; private set; } = new();

    /// <summary>The paper in use, in points, already turned to suit the drawing.</summary>
    public PdfPageSize Paper { get; private set; } = new(595f, 842f);

    /// <summary>Which sheets of the document to print, in order.</summary>
    public IReadOnlyList<int> Pages { get; private set; } = [];

    /// <summary>Sheets of paper this job will produce.</summary>
    public IReadOnlyList<PrintPiece> Pieces => _pieces;

    /// <summary>Output resolution in dots per inch.</summary>
    public float Dpi { get; set; } = 300f;

    /// <summary>Why a sheet came out blank, when one did.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// The marks to print over the drawing, by sheet.
    ///
    /// They are drawn here rather than left to PDFium for two reasons: the ones
    /// made in this sitting are not in the file yet, and the ones that are get
    /// hidden on every handle the queue opens so that the viewer can own them.
    /// Printing them from the same model the screen draws is what makes the
    /// paper match the screen — including marks that have never been saved.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<Annotation>> Marks { get; set; } =
        new Dictionary<int, IReadOnlyList<Annotation>>();

    /// <summary>
    /// The revision laid over each sheet, while the reader is comparing.
    ///
    /// Printed and not left off, because a comparison is made to be shown to
    /// somebody: half of its use is the copy that goes to the meeting. Asked
    /// per sheet rather than handed over as a table because the pairing is the
    /// viewer's to decide and it can change while the dialog is open.
    /// </summary>
    public Func<int, OverlaySheet?>? Overlay { get; set; }

    /// <summary>
    /// Opens a private view of the document for printing — of the arrangement
    /// on screen, which is not always the one in the file.
    ///
    /// Every file the plan reads from is opened, its own handle each, for the
    /// same reason the marks are drawn from the viewer's model: what comes out
    /// of the plotter has to be what the reader is looking at, sheets moved
    /// about and sheets brought in from elsewhere included.
    /// </summary>
    public static async Task<PdfPrintJob> OpenAsync(PagePlan plan)
    {
        var job = new PdfPrintJob(plan);

        foreach (var source in plan.Sources)
        {
            var info = await PdfRenderQueue.Shared.OpenDocumentAsync(source.Path, source.Password);
            job._documents.Add(info.DocumentId);
        }

        job._open = true;
        job._sheets = plan.EffectiveSizes();
        job.Pages = Enumerable.Range(0, plan.Count).ToArray();
        job.Rebuild();
        return job;
    }

    public IReadOnlyList<PdfPageSize> Sheets => _sheets;

    public int RotationOf(int pageIndex) =>
        pageIndex >= 0 && pageIndex < _plan.Count ? _plan[pageIndex].QuarterTurns & 3 : 0;

    /// <summary>
    /// Which open document a sheet is drawn from and which of its pages, or a
    /// document of −1 for blank paper — which prints as the blank it is.
    /// </summary>
    private (int DocumentId, int PageIndex) OriginOf(int sheet)
    {
        if (sheet < 0 || sheet >= _plan.Count) return (-1, -1);

        var slot = _plan[sheet];
        return slot.IsBlank || slot.Source >= _documents.Count
            ? (-1, -1)
            : (_documents[slot.Source], slot.PageIndex);
    }

    public void Configure(PrintSettings settings, PdfPageSize paperSize, IReadOnlyList<int>? pages = null)
    {
        Settings = settings;
        Paper = paperSize;
        if (pages is not null)
        {
            Pages = pages;
        }
        Rebuild();
    }

    private void Rebuild() => _pieces = PrintLayout.Build(_sheets, Pages, Paper, Settings);

    /// <summary>
    /// The paper this piece prints on, turned to suit its drawing. Each sheet
    /// of a mixed set may want the paper a different way round, so this is
    /// asked per piece rather than once for the job.
    /// </summary>
    public PdfPageSize PaperFor(PrintPiece piece) =>
        PrintLayout.OrientPaper(_sheets[piece.PageIndex], Paper, Settings);

    /// <summary>
    /// Draws one sheet of paper. The destination is in device-independent
    /// pixels, because that is the space a print drawing session works in;
    /// everything up to here is in PDF points.
    ///
    /// The drawing goes down a band at a time. Nothing else would fit: a single
    /// bitmap of an A0 at plotter resolution is half a gigabyte, and the bands
    /// are pixel-exact slices of the same render, which is what keeps the seams
    /// invisible.
    /// </summary>
    public void DrawPiece(ICanvasResourceCreator device, CanvasDrawingSession ds, PrintPiece piece)
    {
        // Every early exit says why. A sheet that comes out blank is the one
        // failure that looks like success, so it must never be silent.
        if (!_open)
        {
            Problem = Loc.Get("PrintClosedEarly");
            return;
        }

        var (documentId, pageIndex) = OriginOf(piece.PageIndex);
        if (documentId < 0)
        {
            // Blank paper: nothing to render, and the marks on it still go on.
            DrawMarks(ds, piece);
            return;
        }

        var source = piece.Source;
        var destination = piece.Destination;
        if (source.Width <= 0 || source.Height <= 0)
        {
            Problem = Loc.Get("PrintEmptyArea");
            return;
        }

        if (destination.Width <= 0 || destination.Height <= 0)
        {
            Problem = Loc.Get("PrintEmptySlot");
            return;
        }

        // Output pixels per PDF point, at the resolution asked for.
        double scale = destination.Width / source.Width * (Dpi / 72.0);
        if (scale <= 0)
        {
            Problem = Loc.Get("PrintNoScale");
            return;
        }

        int startX = (int)Math.Floor(source.X * scale);
        int startY = (int)Math.Floor(source.Y * scale);
        int totalWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        int totalHeight = Math.Max(1, (int)Math.Round(source.Height * scale));

        int bandHeight = (int)Math.Clamp(MaxBandPixels / Math.Max(1, totalWidth), 1, totalHeight);

        // Destination is in points and the drawing session works in DIPs, so
        // the conversion belongs here too. Leaving it out shrinks every print
        // to three quarters of the paper, since 72 points is 96 DIPs.
        double dipsPerPixel = destination.Height * PointsToDips / totalHeight;

        // Aliased, and on whole-pixel boundaries, for the same reason the
        // viewer does it: two abutting antialiased edges leave a pale seam.
        var antialiasing = ds.Antialiasing;
        ds.Antialiasing = CanvasAntialiasing.Aliased;

        try
        {
            for (int offset = 0; offset < totalHeight; offset += bandHeight)
            {
                int height = Math.Min(bandHeight, totalHeight - offset);

                // Waited on rather than awaited, and deliberately. This runs on
                // the thread Win2D raised the print event on, and the drawing
                // session belongs to that thread; an await would resume the
                // rest of this method on a thread pool thread and the drawing
                // would go nowhere. The wait cannot deadlock: the render queue
                // is its own thread and completes off this one.
                var band = _queue.RequestPrintBandAsync(
                    documentId,
                    pageIndex,
                    RotationOf(piece.PageIndex),
                    scale,
                    startX,
                    startY + offset,
                    totalWidth,
                    height,
                    Settings.Monochrome,
                    Overlay?.Invoke(piece.PageIndex)).GetAwaiter().GetResult();

                if (band is not { } data)
                {
                    Problem = Loc.Get("PrintNoBitmap");
                    continue;
                }

                using var bitmap = CanvasBitmap.CreateFromBytes(
                    device,
                    data.Bgra,
                    data.Width,
                    data.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized);

                double top = destination.Y * PointsToDips + offset * dipsPerPixel;
                double bottom = destination.Y * PointsToDips + (offset + height) * dipsPerPixel;

                var target = new Rect(
                    destination.X * PointsToDips,
                    top,
                    destination.Width * PointsToDips,
                    bottom - top);
                ds.DrawImage(bitmap, target);
            }
        }
        finally
        {
            ds.Antialiasing = antialiasing;
        }

        DrawMarks(ds, piece);
    }

    /// <summary>
    /// Lays this piece's marks over the drawing, in vector rather than as
    /// pixels: a stroke drawn here comes out at the plotter's own resolution
    /// instead of the render's.
    ///
    /// Clipped to the piece, because a poster split across several sheets must
    /// not print the same arrow on two of them.
    /// </summary>
    private void DrawMarks(CanvasDrawingSession ds, PrintPiece piece)
    {
        if (!Marks.TryGetValue(piece.PageIndex, out var marks) || marks.Count == 0) return;

        var source = piece.Source;
        var destination = piece.Destination;

        // DIPs per point of the sheet, and where the sheet's origin would fall
        // if the whole of it were on this piece of paper.
        double scale = destination.Width / source.Width * PointsToDips;
        double originX = destination.X * PointsToDips - source.X * scale;
        double originY = destination.Y * PointsToDips - source.Y * scale;

        var sheet = _sheets[piece.PageIndex];
        int rotation = RotationOf(piece.PageIndex);

        // The placement wants the sheet as the file draws it; _sheets holds it
        // as the reader turned it, so an odd turn has the axes back to front.
        var (sheetWidth, sheetHeight) = (rotation & 1) == 1
            ? (sheet.HeightPt, sheet.WidthPt)
            : (sheet.WidthPt, sheet.HeightPt);

        var clip = new Rect(
            destination.X * PointsToDips,
            destination.Y * PointsToDips,
            destination.Width * PointsToDips,
            destination.Height * PointsToDips);

        var placement = new SheetPlacement(sheetWidth, sheetHeight, rotation, originX, originY, scale);

        using (ds.CreateLayer(1f, clip))
        {
            foreach (var mark in marks)
            {
                AnnotationRenderer.Draw(ds, mark, placement, Settings.Monochrome);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_open) return;

        _open = false;
        foreach (int documentId in _documents)
        {
            await _queue.CloseDocumentAsync(documentId);
        }
        _documents.Clear();
    }
}
