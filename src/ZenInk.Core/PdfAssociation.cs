//-----------------------------------------------------------------------------------------
// <copyright file="PdfAssociation.cs" company="plano y escala">
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
/// Whether Windows opens PDFs with us, and whether it is worth asking about it.
///
/// Windows 10 and 11 do not let a program make itself the default handler: the
/// association is signed per user and the shell refuses anything written by
/// hand. All an application can do is declare that it opens the type, find out
/// who currently does, and walk the reader to the settings page. So the whole
/// question here is reading an answer, never setting one.
///
/// Reading it is the awkward part. The shell will describe the current handler
/// in several ways and none of them is available in every case, so both of the
/// ones that identify a program are asked for and either is enough. The
/// comparison is here, away from the interface, because it is the piece that
/// can be wrong in a way nobody notices — a mismatch does not fail, it just
/// asks a reader who already said yes.
/// </summary>
public static class PdfAssociation
{
    /// <summary>
    /// Whether the handler the shell describes is this application.
    ///
    /// The model id is the trustworthy half: a packaged application is known to
    /// the shell by it, and it is what the association actually stores. The
    /// executable is the fallback for the cases where the shell answers about
    /// the binary instead — running unpackaged, mainly, where there is no model
    /// id to compare.
    /// </summary>
    public static bool IsOurs(string? handlerAppId, string? handlerExecutable, string? ourAppId, string? ourExecutable)
    {
        if (Same(handlerAppId, ourAppId)) return true;

        return SamePath(handlerExecutable, ourExecutable);
    }

    /// <summary>
    /// Whether to offer to make ZenInk the default.
    ///
    /// Not without package identity: the association is declared by the package
    /// manifest, so a copy running unpackaged is not registered as a handler
    /// and the settings page would have nothing to offer. Sending a reader
    /// there to look for an entry that is not in the list is worse than saying
    /// nothing.
    /// </summary>
    public static bool ShouldOffer(bool packaged, bool isDefault, bool declined) =>
        packaged && !isDefault && !declined;

    /// <summary>
    /// The drawings named by a launch, taken from its arguments.
    ///
    /// This is how a packaged full-trust application is told what to open: the
    /// shell starts it with the paths on the command line, one argument each,
    /// already unquoted by the runtime. The first argument is the program
    /// itself and is skipped.
    ///
    /// Only PDFs, and no check that they are there: a drawing that has moved
    /// should say so when it fails to open, not vanish from the request in
    /// silence. Repeats are dropped, because opening the same sheet twice
    /// would be two tabs of the same thing.
    /// </summary>
    public static IReadOnlyList<string> DrawingsIn(IEnumerable<string> arguments)
    {
        var drawings = new List<string>();

        foreach (string argument in arguments.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(argument)) continue;

            string path = argument.Trim().Trim('"');
            if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
            if (drawings.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;

            drawings.Add(path);
        }

        return drawings;
    }

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Two paths for the same file. Compared after normalising, because the
    /// shell hands back what the registry holds — quoted, with a trailing
    /// argument, or reached through a different spelling of the same folder.
    /// </summary>
    private static bool SamePath(string? a, string? b)
    {
        string? left = Normalise(a), right = Normalise(b);

        return left is not null && right is not null &&
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0) return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception)
        {
            // The registry can hold something that is not a path at all. It is
            // then not our path either, which is all this needs to decide.
            return null;
        }
    }
}
