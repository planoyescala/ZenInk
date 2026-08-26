using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using ZenInk_App.Rendering;

namespace ZenInk_App;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
        UpdateChrome();
    }

    /// <summary>The viewer of the selected tab, or null when nothing is open.</summary>
    private PdfTiledViewer? ActiveViewer =>
        (Tabs.SelectedItem as TabViewItem)?.Content as PdfTiledViewer;

    private async void OnOpenClicked(object sender, RoutedEventArgs e) => await OpenDocumentInNewTabAsync();

    private async void OnAddTabClicked(TabView sender, object args) => await OpenDocumentInNewTabAsync();

    private async Task OpenDocumentInNewTabAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));
        picker.FileTypeFilter.Add(".pdf");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;

        OpenButton.IsEnabled = false;
        try
        {
            var viewer = new PdfTiledViewer();
            viewer.ViewChanged += OnViewerViewChanged;

            var tab = new TabViewItem
            {
                Header = file.Name,
                Content = viewer,
                IconSource = new SymbolIconSource { Symbol = Symbol.Document },
            };
            ToolTipService.SetToolTip(tab, file.Path);

            Tabs.TabItems.Add(tab);
            Tabs.SelectedItem = tab;

            await viewer.OpenAsync(file.Path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(file.Name, ex);
            CloseTab(Tabs.SelectedItem as TabViewItem);
        }
        finally
        {
            OpenButton.IsEnabled = true;
            UpdateChrome();
        }
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

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) =>
        CloseTab(args.Tab);

    private void CloseTab(TabViewItem? tab)
    {
        if (tab is null) return;

        if (tab.Content is PdfTiledViewer viewer)
        {
            viewer.ViewChanged -= OnViewerViewChanged;
            // Explicit teardown: the viewer must not rely on Unloaded, which a
            // TabView also raises when merely switching away from a tab.
            viewer.CloseDocument();
        }

        tab.Content = null;
        Tabs.TabItems.Remove(tab);
        UpdateChrome();
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var removed in e.RemovedItems)
        {
            if (removed is TabViewItem { Content: PdfTiledViewer viewer })
            {
                viewer.SetActive(false);
            }
        }

        ActiveViewer?.SetActive(true);
        UpdateChrome();
    }

    private void OnViewerViewChanged(object? sender, EventArgs e)
    {
        // Only the visible tab drives the toolbar.
        if (!ReferenceEquals(sender, ActiveViewer)) return;
        UpdateChrome();
    }

    // --- toolbar ---------------------------------------------------------

    private void OnFitClicked(object sender, RoutedEventArgs e) => ActiveViewer?.FitToWidth();

    private void OnPanToolClicked(object sender, RoutedEventArgs e) => SelectTool(ViewerTool.Pan);

    private void OnTextToolClicked(object sender, RoutedEventArgs e) => SelectTool(ViewerTool.SelectText);

    private void SelectTool(ViewerTool tool)
    {
        if (ActiveViewer is { } viewer)
        {
            viewer.Tool = tool;
        }
        UpdateChrome();
    }

    private void OnContinuousModeClicked(object sender, RoutedEventArgs e) =>
        SelectLayoutMode(ViewerLayoutMode.Continuous);

    private void OnSingleModeClicked(object sender, RoutedEventArgs e) =>
        SelectLayoutMode(ViewerLayoutMode.SinglePage);

    private void SelectLayoutMode(ViewerLayoutMode mode)
    {
        if (ActiveViewer is { } viewer)
        {
            viewer.LayoutMode = mode;
        }
        UpdateChrome();
    }

    private void OnPreviousPageClicked(object sender, RoutedEventArgs e) => ActiveViewer?.PreviousPage();

    private void OnNextPageClicked(object sender, RoutedEventArgs e) => ActiveViewer?.NextPage();

    /// <summary>Mirrors the active tab's state into the shared toolbar.</summary>
    private void UpdateChrome()
    {
        var viewer = ActiveViewer;
        bool hasDocument = viewer is not null && viewer.PageCount > 0;

        EmptyState.Visibility = Tabs.TabItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        FitButton.IsEnabled = hasDocument;
        PanToolButton.IsEnabled = hasDocument;
        TextToolButton.IsEnabled = hasDocument;
        ContinuousModeButton.IsEnabled = hasDocument;
        SingleModeButton.IsEnabled = hasDocument;
        PreviousPageButton.IsEnabled = viewer?.CanGoPrevious ?? false;
        NextPageButton.IsEnabled = viewer?.CanGoNext ?? false;

        PanToolButton.IsChecked = viewer?.Tool != ViewerTool.SelectText;
        TextToolButton.IsChecked = viewer?.Tool == ViewerTool.SelectText;
        ContinuousModeButton.IsChecked = viewer?.LayoutMode != ViewerLayoutMode.SinglePage;
        SingleModeButton.IsChecked = viewer?.LayoutMode == ViewerLayoutMode.SinglePage;

        if (viewer is null || !hasDocument)
        {
            ViewStatusText.Text = string.Empty;
            return;
        }

        string selection = viewer.HasSelection ? "  ·  texto seleccionado" : string.Empty;
        ViewStatusText.Text = $"Hoja {viewer.CurrentPageNumber} de {viewer.PageCount}  ·  {viewer.ZoomPercent:0}%{selection}";
    }
}
