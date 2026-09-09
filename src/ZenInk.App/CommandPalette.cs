//-----------------------------------------------------------------------------------------
// <copyright file="CommandPalette.cs" company="plano y escala">
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
using Microsoft.UI.Xaml.Input;
using Windows.System;
using ZenInk_App.Rendering;

namespace ZenInk_App;

/// <summary>One thing the app can be asked to do, by name.</summary>
/// <param name="Keys">The shortcut, when there is one. Shown, never parsed.</param>
/// <param name="Also">
/// Other words someone might reach for. Nobody looks up «nube de revisión» by
/// typing "nube" only — they type "revision", or "marcar", or "globo".
///
/// These are translated like everything else, and not word for word: what
/// somebody types when hunting for a tool is a matter of the language they
/// think in, not of what the Spanish list happens to say.
/// </param>
public sealed record AppCommand(string Name, string Keys, string Also, Action Run);

public sealed partial class MainPage
{
    /// <summary>
    /// Every command, built fresh each time the palette opens.
    ///
    /// Built rather than kept, because what a command does depends on which tab
    /// is in front: a list held from one document to the next would run against
    /// the wrong one. Thirty-odd records is nothing to make.
    /// </summary>
    private List<AppCommand> BuildCommands()
    {
        var empty = new RoutedEventArgs();

        var commands = new List<AppCommand>
        {
            Cmd("Open", "", () => OnOpenClicked(OpenButton, null!)),
            Cmd("Save", "Ctrl+S", () => _ = SaveDocumentAsync()),
            Cmd("SaveAs", "", () => OnSaveCopyClicked(this, empty)),
            Cmd("Flatten", "", () => OnFlattenClicked(this, empty)),
            Cmd("Sign", "", () => OnSignClicked(this, empty)),

            // Separate from signing, and named so: it puts a box on the sheet
            // saying who looked at it, and seals nothing at all.
            Cmd("Stamp", "", () => _ = StampHereAsync()),

            Cmd("Discard", "", () => OnDiscardChangesClicked(this, empty)),
            Cmd("Print", "Ctrl+P", () => OnPrintClicked(this, empty)),
            Cmd("Find", "Ctrl+F", () => OnFindClicked(this, empty)),

            Cmd("Undo", "Ctrl+Z", () => OnUndoClicked(this, empty)),
            Cmd("Redo", "Ctrl+Y", () => OnRedoClicked(this, empty)),

            Cmd("ZoomIn", "", () => OnZoomInClicked(this, empty)),
            Cmd("ZoomOut", "", () => OnZoomOutClicked(this, empty)),
            Cmd("FitWidth", "Ctrl+1", () => OnFitWidthClicked(this, empty)),
            Cmd("FitPage", "Ctrl+2", () => OnFitPageClicked(this, empty)),
            Cmd("ActualSize", "Ctrl+0", () => OnActualSizeClicked(this, empty)),

            Cmd("Continuous", "", () => OnContinuousModeClicked(this, empty)),
            Cmd("SinglePage", "", () => OnSingleModeClicked(this, empty)),
            Cmd("OneColumn", "", () => OnOneColumnClicked(this, empty)),
            Cmd("TwoColumns", "", () => OnTwoColumnsClicked(this, empty)),
            Cmd("SheetPanel", "", () => OnThumbnailsClicked(this, empty)),

            Cmd("RotateLeft", "", () => OnRotateLeftClicked(this, empty)),
            Cmd("RotateRight", "", () => OnRotateRightClicked(this, empty)),
            Cmd("RotateAllLeft", "", () => OnRotateAllLeftClicked(this, empty)),
            Cmd("RotateAllRight", "", () => OnRotateAllRightClicked(this, empty)),

            Cmd("MoveUp", "Alt+↑", () => Pages.Run(PageAction.MoveUp)),
            Cmd("MoveDown", "Alt+↓", () => Pages.Run(PageAction.MoveDown)),
            Cmd("MoveTo", "", () => Pages.Run(PageAction.MoveTo)),
            Cmd("Duplicate", "", () => Pages.Run(PageAction.Duplicate)),
            Cmd("RemoveSheet", "", () => Pages.Run(PageAction.Delete)),
            Cmd("InsertFromFile", "", () => Pages.Run(PageAction.InsertFromFile)),
            Cmd("InsertBlank", "", () => Pages.Run(PageAction.InsertBlank)),
            Cmd("Extract", "", () => Pages.Run(PageAction.Extract)),
            Cmd("Split", "", () => Pages.Run(PageAction.Split)),

            Cmd("Compare", "", () => _ = PickRevisionAsync()),
            Cmd("NextChange", "F4", () => StepChange(1)),
            Cmd("PreviousChange", $"{Loc.Get("KeyShift")}+F4", () => StepChange(-1)),
            Cmd("SwapColours", "", () => OnCompareSwapClicked(this, empty)),
            Cmd("StopCompare", "", () => OnCompareStopClicked(this, empty)),

            Cmd("SelectPage", "", () => OnSelectAllClicked(this, empty)),
            Cmd("CopySelection", "Ctrl+C", () => OnCopySelectionClicked(this, empty)),

            Cmd("ThemeSystem", "", () => OnThemeSystemClicked(this, empty)),
            Cmd("ThemeLight", "", () => OnThemeLightClicked(this, empty)),
            Cmd("ThemeDark", "", () => OnThemeDarkClicked(this, empty)),

            Cmd("About", "", () => OnAboutClicked(this, empty)),
        };

        // The marking tools, by name. Since they moved into the ribbon they
        // carry labels of their own, but typing still beats hunting for the
        // tab a rarely-used one lives on.
        commands.AddRange(new (string Key, ViewerTool Tool)[]
        {
            ("Pan", ViewerTool.Pan),
            ("ZoomRect", ViewerTool.ZoomRectangle),
            ("SelectText", ViewerTool.SelectText),
            ("SelectMark", ViewerTool.SelectAnnotation),
            ("Line", ViewerTool.Line),
            ("Arrow", ViewerTool.Arrow),
            ("Polyline", ViewerTool.Polyline),
            ("Rectangle", ViewerTool.Rectangle),
            ("Ellipse", ViewerTool.Ellipse),
            ("Polygon", ViewerTool.Polygon),
            ("Cloud", ViewerTool.Cloud),
            ("Highlight", ViewerTool.Highlight),
            ("FreeText", ViewerTool.FreeText),
            ("Note", ViewerTool.Note),
            ("Ink", ViewerTool.Ink),
            ("Capture", ViewerTool.CaptureRegion),
            ("Calibrate", ViewerTool.Calibrate),
            ("Distance", ViewerTool.Distance),
            ("Perimeter", ViewerTool.Perimeter),
            ("Area", ViewerTool.Area),
            ("Angle", ViewerTool.Angle),
        }.Select(entry => new AppCommand(
            Loc.Get($"Tool{entry.Key}"),
            string.Empty,
            Loc.Get($"Tool{entry.Key}Also"),
            () => SetTool(entry.Tool))));

        return commands;
    }

    /// <summary>
    /// One command, named and described from the resources: <c>Cmd…</c> is what
    /// the list shows, <c>Cmd…Also</c> the words that also find it.
    ///
    /// Two keys per command rather than two literals, because a palette whose
    /// synonyms stayed in Spanish would be a palette that only answers to
    /// someone who speaks it.
    /// </summary>
    private static AppCommand Cmd(string key, string keys, Action run) =>
        new(Loc.Get($"Cmd{key}"), keys, Loc.Get($"Cmd{key}Also"), run);

    private List<AppCommand> _commands = [];

    private void OnCommandPaletteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleCommandPalette();
    }

    private void OnCommandButtonClicked(object sender, RoutedEventArgs e) => ToggleCommandPalette();

    private void ToggleCommandPalette()
    {
        if (CommandPalette.Visibility == Visibility.Visible)
        {
            HideCommandPalette();
            return;
        }

        _commands = BuildCommands();
        CommandQuery.Text = string.Empty;
        ShowMatches(string.Empty);

        CommandPalette.Visibility = Visibility.Visible;
        CommandQuery.Focus(FocusState.Programmatic);
    }

    private void HideCommandPalette()
    {
        CommandPalette.Visibility = Visibility.Collapsed;
        ActiveViewer?.Focus(FocusState.Programmatic);
    }

    private void OnCommandQueryChanged(object sender, TextChangedEventArgs e) => ShowMatches(CommandQuery.Text);

    /// <summary>
    /// Filters by every typed word, against both the name and the extra words.
    /// Not fuzzy on purpose: a list that reorders itself on every keystroke is
    /// harder to aim at than one that only ever gets shorter.
    /// </summary>
    private void ShowMatches(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var matches = _commands
            .Where(command => words.All(word =>
                Folded(command.Name).Contains(Folded(word), StringComparison.Ordinal)
                || Folded(command.Also).Contains(Folded(word), StringComparison.Ordinal)))
            .ToList();

        CommandList.ItemsSource = matches
            .Select(command => command.Keys.Length > 0 ? $"{command.Name}      {command.Keys}" : command.Name)
            .ToList();

        _matches = matches;
        if (matches.Count > 0) CommandList.SelectedIndex = 0;
    }

    private List<AppCommand> _matches = [];

    /// <summary>
    /// Accents and case folded away, so «polilinea» finds «polilínea». The same
    /// reason the finder folds them: nobody reaches for the accent key while
    /// hunting for a button.
    /// </summary>
    private static string Folded(string text)
    {
        var folded = new System.Text.StringBuilder(text.Length);
        foreach (char c in text.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                folded.Append(char.ToLowerInvariant(c));
            }
        }
        return folded.ToString();
    }

    private void OnCommandQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                HideCommandPalette();
                break;

            case VirtualKey.Down:
                e.Handled = true;
                Step(1);
                break;

            case VirtualKey.Up:
                e.Handled = true;
                Step(-1);
                break;

            case VirtualKey.Enter:
                e.Handled = true;
                RunSelected();
                break;
        }
    }

    private void Step(int by)
    {
        if (_matches.Count == 0) return;

        int next = CommandList.SelectedIndex + by;
        CommandList.SelectedIndex = Math.Clamp(next, 0, _matches.Count - 1);
        CommandList.ScrollIntoView(CommandList.SelectedItem);
    }

    private void OnCommandChosen(object sender, ItemClickEventArgs e)
    {
        CommandList.SelectedIndex = CommandList.Items.IndexOf(e.ClickedItem);
        RunSelected();
    }

    private void RunSelected()
    {
        int at = CommandList.SelectedIndex;
        if (at < 0 || at >= _matches.Count) return;

        var chosen = _matches[at];
        HideCommandPalette();
        chosen.Run();
    }
}
