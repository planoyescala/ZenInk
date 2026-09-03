//-----------------------------------------------------------------------------------------
// <copyright file="DefaultPdfApp.cs" company="plano y escala">
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
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.System;
using ZenInk.Core;

namespace ZenInk_App;

/// <summary>
/// The offer to open PDFs with ZenInk.
///
/// The package manifest declares that ZenInk handles <c>.pdf</c>, which puts it
/// in "Abrir con" and in the Windows list of default applications. It does not
/// make it the default: since Windows 10 that choice is the reader's alone and
/// is signed per user, so nothing a program writes will take. What is left is
/// to notice that we are not the default, say so once, and open the page where
/// the reader can change it — with a way to say never again, because a program
/// that asks the same question at every start is a program that gets uninstalled.
///
/// Deciding is in <see cref="PdfAssociation"/>. What is here is asking Windows
/// and showing the dialog.
/// </summary>
public static class DefaultPdfApp
{
    /// <summary>Set once the reader ticks «No volver a preguntar».</summary>
    private const string DeclinedKey = "PdfDefaultDeclined";

    /// <summary>
    /// One offer per run. The window can be reloaded — <c>TabView</c> alone
    /// does it on every tab change — and a dialog that comes back with the
    /// second tab would be worse than one that never appeared.
    /// </summary>
    private static bool _offered;

    /// <summary>
    /// Asks, if there is anything to ask. Silent when ZenInk already opens
    /// PDFs, when the reader has said no before, or when this copy is running
    /// without a package and is therefore not registered as a handler at all.
    /// </summary>
    public static async Task OfferAsync(XamlRoot? root)
    {
        if (_offered || root is null) return;

        _offered = true;

        Handler current = CurrentHandler();
        bool ours = PdfAssociation.IsOurs(current.AppId, current.Executable, OurAppId(), Environment.ProcessPath);

        if (!PdfAssociation.ShouldOffer(IsPackaged(), ours, Declined())) return;

        var explanation = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = current.FriendlyName is { Length: > 0 } name
                ? $"Ahora los PDF se abren con {name}. Windows solo permite cambiarlo desde sus ajustes, así que ZenInk no puede hacerlo por su cuenta: se abrirá la página de aplicaciones predeterminadas, y ahí ZenInk aparece ya en la lista."
                : "Windows solo permite cambiar esto desde sus ajustes, así que ZenInk no puede hacerlo por su cuenta: se abrirá la página de aplicaciones predeterminadas, y ahí ZenInk aparece ya en la lista.",
        };

        var never = new CheckBox { Content = "No volver a preguntar", Margin = new Thickness(0, 16, 0, 0) };

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "¿Abrir los PDF con ZenInk?",
            Content = new StackPanel { Width = 380, Children = { explanation, never } },
            PrimaryButtonText = "Abrir los ajustes",
            CloseButtonText = "Ahora no",
            DefaultButton = ContentDialogButton.Primary,
        };

        AppTheme.Dress(dialog);

        ContentDialogResult result = await Dialogs.ShowAsync(dialog);

        // The tick is honoured whichever button closed the dialog: someone who
        // goes to the settings page has answered the question either way.
        if (never.IsChecked == true)
        {
            Decline();
        }

        if (result == ContentDialogResult.Primary)
        {
            await OpenSettingsAsync();
        }
    }

    /// <summary>
    /// Opens the Windows page where the default is chosen, at ZenInk's own
    /// entry when the version at hand knows how to go there.
    /// </summary>
    public static async Task OpenSettingsAsync()
    {
        string? appId = OurAppId();

        if (appId is { Length: > 0 } &&
            await Launcher.LaunchUriAsync(new Uri($"ms-settings:defaultapps?registeredAUMID={Uri.EscapeDataString(appId)}")))
        {
            return;
        }

        await Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
    }

    /// <summary>Whether this copy could be a registered handler at all.</summary>
    public static bool IsPackaged()
    {
        int length = 0;

        // 15700 is APPMODEL_ERROR_NO_PACKAGE. Asked this way rather than by
        // catching what Package.Current throws, so an unpackaged run does not
        // start by raising an exception on every launch.
        return GetCurrentPackageFullName(ref length, null) != 15700;
    }

    private static bool Declined()
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values[DeclinedKey] is true;
        }
        catch (Exception)
        {
            // No store means no record of a refusal, and asking once is the
            // safe side of that.
            return false;
        }
    }

    private static void Decline()
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[DeclinedKey] = true;
        }
        catch (Exception)
        {
            // Losing the preference means asking again next time, which is a
            // nuisance. Crashing on a settings write is not.
        }
    }

    /// <summary>
    /// How the shell knows this application. Absent without a package, and
    /// also on builds older than 2004, where <see cref="AppInfo"/> is not
    /// there — the manifest still declares support down to 1809. Both cases
    /// fall back to comparing binaries and to the plain settings page.
    /// </summary>
    private static string? OurAppId()
    {
        if (!IsPackaged() || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return null;

        try
        {
            return AppInfo.Current.AppUserModelId;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>What the shell says opens a PDF today.</summary>
    private static Handler CurrentHandler() => new(
        Query(AssocStr.AppId),
        Query(AssocStr.Executable),
        Query(AssocStr.FriendlyAppName));

    private sealed record Handler(string? AppId, string? Executable, string? FriendlyName);

    /// <summary>
    /// One string about the <c>.pdf</c> association. Null for anything the
    /// shell will not answer, which is normal: with no default chosen there is
    /// no handler to describe, and that is itself an answer.
    /// </summary>
    private static string? Query(AssocStr what)
    {
        uint length = 0;
        if (AssocQueryStringW(0, what, ".pdf", null, null, ref length) is not (0 or 1) || length == 0)
        {
            return null;
        }

        var buffer = new StringBuilder((int)length);
        if (AssocQueryStringW(0, what, ".pdf", null, buffer, ref length) != 0)
        {
            return null;
        }

        string value = buffer.ToString();
        return value.Length > 0 ? value : null;
    }

    private enum AssocStr
    {
        Executable = 2,
        FriendlyAppName = 4,
        AppId = 21,
    }

    // The first call returns S_FALSE (1) with the length it wants; only the
    // second, with a buffer, returns S_OK.
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryStringW(
        uint flags, AssocStr str, string assoc, string? extra, StringBuilder? output, ref uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? fullName);
}
