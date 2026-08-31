using System.Globalization;

namespace ZenInk.Core;

/// <summary>
/// Reads "1, 3, 5-7" the way every print dialog in the world does, and gives
/// back sheet numbers counting from zero.
///
/// Written to be forgiving, because this is typed under a picker and not into a
/// config file: spaces anywhere, any of the dashes a keyboard offers, a range
/// given backwards, numbers past the end of the document. What it will not do
/// is guess at nonsense — a word in the middle makes the whole thing invalid,
/// and the caller says so rather than acting on half of it.
/// </summary>
public static class PageRange
{
    /// <summary>
    /// The sheets named, in order and without repeats, or null if the text does
    /// not read as a range at all. Empty text means every sheet: an empty box
    /// under "which sheets" is the reader saying they want all of them.
    /// </summary>
    public static IReadOnlyList<int>? Parse(string? text, int pageCount)
    {
        if (pageCount <= 0) return [];

        if (string.IsNullOrWhiteSpace(text))
        {
            return [.. Enumerable.Range(0, pageCount)];
        }

        var sheets = new SortedSet<int>();

        // An en dash and a minus sign are what a keyboard and a copy-paste give;
        // treating them as the hyphen they look like costs nothing.
        foreach (string part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            string piece = part.Replace('–', '-').Replace('−', '-').Trim();
            if (piece.Length == 0) continue;

            int dash = piece.IndexOf('-', 1);
            if (dash < 0)
            {
                if (!TryPage(piece, pageCount, out int single)) return null;
                sheets.Add(single);
                continue;
            }

            if (!TryPage(piece[..dash], pageCount, out int from)) return null;
            if (!TryPage(piece[(dash + 1)..], pageCount, out int to)) return null;

            // Given backwards is still a range: it is what someone means.
            if (from > to) (from, to) = (to, from);
            for (int i = from; i <= to; i++) sheets.Add(i);
        }

        return [.. sheets];
    }

    /// <summary>Reads one number, counting from one, and clamps it into the document.</summary>
    private static bool TryPage(string text, int pageCount, out int index)
    {
        index = -1;
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int number)) return false;
        if (number < 1) return false;

        index = Math.Min(number, pageCount) - 1;
        return true;
    }
}
