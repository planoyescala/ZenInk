using System.IO.Compression;
using System.Text;

namespace ZenInk.Core;

/// <summary>
/// Where every object lives in a PDF, read by walking the cross-reference
/// chain backwards from the end of the file.
///
/// Both shapes of cross-reference are read, and that is not thoroughness for
/// its own sake: PDFium writes classic tables, so that is what ZenInk's own
/// saves look like, but every real drawing measured on this machine — the ones
/// out of Revit and AutoCAD — carries a cross-reference stream instead, with
/// the catalogue tucked inside a compressed object stream. A reader that only
/// understood tables would work on the fixtures and refuse every actual plan.
/// </summary>
public sealed class PdfFileMap
{
    private readonly Dictionary<int, long> _offsets = [];

    /// <summary>Objects that live inside a compressed object stream: number → the stream holding it.</summary>
    private readonly Dictionary<int, int> _inStream = [];

    private readonly Dictionary<int, Dictionary<int, (int Start, int End)>> _streamIndex = [];
    private readonly Dictionary<int, byte[]> _streamData = [];

    public byte[] Data { get; }

    /// <summary>The newest trailer, as raw text. For a cross-reference stream, its dictionary.</summary>
    public string Trailer { get; private set; } = "";

    /// <summary>Where the newest cross-reference section starts: the /Prev of the one we add.</summary>
    public long LastXref { get; private set; }

    public int Root { get; private set; } = -1;

    public int Size { get; private set; } = -1;

    /// <summary>True when the file's own cross-reference is a stream, so ours has to be one too.</summary>
    public bool UsesXrefStream { get; private set; }

    /// <summary>
    /// True when the document is encrypted. Everything appended to it would have
    /// to be encrypted the same way, and this writer does not do that — so what
    /// matters is noticing, not handling it.
    /// </summary>
    public bool Encrypted { get; private set; }

    public string? Unsupported { get; private set; }

    private PdfFileMap(byte[] data) => Data = data;

    public static PdfFileMap Read(string path)
    {
        var map = new PdfFileMap(File.ReadAllBytes(path));
        try
        {
            map.Walk();
        }
        catch (Exception ex)
        {
            map.Unsupported = $"{ex.GetType().Name}: {ex.Message}";
        }
        return map;
    }

    public long OffsetOf(int number) =>
        _offsets.TryGetValue(number, out long offset) ? offset : -1;

    /// <summary>The raw text of an object's body, wherever it lives.</summary>
    public string Body(int number)
    {
        var (data, start, end) = Locate(number);
        return PdfLexer.Text(data, start, end);
    }

    /// <summary>
    /// The bytes an object's body sits in, and where. For an object inside a
    /// compressed stream those bytes are the decompressed stream, not the file.
    /// </summary>
    public (byte[] Data, int Start, int End) Locate(int number)
    {
        if (_offsets.TryGetValue(number, out long at))
        {
            var (start, end) = PdfLexer.ObjectBody(Data, (int)at);
            return (Data, start, end);
        }

        if (_inStream.TryGetValue(number, out int container))
        {
            var index = ObjectStream(container);
            if (index.TryGetValue(number, out var range))
            {
                return (_streamData[container], range.Start, range.End);
            }
        }

        throw new InvalidDataException($"El objeto {number} no está en el xref.");
    }

    public (int Start, int End) BodyRange(int number)
    {
        var (_, start, end) = Locate(number);
        return (start, end);
    }

    /// <summary>The bytes an object's body sits in — the file, or a decompressed stream.</summary>
    public byte[] BodyData(int number) => Locate(number).Data;

    // --- walking the chain ---------------------------------------------------

    private void Walk()
    {
        int window = Math.Min(2048, Data.Length);
        string tail = Encoding.Latin1.GetString(Data, Data.Length - window, window);
        int mark = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        if (mark < 0) { Unsupported = "no hay startxref"; return; }

        var (number, _) = PdfLexer.Token(Data, Data.Length - window + mark + "startxref".Length);
        if (!long.TryParse(number, out long at)) { Unsupported = "startxref ilegible"; return; }

        LastXref = at;
        bool first = true;
        var seen = new HashSet<long>();

        // Newest section first, so the first offset seen for an object wins.
        while (at > 0 && at < Data.Length && seen.Add(at))
        {
            var (keyword, afterKeyword) = PdfLexer.Token(Data, (int)at);

            long? previous;
            if (keyword == "xref")
            {
                previous = ReadTableSection(afterKeyword, first);
            }
            else
            {
                if (first) UsesXrefStream = true;
                previous = ReadStreamSection((int)at, first);
            }

            if (previous is null) return;
            first = false;
            if (previous.Value <= 0) break;
            at = previous.Value;
        }
    }

    private long? ReadTableSection(int at, bool newest)
    {
        int cursor = ReadTable(at);
        var (keyword, afterKeyword) = PdfLexer.Token(Data, cursor);
        if (keyword != "trailer") { Unsupported = "falta el trailer"; return null; }

        int dictionaryAt = PdfLexer.SkipWhite(Data, afterKeyword);
        int dictionaryEnd = PdfLexer.SkipValue(Data, dictionaryAt);

        if (newest)
        {
            Trailer = PdfLexer.Text(Data, dictionaryAt, dictionaryEnd);
            Encrypted = PdfLexer.Value(Data, dictionaryAt, "/Encrypt") is not null;
            Root = Reference(Data, dictionaryAt, "/Root");
            Size = Number(Data, dictionaryAt, "/Size") ?? -1;
        }

        // A hybrid file hides the rest of its objects in an /XRefStm.
        if (Number(Data, dictionaryAt, "/XRefStm") is int hybrid and > 0)
        {
            ReadStreamSection(hybrid, newest: false);
        }

        return Number(Data, dictionaryAt, "/Prev") is int previous and > 0 ? previous : 0;
    }

    /// <summary>Reads the subsections of one table and returns where it ends.</summary>
    private int ReadTable(int at)
    {
        while (true)
        {
            int save = at;
            var (start, afterStart) = PdfLexer.Token(Data, at);
            if (!int.TryParse(start, out int firstNumber)) return save;

            var (count, afterCount) = PdfLexer.Token(Data, afterStart);
            if (!int.TryParse(count, out int howMany)) return save;

            at = afterCount;
            for (int i = 0; i < howMany; i++)
            {
                var (offset, afterOffset) = PdfLexer.Token(Data, at);
                var (_, afterGeneration) = PdfLexer.Token(Data, afterOffset);
                var (kind, afterKind) = PdfLexer.Token(Data, afterGeneration);

                if (kind == "n" && long.TryParse(offset, out long where))
                {
                    Remember(firstNumber + i, where);
                }
                at = afterKind;
            }
        }
    }

    private long? ReadStreamSection(int at, bool newest)
    {
        var (dictionaryAt, dictionaryEnd) = PdfLexer.ObjectBody(Data, at);
        byte[] entries = StreamBytes(Data, dictionaryAt, dictionaryEnd);

        var widths = Integers(Data, dictionaryAt, "/W");
        if (widths.Count < 3) { Unsupported = "el flujo xref no dice /W"; return null; }

        int size = Number(Data, dictionaryAt, "/Size") ?? 0;
        var index = Integers(Data, dictionaryAt, "/Index");
        if (index.Count == 0) index = [0, size];

        if (newest)
        {
            Trailer = PdfLexer.Text(Data, dictionaryAt, dictionaryEnd);
            Encrypted = PdfLexer.Value(Data, dictionaryAt, "/Encrypt") is not null;
            Root = Reference(Data, dictionaryAt, "/Root");
            Size = size;
        }

        int width = widths[0] + widths[1] + widths[2];
        int cursor = 0;

        for (int section = 0; section + 1 < index.Count; section += 2)
        {
            for (int i = 0; i < index[section + 1] && cursor + width <= entries.Length; i++, cursor += width)
            {
                long kind = widths[0] == 0 ? 1 : Field(entries, cursor, widths[0]);
                long second = Field(entries, cursor + widths[0], widths[1]);
                long third = Field(entries, cursor + widths[0] + widths[1], widths[2]);

                int number = index[section] + i;
                if (kind == 1) Remember(number, second);
                else if (kind == 2) RememberInStream(number, (int)second, (int)third);
            }
        }

        return Number(Data, dictionaryAt, "/Prev") is int previous and > 0 ? previous : 0;
    }

    private void Remember(int number, long offset)
    {
        if (!_inStream.ContainsKey(number)) _offsets.TryAdd(number, offset);
    }

    private void RememberInStream(int number, int container, int _)
    {
        if (!_offsets.ContainsKey(number)) _inStream.TryAdd(number, container);
    }

    private static long Field(byte[] data, int at, int width)
    {
        long value = 0;
        for (int i = 0; i < width; i++) value = (value << 8) | data[at + i];
        return value;
    }

    // --- streams -------------------------------------------------------------

    /// <summary>The decoded contents of the stream whose dictionary sits at the given range.</summary>
    private static byte[] StreamBytes(byte[] data, int dictionaryAt, int dictionaryEnd)
    {
        var (keyword, afterKeyword) = PdfLexer.Token(data, dictionaryEnd);
        if (keyword != "stream") throw new InvalidDataException("Se esperaba un flujo.");

        int start = afterKeyword;
        if (start < data.Length && data[start] == '\r') start++;
        if (start < data.Length && data[start] == '\n') start++;

        int length = Number(data, dictionaryAt, "/Length")
            ?? throw new InvalidDataException("El flujo no dice /Length.");

        var raw = new byte[length];
        Array.Copy(data, start, raw, 0, Math.Min(length, data.Length - start));

        var filter = PdfLexer.Value(data, dictionaryAt, "/Filter");
        string filters = filter is null ? "" : PdfLexer.Text(data, filter.Value.Start, filter.Value.End);

        byte[] decoded = filters.Contains("FlateDecode", StringComparison.Ordinal) ? Inflate(raw) : raw;

        var parameters = PdfLexer.Value(data, dictionaryAt, "/DecodeParms");
        if (parameters is not null)
        {
            int predictor = Number(data, parameters.Value.Start, "/Predictor") ?? 1;
            if (predictor >= 10)
            {
                int columns = Number(data, parameters.Value.Start, "/Columns") ?? 1;
                decoded = UndoPngPredictor(decoded, columns);
            }
        }
        return decoded;
    }

    private static byte[] Inflate(byte[] raw)
    {
        var output = new MemoryStream();
        try
        {
            using var input = new MemoryStream(raw);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            zlib.CopyTo(output);
        }
        catch (InvalidDataException)
        {
            // Some writers leave the zlib wrapper off. Try raw deflate.
            output.SetLength(0);
            using var input = new MemoryStream(raw);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            deflate.CopyTo(output);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Undoes the PNG row filters a cross-reference stream is normally packed
    /// with. Each row carries its filter type in a leading byte that is not
    /// part of the data.
    /// </summary>
    private static byte[] UndoPngPredictor(byte[] data, int columns)
    {
        int rows = data.Length / (columns + 1);
        var output = new byte[rows * columns];
        var previous = new byte[columns];

        for (int row = 0; row < rows; row++)
        {
            int from = row * (columns + 1);
            int kind = data[from];
            int to = row * columns;

            for (int i = 0; i < columns; i++)
            {
                int raw = data[from + 1 + i];
                int left = i >= 1 ? output[to + i - 1] : 0;
                int up = previous[i];

                output[to + i] = (byte)(kind switch
                {
                    0 => raw,
                    1 => raw + left,
                    2 => raw + up,
                    3 => raw + ((left + up) >> 1),
                    4 => raw + Paeth(left, up, i >= 1 ? PreviousLeft(previous, i) : 0),
                    _ => raw,
                });
            }
            Array.Copy(output, to, previous, 0, columns);
        }
        return output;
    }

    private static byte PreviousLeft(byte[] previous, int i) => previous[i - 1];

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Decodes an object stream once, and remembers where each object inside it starts.</summary>
    private Dictionary<int, (int Start, int End)> ObjectStream(int container)
    {
        if (_streamIndex.TryGetValue(container, out var known)) return known;

        long at = OffsetOf(container);
        if (at < 0) throw new InvalidDataException($"El flujo de objetos {container} no está en el xref.");

        var (dictionaryAt, dictionaryEnd) = PdfLexer.ObjectBody(Data, (int)at);
        byte[] decoded = StreamBytes(Data, dictionaryAt, dictionaryEnd);

        int count = Number(Data, dictionaryAt, "/N") ?? 0;
        int firstAt = Number(Data, dictionaryAt, "/First") ?? 0;

        var index = new Dictionary<int, (int, int)>();
        var starts = new List<(int Number, int At)>();

        int cursor = 0;
        for (int i = 0; i < count; i++)
        {
            var (number, afterNumber) = PdfLexer.Token(decoded, cursor);
            var (offset, afterOffset) = PdfLexer.Token(decoded, afterNumber);
            if (!int.TryParse(number, out int which) || !int.TryParse(offset, out int where)) break;

            starts.Add((which, firstAt + where));
            cursor = afterOffset;
        }

        foreach (var (which, where) in starts)
        {
            index[which] = (where, PdfLexer.SkipValue(decoded, where));
        }

        _streamData[container] = decoded;
        _streamIndex[container] = index;
        return index;
    }

    // --- small readers -------------------------------------------------------

    private static int Reference(byte[] data, int dictionaryAt, string key)
    {
        var value = PdfLexer.Value(data, dictionaryAt, key);
        if (value is null) return -1;

        var (token, _) = PdfLexer.Token(data, value.Value.Start);
        return int.TryParse(token, out int number) ? number : -1;
    }

    private static int? Number(byte[] data, int dictionaryAt, string key)
    {
        var value = PdfLexer.Value(data, dictionaryAt, key);
        if (value is null) return null;
        return int.TryParse(PdfLexer.Text(data, value.Value.Start, value.Value.End).Trim(), out int n) ? n : null;
    }

    private static List<int> Integers(byte[] data, int dictionaryAt, string key)
    {
        var value = PdfLexer.Value(data, dictionaryAt, key);
        var numbers = new List<int>();
        if (value is null) return numbers;

        int at = value.Value.Start;
        while (at < value.Value.End)
        {
            var (token, next) = PdfLexer.Token(data, at);
            if (token.Length == 0) break;
            if (int.TryParse(token, out int number)) numbers.Add(number);
            at = next;
        }
        return numbers;
    }
}
