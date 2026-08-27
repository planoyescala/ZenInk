using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    public ToolIcon()
    {
        InitializeComponent();

        // Foreground is inherited from the hosting button and changes with its
        // visual state — checked, disabled, pointer-over — so the icon has to
        // follow it rather than sample it once.
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Apply());

        // A theme switch re-resolves the ThemeResource behind Foreground
        // without raising the change notification above, so an icon whose
        // brush comes from a visual state — a disabled or unchecked button —
        // would otherwise keep painting itself in the outgoing theme's colour.
        ActualThemeChanged += (_, _) => Apply();

        Loaded += (_, _) => Apply();
    }

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
