//-----------------------------------------------------------------------------------------
// <copyright file="RecentDocuments.cs" company="plano y escala">
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
/// The drawings opened lately, most recent first.
///
/// Only the path is kept. Nothing is read from the drawings themselves — not a
/// page count, not a thumbnail, not even whether they are still there. A
/// reviewer's sheets live on a server, and asking a dozen network paths whether
/// they exist is how a program that opens in a blink starts taking seconds to
/// show its first window. What the list cannot know it finds out when one of
/// them is clicked, which is the only moment it matters.
///
/// The store is a plain text file, one path per line, because a list of twelve
/// strings does not need a format with a parser.
/// </summary>
public static class RecentDocuments
{
    /// <summary>How many are kept. Beyond a screenful the list stops being a shortcut.</summary>
    public const int Limit = 12;

    /// <summary>
    /// Puts a path at the head of the list, moving it there if it was already
    /// in it. Windows paths are compared without case, so the same drawing
    /// reached through a differently typed path is still the same entry.
    /// </summary>
    public static IReadOnlyList<string> Promote(IEnumerable<string> current, string path, int limit = Limit)
    {
        if (string.IsNullOrWhiteSpace(path)) return [.. current];

        var kept = new List<string>(limit) { path };

        foreach (string other in current)
        {
            if (kept.Count >= limit) break;
            if (string.IsNullOrWhiteSpace(other)) continue;
            if (string.Equals(other, path, StringComparison.OrdinalIgnoreCase)) continue;
            kept.Add(other);
        }

        return kept;
    }

    /// <summary>Drops a path — what happens when one of them turns out not to be there any more.</summary>
    public static IReadOnlyList<string> Remove(IEnumerable<string> current, string path) =>
        [.. current.Where(other =>
            !string.IsNullOrWhiteSpace(other)
            && !string.Equals(other, path, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Reads the store. A store that cannot be read is an empty history and
    /// nothing more: the drawings are on disk either way, and refusing to open
    /// the window over a list of shortcuts would be absurd.
    /// </summary>
    public static IReadOnlyList<string> Load(string storePath, int limit = Limit)
    {
        try
        {
            if (!File.Exists(storePath)) return [];

            var kept = new List<string>(limit);
            foreach (string line in File.ReadLines(storePath))
            {
                string path = line.Trim();
                if (path.Length == 0) continue;
                if (kept.Any(other => string.Equals(other, path, StringComparison.OrdinalIgnoreCase))) continue;

                kept.Add(path);
                if (kept.Count >= limit) break;
            }

            return kept;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Writes the store, and says nothing if it cannot: this is a convenience, not the work.</summary>
    public static void Save(string storePath, IEnumerable<string> paths)
    {
        try
        {
            string? folder = Path.GetDirectoryName(storePath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllLines(storePath, paths.Where(path => !string.IsNullOrWhiteSpace(path)));
        }
        catch (Exception)
        {
            // A history that failed to save is a history that is one entry out
            // of date. Nothing the reader is doing depends on it.
        }
    }
}
