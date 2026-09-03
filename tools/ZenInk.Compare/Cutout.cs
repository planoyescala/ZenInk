//-----------------------------------------------------------------------------------------
// <copyright file="Cutout.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

namespace ZenInk.Compare;

/// <summary>
/// A tile-sized window out of the middle of the two sweep renders, so the
/// composition can be timed on ink from a real drawing rather than on a
/// generated pattern. The middle because that is where a plan carries its
/// drawing; a corner would time the composition of blank paper.
/// </summary>
public static class Cutout
{
    public static (byte[] Sheet, byte[] Revision) Middle(
        byte[] sheet, byte[] revision, int width, int height, int side)
    {
        int left = Math.Max(0, (width - side) / 2);
        int top = Math.Max(0, (height - side) / 2);

        return (Window(sheet, width, height, left, top, side), Window(revision, width, height, left, top, side));
    }

    private static byte[] Window(byte[] source, int width, int height, int left, int top, int side)
    {
        // White where the band is smaller than a tile, which is blank paper —
        // the same thing the renderer would have put there.
        var cut = new byte[side * side * 4];
        Array.Fill(cut, (byte)255);

        for (int y = 0; y < side && top + y < height; y++)
        {
            int from = (((top + y) * width) + left) * 4;
            int take = Math.Min(side, width - left) * 4;
            if (take <= 0 || from + take > source.Length) break;

            Array.Copy(source, from, cut, y * side * 4, take);
        }

        return cut;
    }
}
