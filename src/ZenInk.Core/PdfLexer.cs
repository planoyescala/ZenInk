using System.Text;

namespace ZenInk.Core;

/// <summary>
/// Just enough PDF syntax to find one key inside one dictionary and to know
/// where a value ends.
///
/// Deliberately not a parser: nothing here builds an object model. Adding a
/// signature means re-emitting three dictionaries that already exist — the
/// catalog, the form and one page — with one entry added to each, and for that
/// the raw bytes plus the extent of a value is all that is needed. Anything
/// more would be a second PDF library living next to PDFium.
/// </summary>
public static class PdfLexer
{
    public static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(byte b) =>
        b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'['
          or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public static int SkipWhite(byte[] data, int at)
    {
        while (at < data.Length)
        {
            if (IsWhite(data[at])) { at++; continue; }
            if (data[at] == '%')
            {
                while (at < data.Length && data[at] is not ((byte)'\n' or (byte)'\r')) at++;
                continue;
            }
            break;
        }
        return at;
    }

    /// <summary>Reads the next token, without interpreting it.</summary>
    public static (string Token, int End) Token(byte[] data, int at)
    {
        at = SkipWhite(data, at);
        if (at >= data.Length) return ("", at);

        byte b = data[at];
        if (b == '<' && at + 1 < data.Length && data[at + 1] == '<') return ("<<", at + 2);
        if (b == '>' && at + 1 < data.Length && data[at + 1] == '>') return (">>", at + 2);
        if (b is (byte)'[' or (byte)']' or (byte)'(' or (byte)'<' or (byte)'{' or (byte)'}')
            return (((char)b).ToString(), at + 1);

        if (b == '/')
        {
            int end = at + 1;
            while (end < data.Length && !IsWhite(data[end]) && !IsDelimiter(data[end])) end++;
            return (Encoding.Latin1.GetString(data, at, end - at), end);
        }

        int stop = at;
        while (stop < data.Length && !IsWhite(data[stop]) && !IsDelimiter(data[stop])) stop++;
        if (stop == at) stop = at + 1;
        return (Encoding.Latin1.GetString(data, at, stop - at), stop);
    }

    /// <summary>Walks past one complete value and returns where it ends.</summary>
    public static int SkipValue(byte[] data, int at)
    {
        at = SkipWhite(data, at);
        if (at >= data.Length) return at;

        byte b = data[at];
        if (b == '(') return SkipLiteralString(data, at);
        if (b == '<' && at + 1 < data.Length && data[at + 1] == '<') return SkipBracketed(data, at, "<<", ">>");
        if (b == '<') return SkipHexString(data, at);
        if (b == '[') return SkipBracketed(data, at, "[", "]");

        // A number may be the start of an indirect reference: "12 0 R".
        var (token, end) = Token(data, at);
        if (token.Length > 0 && (char.IsDigit(token[0]) || token[0] is '+' or '-'))
        {
            var (second, secondEnd) = Token(data, end);
            if (second.Length > 0 && char.IsDigit(second[0]))
            {
                var (third, thirdEnd) = Token(data, secondEnd);
                if (third == "R") return thirdEnd;
            }
        }
        return end;
    }

    private static int SkipLiteralString(byte[] data, int at)
    {
        int depth = 0;
        for (int i = at; i < data.Length; i++)
        {
            if (data[i] == '\\') { i++; continue; }
            if (data[i] == '(') depth++;
            else if (data[i] == ')' && --depth == 0) return i + 1;
        }
        return data.Length;
    }

    private static int SkipHexString(byte[] data, int at)
    {
        for (int i = at + 1; i < data.Length; i++)
        {
            if (data[i] == '>') return i + 1;
        }
        return data.Length;
    }

    private static int SkipBracketed(byte[] data, int at, string open, string close)
    {
        int depth = 0;
        int i = at;
        while (i < data.Length)
        {
            int before = i;
            var (token, end) = Token(data, i);
            if (token.Length == 0) return data.Length;

            if (token == open) depth++;
            else if (token == close) { if (--depth == 0) return end; }
            else if (token == "(") { end = SkipLiteralString(data, SkipWhite(data, before)); }
            else if (token == "<") { end = SkipHexString(data, SkipWhite(data, before)); }

            i = end;
        }
        return data.Length;
    }

    /// <summary>
    /// Finds a key in the dictionary that starts at <paramref name="dictAt"/>,
    /// and returns where its value begins and ends. Only the dictionary's own
    /// keys are considered, not those of dictionaries nested inside it.
    /// </summary>
    public static (int Start, int End)? Value(byte[] data, int dictAt, string key)
    {
        int at = SkipWhite(data, dictAt);
        var (open, afterOpen) = Token(data, at);
        if (open != "<<") return null;

        at = afterOpen;
        while (at < data.Length)
        {
            var (token, afterKey) = Token(data, at);
            if (token is ">>" or "") return null;
            if (!token.StartsWith('/')) { at = SkipValue(data, at); continue; }

            int valueEnd = SkipValue(data, afterKey);
            if (token == key) return (SkipWhite(data, afterKey), valueEnd);
            at = valueEnd;
        }
        return null;
    }

    /// <summary>The body of the indirect object that starts at <paramref name="at"/>, as bytes.</summary>
    public static (int Start, int End) ObjectBody(byte[] data, int at)
    {
        var (_, afterNumber) = Token(data, at);
        var (_, afterGeneration) = Token(data, afterNumber);
        var (keyword, afterKeyword) = Token(data, afterGeneration);
        if (keyword != "obj") throw new InvalidDataException($"No hay un objeto en el desplazamiento {at}.");

        int start = SkipWhite(data, afterKeyword);
        return (start, SkipValue(data, start));
    }

    public static string Text(byte[] data, int start, int end) =>
        Encoding.Latin1.GetString(data, start, end - start);
}
