using System.Text;

namespace ZenInk_App.Rendering;

/// <summary>
/// One glyph's box in page-local space: PDF points, y down, origin at the
/// page's top-left corner after its /Rotate has been applied — the same space
/// the tile grid uses, so no further conversion is needed to draw it.
/// </summary>
public readonly record struct TextChar(char Value, float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;

    public float Height => Bottom - Top;

    /// <summary>Line breaks and other generated characters carry no box.</summary>
    public bool HasArea => Width > 0.01f && Height > 0.01f;
}

/// <summary>A merged run of selected glyphs on one line, in page-local points.</summary>
public readonly record struct TextRun(float Left, float Top, float Right, float Bottom);

/// <summary>
/// The extracted glyph boxes of a single page, in PDFium's reading order.
/// Selection is expressed as an inclusive index range into this list, which is
/// why the order matters: dragging from one glyph to another selects
/// everything between them as the document reads, not as the pointer traveled.
/// </summary>
public sealed class PageTextLayer
{
    private readonly TextChar[] _chars;

    public PageTextLayer(int pageIndex, TextChar[] chars)
    {
        PageIndex = pageIndex;
        _chars = chars;
    }

    public int PageIndex { get; }

    public int Count => _chars.Length;

    public IReadOnlyList<TextChar> Chars => _chars;

    /// <summary>
    /// Index of the glyph at the given page-local point, or the nearest one
    /// within <paramref name="maxDistance"/>. Vertical distance is penalized so
    /// that a point in the gap between lines snaps to the line it is level
    /// with rather than to a horizontally closer glyph on the line above.
    /// </summary>
    public int HitTest(float x, float y, float maxDistance)
    {
        int nearest = -1;
        float nearestScore = float.MaxValue;

        for (int i = 0; i < _chars.Length; i++)
        {
            var ch = _chars[i];
            if (!ch.HasArea) continue;

            if (x >= ch.Left && x <= ch.Right && y >= ch.Top && y <= ch.Bottom)
            {
                return i;
            }

            float dx = x < ch.Left ? ch.Left - x : (x > ch.Right ? x - ch.Right : 0f);
            float dy = y < ch.Top ? ch.Top - y : (y > ch.Bottom ? y - ch.Bottom : 0f);
            float score = dx + dy * 3f;

            if (score < nearestScore)
            {
                nearestScore = score;
                nearest = i;
            }
        }

        return nearestScore <= maxDistance ? nearest : -1;
    }

    public string GetText(int startIndex, int endIndex)
    {
        if (_chars.Length == 0) return string.Empty;

        int start = Math.Clamp(Math.Min(startIndex, endIndex), 0, _chars.Length - 1);
        int end = Math.Clamp(Math.Max(startIndex, endIndex), 0, _chars.Length - 1);

        var sb = new StringBuilder(end - start + 1);
        for (int i = start; i <= end; i++)
        {
            char value = _chars[i].Value;
            if (value != '\0')
            {
                sb.Append(value);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Collapses a selected range into one rectangle per line. Highlighting
    /// each glyph separately leaves visible seams between them, so adjacent
    /// glyphs sharing a baseline are merged into a single run.
    /// </summary>
    public IReadOnlyList<TextRun> BuildRuns(int startIndex, int endIndex)
    {
        var runs = new List<TextRun>();
        if (_chars.Length == 0) return runs;

        int start = Math.Clamp(Math.Min(startIndex, endIndex), 0, _chars.Length - 1);
        int end = Math.Clamp(Math.Max(startIndex, endIndex), 0, _chars.Length - 1);

        bool open = false;
        float left = 0, top = 0, right = 0, bottom = 0;

        for (int i = start; i <= end; i++)
        {
            var ch = _chars[i];
            if (!ch.HasArea) continue;

            if (!open)
            {
                (left, top, right, bottom) = (ch.Left, ch.Top, ch.Right, ch.Bottom);
                open = true;
                continue;
            }

            // Same line if the vertical extents mostly overlap, and the glyph
            // continues rightward rather than wrapping back to a new line.
            float overlap = Math.Min(bottom, ch.Bottom) - Math.Max(top, ch.Top);
            float minHeight = Math.Min(bottom - top, ch.Height);
            bool sameLine = minHeight > 0 && overlap > minHeight * 0.5f;
            bool continues = ch.Left >= left - 0.5f;

            if (sameLine && continues)
            {
                right = Math.Max(right, ch.Right);
                top = Math.Min(top, ch.Top);
                bottom = Math.Max(bottom, ch.Bottom);
            }
            else
            {
                runs.Add(new TextRun(left, top, right, bottom));
                (left, top, right, bottom) = (ch.Left, ch.Top, ch.Right, ch.Bottom);
            }
        }

        if (open)
        {
            runs.Add(new TextRun(left, top, right, bottom));
        }

        return runs;
    }
}
