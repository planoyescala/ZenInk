//-----------------------------------------------------------------------------------------
// <copyright file="RenderTests.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using PDFiumCore;
using static ZenInk.Tests.TestRunner;

namespace ZenInk.Tests;

/// <summary>
/// Guards the two properties the tiled viewer depends on: that a page renders
/// the right way up whatever its /Rotate, and that a tile is exactly the
/// corresponding slice of a full-page render.
/// </summary>
public static class RenderTests
{
    /// <summary>Where the corner mark should land, as a fraction of the rendered page, per /Rotate.</summary>
    private static (double X, double Y) ExpectedCorner(int rotate) => rotate switch
    {
        0 => (0.12, 0.12),    // top-left
        90 => (0.88, 0.12),   // rotated clockwise -> top-right
        180 => (0.88, 0.88),  // bottom-right
        _ => (0.12, 0.88),    // bottom-left
    };

    public static void Run()
    {
        Section("Rendering — orientation and tile geometry");

        foreach (int rotate in new[] { 0, 90, 180, 270 })
        {
            string path = TestPdf.WriteCornerMark(rotate);
            var document = fpdfview.FPDF_LoadDocument(path, null);
            if (document is null)
            {
                Check($"/Rotate {rotate}: document loads", false, $"PDFium error {fpdfview.FPDF_GetLastError()}");
                continue;
            }

            var page = fpdfview.FPDF_LoadPage(document, 0)!;
            int width = (int)Math.Round(fpdfview.FPDF_GetPageWidthF(page));
            int height = (int)Math.Round(fpdfview.FPDF_GetPageHeightF(page));

            bool swapped = rotate is 90 or 270;
            Check($"/Rotate {rotate}: reported size accounts for rotation ({width}x{height})",
                swapped
                    ? width == TestPdf.PageHeight && height == TestPdf.PageWidth
                    : width == TestPdf.PageWidth && height == TestPdf.PageHeight);

            var full = Bitmap.Render(page, 1.0, 0, 0, width, height);

            var (fx, fy) = ExpectedCorner(rotate);
            int markX = (int)(width * fx), markY = (int)(height * fy);
            int oppositeX = (int)(width * (1 - fx)), oppositeY = (int)(height * (1 - fy));

            Check($"/Rotate {rotate}: ink lands in the expected corner",
                Bitmap.IsDark(full, width, markX, markY));
            Check($"/Rotate {rotate}: the opposite corner stays blank",
                !Bitmap.IsDark(full, width, oppositeX, oppositeY));

            CheckTiling(rotate, page, full, width, height);

            fpdfview.FPDF_ClosePage(page);
            fpdfview.FPDF_CloseDocument(document);
            File.Delete(path);
        }
    }

    /// <summary>
    /// Every tile must be pixel-identical to its slice of the full-page render.
    /// This is what makes the tile grid seamless; a one-pixel drift here shows
    /// up as visible seams once the viewer stitches tiles together.
    /// </summary>
    private static void CheckTiling(int rotate, FpdfPageT page, byte[] full, int width, int height)
    {
        const int tileSize = 128;
        int columns = (int)Math.Ceiling(width / (double)tileSize);
        int rows = (int)Math.Ceiling(height / (double)tileSize);
        int mismatched = 0;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                var tile = Bitmap.Render(page, 1.0, -column * tileSize, -row * tileSize, tileSize, tileSize);

                for (int y = 0; y < tileSize; y++)
                {
                    int pageY = row * tileSize + y;
                    if (pageY >= height) break;

                    for (int x = 0; x < tileSize; x++)
                    {
                        int pageX = column * tileSize + x;
                        if (pageX >= width) break;

                        if (Bitmap.IsDark(tile, tileSize, x, y) != Bitmap.IsDark(full, width, pageX, pageY))
                        {
                            mismatched++;
                        }
                    }
                }
            }
        }

        Check($"/Rotate {rotate}: {columns}x{rows} tiles match the full render",
            mismatched == 0, $"{mismatched} pixels differ");
    }
}
