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

        await PdfRenderQueue.Shared.SaveChangesCopyAsync(
            source, target,
            quarterTurns: [],
            annotations: new Dictionary<int, IReadOnlyList<Annotation>> { [0] = marks });

        Console.WriteLine($"{target}  ({marks.Count} marcas)");
        return 0;
    }
}
