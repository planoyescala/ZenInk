using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.Text;

using ZenInk.Core;

namespace ZenInk_App.Rendering;

/// <summary>
/// One sheet in the thumbnail strip. The frame keeps the sheet's aspect ratio
/// from the moment the document opens, so the strip has its final shape before
/// any rendering happens and nothing jumps as previews arrive.
/// </summary>
public sealed class PageThumbnail : INotifyPropertyChanged
{
    private BitmapSource? _image;
    private bool _isCurrent;

    public PageThumbnail(int pageIndex, PdfPageSize size, double frameWidth)
    {
        PageIndex = pageIndex;
        double ratio = size.WidthPt <= 0 ? 1.0 : size.HeightPt / size.WidthPt;
        FrameHeight = Math.Clamp(frameWidth * ratio, 40, 260);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int PageIndex { get; }

    public string Number => (PageIndex + 1).ToString();

    public double FrameHeight { get; }

    public BitmapSource? Image
    {
        get => _image;
        set => Set(ref _image, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (!Set(ref _isCurrent, value)) return;
            Notify(nameof(BorderBrush));
            Notify(nameof(BorderThickness));
            Notify(nameof(LabelBrush));
            Notify(nameof(LabelWeight));
        }
    }

    public Brush BorderBrush => _isCurrent
        ? Accent
        : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

    public Thickness BorderThickness => new(_isCurrent ? 2 : 1);

    public Brush LabelBrush => _isCurrent
        ? Accent
        : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

    public FontWeight LabelWeight => _isCurrent ? FontWeights.SemiBold : FontWeights.Normal;

    private static Brush Accent => (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
