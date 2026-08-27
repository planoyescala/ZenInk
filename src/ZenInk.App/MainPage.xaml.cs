using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using ZenInk_App.Rendering;

namespace ZenInk_App;

public sealed partial class MainPage : Page
{
    private const double ZoomStep = 1.25;

    public MainPage()
    {
        InitializeComponent();
        UpdateChrome();
    }

    /// <summary>
    /// The viewer of the selected tab. Tabs carry their viewer in Tag rather
    /// than Content: the TabView here is only the strip, and the viewer is
    /// hosted in the canvas cell so the rail, thumbnails and properties panel
    /// can sit alongside it.
    /// </summary>
    private PdfTiledViewer? ActiveViewer => (Tabs.SelectedItem as TabViewItem)?.Tag as PdfTiledViewer;

    private async void OnOpenClicked(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private async void OnAddTabClicked(TabView sender, object args) => await PickAndOpenAsync();

    private async Task PickAndOpenAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));
        picker.FileTypeFilter.Add(".pdf");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await OpenPathInNewTabAsync(file.Path, file.Name);
    }

    private async Task OpenPathInNewTabAsync(string path, string displayName)
    {
        OpenButton.IsEnabled = false;
        TabViewItem? tab = null;
        try
        {
            var viewer = new PdfTiledViewer();
            viewer.ViewChanged += OnViewerViewChanged;

            tab = new TabViewItem
            {
                Header = displayName,
                // Long sheet names are the norm, so cap the tab and let the
                // header trim rather than pushing every other tab off-screen.
                MaxWidth = 240,
                IconSource = new SymbolIconSource { Symbol = Symbol.Document },
                Tag = viewer,
            };
            ToolTipService.SetToolTip(tab, path);

            Tabs.TabItems.Add(tab);
            Tabs.SelectedItem = tab;
            ShowViewer(viewer);

            await viewer.OpenAsync(path);
        }
        catch (Exception ex)
        {
            CloseTab(tab);
            await ShowErrorAsync(displayName, ex);
        }
        finally
        {
            OpenButton.IsEnabled = true;
            UpdateChrome();
        }
    }

    private void ShowViewer(PdfTiledViewer? viewer)
    {
        ViewerPresenter.Content = viewer;
        Thumbnails.Attach(viewer);
        viewer?.SetActive(true);
    }

    private async Task ShowErrorAsync(string fileName, Exception ex)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "No se pudo abrir el documento",
            Content = $"{fileName}\n\n{ex.Message}",
            CloseButtonText = "Cerrar",
        };
        await dialog.ShowAsync();
    }

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) => CloseTab(args.Tab);

    private void CloseTab(TabViewItem? tab)
    {
        if (tab is null) return;

        if (tab.Tag is PdfTiledViewer viewer)
        {
            if (ReferenceEquals(ViewerPresenter.Content, viewer))
            {
                ViewerPresenter.Content = null;
                Thumbnails.Attach(null);
            }

            viewer.ViewChanged -= OnViewerViewChanged;
            viewer.CloseDocument();
            tab.Tag = null;
        }

        Tabs.TabItems.Remove(tab);
        UpdateChrome();
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var removed in e.RemovedItems)
        {
            if (removed is TabViewItem { Tag: PdfTiledViewer viewer })
            {
                viewer.SetActive(false);
            }
        }

        ShowViewer(ActiveViewer);
        UpdateChrome();
    }

    private void OnViewerViewChanged(object? sender, EventArgs e)
    {
        // Only the visible tab drives the chrome.
        if (!ReferenceEquals(sender, ActiveViewer)) return;
        UpdateChrome();
    }

    // --- barra superior ---------------------------------------------------

    private void OnFitClicked(object sender, RoutedEventArgs e) => ActiveViewer?.FitToWidth();

    private void OnZoomInClicked(object sender, RoutedEventArgs e) => ActiveViewer?.ZoomBy(ZoomStep);

    private void OnZoomOutClicked(object sender, RoutedEventArgs e) => ActiveViewer?.ZoomBy(1.0 / ZoomStep);

    private void OnContinuousModeClicked(object sender, RoutedEventArgs e) =>
        SetLayoutMode(ViewerLayoutMode.Continuous);

    private void OnSingleModeClicked(object sender, RoutedEventArgs e) =>
        SetLayoutMode(ViewerLayoutMode.SinglePage);

    private void SetLayoutMode(ViewerLayoutMode mode)
    {
        if (ActiveViewer is { } viewer)
        {
            viewer.LayoutMode = mode;
        }
        UpdateChrome();
    }

    private void OnLineWeightClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveViewer is { } viewer)
        {
            viewer.ThinLines = LineWeightButton.IsChecked != true;
        }
        UpdateChrome();
    }

    private void OnPreviousPageClicked(object sender, RoutedEventArgs e) => ActiveViewer?.PreviousPage();

    private void OnNextPageClicked(object sender, RoutedEventArgs e) => ActiveViewer?.NextPage();

    // --- raíl y paneles ---------------------------------------------------

    private void OnPanToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Pan);

    private void OnTextToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.SelectText);

    private void SetTool(ViewerTool tool)
    {
        if (ActiveViewer is { } viewer)
        {
            viewer.Tool = tool;
        }
        UpdateChrome();
    }

    private void OnThumbnailsClicked(object sender, RoutedEventArgs e) => UpdateChrome();

    private void OnCopySelectionClicked(object sender, RoutedEventArgs e) => ActiveViewer?.CopySelection();

    private void OnSelectAllClicked(object sender, RoutedEventArgs e) => ActiveViewer?.SelectCurrentPage();

    /// <summary>Mirrors the active tab's state into the shared chrome.</summary>
    private void UpdateChrome()
    {
        var viewer = ActiveViewer;
        bool hasDocument = viewer is not null && viewer.PageCount > 0;

        EmptyState.Visibility = Tabs.TabItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        FitButton.IsEnabled = hasDocument;
        ZoomInButton.IsEnabled = hasDocument;
        ZoomOutButton.IsEnabled = hasDocument;
        PanToolButton.IsEnabled = hasDocument;
        TextToolButton.IsEnabled = hasDocument;
        ContinuousModeButton.IsEnabled = hasDocument;
        SingleModeButton.IsEnabled = hasDocument;
        LineWeightButton.IsEnabled = hasDocument;
        ThumbnailsButton.IsEnabled = hasDocument;
        PreviousPageButton.IsEnabled = viewer?.CanGoPrevious ?? false;
        NextPageButton.IsEnabled = viewer?.CanGoNext ?? false;

        bool textTool = viewer?.Tool == ViewerTool.SelectText;
        PanToolButton.IsChecked = !textTool;
        TextToolButton.IsChecked = textTool;
        ContinuousModeButton.IsChecked = viewer?.LayoutMode != ViewerLayoutMode.SinglePage;
        SingleModeButton.IsChecked = viewer?.LayoutMode == ViewerLayoutMode.SinglePage;
        LineWeightButton.IsChecked = viewer?.ThinLines != true;

        Thumbnails.Visibility = hasDocument && ThumbnailsButton.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

        // The panel is contextual: it appears for the tool that has options.
        PropertiesPanel.Visibility = hasDocument && textTool ? Visibility.Visible : Visibility.Collapsed;

        bool hasSelection = viewer?.HasSelection ?? false;
        CopySelectionButton.IsEnabled = hasSelection;
        SelectAllButton.IsEnabled = hasDocument;
        SelectionStatus.Text = hasSelection
            ? "Texto seleccionado. Ctrl+C también copia."
            : "Arrastra sobre el plano para seleccionar.";

        if (viewer is null || !hasDocument)
        {
            PageIndicator.Text = string.Empty;
            ZoomIndicator.Text = string.Empty;
            DocumentNameText.Text = string.Empty;
            return;
        }

        PageIndicator.Text = $"Hoja {viewer.CurrentPageNumber} de {viewer.PageCount}";
        ZoomIndicator.Text = $"{viewer.ZoomPercent:0} %";
        DocumentNameText.Text = (Tabs.SelectedItem as TabViewItem)?.Header as string ?? string.Empty;
    }
}
