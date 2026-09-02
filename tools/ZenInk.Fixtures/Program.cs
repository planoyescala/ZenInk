using System.Globalization;
using System.Text;

// Synthetic drawings to test the viewer against. Real plans cannot live in the
// repository — they are clients' — so these stand in for them, each shaped
// around a failure it is good at exposing.
//
//   denso      an A0 with ~40.000 vector objects: the one to measure against,
//              and the one where PDFium's object traversal is the real cost
//   rellenos   large flat colour fills: where a seam between tiles shows as a
//              pale line and nothing else would reveal it
//   conjunto   six sheets with searchable text: for find, rotation and the
//              two-up view
//   revisiones two issues of one drawing, J and K, differing in a handful of
//              named places: for comparing revisions, where what matters is
//              that the changes are few, separated and obvious enough to say
//              by eye whether the ones found are the ones there

string what = args.Length > 0 ? args[0].ToLowerInvariant() : "denso";
string path = args.Length > 1
    ? args[1]
    : Path.Combine(Path.GetTempPath(), $"zenink-{what}.pdf");

switch (what)
{
    case "denso": WriteDense(path); break;
    case "rellenos": WriteFills(path); break;
    case "conjunto": WriteSet(path); break;
    case "revisiones": WriteRevisions(path); return 0;
    default:
        Console.Error.WriteLine("Uso: denso | rellenos | conjunto | revisiones [ruta]");
        return 1;
}

Console.WriteLine($"{path}  ({new FileInfo(path).Length / 1024} KB)");
return 0;

// --- the drawings -----------------------------------------------------------

static void WriteDense(string path)
{
    const int W = 3370, H = 2384;
    var rng = new Random(7);
    var c = new StringBuilder();

    c.Append("0.13 0.20 0.27 rg\n").Append($"0 {H - 260} {W} 260 re f\n");
    c.Append("0.16 0.62 0.87 rg\n").Append($"0 {H - 278} {W} 18 re f\n");
    c.Append("0.90 0.96 0.91 rg\n").Append($"140 {H - 760} {W - 280} 420 re f\n");
    c.Append("0.85 0.30 0.20 rg\n").Append($"140 90 {W - 280} 220 re f\n");
    c.Append("0 0 0 RG\n0.24 w\n");

    // A grid of hatched rooms: the shape a real floor plan's density takes.
    for (int gx = 0; gx < 14; gx++)
    {
        for (int gy = 0; gy < 9; gy++)
        {
            double x = 160 + gx * 225, y = 380 + gy * 165;
            c.Append($"{N(x)} {N(y)} 205 145 re S\n");

            for (int i = 0; i < 24; i++)
            {
                double t = i * 8.5;
                c.Append($"{N(x + t)} {N(y)} m {N(x + t + 40)} {N(y + 145)} l S\n");
            }

            for (int i = 0; i < 12; i++)
            {
                double yy = y + 6 + i * 11.5;
                c.Append($"{N(x + 4)} {N(yy)} m {N(x + 201)} {N(yy)} l S\n");
            }
        }
    }

    for (int i = 0; i < 4000; i++)
    {
        double x = rng.NextDouble() * (W - 300) + 150;
        double y = rng.NextDouble() * (H - 900) + 380;
        c.Append($"{N(x)} {N(y)} m {N(x + rng.NextDouble() * 70)} {N(y + rng.NextDouble() * 40 - 20)} l S\n");
    }

    c.Append("0 0 0 rg\n");
    for (int i = 0; i < 700; i++)
    {
        double x = rng.NextDouble() * (W - 300) + 150;
        double y = rng.NextDouble() * (H - 900) + 380;
        c.Append($"{N(x)} {N(y)} {N(4 + rng.NextDouble() * 90)} 6 re f\n");
    }

    c.Append($"BT /F1 48 Tf 90 {H - 150} Td (PLANTA GENERAL - NIVEL 00) Tj ET\n");
    for (int i = 0; i < 900; i++)
    {
        double x = rng.NextDouble() * (W - 500) + 160;
        double y = rng.NextDouble() * (H - 900) + 380;
        c.Append($"BT /F1 9 Tf {N(x)} {N(y)} Td (MURO DE CARGA {i % 40:00}) Tj ET\n");
    }

    Write(path, W, H, [c.ToString()], withFont: true);
}

static void WriteFills(string path)
{
    const int W = 1190, H = 842;
    var c = new StringBuilder();

    c.Append(Rgb(0.13, 0.20, 0.27)).Append($"0 {H - 170} {W} 170 re f\n");
    c.Append(Rgb(0.16, 0.62, 0.87)).Append($"0 {H - 182} {W} 12 re f\n");
    c.Append(Rgb(0.90, 0.96, 0.91)).Append($"70 {H - 400} {W - 140} 180 re f\n");

    for (int i = 0; i < 9; i++)
    {
        double grey = i % 2 == 0 ? 0.94 : 1.0;
        c.Append(Rgb(grey, grey, grey)).Append($"70 {H - 460 - i * 42} {W - 140} 42 re f\n");
    }

    c.Append(Rgb(0.85, 0.30, 0.20)).Append($"70 40 {W - 140} 120 re f\n");

    Write(path, W, H, [c.ToString()], withFont: false);
}

static void WriteSet(string path)
{
    const int W = 1190, H = 842;
    string[] titles =
        ["PLANTA BAJA", "PLANTA PRIMERA", "SECCIÓN A-A", "SECCIÓN B-B", "ALZADO NORTE", "DETALLES"];

    var pages = new List<string>();
    for (int p = 0; p < titles.Length; p++)
    {
        var c = new StringBuilder();
        c.Append("0 0 0 RG\n2 w\n");
        c.Append($"20 20 {W - 40} {H - 40} re S\n");
        c.Append($"{W - 340} 30 300 140 re S\n");
        c.Append("1 w\n");
        for (int i = 1; i < 6; i++)
        {
            c.Append($"120 {120 + i * 110} m {W - 380} {120 + i * 110} l S\n");
        }

        c.Append($"BT /F1 34 Tf 60 {H - 90} Td ({titles[p]}) Tj ET\n");
        c.Append($"BT /F1 20 Tf {W - 320} 120 Td (ZenInk - conjunto de prueba) Tj ET\n");
        c.Append($"BT /F1 26 Tf {W - 320} 60 Td (E-{p + 1:00}) Tj ET\n");

        // A phrase repeated across the sheet, so a search has hits to walk.
        for (int i = 0; i < 4; i++)
        {
            c.Append($"BT /F1 16 Tf {160 + i * 200} {200 + (i % 2) * 320} Td (MURO DE CARGA {i + 1}) Tj ET\n");
        }
        c.Append($"BT /F1 16 Tf 160 {H - 200} Td (Cota de replanteo 3,45 m) Tj ET\n");

        pages.Add(c.ToString());
    }

    Write(path, W, H, pages, withFont: true);
}

/// <summary>
/// One drawing issued twice. Everything is shared except five places, and the
/// five are the point: a comparison is only believable if you can look at the
/// result and say whether what it found is what is there.
/// </summary>
static void WriteRevisions(string path)
{
    string stem = path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        ? path[..^4]
        : path;

    string first = $"{stem}-J.pdf";
    string second = $"{stem}-K.pdf";

    WriteIssue(first, "J", newer: false);
    WriteIssue(second, "K", newer: true);

    Console.WriteLine($"{first}  ({new FileInfo(first).Length / 1024} KB)");
    Console.WriteLine($"{second}  ({new FileInfo(second).Length / 1024} KB)");
}

static void WriteIssue(string path, string revision, bool newer)
{
    const int W = 2384, H = 1684;
    var c = new StringBuilder();

    c.Append("0 0 0 RG\n2 w\n");
    c.Append($"40 40 {W - 80} {H - 80} re S\n");
    c.Append($"{W - 700} 70 630 300 re S\n");
    c.Append("1 w\n");

    // The rooms. Shared between the two issues, apart from the one whose wall
    // moved — the change a comparison exists to catch.
    for (int gx = 0; gx < 6; gx++)
    {
        for (int gy = 0; gy < 3; gy++)
        {
            double x = 160 + (gx * 340);
            double y = 480 + (gy * 340);
            double width = newer && gx == 3 && gy == 1 ? 300 : 240;

            c.Append($"{N(x)} {N(y)} {N(width)} 240 re S\n");
            c.Append($"BT /F1 22 Tf {N(x + 16)} {N(y + 200)} Td (LOCAL {(gy * 6) + gx + 1:00}) Tj ET\n");
        }
    }

    // A door added in the newer issue, and a stair block dropped from it.
    if (newer)
    {
        c.Append("3 w\n");
        c.Append($"1360 480 m 1440 480 l S\n");
        c.Append($"1360 480 m 1360 560 l S\n");
        c.Append("1 w\n");
    }
    else
    {
        for (int i = 0; i < 8; i++)
        {
            c.Append($"{N(1800 + (i * 22))} 1500 m {N(1800 + (i * 22))} 1620 l S\n");
        }
        c.Append("1800 1500 176 120 re S\n");
    }

    // A dimension that was revised, and the revision letter in the title block.
    string figure = newer ? "3,80" : "3,45";
    c.Append($"BT /F1 26 Tf 300 1560 Td (COTA DE REPLANTEO {figure} m) Tj ET\n");

    c.Append($"BT /F1 40 Tf {W - 660} 250 Td (PLANTA GENERAL) Tj ET\n");
    c.Append($"BT /F1 28 Tf {W - 660} 180 Td (E-01) Tj ET\n");
    c.Append($"BT /F1 34 Tf {W - 300} 180 Td (REV. {revision}) Tj ET\n");

    Write(path, W, H, [c.ToString()], withFont: true);
}

// --- the file ---------------------------------------------------------------

static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

static string Rgb(double r, double g, double b) =>
    string.Format(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} rg\n", r, g, b);

/// <summary>
/// Writes a PDF by hand rather than through a library. Ground truth for the
/// engine has to be something this project did not produce with the same code
/// it is testing.
/// </summary>
static void Write(string path, int width, int height, IReadOnlyList<string> contents, bool withFont)
{
    int fontObject = 2 + contents.Count * 2 + 1;
    var objects = new List<string>();

    var kids = new StringBuilder();
    for (int p = 0; p < contents.Count; p++)
    {
        kids.Append($"{3 + p * 2} 0 R ");
    }

    objects.Add("<</Type/Catalog/Pages 2 0 R>>");
    objects.Add($"<</Type/Pages/Kids[{kids.ToString().Trim()}]/Count {contents.Count}>>");

    for (int p = 0; p < contents.Count; p++)
    {
        string resources = withFont ? $"/Resources<</Font<</F1 {fontObject} 0 R>>>>" : "";
        objects.Add(
            $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 {width} {height}]{resources}/Contents {4 + p * 2} 0 R>>");
        objects.Add($"<</Length {Encoding.Latin1.GetByteCount(contents[p])}>>\nstream\n{contents[p]}endstream");
    }

    if (withFont)
    {
        objects.Add("<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>");
    }

    var sb = new StringBuilder();
    var offsets = new List<int>();
    sb.Append("%PDF-1.4\n");

    for (int i = 0; i < objects.Count; i++)
    {
        offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
        sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
    }

    int xref = Encoding.Latin1.GetByteCount(sb.ToString());
    sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
    foreach (int offset in offsets)
    {
        sb.Append($"{offset:D10} 00000 n \n");
    }
    sb.Append($"trailer\n<</Size {objects.Count + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");

    File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
}
