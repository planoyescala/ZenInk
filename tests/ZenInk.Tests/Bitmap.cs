//-----------------------------------------------------------------------------------------
// <copyright file="Bitmap.cs" company="plano y escala">
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

namespace ZenInk.Tests;

/// <summary>Direct PDFium rendering helpers, used to establish ground truth for the engine's output.</summary>
public static class Bitmap
{
    /// <summary>
    /// Renders a region using the same call the engine makes: the whole scaled
    /// page positioned so that the requested tile lands on the bitmap.
    /// </summary>
    public static byte[] Render(FpdfPageT page, double scale, int startX, int startY, int outWidth, int outHeight)
    {
        int scaledWidth = Math.Max(1, (int)Math.Ceiling(fpdfview.FPDF_GetPageWidthF(page) * scale));
        int scaledHeight = Math.Max(1, (int)Math.Ceiling(fpdfview.FPDF_GetPageHeightF(page) * scale));

        var bitmap = fpdfview.FPDFBitmapCreateEx(outWidth, outHeight, (int)FPDFBitmapFormat.BGRA, IntPtr.Zero, outWidth * 4)
            ?? throw new InvalidOperationException("FPDFBitmap_CreateEx returned null.");
        try
        {
            fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, outWidth, outHeight, 0xFFFFFFFFUL);
            fpdfview.FPDF_RenderPageBitmap(bitmap, page, startX, startY, scaledWidth, scaledHeight, 0, 0);

            int stride = fpdfview.FPDFBitmapGetStride(bitmap);
            IntPtr buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);

            var packed = new byte[outWidth * 4 * outHeight];
            for (int row = 0; row < outHeight; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    buffer + row * stride, packed, row * outWidth * 4, outWidth * 4);
            }
            return packed;
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }

    /// <summary>
    /// Renders a whole page the way a reader that shows annotations would —
    /// which is the only way to see what ZenInk actually put in the file, since
    /// the engine hides its own marks on every handle it opens.
    /// </summary>
    public static byte[] RenderWithAnnotations(FpdfPageT page, int width, int height)
    {
        var bitmap = fpdfview.FPDFBitmapCreateEx(width, height, (int)FPDFBitmapFormat.BGRA, IntPtr.Zero, width * 4)
            ?? throw new InvalidOperationException("FPDFBitmap_CreateEx returned null.");
        try
        {
            fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFFUL);
            fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, (int)RenderFlags.RenderAnnotations);

            int stride = fpdfview.FPDFBitmapGetStride(bitmap);
            IntPtr buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);

            var packed = new byte[width * 4 * height];
            for (int row = 0; row < height; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    buffer + row * stride, packed, row * width * 4, width * 4);
            }
            return packed;
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }

    /// <summary>One pixel, as blue/green/red.</summary>
    public static (byte B, byte G, byte R) Pixel(byte[] bgra, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2]);
    }

    public static bool IsDark(byte[] bgra, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return bgra[i] < 128 && bgra[i + 1] < 128 && bgra[i + 2] < 128;
    }

    /// <summary>Bounding box of the non-white pixels, or null if the image is blank.</summary>
    public static (int Left, int Top, int Right, int Bottom)? InkBounds(byte[] bgra, int width, int height)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!IsDark(bgra, width, x, y)) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < left ? null : (left, top, right, bottom);
    }
}
