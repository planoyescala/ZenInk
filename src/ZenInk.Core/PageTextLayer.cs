//-----------------------------------------------------------------------------------------
// <copyright file="PageTextLayer.cs" company="plano y escala">
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
/// A search hit, as an inclusive glyph range — the same shape a selection
/// uses, so highlighting a hit goes through <see cref="PageTextLayer.BuildRuns"/>
/// like any other range.
/// </summary>
public readonly record struct TextMatch(int StartIndex, int EndIndex);

/// <summary>
/// How <see cref="PageTextLayer.Find"/> compares. Case and accents travel
/// together on purpose: "coincidir mayúsculas" is how a reader asks for an
/// exact match, and a Spanish drawing that writes SECCIÓN one place and SECCION
/// another should still answer a loose search for either.
/// </summary>
public readonly record struct TextSearchOptions(bool MatchCase = false, bool WholeWord = false);

/// <summary>
/// The extracted glyph boxes of a single page, in PDFium's reading order.
/// Selection is expressed as an inclusive index range into this list, which is
/// why the order matters: dragging from one glyph to another selects
/// everything between them as the document reads, not as the pointer traveled.
/// </summary>
public sealed class PageTextLayer
{
    private readonly TextChar[] _chars;

    /// <summary>
    /// The page's text folded for searching, with a map back to glyph indices.
    /// Built on first use and kept, because a search re-runs on every keystroke
    /// and folding a dense sheet's text again each time is the one part of a
    /// find that would be felt.
    /// </summary>
    private (string Text, int[] SourceIndex)? _loose;

    private (string Text, int[] SourceIndex)? _exact;

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
    /// Every occurrence of <paramref name="query"/> on this page, in reading
    /// order, as inclusive glyph ranges.
    ///
    /// Matching runs against a folded copy of the page rather than the raw
    /// glyph sequence, because PDF text rarely reads the way it looks: a line
    /// that wraps, or a run broken for kerning, arrives here as glyphs
    /// separated by newlines and stray spaces. Every stretch of whitespace
    /// collapses to a single space on both sides of the comparison, so a
    /// caption split across two lines still answers a search typed as one.
    /// </summary>
    public IReadOnlyList<TextMatch> Find(string query, TextSearchOptions options)
    {
        var matches = new List<TextMatch>();
        if (string.IsNullOrEmpty(query) || _chars.Length == 0) return matches;

        string needle = Fold(query, options.MatchCase);
        if (needle.Length == 0) return matches;

        var (haystack, sourceIndex) = FoldedText(options.MatchCase);
        if (haystack.Length == 0) return matches;

        int from = 0;
        while (from <= haystack.Length - needle.Length)
        {
            int at = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0) break;

            int end = at + needle.Length - 1;
            if (!options.WholeWord || IsWholeWord(haystack, at, end))
            {
                matches.Add(new TextMatch(sourceIndex[at], sourceIndex[end]));
            }

            from = at + Math.Max(1, needle.Length);
        }

        return matches;
    }

    /// <summary>
    /// A hit counts as a whole word when neither side touches another letter or
    /// digit. Punctuation and whitespace are boundaries, which is what makes
    /// searching a sheet number like "E-04" behave.
    /// </summary>
    private static bool IsWholeWord(string text, int start, int end)
    {
        if (start > 0 && char.IsLetterOrDigit(text[start - 1])) return false;
        if (end + 1 < text.Length && char.IsLetterOrDigit(text[end + 1])) return false;
        return true;
    }

    /// <summary>
    /// The page folded for comparison, alongside the glyph index each folded
    /// character came from. A collapsed run of whitespace maps to the first
    /// glyph of the run, so a hit that spans a line break still highlights from
    /// the right place.
    /// </summary>
    private (string Text, int[] SourceIndex) FoldedText(bool matchCase)
    {
        // Benign race: two callers may each build it, and both build the same
        // thing from immutable input.
        var cached = matchCase ? _exact : _loose;
        if (cached is { } ready) return ready;

        var text = new StringBuilder(_chars.Length);
        var map = new List<int>(_chars.Length);
        bool pendingSpace = false;

        for (int i = 0; i < _chars.Length; i++)
        {
            char value = _chars[i].Value;
            if (value == '\0') continue;

            if (char.IsWhiteSpace(value))
            {
                // Collapse the run, but remember where it started.
                if (!pendingSpace && text.Length > 0)
                {
                    pendingSpace = true;
                    text.Append(' ');
                    map.Add(i);
                }
                continue;
            }

            pendingSpace = false;
            char folded = FoldChar(value, matchCase);
            if (folded == '\0') continue;

            text.Append(folded);
            map.Add(i);
        }

        var built = (text.ToString(), map.ToArray());
        if (matchCase)
        {
            _exact = built;
        }
        else
        {
            _loose = built;
        }
        return built;
    }

    private static string Fold(string value, bool matchCase)
    {
        var text = new StringBuilder(value.Length);
        bool pendingSpace = false;

        foreach (char raw in value)
        {
            if (char.IsWhiteSpace(raw))
            {
                if (pendingSpace) continue;
                pendingSpace = true;
                text.Append(' ');
                continue;
            }

            pendingSpace = false;
            char folded = FoldChar(raw, matchCase);
            if (folded != '\0')
            {
                text.Append(folded);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Strips case and accents unless an exact match was asked for. Decomposing
    /// and dropping the combining marks is what turns SECCIÓN and seccion into
    /// the same needle; a character that is nothing but a mark folds away.
    /// </summary>
    private static char FoldChar(char value, bool matchCase)
    {
        if (matchCase) return value;

        foreach (char part in value.ToString().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(part) == UnicodeCategory.NonSpacingMark) continue;
            return char.ToLowerInvariant(part);
        }

        return '\0';
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
