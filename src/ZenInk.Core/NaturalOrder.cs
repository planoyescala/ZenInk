//-----------------------------------------------------------------------------------------
// <copyright file="NaturalOrder.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

namespace ZenInk.Core;

/// <summary>
/// Orders names the way a person reads them: <c>HOJA-2</c> before
/// <c>HOJA-10</c>, not after it.
///
/// Plain text order puts <c>10</c> before <c>2</c> because it compares the
/// characters one at a time, and a set of drawings named by code comes out
/// shuffled. This walks the name in runs instead — digits with digits, the rest
/// with the rest — and compares a run of digits by what it counts to.
///
/// It is written here rather than borrowed from <c>StrCmpLogicalW</c>, which is
/// what the Explorer uses, so that what it does is fixed by the checks in this
/// repository and not by a system call whose rules are undocumented.
/// </summary>
public static class NaturalOrder
{
    public static IComparer<string> Comparer { get; } = new NameComparer();

    /// <summary>Orders paths by their file name, which is what the reader saw when picking them.</summary>
    public static IComparer<string> ByFileName { get; } = new PathComparer();

    public static int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;

        int a = 0, b = 0;

        while (a < left.Length && b < right.Length)
        {
            bool digitsA = char.IsDigit(left[a]);
            bool digitsB = char.IsDigit(right[b]);

            // A run of digits and a run of letters are never the same thing, so
            // one of them has to come first: numbers do, which is where a
            // reader looks for them.
            if (digitsA != digitsB) return digitsA ? -1 : 1;

            int endA = RunEnd(left, a, digitsA);
            int endB = RunEnd(right, b, digitsB);

            int order = digitsA
                ? CompareNumbers(left.AsSpan(a, endA - a), right.AsSpan(b, endB - b))
                : string.Compare(left[a..endA], right[b..endB], StringComparison.CurrentCultureIgnoreCase);

            if (order != 0) return order;

            a = endA;
            b = endB;
        }

        if (a < left.Length) return 1;
        if (b < right.Length) return -1;

        // Everything read the same — different spellings of the same reading,
        // such as a number written with a leading zero. Ordinary text order
        // settles it, so the result is one order and not a coin toss.
        return string.CompareOrdinal(left, right);
    }

    private static int RunEnd(string text, int from, bool digits)
    {
        int at = from;
        while (at < text.Length && char.IsDigit(text[at]) == digits) at++;
        return at;
    }

    /// <summary>
    /// Compares two runs of digits by what they count to, without turning them
    /// into a number: a drawing number can be longer than any integer type, and
    /// a run that overflowed would order at random.
    /// </summary>
    private static int CompareNumbers(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        left = left.TrimStart('0');
        right = right.TrimStart('0');

        if (left.Length != right.Length) return left.Length < right.Length ? -1 : 1;
        return left.SequenceCompareTo(right);
    }

    private sealed class NameComparer : IComparer<string>
    {
        public int Compare(string? x, string? y) => NaturalOrder.Compare(x, y);
    }

    private sealed class PathComparer : IComparer<string>
    {
        public int Compare(string? x, string? y) => NaturalOrder.Compare(NameOf(x), NameOf(y));

        private static string? NameOf(string? path) =>
            path is null ? null : Path.GetFileName(path);
    }
}
