//-----------------------------------------------------------------------------------------
// <copyright file="TestPlan.cs" company="plano y escala">
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

namespace ZenInk.Tests;

/// <summary>
/// Builds the arrangements a save is given. Most checks want "the document as
/// it came, with these turns on it", which is what every save looked like
/// before sheets could be moved.
/// </summary>
public static class TestPlan
{
    /// <summary>One sheet per turn given, in the document's own order.</summary>
    public static PagePlan Turns(string path, params int[] quarterTurns)
    {
        var sizes = new PdfPageSize[quarterTurns.Length];
        Array.Fill(sizes, new PdfPageSize(TestPdf.PageWidth, TestPdf.PageHeight));

        var plan = PagePlan.Identity(sizes, path);
        for (int i = 0; i < quarterTurns.Length; i++)
        {
            if ((quarterTurns[i] & 3) != 0)
            {
                plan = plan.Rotate([i], quarterTurns[i]).Plan;
            }
        }
        return plan;
    }

    /// <summary>The document as it came, however many sheets it has.</summary>
    public static PagePlan Of(string path, int pageCount) => Turns(path, new int[pageCount]);
}
