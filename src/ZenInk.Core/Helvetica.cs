namespace ZenInk.Core;

/// <summary>
/// How wide Helvetica draws a string, in thousandths of the type size.
///
/// The numbers are the font's own, not an average. An average was what the
/// signature stamp used first, and it was wrong in the direction that shows: a
/// name in bold capitals is far wider per letter than the mean, so the type was
/// chosen too large and the name ran out of its box.
///
/// Only the two faces a stamp uses are here, because those are the two a PDF
/// reader is guaranteed to have without embedding anything.
/// </summary>
public static class Helvetica
{
    /// <summary>Widths for the printable ASCII range, 32 to 126.</summary>
    private static readonly short[] Regular =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly short[] Bold =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];

    /// <summary>
    /// A letter that is not plain ASCII — an eñe, an accented vowel — is drawn
    /// at the width of a letter, not of a space. Six hundred is a shade wider
    /// than most of them, which errs towards type that is a little small rather
    /// than a name that runs off its box.
    /// </summary>
    private const short Unknown = 600;

    /// <summary>The width of <paramref name="text"/> at size 1, in ems.</summary>
    public static float Width(string text, bool bold)
    {
        var widths = bold ? Bold : Regular;

        int total = 0;
        foreach (char c in text)
        {
            total += c is >= ' ' and <= '~' ? widths[c - ' '] : Unknown;
        }
        return total / 1000f;
    }

    /// <summary>The largest size at which <paramref name="text"/> still fits in <paramref name="room"/>.</summary>
    public static float SizeThatFits(string text, bool bold, float room)
    {
        float width = Width(text, bold);
        return width <= 0f ? float.MaxValue : room / width;
    }
}
