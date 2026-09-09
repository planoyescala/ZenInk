//-----------------------------------------------------------------------------------------
// <copyright file="AppLanguage.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using Windows.Globalization;

namespace ZenInk_App;

/// <summary>
/// Which language the program speaks, remembered between sessions.
///
/// Nothing is chosen by default: with no preference stored, Windows picks —
/// a Spanish machine finds es-ES, everybody else falls back to the en-US that
/// the project file declares as the default language. The choice is here for
/// the reader whose Windows is in one language and who would rather read
/// another, which is common enough on a work machine.
///
/// It has to be set before the first piece of XAML is parsed, because that is
/// when x:Uid is resolved and a loaded page never asks again. So this is
/// applied from the application's constructor, and a change made from the menu
/// only shows up the next time ZenInk is opened — which the dialog says.
/// </summary>
public static class AppLanguage
{
    private const string SettingKey = "AppLanguage";

    /// <summary>The languages ZenInk is written in, in the order the menu shows them.</summary>
    public static readonly string[] Available = ["es-ES", "en-US"];

    /// <summary>
    /// The stored preference: a BCP-47 tag, or empty for "whatever Windows says".
    /// </summary>
    public static string Chosen => LocalSettings.Get(SettingKey) ?? string.Empty;

    /// <summary>
    /// Hands the preference to Windows, which is what makes both the XAML
    /// resources and <see cref="Loc"/> resolve in that language.
    ///
    /// Set on every launch, empty string included, and not only when there is a
    /// choice to apply: Windows remembers the override itself, so a reader who
    /// picks English and later goes back to "follow Windows" would go on
    /// reading English for ever if this returned early. Empty is what clears
    /// it — measured, after exactly that happened.
    ///
    /// Wrapped because the override is stored in the application's own data,
    /// and an installation without package identity —the one the Inno Setup
    /// installer makes— has none to store it in. Losing the preference is a
    /// nuisance; failing to start is not.
    /// </summary>
    public static void Apply()
    {
        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = Chosen;
        }
        catch
        {
            // Windows keeps its own language, which is a reasonable answer.
        }
    }

    /// <summary>
    /// Remembers a choice. Empty means "follow Windows".
    /// </summary>
    public static void Choose(string tag) => LocalSettings.Set(SettingKey, tag);
}
