using Microsoft.UI.Xaml.Controls;

namespace ZenInk_App;

/// <summary>
/// The one door every <see cref="ContentDialog"/> goes through.
///
/// WinUI allows exactly one dialog open at a time and does not decline a second
/// one politely: <c>ShowAsync</c> throws, and because every caller is an
/// <c>async void</c> event handler, the throw takes the window down with it —
/// unsaved marks included. That is not hypothetical. The offer to become the
/// system's PDF reader is up while the app finishes starting, and any toolbar
/// button pressed behind it used to be fatal.
///
/// A second request is answered with <see cref="ContentDialogResult.None"/>,
/// which every caller already treats as "the reader said no": nothing happens,
/// and the dialog that is actually on screen keeps the reader's attention,
/// which is where it belongs.
/// </summary>
public static class Dialogs
{
    private static bool _open;

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (_open) return ContentDialogResult.None;

        _open = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _open = false;
        }
    }
}
