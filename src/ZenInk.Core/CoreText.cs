//-----------------------------------------------------------------------------------------
// <copyright file="CoreText.cs" company="plano y escala">
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
/// The few sentences the engine says to the reader — every one of them the text
/// of a failure that the app puts straight into a dialog.
///
/// The engine has no window and no resource file of its own: it is built
/// against nothing, so the test suite and the measuring tools can run it in a
/// console. So the translation is handed in from outside instead. The app plugs
/// its own lookup into <see cref="Translator"/> as it starts; anything else that
/// loads this library leaves it alone and gets the English written at the call
/// site.
///
/// Which is why every call carries its English with it: a message that only
/// exists in a resource file is a message that disappears the moment somebody
/// runs the engine without one.
/// </summary>
public static class CoreText
{
    /// <summary>
    /// Turns a key and its parts into a sentence in the reader's language, or
    /// answers null when it has none for that key.
    /// </summary>
    public static Func<string, object?[], string?>? Translator { get; set; }

    /// <summary>
    /// The sentence for a key, translated if anybody can, and otherwise the
    /// English given here with its <c>{0}</c>, <c>{1}</c>… filled in.
    /// </summary>
    public static string Say(string key, string english, params object?[] parts)
    {
        if (Translator?.Invoke(key, parts) is { Length: > 0 } translated)
        {
            return translated;
        }

        return parts.Length == 0 ? english : string.Format(english, parts);
    }
}
