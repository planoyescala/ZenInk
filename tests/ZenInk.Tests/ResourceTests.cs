//-----------------------------------------------------------------------------------------
// <copyright file="ResourceTests.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Text.RegularExpressions;
using System.Xml.Linq;
using static ZenInk.Tests.TestRunner;

namespace ZenInk.Tests;

/// <summary>
/// The two languages, checked against each other.
///
/// Nothing here touches the engine: it reads the .resw files as they sit in the
/// repository. It is here because the ways these files go wrong are silent
/// ones — a key added to one language and not the other shows the reader an
/// English word in a Spanish sentence, or the bare key; a placeholder that
/// changed number throws a FormatException in the middle of a dialog. Both are
/// found in a second by reading the files, and by nothing else short of opening
/// every dialog in both languages.
/// </summary>
public static class ResourceTests
{
    public static void Run()
    {
        Section("Resources — the two languages against each other");

        if (Root() is not { } root)
        {
            Check("the Strings folder is where it is expected", false, "not found above the test binary");
            return;
        }

        var spanish = Read(Path.Combine(root, "es-ES", "Resources.resw"));
        var english = Read(Path.Combine(root, "en-US", "Resources.resw"));

        Check("Spanish has strings", spanish.Count > 0, $"{spanish.Count}");
        Check("English has strings", english.Count > 0, $"{english.Count}");
        Check(
            "both languages carry the same number of strings",
            spanish.Count == english.Count,
            $"es {spanish.Count}, en {english.Count}");

        var onlySpanish = spanish.Keys.Where(key => !english.ContainsKey(key)).Order().ToList();
        var onlyEnglish = english.Keys.Where(key => !spanish.ContainsKey(key)).Order().ToList();

        Check(
            "no key is Spanish only",
            onlySpanish.Count == 0,
            onlySpanish.Count == 0 ? null : string.Join(", ", onlySpanish.Take(5)));
        Check(
            "no key is English only",
            onlyEnglish.Count == 0,
            onlyEnglish.Count == 0 ? null : string.Join(", ", onlyEnglish.Take(5)));

        var empty = spanish.Concat(english).Where(pair => pair.Value.Length == 0).Select(pair => pair.Key).ToList();
        Check(
            "nothing is left blank",
            empty.Count == 0,
            empty.Count == 0 ? null : string.Join(", ", empty.Take(5)));

        // {0}, {1}… are filled in by the caller with a fixed number of parts, so
        // the two languages have to ask for the same ones. They may ask in a
        // different order — that is half the reason the numbers are there.
        var mismatched = spanish
            .Where(pair => english.ContainsKey(pair.Key))
            .Where(pair => !Placeholders(pair.Value).SetEquals(Placeholders(english[pair.Key])))
            .Select(pair => pair.Key)
            .Order()
            .ToList();

        Check(
            "both languages ask for the same parts",
            mismatched.Count == 0,
            mismatched.Count == 0 ? null : string.Join(", ", mismatched.Take(5)));

        // A XAML entry is «Uid.Property»; the property half is what the framework
        // sets, and a typo there is a string that never appears on screen.
        var suspicious = spanish.Keys
            .Where(key => key.Contains('.') && !key.Contains("[using:"))
            .Where(key => !Known.Contains(key[(key.LastIndexOf('.') + 1)..]))
            .Order()
            .ToList();

        Check(
            "every XAML entry names a property the framework sets",
            suspicious.Count == 0,
            suspicious.Count == 0 ? null : string.Join(", ", suspicious.Take(5)));
    }

    private static readonly HashSet<string> Known =
        ["Text", "Content", "Header", "PlaceholderText", "Description", "Title", "ToolTip"];

    private static HashSet<string> Placeholders(string text) =>
        [.. Regex.Matches(text, @"\{(\d+)\}").Select(match => match.Groups[1].Value)];

    private static Dictionary<string, string> Read(string path)
    {
        if (!File.Exists(path)) return [];

        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(
                data => (string)data.Attribute("name")!,
                data => (string?)data.Element("value") ?? string.Empty,
                StringComparer.Ordinal);
    }

    /// <summary>
    /// The Strings folder, found by walking up from the test binary rather than
    /// by a path written out: the suite is run from the repository, and where
    /// its bin folder sits depends on the configuration it was built in.
    /// </summary>
    private static string? Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "ZenInk.App", "Strings");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }
}
