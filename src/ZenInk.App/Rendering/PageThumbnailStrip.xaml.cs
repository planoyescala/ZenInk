using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

using ZenInk.Core;

namespace ZenInk_App.Rendering;

/// <summary>
/// The sheet navigator. Previews are rendered on demand for the slots that are
/// actually scrolled into view and never for the rest: a dense A0 sheet costs
/// seconds of PDFium time whatever size it is drawn at, so rendering a whole
/// set up front would stall the document the user is reading.
/// </summary>
public sealed partial class PageThumbnailStrip : UserControl
{
    private const double FrameWidth = 168;
    private const int PreviewMaxEdge = 220;

    /// <summary>Slots this far outside the visible band are rendered too, so scrolling lands on ready thumbnails.</summary>
    private const double PrefetchMargin = 240;

    private readonly List<PageThumbnail> _thumbnails = [];
    private readonly HashSet<int> _requested = [];

    private PdfTiledViewer? _viewer;
    private int _generation;

    public PageThumbnailStrip()
    {
        InitializeComponent();

        // Slots are only asked for once they have been laid out, and a
        // collapsed strip lays nothing out — so revealing it, or resizing it,
        // is the moment to ask again. Without this the strip opens on a column
        // of empty frames that fill in only once the reader scrolls.
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => QueueVisiblePreviews());
        Scroller.SizeChanged += (_, _) => QueueVisiblePreviews();
    }

    /// <summary>Asks after the current layout pass, when the slots exist to measure.</summary>
    private void QueueVisiblePreviews() => DispatcherQueue.TryEnqueue(RequestVisiblePreviews);

    /// <summary>
    /// The repeater realising a slot is the one dependable signal that there is
    /// something to measure. Rebuilding the list, revealing the strip and
    /// scrolling all end here, which is what stops the strip from showing a
    /// column of empty frames until something else happens to nudge it.
    /// </summary>
    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args) =>
        QueueVisiblePreviews();

    public void Attach(PdfTiledViewer? viewer)
    {
        if (ReferenceEquals(_viewer, viewer)) return;

        if (_viewer is { } previous)
        {
            previous.DocumentOpened -= OnDocumentOpened;
            previous.PagesChanged -= OnDocumentOpened;
            previous.ViewChanged -= OnViewChanged;
        }

        _viewer = viewer;

        if (_viewer is { } current)
        {
            current.DocumentOpened += OnDocumentOpened;
            // A turn changes every frame's proportions, so the strip is rebuilt
            // rather than patched.
            current.PagesChanged += OnDocumentOpened;
            current.ViewChanged += OnViewChanged;
        }

        Rebuild();
    }

    private void OnDocumentOpened(object? sender, EventArgs e) => Rebuild();

    private void OnViewChanged(object? sender, EventArgs e) => UpdateCurrent();

    private void Rebuild()
    {
        _generation++;
        _requested.Clear();
        _thumbnails.Clear();
        Repeater.ItemsSource = null;

        if (_viewer is not { } viewer || viewer.PageCount == 0)
        {
            return;
        }

        foreach (var size in viewer.PageSizes)
        {
            _thumbnails.Add(new PageThumbnail(_thumbnails.Count, size, FrameWidth));
        }

        Repeater.ItemsSource = _thumbnails;
        UpdateCurrent();

        // The repeater has not laid out yet, so ask again once it has.
        QueueVisiblePreviews();
    }

    private void UpdateCurrent()
    {
        if (_viewer is not { } viewer || _thumbnails.Count == 0) return;

        int current = viewer.CurrentPageIndex;
        for (int i = 0; i < _thumbnails.Count; i++)
        {
            _thumbnails[i].IsCurrent = i == current;
        }
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => RequestVisiblePreviews();

    /// <summary>Renders previews for the slots inside the visible band, plus a margin.</summary>
    private void RequestVisiblePreviews()
    {
        if (_viewer is not { } viewer || viewer.DocumentId < 0 || _thumbnails.Count == 0) return;

        double top = Scroller.VerticalOffset - PrefetchMargin;
        double bottom = Scroller.VerticalOffset + Scroller.ViewportHeight + PrefetchMargin;

        for (int i = 0; i < _thumbnails.Count; i++)
        {
            if (_requested.Contains(i)) continue;
            if (Repeater.TryGetElement(i) is not FrameworkElement element) continue;

            var bounds = element.TransformToVisual(Scroller.Content as UIElement)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

            if (bounds.Bottom < top || bounds.Top > bottom) continue;

            _requested.Add(i);
            _ = LoadPreviewAsync(i, viewer.DocumentId, viewer.RotationOf(i), _generation);
        }
    }

    private async Task LoadPreviewAsync(int pageIndex, int documentId, int rotation, int generation)
    {
        try
        {
            var preview = await PdfRenderQueue.Shared.RequestPagePreviewAsync(documentId, pageIndex, PreviewMaxEdge, rotation);
            if (generation != _generation || preview is not { } image) return;

            var bitmap = new WriteableBitmap(image.Width, image.Height);
            image.Bgra.CopyTo(0, bitmap.PixelBuffer, 0, image.Bgra.Length);
            bitmap.Invalidate();

            _thumbnails[pageIndex].Image = bitmap;
        }
        catch (Exception ex)
        {
            // A preview is a convenience; losing one must not disturb the viewer.
            System.Diagnostics.Debug.WriteLine($"ZenInk: fallo al generar la miniatura de la hoja {pageIndex}: {ex}");
            _requested.Remove(pageIndex);
        }
    }

    private void OnThumbnailTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PageThumbnail thumbnail })
        {
            _viewer?.GoToPage(thumbnail.PageIndex);
        }
    }
}
