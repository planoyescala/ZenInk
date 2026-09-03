//-----------------------------------------------------------------------------------------
// <copyright file="Marks.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Numerics;
using ZenInk.Core;

namespace ZenInk.Signing;

/// <summary>
/// Writes a copy of a drawing carrying ZenInk's own marks, through the very
/// path the app uses.
///
/// The battery needs one of these because the question that decides milestone 3
/// is not whether PDFsharp can rewrite a drawing — it is whether a drawing that
/// already carries marks comes out the other side with them intact, appearance
/// streams and all.
///
/// It runs as its own command, and never in the same process as the rest of the
/// tool: PDFium's library init is global and unreferenced, so the engine's queue
/// and this tool's own renderer must not be alive at the same time.
/// </summary>
public static class Marks
{
    public static async Task<int> WriteAsync(string source, string target)
    {
        var style = new AnnotationStyle(AnnotationColor.Red, 3f);
        var filled = new AnnotationStyle(new AnnotationColor(32, 96, 216), 2f, new AnnotationColor(32, 96, 216));

        var marks = new List<Annotation>
        {
            new(AnnotationKind.Rectangle, [new Vector2(120, 160), new Vector2(420, 340)], filled),
            new(AnnotationKind.Cloud, [new Vector2(500, 200), new Vector2(900, 460)], style),
            new(AnnotationKind.Arrow, [new Vector2(150, 500), new Vector2(600, 640)], style),
            new(AnnotationKind.Note, [new Vector2(700, 700)], style, "Revisar cota"),
        };

        // The sheets as they came: this only adds marks to the first one.
        var opened = await PdfRenderQueue.Shared.OpenDocumentAsync(source);
        var plan = PagePlan.Identity(opened.Pages, source);
        await PdfRenderQueue.Shared.CloseDocumentAsync(opened.DocumentId);

        await PdfRenderQueue.Shared.SaveChangesCopyAsync(
            plan, target,
            annotations: new Dictionary<int, IReadOnlyList<Annotation>> { [0] = marks });

        Console.WriteLine($"{target}  ({marks.Count} marcas)");
        return 0;
    }
}
