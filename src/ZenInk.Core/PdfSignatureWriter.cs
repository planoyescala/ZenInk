//-----------------------------------------------------------------------------------------
// <copyright file="PdfSignatureWriter.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Globalization;
using System.Text;

namespace ZenInk.Core;

/// <summary>
/// Adds a signature to a PDF by appending to it, never by rewriting it.
///
/// An incremental update leaves every original byte where it was, and three
/// things follow from that at once: a signature that arrived with the drawing
/// stays valid, the reader can still recover the version that was signed, and
/// the file handed back differs from the one taken in only by what was added.
/// A library that re-serialises the document cannot offer any of the three,
/// which is why nothing else writes this.
///
/// Only what a signature needs is written: the signature itself, its form
/// field, and re-emitted copies of the catalogue, the form and the one page the
/// field sits on, each with a single entry added.
/// </summary>
internal static class PdfSignatureWriter
{
    /// <summary>Room left for the /ByteRange numbers, patched once the layout is known.</summary>
    private const int ByteRangeRoom = 46;

    internal static void Append(
        string sourcePath, string targetPath, IPdfSigner signer, PdfSignatureOptions options)
    {
        var map = PdfFileMap.Read(sourcePath);
        if (map.Unsupported is not null)
        {
            throw new NotSupportedException($"No se puede firmar este PDF: {map.Unsupported}.");
        }
        if (map.Encrypted)
        {
            // Everything appended would have to be encrypted the same way, and
            // it is not. Writing anyway would produce a file that opens and is
            // quietly wrong, which is the worst of the outcomes available.
            throw new NotSupportedException(
                CoreText.Say(
                    "CoreEncryptedCannotSign",
                    "This PDF is encrypted and cannot be signed yet. Save a copy without a password and sign that."));
        }
        if (map.Root < 0 || map.Size < 0)
        {
            throw new InvalidDataException(CoreText.Say(
                "CoreNoCatalogue", "The PDF does not say where its catalogue is."));
        }

        int pageNumber = FindPage(map, map.Root, options.PageIndex)
            ?? throw new InvalidDataException($"No hay hoja {options.PageIndex + 1} en el documento.");

        int field = map.Size;
        int signature = map.Size + 1;
        int nextFree = map.Size + 2;

        var changed = new Dictionary<int, string>();

        // The form field the signature hangs off. Its rectangle is empty unless
        // one is asked for: an invisible signature is the honest default on a
        // drawing, where a stamp lands on somebody's ink.
        var where = options.Rectangle;
        string rectangle = where is null
            ? "0 0 0 0"
            : Invariant($"{where.Value.X} {where.Value.Y} {where.Value.X + where.Value.Width} {where.Value.Y + where.Value.Height}");

        string appearance = where is { } box && options.Appearance is { } stamp
            ? Stamp(box, options, stamp, changed, ref nextFree)
            : "";

        changed[field] =
            $"<</Type/Annot/Subtype/Widget/FT/Sig/T({Escape(FieldName(map))})"
            + $"/Rect[{rectangle}]/F 132/P {pageNumber} 0 R/V {signature} 0 R{appearance}>>";

        int reserved = signer.ReserveBytes;
        changed[signature] = SignatureDictionary(signer.Name, options, reserved);

        // The page gains the field among its annotations.
        changed[pageNumber] = AddToArray(map, pageNumber, "/Annots", $"{field} 0 R", changed);

        // And the catalogue gains a form to hold it.
        string catalogue = Body(map, map.Root, changed);
        var acroForm = KeyValue(catalogue, "/AcroForm");
        if (acroForm is null)
        {
            int form = nextFree++;
            changed[form] = $"<</Fields[{field} 0 R]/SigFlags 3>>";
            changed[map.Root] = Insert(catalogue, $"/AcroForm {form} 0 R");
        }
        else if (ReferenceIn(catalogue, acroForm.Value) is int existing)
        {
            string body = AddToArray(map, existing, "/Fields", $"{field} 0 R", changed);
            changed[existing] = SetKey(body, "/SigFlags", "3");
        }
        else
        {
            // An inline form: the same edit, one level in.
            string inner = catalogue[acroForm.Value.Start..acroForm.Value.End];
            string edited = SetKey(AddToArrayText(inner, "/Fields", $"{field} 0 R"), "/SigFlags", "3");
            changed[map.Root] = catalogue[..acroForm.Value.Start] + edited + catalogue[acroForm.Value.End..];
        }

        Write(map, targetPath, changed, signature, reserved, signer);
    }

    /// <summary>
    /// A name no other field on the document already uses. Readers key their
    /// signature panel off it, and two fields sharing a name collapse into one.
    /// </summary>
    private static string FieldName(PdfFileMap map)
    {
        string stamp = DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        return $"ZenInk firma {stamp}";
    }

    /// <summary>
    /// Writes the objects a visible signature needs — two fonts and the form
    /// that draws the stamp — and returns the /AP entry that points at it.
    ///
    /// The fonts are the standard Helvetica pair, not embedded. A PDF reader has
    /// had those built in since 1993, and embedding a face here would put a
    /// couple of hundred kilobytes into a drawing to write six short lines.
    /// </summary>
    private static string Stamp(
        RectPt box,
        PdfSignatureOptions options,
        PdfSignatureAppearance appearance,
        Dictionary<int, string> changed,
        ref int nextFree)
    {
        int regular = nextFree++;
        int bold = nextFree++;
        int form = nextFree++;

        changed[regular] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>";
        changed[bold] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica-Bold/Encoding/WinAnsiEncoding>>";

        var lines = PdfSignatureStamp.Lines(
            appearance, options.Reason, options.Location, DateTimeOffset.Now);

        var (width, height) = PdfSignatureStamp.LayoutBox(box.Width, box.Height, options.PageQuarterTurns);
        string drawing = PdfSignatureStamp.Draw(lines, width, height);
        int length = Encoding.Latin1.GetByteCount(drawing);

        // The bounding box is the box the text is laid out in; the matrix turns
        // it against the page's own /Rotate. A reader maps the turned box onto
        // /Rect for us, which is why nothing here has to know where /Rect is.
        changed[form] =
            $"<</Type/XObject/Subtype/Form/FormType 1"
            + Invariant($"/BBox[0 0 {width} {height}]")
            + $"/Matrix[{PdfSignatureStamp.Matrix(options.PageQuarterTurns)}]"
            + $"/Resources<</ProcSet[/PDF/Text]/Font<</F1 {regular} 0 R/F2 {bold} 0 R>>>>"
            + $"/Length {length}>>\nstream\n{drawing}endstream";

        return $"/AP<</N {form} 0 R>>";
    }

    private static string SignatureDictionary(string name, PdfSignatureOptions options, int reserved)
    {
        var now = DateTimeOffset.Now;
        string when = now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string zone = Invariant(
            $"{(now.Offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(now.Offset.Hours):00}'{Math.Abs(now.Offset.Minutes):00}'");

        var dictionary = new StringBuilder();
        dictionary.Append("<</Type/Sig/Filter/Adobe.PPKLite");
        // What makes it a PAdES signature rather than a bare PKCS#7 one. The
        // blob has to genuinely be CAdES for this to be true, which is what the
        // signed attributes in CertificateSigner are for.
        dictionary.Append("/SubFilter/ETSI.CAdES.detached");
        dictionary.Append($"/Name({Escape(name)})");
        dictionary.Append($"/M(D:{when}{zone})");
        if (options.Reason.Length > 0) dictionary.Append($"/Reason({Escape(options.Reason)})");
        if (options.Location.Length > 0) dictionary.Append($"/Location({Escape(options.Location)})");
        // Placeholders of a fixed width, patched once the file is laid out.
        dictionary.Append($"/ByteRange[{new string(' ', ByteRangeRoom)}]");
        dictionary.Append($"/Contents<{new string('0', reserved * 2)}>");
        dictionary.Append(">>");
        return dictionary.ToString();
    }

    /// <summary>
    /// Appends the changed objects and a cross-reference for them, and hands
    /// back the file as it now stands.
    ///
    /// Both things that are ever appended to a signed drawing go through here —
    /// the signature itself and, later, the proof that its certificate was good
    /// at the time — because the part that must not go wrong is the same for
    /// both: every original byte stays where it was.
    /// </summary>
    private static byte[] Appended(
        PdfFileMap map,
        Dictionary<int, string> changed,
        int reserved,
        int signature,
        out long byteRangeAt,
        out long contentsAt)
    {
        var output = new MemoryStream(map.Data.Length + reserved * 2 + 8192);
        output.Write(map.Data, 0, map.Data.Length);
        if (map.Data[^1] is not ((byte)'\n' or (byte)'\r')) output.WriteByte((byte)'\n');

        var offsets = new Dictionary<int, long>();
        byteRangeAt = 0;
        contentsAt = 0;

        foreach (int number in changed.Keys.OrderBy(n => n))
        {
            offsets[number] = output.Position;
            string body = changed[number];

            if (number == signature)
            {
                // Where the two placeholders land in the finished file.
                long bodyAt = output.Position + $"{number} 0 obj\n".Length;
                byteRangeAt = bodyAt + body.IndexOf("/ByteRange[", StringComparison.Ordinal) + "/ByteRange[".Length;
                contentsAt = bodyAt + body.IndexOf("/Contents<", StringComparison.Ordinal) + "/Contents<".Length - 1;
            }

            Append(output, $"{number} 0 obj\n{body}\nendobj\n");
        }

        string identifier = KeyValue(map.Trailer, "/ID") is { } id
            ? $"/ID{map.Trailer[id.Start..id.End]}"
            : "";

        long xrefAt = output.Position;
        if (map.UsesXrefStream)
        {
            // A file whose cross-reference is a stream has to be extended with
            // another stream: a reader following our /Prev backwards would
            // otherwise find a table where the format says a stream must be.
            int xrefObject = changed.Keys.Max() + 1;
            offsets[xrefObject] = xrefAt;

            Append(output, XrefStream(
                xrefObject, offsets, Math.Max(xrefObject + 1, map.Size), map.Root, map.LastXref, identifier));
        }
        else
        {
            Append(output, Table(offsets));
            Append(output, "trailer\n<<");
            Append(output, $"/Size {Math.Max(changed.Keys.Max() + 1, map.Size)}/Root {map.Root} 0 R/Prev {map.LastXref}");
            Append(output, identifier);
            Append(output, ">>\n");
        }
        Append(output, $"startxref\n{xrefAt}\n%%EOF\n");

        return output.ToArray();
    }

    private static void Write(
        PdfFileMap map,
        string targetPath,
        Dictionary<int, string> changed,
        int signature,
        int reserved,
        IPdfSigner signer)
    {
        byte[] file = Appended(map, changed, reserved, signature, out long byteRangeAt, out long contentsAt);

        // The signature covers everything but its own /Contents.
        long from = contentsAt;                       // the '<'
        long to = contentsAt + 1 + reserved * 2 + 1;  // just past the '>'
        Patch(file, byteRangeAt, $"0 {from} {to} {file.LongLength - to}".PadRight(ByteRangeRoom));

        byte[] blob = signer.Sign(new PdfSignedRange(file, from, to));
        if (blob.Length > reserved)
        {
            throw new InvalidOperationException(
                $"La firma ocupa {blob.Length} B y solo se reservaron {reserved} B.");
        }
        Patch(file, from + 1, Convert.ToHexString(blob).PadRight(reserved * 2, '0'));

        File.WriteAllBytes(targetPath, file);
    }

    /// <summary>
    /// Appends the proof that the signing certificates were good — the
    /// certificates themselves, and the answers the authorities gave about
    /// them — as a document security store.
    ///
    /// It goes in as another incremental update, on top of the signature and
    /// after it, which is the only shape that works: the data is about a
    /// signature that has to exist first, and appending leaves that signature
    /// exactly as it was. The signature stops being the newest thing in the
    /// file, and that is normal — it still covers everything up to itself.
    /// </summary>
    internal static void AppendValidation(
        string sourcePath,
        string targetPath,
        IReadOnlyList<byte[]> certificates,
        IReadOnlyList<byte[]> ocsps,
        IReadOnlyList<byte[]> crls,
        IReadOnlyDictionary<string, (int[] Certs, int[] Ocsps, int[] Crls)>? perSignature = null)
    {
        var map = PdfFileMap.Read(sourcePath);
        if (map.Unsupported is not null)
        {
            throw new NotSupportedException(CoreText.Say(
                "CoreValidationUnsupported",
                "The validation data cannot be added: {0}.",
                map.Unsupported));
        }
        if (map.Root < 0 || map.Size < 0)
        {
            throw new InvalidDataException(CoreText.Say(
                "CoreNoCatalogue", "The PDF does not say where its catalogue is."));
        }

        var changed = new Dictionary<int, string>();
        int next = map.Size;

        // Every blob becomes a stream of its own, because that is what a /DSS
        // array holds: references, not bytes.
        List<int> Streams(IReadOnlyList<byte[]> blobs)
        {
            var numbers = new List<int>(blobs.Count);
            foreach (byte[] blob in blobs)
            {
                int number = next++;
                changed[number] = $"<</Length {blob.Length}>>\nstream\n{Encoding.Latin1.GetString(blob)}\nendstream";
                numbers.Add(number);
            }
            return numbers;
        }

        var certObjects = Streams(certificates);
        var ocspObjects = Streams(ocsps);
        var crlObjects = Streams(crls);

        string Array(string key, List<int> numbers) =>
            numbers.Count == 0 ? "" : $"/{key}[{string.Join(" ", numbers.Select(n => $"{n} 0 R"))}]";

        // The per-signature index. Acrobat looks for it before it will call a
        // signature long-term valid: the global arrays say the file carries
        // proof, and this says which proof belongs to which signature.
        string vriEntries = "";
        if (perSignature is { Count: > 0 })
        {
            var entries = new List<string>();
            foreach (var (key, which) in perSignature)
            {
                string body =
                    Array("Cert", [.. which.Certs.Select(i => certObjects[i])])
                    + Array("OCSP", [.. which.Ocsps.Select(i => ocspObjects[i])])
                    + Array("CRL", [.. which.Crls.Select(i => crlObjects[i])]);

                int number = next++;
                changed[number] = $"<<{body}>>";
                entries.Add($"/{key} {number} 0 R");
            }

            int vri = next++;
            changed[vri] = $"<<{string.Join("", entries)}>>";
            vriEntries = $"/VRI {vri} 0 R";
        }

        int dss = next++;
        changed[dss] = $"<<{Array("Certs", certObjects)}{Array("OCSPs", ocspObjects)}{Array("CRLs", crlObjects)}{vriEntries}>>";

        string catalogue = Body(map, map.Root, changed);
        changed[map.Root] = KeyValue(catalogue, "/DSS") is { } already
            ? catalogue[..already.Start] + $"{dss} 0 R" + catalogue[already.End..]
            : Insert(catalogue, $"/DSS {dss} 0 R");

        byte[] file = Appended(map, changed, 0, -1, out _, out _);
        File.WriteAllBytes(targetPath, file);
    }

    /// <summary>The cross-reference table for the appended objects, in runs of consecutive numbers.</summary>
    private static string Table(Dictionary<int, long> offsets)
    {
        var table = new StringBuilder("xref\n");
        var numbers = offsets.Keys.OrderBy(n => n).ToList();

        for (int i = 0; i < numbers.Count;)
        {
            int run = 1;
            while (i + run < numbers.Count && numbers[i + run] == numbers[i] + run) run++;

            table.Append($"{numbers[i]} {run}\n");
            for (int k = 0; k < run; k++)
            {
                table.Append($"{offsets[numbers[i + k]]:D10} 00000 n \n");
            }
            i += run;
        }
        return table.ToString();
    }

    /// <summary>
    /// The same cross-reference, as the stream a PDF 1.5 file wants. Written
    /// uncompressed on purpose: the entries are a few dozen bytes, and leaving
    /// out the filter leaves out the predictor with it — one less thing between
    /// the offsets computed here and the offsets a reader will read back.
    /// </summary>
    private static string XrefStream(
        int self, Dictionary<int, long> offsets, int size, int root, long previous, string identifier)
    {
        var numbers = offsets.Keys.OrderBy(n => n).ToList();
        var index = new StringBuilder();
        var entries = new List<byte>();

        for (int i = 0; i < numbers.Count;)
        {
            int run = 1;
            while (i + run < numbers.Count && numbers[i + run] == numbers[i] + run) run++;

            index.Append($"{numbers[i]} {run} ");
            for (int k = 0; k < run; k++)
            {
                entries.Add(1);                                    // type: a plain object
                entries.AddRange(BigEndian(offsets[numbers[i + k]], 8));
                entries.AddRange(BigEndian(0, 2));                 // generation
            }
            i += run;
        }

        var stream = new StringBuilder();
        stream.Append($"{self} 0 obj\n<</Type/XRef/Size {size}/Index[{index.ToString().TrimEnd()}]");
        stream.Append($"/W[1 8 2]/Root {root} 0 R/Prev {previous}{identifier}/Length {entries.Count}>>\nstream\n");
        stream.Append(Encoding.Latin1.GetString([.. entries]));
        stream.Append("\nendstream\nendobj\n");
        return stream.ToString();
    }

    private static byte[] BigEndian(long value, int width)
    {
        var bytes = new byte[width];
        for (int i = width - 1; i >= 0; i--) { bytes[i] = (byte)(value & 0xFF); value >>= 8; }
        return bytes;
    }

    // --- editing a dictionary that is already in the file ---------------------

    private static string Body(PdfFileMap map, int number, Dictionary<int, string> changed) =>
        changed.TryGetValue(number, out string? edited) ? edited : map.Body(number);

    /// <summary>
    /// Appends a reference to an array-valued key, whether the array is written
    /// in place, reached through a reference, or not there at all yet.
    /// </summary>
    private static string AddToArray(
        PdfFileMap map, int number, string key, string reference, Dictionary<int, string> changed)
    {
        string body = Body(map, number, changed);
        var value = KeyValue(body, key);

        if (value is not null && ReferenceIn(body, value.Value) is int array)
        {
            string arrayBody = Body(map, array, changed).TrimEnd();
            changed[array] = arrayBody[..^1] + $" {reference}]";
            return body;
        }
        return AddToArrayText(body, key, reference);
    }

    private static string AddToArrayText(string body, string key, string reference)
    {
        var value = KeyValue(body, key);
        if (value is null) return Insert(body, $"{key}[{reference}]");

        string array = body[value.Value.Start..value.Value.End].TrimEnd();
        if (!array.EndsWith(']')) return Insert(body, $"{key}[{reference}]");

        return body[..value.Value.Start] + array[..^1] + $" {reference}]" + body[value.Value.End..];
    }

    private static string SetKey(string body, string key, string value)
    {
        var found = KeyValue(body, key);
        return found is null
            ? Insert(body, $"{key} {value}")
            : body[..found.Value.Start] + value + body[found.Value.End..];
    }

    /// <summary>Puts an entry right after the dictionary's opening.</summary>
    private static string Insert(string body, string entry)
    {
        int at = body.IndexOf("<<", StringComparison.Ordinal);
        if (at < 0) throw new InvalidDataException("Se esperaba un diccionario.");
        return body[..(at + 2)] + entry + body[(at + 2)..];
    }

    private static (int Start, int End)? KeyValue(string body, string key) =>
        PdfLexer.Value(Encoding.Latin1.GetBytes(body), 0, key);

    private static int? ReferenceIn(string body, (int Start, int End) value)
    {
        var parts = body[value.Start..value.End].Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length == 3 && parts[2] == "R" && int.TryParse(parts[0], out int number)
            ? number
            : null;
    }

    // --- finding the page ----------------------------------------------------

    private static int? FindPage(PdfFileMap map, int catalogue, int index)
    {
        // The bytes an object sits in are not always the file's: a catalogue in
        // a PDF 1.5 drawing usually lives inside a compressed object stream.
        var (data, start, _) = map.Locate(catalogue);
        var pages = PdfLexer.Value(data, start, "/Pages");
        if (pages is null) return null;

        var (token, _) = PdfLexer.Token(data, pages.Value.Start);
        if (!int.TryParse(token, out int root)) return null;

        int counter = 0;
        return Descend(map, root, index, ref counter, depth: 0);
    }

    private static int? Descend(PdfFileMap map, int node, int index, ref int counter, int depth)
    {
        if (depth > 32) return null;

        var (data, start, _) = map.Locate(node);
        var type = PdfLexer.Value(data, start, "/Type");
        string kind = type is null ? "" : PdfLexer.Text(data, type.Value.Start, type.Value.End).Trim();

        if (kind == "/Page")
        {
            return counter++ == index ? node : null;
        }

        var kids = PdfLexer.Value(data, start, "/Kids");
        if (kids is null) return null;

        foreach (int child in References(data, kids.Value.Start, kids.Value.End))
        {
            if (Descend(map, child, index, ref counter, depth + 1) is int found) return found;
        }
        return null;
    }

    /// <summary>Every "N G R" in a stretch of bytes, in order.</summary>
    private static List<int> References(byte[] data, int start, int end)
    {
        var found = new List<int>();
        int at = start;
        while (at < end)
        {
            var (first, afterFirst) = PdfLexer.Token(data, at);
            if (first.Length == 0) break;

            if (int.TryParse(first, out int number))
            {
                var (_, afterSecond) = PdfLexer.Token(data, afterFirst);
                var (third, afterThird) = PdfLexer.Token(data, afterSecond);
                if (third == "R") { found.Add(number); at = afterThird; continue; }
            }
            at = afterFirst;
        }
        return found;
    }

    // --- bytes ---------------------------------------------------------------

    private static void Append(Stream output, string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        output.Write(bytes, 0, bytes.Length);
    }

    private static void Patch(byte[] file, long at, string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        Array.Copy(bytes, 0, file, at, bytes.Length);
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static string Invariant(FormattableString text) =>
        text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The file with the /Contents hole cut out of it: exactly the bytes a
/// signature covers, presented as one stream so nothing has to hold two copies.
/// </summary>
internal sealed class PdfSignedRange(byte[] file, long holeStart, long holeEnd) : Stream
{
    private long _position;

    private long Total => holeStart + (file.LongLength - holeEnd);

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => Total;
    public override long Position { get => _position; set => _position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int done = 0;
        while (done < count && _position < Total)
        {
            long source = _position < holeStart ? _position : holeEnd + (_position - holeStart);
            long until = _position < holeStart ? holeStart : Total;
            int take = (int)Math.Min(count - done, until - _position);

            Array.Copy(file, source, buffer, offset + done, take);
            _position += take;
            done += take;
        }
        return done;
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Total + offset,
        };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
