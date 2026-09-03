//-----------------------------------------------------------------------------------------
// <copyright file="Picture.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ZenInk.Core;

namespace ZenInk.Compare;

/// <summary>
/// Writes a composed sheet out to look at. Numbers say what a comparison costs;
/// only a picture says whether the red, the blue and the grey landed where the
/// drawing says they should.
/// </summary>
public static class Picture
{
    /// <summary>
    /// Wide enough that a title block is still readable on an A0, small enough
    /// to open. About 60 ppp on a sheet 3.370 points across.
    /// </summary>
    public const double WidestPixels = 2800;

    public static void Write(TileBitmapData data, string path)
    {
        // The engine hands back BGRA, bottom byte first, which is exactly what
        // GDI+ calls Format32bppArgb — so the buffer goes in a row at a time
        // with no channel shuffling.
        using var bitmap = new Bitmap(data.Width, data.Height, PixelFormat.Format32bppArgb);
        var locked = bitmap.LockBits(
            new Rectangle(0, 0, data.Width, data.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            for (int y = 0; y < data.Height; y++)
            {
                Marshal.Copy(
                    data.Bgra,
                    y * data.Width * 4,
                    locked.Scan0 + (y * locked.Stride),
                    data.Width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }

        bitmap.Save(path, ImageFormat.Png);
    }
}
