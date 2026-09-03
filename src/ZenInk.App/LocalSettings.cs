//-----------------------------------------------------------------------------------------
// <copyright file="LocalSettings.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using Windows.Storage;

namespace ZenInk_App;

/// <summary>
/// The handful of things the app remembers between launches: the theme, whether
/// the reader turned down the offer to open PDFs, and which authority stamps
/// the time on a signature.
///
/// <c>ApplicationData.Current</c> is the packaged store, and it throws without
/// package identity. Every caller used to swallow that on its own, which meant
/// an installation made by the Inno Setup installer — the same files, no
/// package — forgot the theme on every launch and asked for the timestamp
/// authority every time. So the fallback lives here instead, and it is the one
/// the history already uses: a file of its own under LocalApplicationData.
///
/// Losing a preference is a nuisance; failing to start, or refusing to sign, is
/// not. Nothing here throws.
/// </summary>
internal static class LocalSettings
{
    private static Dictionary<string, string>? _file;

    public static string? Get(string key)
    {
        if (DefaultPdfApp.IsPackaged())
        {
            try
            {
                object? stored = ApplicationData.Current.LocalSettings.Values[key];

                // Older versions wrote the refusal as a bool rather than as text.
                return stored switch
                {
                    string text => text,
                    bool flag => flag ? bool.TrueString : bool.FalseString,
                    null => null,
                    _ => stored.ToString(),
                };
            }
            catch
            {
                return null;
            }
        }

        return File().TryGetValue(key, out string? value) ? value : null;
    }

    public static void Set(string key, string value)
    {
        if (DefaultPdfApp.IsPackaged())
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
            catch
            {
                // Nothing to do about it, and nothing worth failing over.
            }

            return;
        }

        var settings = File();
        settings[key] = value;

        try
        {
            string path = Path();

            // First run: nobody has made the folder yet.
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            System.IO.File.WriteAllLines(
                path,
                settings.Select(entry => $"{entry.Key}={entry.Value}"));
        }
        catch
        {
            // A read-only folder or a locked file: the preference is lost, the
            // program carries on.
        }
    }

    /// <summary>
    /// The file, read once. Written whole on every change — three keys are not
    /// worth a format that can be edited in place.
    /// </summary>
    private static Dictionary<string, string> File()
    {
        if (_file is not null) return _file;

        _file = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            foreach (string line in System.IO.File.ReadAllLines(Path()))
            {
                int split = line.IndexOf('=');
                if (split > 0)
                {
                    _file[line[..split]] = line[(split + 1)..];
                }
            }
        }
        catch
        {
            // No file yet on the first run, which is not a problem: every
            // caller has a sensible answer for a setting that is not there.
        }

        return _file;
    }

    private static string Path() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZenInk",
        "ajustes.txt");
}
