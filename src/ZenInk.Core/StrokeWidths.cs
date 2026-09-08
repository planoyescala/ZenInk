//-----------------------------------------------------------------------------------------
// <copyright file="StrokeWidths.cs" company="plano y escala">
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

/// <summary>
/// The line widths a mark can be drawn with, as a ladder of round numbers.
///
/// A plain range would not do. What a reviewer needs at the thin end is a
/// quarter of a point between one stop and the next — the difference between a
/// line that sits on the drawing and one that buries it — and what they need at
/// the thick end is a mark somebody can see across an A0 from the other side of
/// the table. Those are two orders of magnitude apart, and a slider that spans
/// both evenly puts every width worth using in the first tenth of its travel.
///
/// So the stops are spaced the way a reader thinks about them rather than the
/// way a number line does: closely where the choice is delicate, and doubling
/// once it stops being. Every one of them is a figure you would say out loud.
///
/// The values are PDF points, which is what the mark stores and what goes into
/// the file. On an A0 the top of the ladder is about seventeen millimetres of
/// ink — a marker pen, which is the point of it.
/// </summary>
public static class StrokeWidths
{
    /// <summary>The stops, thinnest first. Points.</summary>
    public static readonly IReadOnlyList<float> Stops =
    [
        0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f, 5f, 6f, 8f, 10f, 12f, 16f, 20f, 24f, 32f, 40f, 48f,
    ];

    /// <summary>The thinnest and the thickest a mark may be.</summary>
    public static float Thinnest => Stops[0];

    public static float Thickest => Stops[^1];

    /// <summary>
    /// The stop at a position on the ladder, for a position that may be off
    /// either end — which is what a slider hands over while it is being built.
    /// </summary>
    public static float At(int index) => Stops[Math.Clamp(index, 0, Stops.Count - 1)];

    /// <summary>
    /// Where a width sits on the ladder: the nearest stop to it.
    ///
    /// Nearest and not the one below, because this is what reads a mark that
    /// was made elsewhere — an older file, another program, a width typed into
    /// a PDF by hand — back onto the slider. Rounding those down would walk
    /// every reopened drawing one stop thinner.
    /// </summary>
    public static int IndexOf(float widthPt)
    {
        int nearest = 0;
        float best = Math.Abs(Stops[0] - widthPt);

        for (int i = 1; i < Stops.Count; i++)
        {
            float distance = Math.Abs(Stops[i] - widthPt);
            if (distance >= best) continue;

            best = distance;
            nearest = i;
        }

        return nearest;
    }
}
