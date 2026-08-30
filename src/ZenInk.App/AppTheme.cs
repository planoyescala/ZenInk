using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.UI;

namespace ZenInk_App;

/// <summary>
/// The window's light/dark preference, remembered between sessions.
///
/// The theme is applied to the window's root element rather than to the
/// Application: <see cref="Application.RequestedTheme"/> can only be set before
/// the first window exists and has no "follow the system" value, whereas
/// <see cref="ElementTheme.Default"/> on the root does follow it, and can be
/// changed while the app is running.
/// </summary>
public static class AppTheme
{
    private const string SettingKey = "AppTheme";

    public static ElementTheme Current { get; private set; } = Load();

    public static void Apply(ElementTheme theme, bool persist = true)
    {
        Current = theme;

        if (App.Current.MainWindowOrNull is not { } window) return;

        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }

        UpdateCaptionButtons(window, theme);

        if (persist)
        {
            Save(theme);
        }
    }

    /// <summary>
    /// Puts a dialog in the window's theme.
    ///
    /// The theme is set on the window's root element, and a dialog is not
    /// under it: it opens in the popup layer, which hangs off the same XamlRoot
    /// as a sibling. So it inherits nothing and falls back to the
    /// Application's theme, which is always light — a white dialog over a dark
    /// window. Reading <see cref="FrameworkElement.ActualTheme"/> rather than
    /// <see cref="Current"/> is what makes "según el sistema" come out right:
    /// as a request, Default means light, and only the root knows what the
    /// system actually chose.
    /// </summary>
    public static void Dress(FrameworkElement dialog)
    {
        if (App.Current.MainWindowOrNull?.Content is FrameworkElement root)
        {
            dialog.RequestedTheme = root.ActualTheme;
        }
    }

    /// <summary>
    /// The caption buttons are drawn by the window frame, not by XAML, so they
    /// have to be recoloured by hand or they stay dark-on-dark after a switch.
    /// </summary>
    private static void UpdateCaptionButtons(Window window, ElementTheme theme)
    {
        bool dark = theme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => window.Content is FrameworkElement { ActualTheme: ElementTheme.Dark },
        };

        var titleBar = window.AppWindow.TitleBar;
        Color foreground = dark ? Colors.White : Color.FromArgb(255, 26, 26, 26);
        Color hover = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0);

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = dark
            ? Color.FromArgb(140, 255, 255, 255)
            : Color.FromArgb(140, 0, 0, 0);
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = hover;
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private static ElementTheme Load()
    {
        try
        {
            object? stored = ApplicationData.Current.LocalSettings.Values[SettingKey];
            if (stored is string name && Enum.TryParse(name, out ElementTheme theme))
            {
                return theme;
            }
        }
        catch
        {
            // An unreadable setting is not worth failing startup over.
        }

        return ElementTheme.Default;
    }

    private static void Save(ElementTheme theme)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[SettingKey] = theme.ToString();
        }
        catch
        {
            // Losing the preference is survivable; crashing on a settings write is not.
        }
    }
}
