using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ZenInk_App.Controls;

/// <summary>
/// A vector icon drawn from path data on a 20x20 grid.
///
/// Icons are drawn rather than taken from a symbol font because the font's
/// glyph names are easy to get wrong — a mistyped code point silently renders
/// some unrelated pictogram — and because a shared geometry is the only way to
/// keep the app matching the icons the design was approved on.
/// </summary>
public sealed partial class ToolIcon : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(ToolIcon), new PropertyMetadata(null, OnVisualChanged));

    /// <summary>Fills the geometry instead of stroking it, for icons built from solid shapes.</summary>
    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(ToolIcon), new PropertyMetadata(false, OnVisualChanged));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ToolIcon), new PropertyMetadata(1.4, OnVisualChanged));

    private ButtonBase? _host;

    public ToolIcon()
    {
        InitializeComponent();

        // Foreground is inherited from the hosting button and changes with its
        // visual state — checked, disabled, pointer-over — so the icon has to
        // follow it rather than sample it once.
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Apply());
        ActualThemeChanged += (_, _) => Apply();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Foreground reaches this control by inheritance, and an inherited value
    /// changing does not raise the notification registered above. Every colour
    /// this icon should follow — theme, enabled, checked, pointer-over —
    /// arrives that way, so the icon re-reads it whenever the hosting button
    /// changes state instead of waiting for a notification that never comes.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Apply();

        if (_host is not null) return;

        DependencyObject? node = this;
        while (node is not null and not ButtonBase)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        if (node is not ButtonBase button) return;

        _host = button;
        button.RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Refresh());
        button.IsEnabledChanged += (_, _) => Refresh();

        if (button is ToggleButton toggle)
        {
            toggle.RegisterPropertyChangedCallback(ToggleButton.IsCheckedProperty, (_, _) => Refresh());
        }
    }

    /// <summary>
    /// Defers a tick: the button's visual state runs after the state property
    /// changes, so Foreground still holds the outgoing colour at this point.
    /// </summary>
    private void Refresh() => DispatcherQueue.TryEnqueue(Apply);

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ToolIcon)d).Apply();

    private void Apply()
    {
        if (IconPath is null) return;

        IconPath.Data = Data;

        if (Filled)
        {
            IconPath.Fill = Foreground;
            IconPath.Stroke = null;
            IconPath.StrokeThickness = 0;
        }
        else
        {
            IconPath.Fill = null;
            IconPath.Stroke = Foreground;
            IconPath.StrokeThickness = Thickness;
        }
    }
}
