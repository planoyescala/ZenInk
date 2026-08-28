using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using ZenInk.Core;
using ZenInk_App.Printing;
using ZenInk_App.Rendering;

namespace ZenInk_App;

public sealed partial class MainPage : Page
{
    private const double ZoomStep = 1.25;

    /// <summary>Set while a file operation is running; the bar is locked and says so.</summary>
    private string? _busyMessage;

    public MainPage()
    {
        InitializeComponent();
        SyncThemeMenu();
        UpdateChrome();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var window = App.Current.MainWindow;
        window.SetTitleBar(TitleBarDragRegion);
        window.AppWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange)
            {
                SizeTitleBarDragRegion();
            }
        };
        SizeTitleBarDragRegion();
        AppTheme.Apply(AppTheme.Current, persist: false);
    }

    /// <summary>
    /// Reserves room for the caption buttons at the end of the tab strip. Their
    /// width is reported in physical pixels and varies with the window's
    /// scaling, so it is converted rather than assumed.
    /// </summary>
    private void SizeTitleBarDragRegion()
    {
        var titleBar = App.Current.MainWindow.AppWindow.TitleBar;
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        TitleBarDragRegion.MinWidth = (titleBar.RightInset / Math.Max(scale, 0.1)) + 16;
    }

    // --- preferencias -----------------------------------------------------

    private void OnThemeSystemClicked(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Default);

    private void OnThemeLightClicked(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Light);

    private void OnThemeDarkClicked(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Dark);

    private void SetTheme(ElementTheme theme)
    {
        AppTheme.Apply(theme);
        SyncThemeMenu();
    }

    private void SyncThemeMenu()
    {
        ThemeSystemItem.IsChecked = AppTheme.Current == ElementTheme.Default;
        ThemeLightItem.IsChecked = AppTheme.Current == ElementTheme.Light;
        ThemeDarkItem.IsChecked = AppTheme.Current == ElementTheme.Dark;
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

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Abrir en ZenInk";
        e.DragUIOverride.IsGlyphVisible = false;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        // The deferral matters: the drag data is only readable while the drop
        // is still in flight, and opening is asynchronous.
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (item is not StorageFile file) continue;
                if (!string.Equals(file.FileType, ".pdf", StringComparison.OrdinalIgnoreCase)) continue;

                await OpenPathInNewTabAsync(file.Path, file.Name);
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("No se pudo abrir lo que se ha soltado", ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task OpenPathInNewTabAsync(string path, string displayName)
    {
        OpenButton.IsEnabled = false;
        TabViewItem? tab = null;
        try
        {
            var viewer = new PdfTiledViewer();
            viewer.ViewChanged += OnViewerViewChanged;
            viewer.PagesChanged += OnViewerViewChanged;

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

    private async Task ShowErrorAsync(string fileName, Exception ex) =>
        await ShowMessageAsync("No se pudo abrir el documento", $"{fileName}\n\n{ex.Message}");

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "Cerrar",
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// Closing a tab with turns that never reached the file is the one place
    /// work can be lost silently, so it asks. TabView removes nothing on its
    /// own — the tab only goes when <see cref="CloseTab"/> says so.
    /// </summary>
    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        var tab = args.Tab;
        if (tab.Tag is not PdfTiledViewer { HasUnsavedChanges: true } viewer)
        {
            CloseTab(tab);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Hay cambios sin guardar",
            Content = $"{tab.Header} tiene cambios que no se han escrito en el PDF.",
            PrimaryButtonText = "Guardar y cerrar",
            SecondaryButtonText = "Cerrar sin guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        var answer = await dialog.ShowAsync();
        if (answer == ContentDialogResult.None) return;

        if (answer == ContentDialogResult.Primary)
        {
            using (BusyScope("Guardando los giros…"))
            {
                string? error = await viewer.SaveRotationsAsync();
                if (error is not null)
                {
                    await ShowMessageAsync("No se pudieron guardar los giros", error);
                    return;
                }
            }
        }

        CloseTab(tab);
    }

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
            viewer.PagesChanged -= OnViewerViewChanged;
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

    // --- cómo se ve el documento ------------------------------------------

    private void OnFitWidthClicked(object sender, RoutedEventArgs e) => SetFitMode(ViewerFitMode.Width);

    private void OnFitPageClicked(object sender, RoutedEventArgs e) => SetFitMode(ViewerFitMode.Page);

    private void OnFitFreeClicked(object sender, RoutedEventArgs e) => SetFitMode(ViewerFitMode.Free);

    private void OnActualSizeClicked(object sender, RoutedEventArgs e) => ZoomToActualSize();

    private void OnActualSizeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ZoomToActualSize();
        args.Handled = true;
    }

    private void ZoomToActualSize()
    {
        ActiveViewer?.ZoomToActualSize();
        UpdateChrome();
    }

    private void SetFitMode(ViewerFitMode mode)
    {
        if (ActiveViewer is { } viewer)
        {
            viewer.FitMode = mode;
        }
        UpdateChrome();
    }

    private void OnOneColumnClicked(object sender, RoutedEventArgs e) => SetColumns(1);

    private void OnTwoColumnsClicked(object sender, RoutedEventArgs e) => SetColumns(2);

    private void SetColumns(int columns)
    {
        if (ActiveViewer is { } viewer)
        {
            viewer.Columns = columns;
        }
        UpdateChrome();
    }

    private void OnFitWidthAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetFitMode(ViewerFitMode.Width);
        args.Handled = true;
    }

    private void OnFitPageAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetFitMode(ViewerFitMode.Page);
        args.Handled = true;
    }

    // --- girar y guardar ---------------------------------------------------

    private void OnRotateLeftClicked(object sender, RoutedEventArgs e) => Rotate(-1, everySheet: false);

    private void OnRotateRightClicked(object sender, RoutedEventArgs e) => Rotate(1, everySheet: false);

    private void OnRotateAllLeftClicked(object sender, RoutedEventArgs e) => Rotate(-1, everySheet: true);

    private void OnRotateAllRightClicked(object sender, RoutedEventArgs e) => Rotate(1, everySheet: true);

    private void Rotate(int quarterTurns, bool everySheet)
    {
        if (ActiveViewer is not { } viewer) return;

        if (everySheet)
        {
            viewer.RotateAllPages(quarterTurns);
        }
        else
        {
            viewer.RotateCurrentPage(quarterTurns);
        }

        UpdateChrome();
    }

    private async void OnDiscardRotationsClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveViewer is not { } viewer || viewer.SourcePath is not { } path) return;
        if (!viewer.HasUnsavedRotations) return;

        // Reopening the file is the honest undo: it puts every sheet back the
        // way the PDF has it, with no separate history to keep in step.
        await ReloadAsync(viewer, path);
    }

    private async void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveDocumentAsync();
    }

    private async void OnSaveClicked(SplitButton sender, SplitButtonClickEventArgs args) => await SaveDocumentAsync();

    private async void OnSaveItemClicked(object sender, RoutedEventArgs e) => await SaveDocumentAsync();

    /// <summary>
    /// Writes the document's pending changes back to its file. Sheet turns are
    /// all there is to write today; comments will join them here.
    /// </summary>
    private async Task SaveDocumentAsync()
    {
        if (ActiveViewer is not { } viewer || !viewer.HasUnsavedChanges) return;

        using (BusyScope("Guardando…"))
        {
            string? error = await viewer.SaveRotationsAsync();
            if (error is not null)
            {
                await ShowMessageAsync("No se pudo guardar el documento", error);
            }
        }

        UpdateChrome();
    }

    private async void OnSaveCopyClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveViewer is not { } viewer || viewer.SourcePath is not { } source) return;

        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));
        picker.FileTypeChoices.Add("Documento PDF", [".pdf"]);
        // The document's own name: this saves whatever the document carries,
        // and naming it after one kind of change would go stale the moment
        // there is another.
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(source);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null) return;

        // Choosing the file it is already reading is a plain save, and has to
        // go the plain save's way: the source stays open while a copy is
        // written, so it cannot be its own destination.
        if (IsSamePath(file.Path, source))
        {
            await SaveDocumentAsync();
            return;
        }

        using (BusyScope("Guardando…"))
        {
            try
            {
                await viewer.SaveRotationsCopyAsync(file.Path);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("No se pudo guardar el documento", ex.Message);
            }
        }

        UpdateChrome();
    }

    private static bool IsSamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Closes and reopens a tab's document, discarding anything not written to the file.</summary>
    private async Task ReloadAsync(PdfTiledViewer viewer, string path)
    {
        int page = viewer.CurrentPageIndex;

        using (BusyScope("Recargando…"))
        {
            try
            {
                await viewer.OpenAsync(path);
                // Reopening starts at the first sheet; the reader was not.
                viewer.GoToPage(page);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("No se pudo recargar el documento", ex.Message);
            }
        }

        UpdateChrome();
    }

    /// <summary>
    /// Locks the bar for the length of a file operation. A dense A0 set takes
    /// long enough to write that a second click would otherwise land mid-save.
    /// </summary>
    private IDisposable BusyScope(string message)
    {
        _busyMessage = message;
        UpdateChrome();
        return new Busy(this);
    }

    private sealed class Busy(MainPage page) : IDisposable
    {
        public void Dispose()
        {
            page._busyMessage = null;
            page.UpdateChrome();
        }
    }

    // --- imprimir ----------------------------------------------------------

    private async void OnPrintClicked(object sender, RoutedEventArgs e) => await PrintAsync();

    private async void OnPrintAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PrintAsync();
    }

    /// <summary>
    /// Hands the drawing to Windows' print pane, with the sheet turns the
    /// reader has on screen. Turns that have not been saved still print: what
    /// is on paper should be what is on screen, and being made to save first
    /// would be a strange price for a printout.
    /// </summary>
    private async Task PrintAsync()
    {
        if (ActiveViewer is not { } viewer || viewer.SourcePath is not { } path) return;
        if (viewer.PageCount == 0) return;

        var rotations = new int[viewer.PageCount];
        for (int i = 0; i < rotations.Length; i++)
        {
            rotations[i] = viewer.RotationOf(i);
        }

        string title = (Tabs.SelectedItem as TabViewItem)?.Header as string ?? "Documento";
        PdfPrintSource? source = null;

        try
        {
            using (BusyScope("Preparando la impresión…"))
            {
                var job = await PdfPrintJob.OpenAsync(path, rotations);
                source = new PdfPrintSource(job, title, WindowNative.GetWindowHandle(App.Current.MainWindow));
            }

            await source.ShowAsync();

            if (source.Problem is { } problem)
            {
                await ShowMessageAsync("Hubo un problema al imprimir", problem);
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("No se pudo imprimir", ex.Message);
        }
        finally
        {
            if (source is not null)
            {
                await source.DisposeAsync();
            }
            UpdateChrome();
        }
    }

    // --- buscar ------------------------------------------------------------

    private void OnFindClicked(object sender, RoutedEventArgs e) => ActiveViewer?.OpenFind();

    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ActiveViewer?.OpenFind();
        args.Handled = true;
    }

    private void OnFindNextAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ActiveViewer?.StepFind(1);
        args.Handled = true;
    }

    private void OnFindPreviousAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ActiveViewer?.StepFind(-1);
        args.Handled = true;
    }

    /// <summary>
    /// Escape closes the find bar wherever the focus happens to be. The bar's
    /// own text box handles it too, but the reader has usually clicked back
    /// onto the drawing by the time they want it gone.
    /// </summary>
    private void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ActiveViewer is not { IsFindOpen: true } viewer) return;
        viewer.CloseFind();
        args.Handled = true;
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

    private void OnZoomToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.ZoomRectangle);

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
        bool busy = _busyMessage is not null;
        bool hasDocument = viewer is not null && viewer.PageCount > 0;

        // Busy locks the controls but leaves the panels where they are: a save
        // that made the thumbnails and the properties panel blink out and back
        // would read as the document having been reloaded.
        bool interactive = hasDocument && !busy;

        EmptyState.Visibility = Tabs.TabItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ZoomInButton.IsEnabled = interactive;
        ZoomOutButton.IsEnabled = interactive;
        FindButton.IsEnabled = interactive;
        PrintButton.IsEnabled = interactive;
        PanToolButton.IsEnabled = interactive;
        TextToolButton.IsEnabled = interactive;
        ViewModeButton.IsEnabled = interactive;
        RotateButton.IsEnabled = interactive;
        LineWeightButton.IsEnabled = interactive;
        ThumbnailsButton.IsEnabled = interactive;
        OpenButton.IsEnabled = !busy;
        PreviousPageButton.IsEnabled = interactive && (viewer?.CanGoPrevious ?? false);
        NextPageButton.IsEnabled = interactive && (viewer?.CanGoNext ?? false);

        var tool = viewer?.Tool ?? ViewerTool.Pan;
        bool textTool = tool == ViewerTool.SelectText;
        PanToolButton.IsChecked = tool == ViewerTool.Pan;
        TextToolButton.IsChecked = textTool;
        ZoomToolButton.IsChecked = tool == ViewerTool.ZoomRectangle;
        ZoomToolButton.IsEnabled = interactive;
        LineWeightButton.IsChecked = viewer?.ThinLines != true;

        UpdateViewMenu(viewer);
        UpdateSaveChrome(viewer, interactive);

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

        if (_busyMessage is { } message)
        {
            DocumentNameText.Text = message;
            return;
        }

        if (viewer is null || viewer.PageCount == 0)
        {
            PageIndicator.Text = string.Empty;
            ZoomIndicator.Text = string.Empty;
            DocumentNameText.Text = string.Empty;
            return;
        }

        PageIndicator.Text = $"Hoja {viewer.CurrentPageNumber} de {viewer.PageCount}";
        ZoomIndicator.Text = $"{viewer.ZoomPercent:0} %";

        // A permanent Save button says "there is something to write" only by
        // being enabled, which is easy to miss, so the name carries a dot. A
        // dot and not a phrase: the file name is what this line is for, and a
        // sentence here just trimmed its last characters away.
        string name = (Tabs.SelectedItem as TabViewItem)?.Header as string ?? string.Empty;
        DocumentNameText.Text = viewer.HasUnsavedChanges ? $"{name}  •" : name;
    }

    /// <summary>
    /// The view menu's three groups, plus the button's own label: it names the
    /// choice in force so the reader can tell what they are looking at without
    /// opening the menu.
    /// </summary>
    private void UpdateViewMenu(PdfTiledViewer? viewer)
    {
        var fit = viewer?.FitMode ?? ViewerFitMode.Width;
        int columns = viewer?.Columns ?? 1;
        bool continuous = viewer?.LayoutMode != ViewerLayoutMode.SinglePage;

        bool actualSize = fit == ViewerFitMode.Free
            && viewer is not null
            && Math.Abs(viewer.ZoomPercent - 100.0) < 0.5;

        FitWidthItem.IsChecked = fit == ViewerFitMode.Width;
        FitPageItem.IsChecked = fit == ViewerFitMode.Page;
        FitActualItem.IsChecked = actualSize;
        FitFreeItem.IsChecked = fit == ViewerFitMode.Free && !actualSize;

        OneColumnItem.IsChecked = columns == 1;
        TwoColumnsItem.IsChecked = columns == 2;

        ContinuousItem.IsChecked = continuous;
        SingleItem.IsChecked = !continuous;

        // Two-up only means anything as a way of laying sheets out side by
        // side; on one sheet at a time it is the spread that changes, so the
        // choice stays available and the label carries the distinction.
        string flow = continuous ? "Continuo" : "Hoja a hoja";
        string spread = columns == 2 ? " · 2 pág." : string.Empty;
        ViewModeText.Text = $"{flow}{spread}";
    }

    /// <summary>
    /// Saving stays on screen whether or not there is anything to write, and
    /// greys out when there is not — the way an editor's Save does. "Guardar
    /// como…" needs only an open document, since a copy of an unchanged
    /// drawing is still a reasonable thing to ask for.
    /// </summary>
    private void UpdateSaveChrome(PdfTiledViewer? viewer, bool hasDocument)
    {
        bool pending = hasDocument && (viewer?.HasUnsavedChanges ?? false);

        SaveButton.IsEnabled = hasDocument;
        SaveItem.IsEnabled = pending;
        SaveCopyItem.IsEnabled = hasDocument;
        DiscardRotationsItem.IsEnabled = hasDocument && (viewer?.HasUnsavedRotations ?? false);
    }
}
