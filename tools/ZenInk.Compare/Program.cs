//-----------------------------------------------------------------------------------------
// <copyright file="Program.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Diagnostics;
using ZenInk.Compare;
using ZenInk.Core;

// Measures the revision comparison against real drawings — the A0s out of a CAD
// package that cannot live in the repository because they are clients'. The
// test suite covers the composition on fixtures of a couple of kilobytes; what
// it cannot cover is what a sweep of two three-million-object sheets costs,
// whether panning stays under the hand when every tile is two renders, and
// whether the automatic alignment lands on two real plots of one drawing.
//
//   dotnet run --project tools/ZenInk.Compare -- hoja.pdf revision.pdf [opciones]
//   dotnet run --project tools/ZenInk.Compare              (usa las fixtures de %TEMP%)
//
//   --hoja N       qué hoja del documento se mide (1 por defecto)
//   --pagina N     qué página de la revisión se le empareja (la misma por defecto)
//   --tiles N      cuántos tiles se cronometran por zoom (5)
//   --zoom X       a qué escala; repetible (0,35 y 1 por defecto: encajada y al 100 %)
//   --estirar      alinea con CompareFit.Stretch en vez de Fit
//   --sin-imagen   no escribe el PNG de la composición
//
// Both files are copied into %TEMP% before anything opens them. The drawings
// this exists for are the ones on the desktop, and nothing here has any
// business touching those in place.

var options = Options.Parse(args);
if (options is null) return 1;

string sheetPath = Workbench.Stage(options.SheetPath);
string revisionPath = Workbench.Stage(options.RevisionPath);

Footprint.Mark("al empezar");

var queue = PdfRenderQueue.Shared;
var sheetDoc = await queue.OpenDocumentAsync(sheetPath);
var revisionDoc = await queue.OpenDocumentAsync(revisionPath);

Footprint.Mark("los dos abiertos");

int sheet = Math.Clamp(options.Sheet, 0, sheetDoc.Pages.Count - 1);
int revisionPage = Math.Clamp(options.RevisionPage ?? sheet, 0, revisionDoc.Pages.Count - 1);

var laid = sheetDoc.Pages[sheet];
var other = revisionDoc.Pages[revisionPage];

Console.WriteLine($"── hoja      {Path.GetFileName(options.SheetPath)}  ({Workbench.Size(options.SheetPath)})");
Console.WriteLine($"             {sheetDoc.Pages.Count} hoja(s) · hoja {sheet + 1}: {laid.WidthPt:0}x{laid.HeightPt:0} pt "
                  + $"({laid.WidthPt / 72 * 25.4:0} x {laid.HeightPt / 72 * 25.4:0} mm)");
Console.WriteLine($"── revisión  {Path.GetFileName(options.RevisionPath)}  ({Workbench.Size(options.RevisionPath)})");
Console.WriteLine($"             {revisionDoc.Pages.Count} hoja(s) · página {revisionPage + 1}: {other.WidthPt:0}x{other.HeightPt:0} pt");
Console.WriteLine();

// The alignment the viewer would pick, with no reader's turn on top: this tool
// looks at a sheet the way it comes out of the file.
var fit = options.Stretch ? CompareFit.Stretch : CompareFit.Fit;
var alignment = SheetAlignment.For(laid, other, 0, fit);
var overlay = new OverlaySheet(revisionDoc.DocumentId, revisionPage, alignment, ComparePalette.Default);

Console.WriteLine($"Alineación ({fit}): giro {alignment.QuarterTurns} · "
                  + $"escala {alignment.ScaleX:0.0000} x {alignment.ScaleY:0.0000} · "
                  + $"desplazamiento {alignment.OffsetXPt:0.0} , {alignment.OffsetYPt:0.0} pt");
Console.WriteLine();

// --- the sweep --------------------------------------------------------------
//
// Exactly what PdfTiledViewer.SweepAsync does, timed in its three parts: the
// two full-page renders and the flood. Which of the three dominates decides
// what could be done about it — slow renders want the sweep moved off the first
// draw, a slow flood wants a coarser grid.

double scale = RevisionInk.DetectionDpi / 72.0;
long pixels = (long)Math.Ceiling(laid.WidthPt * scale) * (long)Math.Ceiling(laid.HeightPt * scale);
if (pixels > RevisionInk.MaxDetectionPixels)
{
    scale *= Math.Sqrt(RevisionInk.MaxDetectionPixels / (double)pixels);
}

int width = Math.Max(1, (int)Math.Ceiling(laid.WidthPt * scale));
int height = Math.Max(1, (int)Math.Ceiling(laid.HeightPt * scale));

Console.WriteLine($"Barrido  {width}x{height} px ({scale * 72:0} ppp, {(double)width * height / 1e6:0.00} Mpx)");

var clock = Stopwatch.StartNew();
var sheetBand = await queue.RequestPrintBandAsync(
    sheetDoc.DocumentId, sheet, 0, scale, 0, 0, width, height, monochrome: false);
long sheetMs = clock.ElapsedMilliseconds;

clock.Restart();
var revisionBand = await queue.RequestBandAsync(
    overlay.DocumentId,
    overlay.PageIndex,
    alignment.QuarterTurns,
    scale * alignment.ScaleX,
    scale * alignment.ScaleY,
    -(int)Math.Round(alignment.OffsetXPt * scale),
    -(int)Math.Round(alignment.OffsetYPt * scale),
    width,
    height,
    monochrome: false);
long revisionMs = clock.ElapsedMilliseconds;

if (sheetBand is not { } a || revisionBand is not { } b)
{
    Console.Error.WriteLine("El motor no devolvió una de las dos páginas.");
    return 1;
}

clock.Restart();
var found = RevisionInk.FindChanges(a.Bgra, b.Bgra, width, height, 1.0 / scale);
long floodMs = clock.ElapsedMilliseconds;

long total = sheetMs + revisionMs + floodMs;
Console.WriteLine($"         hoja {sheetMs:N0} ms · revisión {revisionMs:N0} ms · barrido {floodMs:N0} ms "
                  + $"→ {total:N0} ms de «Buscando…»");

long changedArea = found.Sum(region => (long)region.Pixels);
Console.WriteLine($"         {found.Count} cambio(s) · {changedArea * 100.0 / (width * height):0.00} % del papel"
                  + (found.Count >= 200 ? "  (tope de 200: la hoja se redibujó, no se revisó)" : ""));

// How much of the sheet the biggest box covers. A change the reader is meant
// to step onto is a place; one that spans the drawing is the merge having
// swallowed the sheet, and it says "1 cambio" while meaning "todo".
double widest = found.Count == 0
    ? 0
    : found.Max(region => region.Box.Width * (double)region.Box.Height) * 100.0 / (laid.WidthPt * laid.HeightPt);

if (widest > 25)
{
    Console.WriteLine($"         el cambio mayor cubre el {widest:0} % de la hoja: "
                      + "eso no es un sitio al que ir, es la hoja entera.");
}

foreach (var region in found.Take(5))
{
    Console.WriteLine($"           · {region.Box.X:0},{region.Box.Y:0} pt  "
                      + $"{region.Box.Width:0}x{region.Box.Height:0} pt  ({region.Pixels} px)");
}
if (found.Count > 5) Console.WriteLine($"           · … y {found.Count - 5} más");
Console.WriteLine();

// --- does the automatic alignment land? -------------------------------------
//
// The question the fixture cannot answer. Two real plots of one drawing differ
// in margins and in title block, not in a clean rounding, and if a nudge of a
// few points would agree far better than where the alignment puts it, then
// SheetAlignment.Nudged — which today nobody calls — is what the reader needs.

Footprint.Mark("tras el barrido");

var residual = Residual.Search(a.Bgra, b.Bgra, width, height, radius: 8);
Console.WriteLine($"Encaje   tinta discrepante donde cae ahora: {residual.AtAlignment:0.00} %");
Console.WriteLine($"         el mejor desplazamiento está en {residual.DxPt(1.0 / scale):+0.0;-0.0;0} , "
                  + $"{residual.DyPt(1.0 / scale):+0.0;-0.0;0} pt → {residual.AtBest:0.00} %");
Console.WriteLine(residual.Dx == 0 && residual.Dy == 0
    ? "         la alineación automática ya está en el mejor sitio."
    : $"         moverla ganaría {residual.AtAlignment - residual.AtBest:0.00} puntos"
      + (residual.AtAlignment - residual.AtBest > 0.2
          ? ": hace falta el desplazamiento manual."
          : ": poco, la automática vale."));
Console.WriteLine();

// --- what panning costs -----------------------------------------------------
//
// A composed tile asks PDFium for two renders instead of one, and on a dense A0
// a render costs walking the objects whatever size the bitmap is. If the
// composed tile is not roughly twice the plain one, something else is paying.

// A 2560x1440 screen in 512-pixel tiles, which is what a pan onto untouched
// paper has to fill before the reader sees the drawing again.
const double ScreenTiles = 5 * 3;

// What the composition itself costs on real ink, apart from the two renders it
// waits for. This is the number the architecture question turns on: if
// composing a tile's worth of pixels is milliseconds, then composing over
// rasters already in the cache would be worth the cache it needs; if it is
// hundreds, rasterizing twice was never the expensive half.
{
    int side = ZoomLevels.TileSize + (RevisionInk.Spread * 2);
    var (cropA, cropB) = Cutout.Middle(a.Bgra, b.Bgra, width, height, side);
    var runs = new List<long>();

    for (int i = 0; i < 5; i++)
    {
        clock.Restart();
        RevisionInk.Compose(cropA, cropB, side, side, RevisionInk.Spread, ComparePalette.Default);
        runs.Add(clock.ElapsedMilliseconds);
    }

    Console.WriteLine($"Componer un tile de {side}x{side} px sobre tinta ya rasterizada: "
                      + $"{Numbers.Median(runs):N0} ms  ({string.Join(" ", runs)})");
    Console.WriteLine();
}

foreach (double zoom in options.Zooms)
{
    int level = ZoomLevels.LevelForScale(zoom);
    double levelScale = ZoomLevels.ScaleForLevel(level);
    int cols = Math.Max(1, (int)Math.Ceiling(laid.WidthPt * levelScale / ZoomLevels.TileSize));
    int rows = Math.Max(1, (int)Math.Ceiling(laid.HeightPt * levelScale / ZoomLevels.TileSize));

    Console.WriteLine($"Tiles al {zoom * 100:0} %  (nivel {level}, escala {levelScale:0.000}, "
                      + $"{cols}x{rows} tiles para la hoja entera)");

    var plain = new List<long>();
    var composed = new List<long>();
    var recoloured = new List<long>();

    // The same square asked for again in the other colours: what moving the
    // background slider, swapping which file is red, or panning back over
    // paper already seen actually costs, now that both halves stay rasterized.
    var otherColours = overlay with { Palette = ComparePalette.Default.Swapped() };

    // Down the diagonal: an empty corner and a full title block cost different
    // amounts, and one column of the sheet would report only one of them.
    // Distinct squares only — asking twice for the same one would time the
    // cache and call it a render.
    var walk = new List<(int Col, int Row)>();
    for (int i = 0; i < options.Tiles; i++)
    {
        var at = (
            Col: cols == 1 ? 0 : i * (cols - 1) / Math.Max(1, options.Tiles - 1),
            Row: rows == 1 ? 0 : i * (rows - 1) / Math.Max(1, options.Tiles - 1));

        if (!walk.Contains(at)) walk.Add(at);
    }

    foreach (var (col, row) in walk)
    {
        var key = new TileKey(sheet, level, col, row, 0, sheetDoc.DocumentId);

        clock.Restart();
        await queue.RequestTileAsync(sheetDoc.DocumentId, key, ZoomLevels.TileSize);
        plain.Add(clock.ElapsedMilliseconds);

        clock.Restart();
        await queue.RequestComparisonTileAsync(sheetDoc.DocumentId, key, ZoomLevels.TileSize, overlay);
        composed.Add(clock.ElapsedMilliseconds);

        clock.Restart();
        await queue.RequestComparisonTileAsync(sheetDoc.DocumentId, key, ZoomLevels.TileSize, otherColours);
        recoloured.Add(clock.ElapsedMilliseconds);
    }

    double plainMedian = Numbers.Median(plain);
    double composedMedian = Numbers.Median(composed);

    Console.WriteLine($"         normal      {plainMedian:N0} ms de mediana  ({string.Join(" ", plain)})");
    Console.WriteLine($"         compuesto   {composedMedian:N0} ms de mediana  ({string.Join(" ", composed)})");
    Console.WriteLine($"         recoloreado {Numbers.Median(recoloured):N0} ms de mediana  ({string.Join(" ", recoloured)})");
    // Zoomed out the whole sheet is fewer tiles than the screen holds, and
    // counting a full screen there would invent a wait nobody ever sits through.
    double onScreen = Math.Min(ScreenTiles, cols * rows);

    Console.WriteLine($"         ×{composedMedian / Math.Max(1.0, plainMedian):0.00} · lo que se ve son {onScreen:0} tiles "
                      + $"→ {composedMedian * onScreen / 1000:0.0} s de reponer la pantalla");
    Console.WriteLine();
}

// --- memory -----------------------------------------------------------------

Footprint.Mark("tras los tiles");

Console.WriteLine($"Memoria  ({GC.GetTotalMemory(false) / (1024 * 1024):N0} MB de ellos gestionados)");
Footprint.Report();
Console.WriteLine($"         un tile compuesto son {ZoomLevels.TileSize * ZoomLevels.TileSize * 4 / 1024:N0} KB; "
                  + "la caché de comparación son 192 MB además de los 384 del visor");
Console.WriteLine();

// --- the picture ------------------------------------------------------------
//
// The one part of a comparison that no number settles. Written through the same
// band path the capture and the print use, so what lands in the PNG is what the
// tiles would have shown.

if (!options.SkipImage)
{
    double shrink = Math.Min(1.0, Picture.WidestPixels / Math.Max(laid.WidthPt, 1f));
    int pngWidth = Math.Max(1, (int)Math.Ceiling(laid.WidthPt * shrink));
    int pngHeight = Math.Max(1, (int)Math.Ceiling(laid.HeightPt * shrink));

    clock.Restart();
    var picture = await queue.RequestPrintBandAsync(
        sheetDoc.DocumentId, sheet, 0, shrink, 0, 0, pngWidth, pngHeight, monochrome: false, overlay);
    long pictureMs = clock.ElapsedMilliseconds;

    if (picture is { } composedSheet)
    {
        string png = Path.Combine(
            Path.GetTempPath(),
            $"zenink-comparacion-{Path.GetFileNameWithoutExtension(options.SheetPath)}.png");

        Picture.Write(composedSheet, png);
        Console.WriteLine($"Imagen   {png}  ({pngWidth}x{pngHeight} px, {pictureMs:N0} ms)");
    }
}

await queue.CloseDocumentAsync(sheetDoc.DocumentId);
await queue.CloseDocumentAsync(revisionDoc.DocumentId);
return 0;
