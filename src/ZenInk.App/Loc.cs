//-----------------------------------------------------------------------------------------
// <copyright file="Loc.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using Microsoft.Windows.ApplicationModel.Resources;

namespace ZenInk_App;

/// <summary>
/// Every string the reader sees that XAML cannot carry: dialogs, messages built
/// out of numbers, the words on a mark.
///
/// The name is short on purpose — it is written three hundred times.
///
/// Same resources as x:Uid, same files, same language: <c>Strings/en-US</c> and
/// <c>Strings/es-ES</c> are compiled into the resources.pri that sits beside the
/// executable, and this is the other door into it. Keys used from here carry no
/// dot: a dot is what separates the element from its property in a XAML entry,
/// and MRT reads it as a folder.
/// </summary>
internal static class Loc
{
    /// <summary>
    /// Built once and kept. It is the Windows App SDK loader, not the WinRT one:
    /// this one finds resources.pri next to an unpackaged executable, which is
    /// every copy the installer makes.
    /// </summary>
    private static readonly ResourceLoader Loader = new();

    /// <summary>
    /// The text for a key, or the key itself when there is none.
    ///
    /// A missing string comes back empty from MRT, and empty is invisible: a
    /// button with no words on it looks like a layout bug rather than a missing
    /// translation. The key is ugly, which is the point.
    /// </summary>
    public static string Get(string key)
    {
        try
        {
            string text = Loader.GetString(key);
            return text.Length > 0 ? text : key;
        }
        catch
        {
            return key;
        }
    }

    /// <summary>
    /// The text for a key, or null when there is none — which is how the
    /// engine's own messages fall back to the English written beside them.
    /// </summary>
    public static string? Find(string key)
    {
        try
        {
            string text = Loader.GetString(key);
            return text.Length > 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A message with numbers or names in it. The placeholders are {0}, {1} …
    /// and they travel with the translation, so a language that needs a
    /// different order can have one.
    /// </summary>
    public static string Format(string key, params object?[] parts)
    {
        try
        {
            return string.Format(Get(key), parts);
        }
        catch (FormatException)
        {
            // A translation with a stray brace in it. Better the raw text than
            // an exception in the middle of a dialog.
            return Get(key);
        }
    }
}
