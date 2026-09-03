//-----------------------------------------------------------------------------------------
// <copyright file="PdfOutline.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Text;

using PDFiumCore;

namespace ZenInk.Core;

/// <summary>
/// One entry of a PDF's own index. <see cref="PageIndex"/> is −1 when the entry
/// points at something ZenInk cannot go to — a web address, another file, a
/// destination the document never defined — in which case it is a heading and
/// nothing more.
/// </summary>
public sealed record OutlineEntry(string Title, int PageIndex, IReadOnlyList<OutlineEntry> Children)
{
    public bool CanGo => PageIndex >= 0;
}

/// <summary>
/// Reads the bookmark tree a drawing set carries. A set of floor plans out of
/// Revit usually brings one, and until now ZenInk was the only reader on the
/// machine that did not show it.
///
/// Everything here runs on the queue's thread, like every other call into
/// PDFium.
/// </summary>
public static class PdfOutline
{
    /// <summary>The kind of action that goes somewhere inside this document.</summary>
    private const uint ActionGoTo = 1;

    /// <summary>
    /// How deep the tree is walked. A malformed file can point a child back at
    /// its own parent, and a reader that trusted the structure would follow
    /// that until the stack ran out.
    /// </summary>
    private const int MaxDepth = 16;

    /// <summary>How many entries are taken in total, for the same reason.</summary>
    private const int MaxEntries = 8000;

    public static IReadOnlyList<OutlineEntry> Read(FpdfDocumentT document)
    {
        int budget = MaxEntries;
        return ReadChildren(document, null, 0, ref budget);
    }

    private static IReadOnlyList<OutlineEntry> ReadChildren(
        FpdfDocumentT document,
        FpdfBookmarkT? parent,
        int depth,
        ref int budget)
    {
        if (depth >= MaxDepth || budget <= 0) return [];

        var entries = new List<OutlineEntry>();

        // Passing no bookmark asks for the top of the tree, which is why the
        // first call and every deeper one are the same call.
        var bookmark = fpdf_doc.FPDFBookmarkGetFirstChild(document, parent);
        while (bookmark is not null && budget > 0)
        {
            budget--;

            // Children first: the count is spent depth-first, so a file that
            // buries thousands of entries under one branch cannot starve its
            // siblings of the budget silently — it runs out visibly, in order.
            var children = ReadChildren(document, bookmark, depth + 1, ref budget);
            entries.Add(new OutlineEntry(Title(bookmark), PageOf(document, bookmark), children));

            bookmark = fpdf_doc.FPDFBookmarkGetNextSibling(document, bookmark);
        }

        return entries;
    }

    /// <summary>
    /// Where an entry goes. A bookmark either names a destination outright or
    /// wraps one in an action; both shapes are common, and a reader that only
    /// understood the first would show half a set's index as unclickable
    /// headings.
    /// </summary>
    private static int PageOf(FpdfDocumentT document, FpdfBookmarkT bookmark)
    {
        var destination = fpdf_doc.FPDFBookmarkGetDest(document, bookmark);

        if (destination is null && fpdf_doc.FPDFBookmarkGetAction(bookmark) is { } action)
        {
            if (fpdf_doc.FPDFActionGetType(action) != ActionGoTo) return -1;
            destination = fpdf_doc.FPDFActionGetDest(document, action);
        }

        if (destination is null) return -1;

        int page = fpdf_doc.FPDFDestGetDestPageIndex(document, destination);
        return page < 0 ? -1 : page;
    }

    private static string Title(FpdfBookmarkT bookmark)
    {
        // The length comes back in bytes and includes the terminator; the text
        // itself is UTF-16.
        ulong size = fpdf_doc.FPDFBookmarkGetTitle(bookmark, IntPtr.Zero, 0);
        if (size <= 2) return string.Empty;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            fpdf_doc.FPDFBookmarkGetTitle(bookmark, buffer, size);

            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, (int)size);

            string title = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            return title.Trim();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
