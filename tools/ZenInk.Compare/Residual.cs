//-----------------------------------------------------------------------------------------
// <copyright file="Residual.cs" company="plano y escala">
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
/// How much of the ink the two renders disagree about, and whether sliding one
/// over the other by a few points would agree better.
///
/// This is the question a fixture cannot answer. Two plots of the same drawing
/// out of the same file differ by a clean rounding, and the alignment nails
/// them; two real re-issues differ in margins, in the title block and in
/// wherever the plotter put the origin. If a nudge of two millimetres agrees
/// far better than where the automatic alignment lands, then
/// <c>SheetAlignment.Nudged</c> — which the engine has and nothing calls — is
/// what the reader is missing.
///
/// The measure mirrors the engine's own: ink that has no counterpart within a
/// pixel, asked from both sides. A dilation is shift-invariant, so both spreads
/// are computed once and the offsets are read out of them — otherwise this
/// would be three hundred dilations of a three-megapixel sheet.
/// </summary>
public static class Residual
{
    /// <summary>Same threshold the engine floods at, so the number means the same thing.</summary>
    private const int ChangeThreshold = 32;

    /// <summary>Every other pixel in each direction. A quarter of the work, and a percentage does not need more.</summary>
    private const int Step = 2;

    public sealed record Hit(int Dx, int Dy, double AtAlignment, double AtBest, bool AtEdge)
    {
        public double DxPt(double pointsPerPixel) => Dx * pointsPerPixel;

        public double DyPt(double pointsPerPixel) => Dy * pointsPerPixel;
    }

    public static Hit Search(
        ReadOnlySpan<byte> sheetBgra, ReadOnlySpan<byte> revisionBgra, int width, int height, int radius)
    {
        var sheet = Ink(sheetBgra, width, height);
        var revision = Ink(revisionBgra, width, height);
        var sheetSpread = Dilate(sheet, width, height, 1);
        var revisionSpread = Dilate(revision, width, height, 1);

        double atAlignment = Disagreement(sheet, revision, sheetSpread, revisionSpread, width, height, 0, 0);

        int bestDx = 0, bestDy = 0;
        double best = atAlignment;

        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx == 0 && dy == 0) continue;

                double score = Disagreement(sheet, revision, sheetSpread, revisionSpread, width, height, dx, dy);
                if (score >= best) continue;

                best = score;
                bestDx = dx;
                bestDy = dy;
            }
        }

        return new Hit(
            bestDx,
            bestDy,
            atAlignment,
            best,
            Math.Abs(bestDx) == radius || Math.Abs(bestDy) == radius);
    }

    /// <summary>
    /// The share of sampled pixels the two do not agree about, with the
    /// revision slid by (<paramref name="dx"/>, <paramref name="dy"/>) — positive
    /// meaning right and down, the same direction as the alignment's offset.
    /// </summary>
    private static double Disagreement(
        byte[] sheet,
        byte[] revision,
        byte[] sheetSpread,
        byte[] revisionSpread,
        int width,
        int height,
        int dx,
        int dy)
    {
        long changed = 0;
        long looked = 0;

        for (int y = 0; y < height; y += Step)
        {
            int from = y - dy;
            if (from < 0 || from >= height) continue;

            int row = y * width;
            int sourceRow = from * width;

            for (int x = 0; x < width; x += Step)
            {
                int at = x - dx;
                if (at < 0 || at >= width) continue;

                int here = row + x;
                int there = sourceRow + at;
                looked++;

                int onlySheet = sheet[here] - Math.Min(sheet[here], revisionSpread[there]);
                int onlyRevision = revision[there] - Math.Min(revision[there], sheetSpread[here]);

                if (Math.Max(onlySheet, onlyRevision) >= ChangeThreshold) changed++;
            }
        }

        return looked == 0 ? 100.0 : changed * 100.0 / looked;
    }

    /// <summary>How dark each pixel is — Rec. 601, the same weighting the engine greys with.</summary>
    private static byte[] Ink(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var ink = new byte[width * height];
        for (int i = 0; i < ink.Length; i++)
        {
            int at = i * 4;
            if (at + 2 >= bgra.Length) break;

            int luma = ((bgra[at + 2] * 299) + (bgra[at + 1] * 587) + (bgra[at] * 114)) / 1000;
            ink[i] = (byte)(255 - luma);
        }
        return ink;
    }

    /// <summary>Square maximum, separated into a pass across and a pass down.</summary>
    private static byte[] Dilate(byte[] ink, int width, int height, int radius)
    {
        var across = new byte[ink.Length];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                byte most = 0;
                for (int at = Math.Max(0, x - radius); at <= Math.Min(width - 1, x + radius); at++)
                {
                    if (ink[row + at] > most) most = ink[row + at];
                }
                across[row + x] = most;
            }
        }

        var down = new byte[ink.Length];
        for (int y = 0; y < height; y++)
        {
            int from = Math.Max(0, y - radius);
            int to = Math.Min(height - 1, y + radius);
            for (int x = 0; x < width; x++)
            {
                byte most = 0;
                for (int at = from; at <= to; at++)
                {
                    byte value = across[(at * width) + x];
                    if (value > most) most = value;
                }
                down[(y * width) + x] = most;
            }
        }

        return down;
    }
}
