//-----------------------------------------------------------------------------------------
// <copyright file="TestPdf.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Security.Cryptography;
using System.Text;

namespace ZenInk.Tests;

/// <summary>
/// Builds small PDFs by hand so the suite has known-good ground truth without
/// depending on sample files. Content is deliberately asymmetric on both axes,
/// so any flip, rotation or offset error shows up as ink in the wrong corner.
/// </summary>
public static class TestPdf
{
    public const int PageWidth = 400;
    public const int PageHeight = 600;

    /// <summary>A single page with a black rectangle in the top-left of the rendered image.</summary>
    public static string WriteCornerMark(int rotate)
    {
        // PDF space is y-up, so a high y is the visual top.
        string content = "0 0 0 rg\n0 450 100 150 re f\n";
        string rotateEntry = rotate == 0 ? "" : $"/Rotate {rotate}";

        return Write($"zenink-corner-{rotate}", content, rotateEntry, withFont: false);
    }

    /// <summary>A single page with the word ZENINK near the top-left, in a standard font.</summary>
    public static string WriteText(int rotate)
    {
        string content = "BT /F1 36 Tf 30 520 Td (ZENINK) Tj ET\n";
        string rotateEntry = rotate == 0 ? "" : $"/Rotate {rotate}";

        return Write($"zenink-text-{rotate}", content, rotateEntry, withFont: true);
    }

    /// <summary>A single page with a black rectangle at an arbitrary spot, for telling documents apart.</summary>
    public static string WriteRectangle(string name, string rectangle)
    {
        return Write(name, $"0 0 0 rg\n{rectangle} re f\n", "", withFont: false);
    }

    /// <summary>
    /// A page with several rectangles on it. What comparing two revisions needs
    /// and a single rectangle cannot give: a drawing that is mostly the same as
    /// another one, and differs in a named place.
    /// </summary>
    public static string WriteRectangles(string name, params string[] rectangles)
    {
        string content = "0 0 0 rg\n" + string.Concat(rectangles.Select(rectangle => $"{rectangle} re f\n"));
        return Write(name, content, "", withFont: false);
    }

    /// <summary>
    /// A set of sheets, each one a different width and each with its ink at a
    /// different height.
    ///
    /// Both on purpose: the width says which sheet a page is without rendering
    /// anything, and the ink says the drawing travelled with it. A set where
    /// every sheet looked alike — which is what a real set of plans looks
    /// like — would let a reordering check pass on a document that never
    /// moved.
    /// </summary>
    public static string WriteSheets(string name, int sheets)
    {
        var objects = new List<string> { "<</Type/Catalog/Pages 2 0 R>>", "" };

        var kids = new List<string>();
        for (int i = 0; i < sheets; i++)
        {
            // Page object then content object, so a page's number is 3 + 2i.
            int page = 3 + (i * 2);
            kids.Add($"{page} 0 R");

            string content = $"0 0 0 rg\n20 {40 + (i * 60)} 100 40 re f\n";
            objects.Add($"<</Type/Page/Parent 2 0 R/MediaBox[0 0 {SheetWidth(i)} {PageHeight}]/Contents {page + 1} 0 R>>");
            objects.Add($"<</Length {content.Length}>>\nstream\n{content}endstream");
        }

        objects[1] = $"<</Type/Pages/Kids[{string.Join(' ', kids)}]/Count {sheets}>>";
        return WriteObjects(name, objects);
    }

    /// <summary>The width of sheet <paramref name="index"/> of a <see cref="WriteSheets"/> set.</summary>
    public static float SheetWidth(int index) => PageWidth + (index * 10);

    /// <summary>
    /// Three sheets with an index over them: two headings, the first with a
    /// child. A set of floor plans out of Revit brings one of these, and
    /// nothing else in the suite exercises the bookmark tree.
    /// </summary>
    public static string WriteOutlined(string name)
    {
        var objects = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R/Outlines 9 0 R>>",
            "<</Type/Pages/Kids[3 0 R 5 0 R 7 0 R]/Count 3>>",
        };

        for (int i = 0; i < 3; i++)
        {
            string content = $"0 0 0 rg\n20 {40 + (i * 60)} 100 40 re f\n";
            objects.Add($"<</Type/Page/Parent 2 0 R/MediaBox[0 0 {PageWidth} {PageHeight}]/Contents {4 + (i * 2)} 0 R>>");
            objects.Add($"<</Length {content.Length}>>\nstream\n{content}endstream");
        }

        objects.Add("<</Type/Outlines/First 10 0 R/Last 12 0 R/Count 3>>");
        objects.Add("<</Title(Planta baja)/Parent 9 0 R/Dest[3 0 R/Fit]/First 11 0 R/Last 11 0 R/Count 1/Next 12 0 R>>");
        objects.Add("<</Title(Detalle de escalera)/Parent 10 0 R/Dest[5 0 R/Fit]>>");
        objects.Add("<</Title(Planta primera)/Parent 9 0 R/Dest[7 0 R/Fit]/Prev 10 0 R>>");

        return WriteObjects(name, objects);
    }

    /// <summary>
    /// A single page holding stroked lines wide enough that forcing them to
    /// hairline is unmistakable, unlike the sub-point strokes typical of a
    /// real drawing.
    /// </summary>
    public static string WriteThickLines(string name, float strokeWidth)
    {
        var content = new StringBuilder();
        content.Append("0 0 0 RG\n");
        content.Append($"{strokeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)} w\n");
        for (int i = 0; i < 6; i++)
        {
            int y = 80 + i * 80;
            content.Append($"40 {y} m 360 {y} l S\n");
        }
        return Write(name, content.ToString(), "", withFont: false);
    }

    /// <summary>
    /// A single page carrying an annotation ZenInk did not write — someone
    /// else's review comment. Nothing marks it as ours, so saving must leave it
    /// exactly where it is, and PDFium must go on drawing it into the tiles.
    /// </summary>
    public static string WriteForeignAnnotation(string name) =>
        Write(
            name,
            "0 0 0 rg\n0 450 100 150 re f\n",
            "",
            withFont: false,
            extraPageEntries: "/Annots[5 0 R]",
            extraObjects: ["<</Type/Annot/Subtype/Square/Rect[200 100 350 250]/C[0 0.35 0.78]/F 4/Border[0 0 3]>>"]);

    /// <summary>
    /// The same drawing written the way a real plan is: a cross-reference
    /// stream instead of a table, with the catalogue and the page tucked inside
    /// a compressed object stream, and the rows packed behind a PNG predictor.
    ///
    /// This is not an exotic case to be thorough about — every drawing out of
    /// Revit or AutoCAD looks like this, and anything that reads only the
    /// classic table works on every fixture here and on no actual plan.
    /// </summary>
    public static string WriteModern(string name)
    {
        const string content = "0 0 0 rg\n0 450 100 150 re f\n";

        // Objects 2, 3 and 4 go inside the object stream. The content stream
        // cannot: a stream is never packed into another one.
        string[] packed =
        [
            "<</Type/Catalog/Pages 3 0 R>>",
            "<</Type/Pages/Kids[4 0 R]/Count 1>>",
            $"<</Type/Page/Parent 3 0 R/MediaBox[0 0 {PageWidth} {PageHeight}]/Contents 5 0 R>>",
        ];

        var bodies = new StringBuilder();
        var pairs = new StringBuilder();
        for (int i = 0; i < packed.Length; i++)
        {
            pairs.Append($"{i + 2} {bodies.Length} ");
            bodies.Append(packed[i]).Append('\n');
        }

        string header = pairs.ToString();
        byte[] objectStream = Deflate(Encoding.Latin1.GetBytes(header + bodies));

        var output = new MemoryStream();
        Put(output, "%PDF-1.5\n");

        long objectStreamAt = output.Position;
        Put(output, $"1 0 obj\n<</Type/ObjStm/N {packed.Length}/First {header.Length}"
                    + $"/Filter/FlateDecode/Length {objectStream.Length}>>\nstream\n");
        output.Write(objectStream);
        Put(output, "\nendstream\nendobj\n");

        long contentAt = output.Position;
        Put(output, $"5 0 obj\n<</Length {content.Length}>>\nstream\n{content}endstream\nendobj\n");

        long xrefAt = output.Position;

        // type, then two fields whose meaning depends on it: an offset and a
        // generation for a plain object, the holding stream and an index for a
        // packed one.
        var rows = new List<byte[]>
        {
            Row(0, 0, 65535),
            Row(1, objectStreamAt, 0),
            Row(2, 1, 0),
            Row(2, 1, 1),
            Row(2, 1, 2),
            Row(1, contentAt, 0),
            Row(1, xrefAt, 0),
        };

        byte[] table = Deflate(PngUp(rows));
        Put(output, $"6 0 obj\n<</Type/XRef/Size {rows.Count}/Index[0 {rows.Count}]/W[1 4 2]"
                    + $"/Root 2 0 R/Filter/FlateDecode/DecodeParms<</Predictor 12/Columns 7>>"
                    + $"/Length {table.Length}>>\nstream\n");
        output.Write(table);
        Put(output, $"\nendstream\nendobj\nstartxref\n{xrefAt}\n%%EOF\n");

        string path = Path.Combine(Path.GetTempPath(), $"{name}.pdf");
        File.WriteAllBytes(path, output.ToArray());
        return path;
    }

    /// <summary>
    /// A PDF locked with a password, written the oldest and simplest way the
    /// format allows: the standard security handler, revision 2, RC4 at 40 bits.
    ///
    /// Weak on purpose — it is a fixture, not a safe — and it is what every
    /// reader has understood since 1996, so what it proves is that ZenInk tells
    /// «locked» apart from «broken», which is the only thing being tested.
    /// </summary>
    public static string WriteEncrypted(string name, string password)
    {
        const string content = "0 0 0 rg\n0 450 100 150 re f\n";
        byte[] id = Convert.FromHexString("0123456789ABCDEF0123456789ABCDEF");

        byte[] padded = Pad(password);
        byte[] owner = Rc4(MD5.HashData(padded)[..5], padded);

        var forKey = new MemoryStream();
        forKey.Write(padded);
        forKey.Write(owner);
        forKey.Write(BitConverter.GetBytes(-1));   // /P, little-endian
        forKey.Write(id);
        byte[] fileKey = MD5.HashData(forKey.ToArray())[..5];

        byte[] user = Rc4(fileKey, Padding);
        byte[] stream = Rc4(ObjectKey(fileKey, 4, 0), Encoding.Latin1.GetBytes(content));

        var objects = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 {PageWidth} {PageHeight}]/Contents 4 0 R>>",
            $"<</Length {stream.Length}>>\nstream\n{Encoding.Latin1.GetString(stream)}\nendstream",
            // The encryption dictionary is the one thing that is never encrypted.
            $"<</Filter/Standard/V 1/R 2/O <{Convert.ToHexString(owner)}>/U <{Convert.ToHexString(user)}>/P -1>>",
        };

        var output = new MemoryStream();
        Put(output, "%PDF-1.4\n");

        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Put(output, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        long xrefAt = output.Position;
        Put(output, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            Put(output, $"{offset:D10} 00000 n \n");
        }
        Put(output, $"trailer\n<</Size {objects.Count + 1}/Root 1 0 R/Encrypt {objects.Count} 0 R"
                    + $"/ID[<{Convert.ToHexString(id)}><{Convert.ToHexString(id)}>]>>\n"
                    + $"startxref\n{xrefAt}\n%%EOF\n");

        string path = Path.Combine(Path.GetTempPath(), $"{name}.pdf");
        File.WriteAllBytes(path, output.ToArray());
        return path;
    }

    /// <summary>The pad the standard security handler tops every password up with.</summary>
    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private static byte[] Pad(string password)
    {
        var bytes = Encoding.Latin1.GetBytes(password);
        var padded = new byte[32];

        int taken = Math.Min(32, bytes.Length);
        Array.Copy(bytes, padded, taken);
        Array.Copy(Padding, 0, padded, taken, 32 - taken);
        return padded;
    }

    /// <summary>Every object gets its own key, mixed from the file's and the object's number.</summary>
    private static byte[] ObjectKey(byte[] fileKey, int number, int generation)
    {
        var mixed = new MemoryStream();
        mixed.Write(fileKey);
        mixed.Write([(byte)number, (byte)(number >> 8), (byte)(number >> 16)]);
        mixed.Write([(byte)generation, (byte)(generation >> 8)]);

        return MD5.HashData(mixed.ToArray())[..Math.Min(fileKey.Length + 5, 16)];
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;

        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return output;
    }

    private static void Put(Stream output, string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        output.Write(bytes, 0, bytes.Length);
    }

    private static byte[] Row(byte kind, long second, int third) =>
        [kind, (byte)(second >> 24), (byte)(second >> 16), (byte)(second >> 8), (byte)second,
         (byte)(third >> 8), (byte)third];

    /// <summary>Packs the rows with the PNG "Up" filter, which is what writers use here.</summary>
    private static byte[] PngUp(List<byte[]> rows)
    {
        int columns = rows[0].Length;
        var packed = new byte[rows.Count * (columns + 1)];
        var previous = new byte[columns];

        for (int r = 0; r < rows.Count; r++)
        {
            int at = r * (columns + 1);
            packed[at] = 2;
            for (int i = 0; i < columns; i++)
            {
                packed[at + 1 + i] = (byte)(rows[r][i] - previous[i]);
            }
            previous = rows[r];
        }
        return packed;
    }

    private static byte[] Deflate(byte[] data)
    {
        var output = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(
                   output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Writes numbered objects out with a classic cross-reference table. Object
    /// 1 is the catalogue, which is what the trailer points at.
    /// </summary>
    private static string WriteObjects(string name, IReadOnlyList<string> objects)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.4\n");

        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xrefOffset = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }
        sb.Append($"trailer\n<</Size {objects.Count + 1}/Root 1 0 R>>\nstartxref\n{xrefOffset}\n%%EOF\n");

        string path = Path.Combine(Path.GetTempPath(), $"{name}.pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
        return path;
    }

    private static string Write(
        string name,
        string content,
        string rotateEntry,
        bool withFont,
        string extraPageEntries = "",
        IReadOnlyList<string>? extraObjects = null)
    {
        int contentLength = Encoding.ASCII.GetByteCount(content);
        string resources = withFont ? "/Resources<</Font<</F1 5 0 R>>>>" : "";

        var objects = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 {PageWidth} {PageHeight}]{rotateEntry}{resources}{extraPageEntries}/Contents 4 0 R>>",
            $"<</Length {contentLength}>>\nstream\n{content}endstream",
        };

        if (withFont)
        {
            objects.Add("<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        }

        if (extraObjects is not null)
        {
            objects.AddRange(extraObjects);
        }

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.4\n");

        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xrefOffset = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }
        sb.Append($"trailer\n<</Size {objects.Count + 1}/Root 1 0 R>>\nstartxref\n{xrefOffset}\n%%EOF\n");

        string path = Path.Combine(Path.GetTempPath(), $"{name}.pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
        return path;
    }
}
