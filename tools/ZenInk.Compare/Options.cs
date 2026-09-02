using System.Globalization;

namespace ZenInk.Compare;

/// <summary>What to measure, and against what.</summary>
public sealed record Options(
    string SheetPath,
    string RevisionPath,
    int Sheet,
    int? RevisionPage,
    int Tiles,
    IReadOnlyList<double> Zooms,
    bool Stretch,
    bool SkipImage)
{
    /// <summary>
    /// The two zooms worth knowing about: a sheet laid out to fit the screen,
    /// which is where a reader arrives, and the drawing at its own size, which
    /// is where they go to read a change.
    /// </summary>
    private static readonly double[] DefaultZooms = [0.35, 1.0];

    public static Options? Parse(string[] args)
    {
        var loose = new List<string>();
        int sheet = 0;
        int? revisionPage = null;
        int tiles = 5;
        var zooms = new List<double>();
        bool stretch = false;
        bool skipImage = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hoja" when i + 1 < args.Length:
                    sheet = Math.Max(0, Whole(args[++i]) - 1);
                    break;
                case "--pagina" when i + 1 < args.Length:
                    revisionPage = Math.Max(0, Whole(args[++i]) - 1);
                    break;
                case "--tiles" when i + 1 < args.Length:
                    tiles = Math.Clamp(Whole(args[++i]), 1, 40);
                    break;
                case "--zoom" when i + 1 < args.Length:
                    zooms.Add(Fraction(args[++i]));
                    break;
                case "--estirar":
                    stretch = true;
                    break;
                case "--sin-imagen":
                    skipImage = true;
                    break;
                default:
                    loose.Add(args[i]);
                    break;
            }
        }

        // With nothing given, the fixtures: two issues of one drawing that
        // tools/ZenInk.Fixtures leaves in %TEMP%. They prove the tool works;
        // they are not what it is for.
        string[] paths = loose.Count >= 2
            ?
            [
                loose[0], loose[1]
            ]
            :
            [
                Path.Combine(Path.GetTempPath(), "zenink-revisiones-J.pdf"),
                Path.Combine(Path.GetTempPath(), "zenink-revisiones-K.pdf"),
            ];

        foreach (string path in paths)
        {
            if (File.Exists(path)) continue;

            Console.Error.WriteLine($"No existe «{path}».");
            Console.Error.WriteLine("""
                Uso: dotnet run --project tools/ZenInk.Compare -- hoja.pdf revision.pdf [opciones]
                     --hoja N · --pagina N · --tiles N · --zoom X · --estirar · --sin-imagen

                Sin argumentos usa las dos revisiones de tools/ZenInk.Fixtures:
                     dotnet run --project tools/ZenInk.Fixtures -- revisiones
                """);
            return null;
        }

        return new Options(
            paths[0],
            paths[1],
            sheet,
            revisionPage,
            tiles,
            zooms.Count > 0 ? zooms : DefaultZooms,
            stretch,
            skipImage);
    }

    private static int Whole(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 1;

    /// <summary>Takes a zoom either way round the decimal mark, since it is typed in a Spanish shell.</summary>
    private static double Fraction(string text) =>
        double.TryParse(
            text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        && value > 0
            ? value
            : 1.0;
}

public static class Numbers
{
    /// <summary>
    /// The middle time rather than the mean: the first tile of a page pays for
    /// parsing it, and one such outlier would drag an average away from what
    /// panning actually feels like.
    /// </summary>
    public static double Median(IReadOnlyList<long> times)
    {
        if (times.Count == 0) return 0;

        var sorted = times.Order().ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2.0;
    }
}
