//-----------------------------------------------------------------------------------------
// <copyright file="BetaNotice.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ZenInk_App;

/// <summary>
/// What the reader is told the first time they open a build that is not
/// finished yet: that it is a beta, and to keep a copy of anything that
/// matters.
///
/// Said once and then not again, which is what makes it worth saying at all: a
/// warning that appears every morning is a warning nobody reads. What is
/// remembered is the version that said it, so the next beta says it once more —
/// it is a different program by then — and a stable release says nothing,
/// because <see cref="IsBeta"/> is turned off with the same commit that stops
/// it being one.
/// </summary>
internal static class BetaNotice
{
    /// <summary>
    /// Whether this build is still a beta. One line to change on the day it
    /// stops being one, next to the version in the project file.
    /// </summary>
    public const bool IsBeta = true;

    private const string SettingKey = "BetaNoticeSeen";

    public static async Task ShowOnceAsync(XamlRoot? root, string version)
    {
        if (!IsBeta || root is null) return;
        if (LocalSettings.Get(SettingKey) == version) return;

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.Get("BetaTitle"),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Width = 400,
                Text = Loc.Format("BetaBody", version),
            },
            CloseButtonText = Loc.Get("Understood"),
        };

        AppTheme.Dress(dialog);
        await Dialogs.ShowAsync(dialog);

        // Written after it has been read and closed, not before: a launch that
        // dies with the dialog on screen has told the reader nothing.
        LocalSettings.Set(SettingKey, version);
    }
}
