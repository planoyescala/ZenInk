using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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

    /// <summary>
    /// Set while the panel is being filled in from the viewer. The slider and
    /// the comment box raise the same events whether a person or this code
    /// changed them, and without the guard writing a value back would read as
    /// the reader having edited it.
    /// </summary>
    private bool _syncingPanel;

    /// <summary>
    /// The drawings opened lately. Read from disk the first time something asks
    /// for them and not at startup: opening the window must not wait on a file,
    /// however small.
    /// </summary>
    private IReadOnlyList<string> _recent = [];

    private bool _recentLoaded;

    /// <summary>Set when the history changed; the start page rebuilds next time it is on screen.</summary>
    private bool _startPageStale = true;

    /// <summary>
    /// Set once the page has done its start-up work. <c>Loaded</c> can come
    /// round again, and opening the drawings of the launch a second time — or
    /// listening for activations twice — would show every sheet twice.
    /// </summary>
    private bool _started;

    public MainPage()
    {
        InitializeComponent();
        SyncThemeMenu();
        UpdateChrome();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
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

        if (_started) return;

        _started = true;

        // Without a package there is no file association and no settings entry
        // to send anyone to, so the menu does not offer one either.
        DefaultPdfItem.Visibility = DefaultPdfApp.IsPackaged() ? Visibility.Visible : Visibility.Collapsed;

        App.Current.FilesActivated += OnFilesActivated;
        await OpenAllAsync(App.Current.LaunchFiles);

        // After the drawings, never before: this opens a dialog, and one that
        // stands between the reader and the sheet they double-clicked is a
        // dialog they will resent.
        await DefaultPdfApp.OfferAsync(XamlRoot);
    }

    /// <summary>Drawings handed over by a second launch — see <see cref="App.FilesActivated"/>.</summary>
    private async void OnFilesActivated(IReadOnlyList<string> paths) => await OpenAllAsync(paths);

    private async Task OpenAllAsync(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            await OpenPathInNewTabAsync(path, System.IO.Path.GetFileName(path));
        }
    }

    private async void OnDefaultPdfClicked(object sender, RoutedEventArgs e) =>
        await DefaultPdfApp.OpenSettingsAsync();

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

    private async void OnOpenClicked(SplitButton sender, SplitButtonClickEventArgs args) => await PickAndOpenAsync();

    private async void OnAddTabClicked(TabView sender, object args) => await PickAndOpenAsync();

    // --- planos abiertos hace poco ----------------------------------------

    /// <summary>The history, read on first use.</summary>
    private IReadOnlyList<string> Recent
    {
        get
        {
            if (_recentLoaded) return _recent;

            _recentLoaded = true;
            _recent = RecentDocuments.Load(RecentFiles.StorePath);
            return _recent;
        }
    }

    private void SetRecent(IReadOnlyList<string> paths)
    {
        _recent = paths;
        _recentLoaded = true;
        _startPageStale = true;
        RecentDocuments.Save(RecentFiles.StorePath, paths);
    }

    /// <summary>
    /// Fills the start page's list. Only when it is actually on screen and only
    /// when the history has moved: the chrome is refreshed on every scroll and
    /// every zoom, and rebuilding a list nobody is looking at on each of those
    /// would be work for nothing.
    /// </summary>
    private void UpdateStartPage()
    {
        if (!_startPageStale || EmptyState.Visibility != Visibility.Visible) return;

        _startPageStale = false;

        var entries = Recent.Select(path => new RecentEntry(path)).ToList();
        RecentList.ItemsSource = entries;
        RecentSection.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Builds the menu each time it opens rather than keeping it in step: a
    /// dozen items is nothing to make, and this way it cannot go stale.
    /// </summary>
    private void OnRecentFlyoutOpening(object? sender, object e)
    {
        RecentFlyout.Items.Clear();

        if (Recent.Count == 0)
        {
            RecentFlyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Todavía no has abierto ningún plano",
                IsEnabled = false,
            });
            return;
        }

        foreach (string path in Recent)
        {
            var item = new MenuFlyoutItem { Text = new RecentEntry(path).Name, Tag = path };
            ToolTipService.SetToolTip(item, path);
            item.Click += OnRecentClicked;
            RecentFlyout.Items.Add(item);
        }

        RecentFlyout.Items.Add(new MenuFlyoutSeparator());

        var clear = new MenuFlyoutItem { Text = "Vaciar la lista" };
        clear.Click += (_, _) =>
        {
            SetRecent([]);
            UpdateStartPage();
        };
        RecentFlyout.Items.Add(clear);
    }

    private async void OnRecentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;

        await OpenPathInNewTabAsync(path, System.IO.Path.GetFileName(path));
    }

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
            viewer.TextWanted += OnViewerTextWanted;

            tab = new TabViewItem
            {
                Header = displayName,
                // Long sheet names are the norm, so cap the tab and let the
                // header trim rather than pushing every other tab off-screen.
                // The floor is what keeps a dozen drawings on one strip: past
                // it the tabs would start scrolling, and a tab you have to
                // scroll to find is worse than a narrow one.
                MinWidth = 54,
                MaxWidth = 240,
                IconSource = new SymbolIconSource { Symbol = Symbol.Document },
                Tag = viewer,
            };
            ToolTipService.SetToolTip(tab, path);

            Tabs.TabItems.Add(tab);
            Tabs.SelectedItem = tab;
            ShowViewer(viewer);

            await viewer.OpenAsync(path);
            SetRecent(RecentDocuments.Promote(Recent, path));
        }
        catch (Exception ex)
        {
            CloseTab(tab);

            // A drawing that will not open is one the shortcut list should stop
            // offering — a moved or deleted sheet is the usual reason.
            SetRecent(RecentDocuments.Remove(Recent, path));
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

        AppTheme.Dress(dialog);
        await dialog.ShowAsync();
    }

    /// <summary>
    /// Closing a tab with turns or marks that never reached the file is the one
    /// place work can be lost silently, so it asks. TabView removes nothing on its
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

        AppTheme.Dress(dialog);

        var answer = await dialog.ShowAsync();
        if (answer == ContentDialogResult.None) return;

        if (answer == ContentDialogResult.Primary)
        {
            using (BusyScope("Guardando…"))
            {
                string? error = await viewer.SaveChangesAsync();
                if (error is not null)
                {
                    await ShowMessageAsync("No se pudo guardar el documento", error);
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
            viewer.TextWanted -= OnViewerTextWanted;
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

    /// <summary>
    /// A mark was just placed that is waiting to be typed into, so the caret
    /// goes to the panel. Otherwise the reader clicks on the sheet, a marker
    /// appears, and the keys they type go nowhere.
    /// </summary>
    private void OnViewerTextWanted(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, ActiveViewer)) return;

        UpdateChrome();
        NoteText.Focus(FocusState.Programmatic);
        NoteText.SelectAll();
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

    /// <summary>
    /// Throws away everything that has not been written: turns and marks alike.
    ///
    /// It asks first, and it has to. Reopening the file is the honest undo —
    /// it puts the document back exactly as the PDF has it, with no separate
    /// history to keep in step — but that also means a morning's marks go with
    /// the turns, and the two arrive at this menu item together.
    /// </summary>
    private async void OnDiscardChangesClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveViewer is not { } viewer || viewer.SourcePath is not { } path) return;
        if (!viewer.HasUnsavedChanges) return;

        int marks = viewer.Annotations.Count;
        string what = (viewer.HasUnsavedRotations, marks) switch
        {
            (true, 0) => "Los giros que no se han guardado se perderán.",
            (true, _) => $"Los giros y las {marks} marcas que no se han guardado se perderán.",
            (false, 1) => "La marca que no se ha guardado se perderá.",
            _ => $"Las {marks} marcas que no se han guardado se perderán.",
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Descartar los cambios",
            Content = $"{what}\n\nEl documento volverá a como está en el archivo.",
            PrimaryButtonText = "Descartar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        };

        AppTheme.Dress(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

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
    /// Writes the document's pending changes back to its file: the sheet turns
    /// and the marks, in one write.
    /// </summary>
    private async Task SaveDocumentAsync()
    {
        if (ActiveViewer is not { } viewer || !viewer.HasUnsavedChanges) return;

        using (BusyScope("Guardando…"))
        {
            string? error = await viewer.SaveChangesAsync();
            if (error is not null)
            {
                await ShowMessageAsync("No se pudo guardar el documento", error);
            }
        }

        UpdateChrome();
    }

    /// <summary>
    /// Burns the marks into the drawing, for good. Everything pending is
    /// written first, so nothing is lost on the way — but the marks stop being
    /// marks, and that is worth asking about plainly rather than through a
    /// word like "flatten" on a menu.
    /// </summary>
    private async void OnFlattenClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveViewer is not { } viewer || viewer.PageCount == 0) return;

        int marks = viewer.Annotations.Count;
        string count = marks switch
        {
            0 => "Las anotaciones del documento pasarán a formar parte del dibujo.",
            1 => "La marca de este documento pasará a formar parte del dibujo.",
            _ => $"Las {marks} marcas de este documento pasarán a formar parte del dibujo.",
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Aplanar las marcas",
            Content = $"{count}\n\nDespués nadie podrá moverlas, cambiarlas ni borrarlas, "
                + "ni en ZenInk ni en otro programa. También se aplanan las anotaciones "
                + "que traía el archivo de otras herramientas.\n\nEsto no se puede deshacer.",
            PrimaryButtonText = "Aplanar y guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        };

        AppTheme.Dress(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        using (BusyScope("Aplanando las marcas…"))
        {
            string? error = await viewer.FlattenAsync();
            if (error is not null)
            {
                await ShowMessageAsync("No se pudieron aplanar las marcas", error);
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
                await viewer.SaveChangesCopyAsync(file.Path);
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
                // Marks print whether or not they have been saved, for the same
                // reason unsaved turns do: the paper should be what is on screen.
                job.Marks = viewer.Annotations.Snapshot();
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

    private void OnSelectMarkToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.SelectAnnotation);

    private void OnInkToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Ink);

    private void OnLineToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Line);

    private void OnArrowToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Arrow);

    private void OnRectangleToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Rectangle);

    private void OnEllipseToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Ellipse);

    private void OnPolylineToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Polyline);

    private void OnPolygonToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Polygon);

    private void OnCloudToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Cloud);

    private void OnHighlightToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Highlight);

    private void OnFreeTextToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.FreeText);

    private void OnNoteToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Note);

    // --- marcas -----------------------------------------------------------

    private void OnUndoClicked(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClicked(object sender, RoutedEventArgs e) => Redo();

    private void OnUndoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Undo();
        args.Handled = true;
    }

    private void OnRedoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Redo();
        args.Handled = true;
    }

    private void Undo()
    {
        ActiveViewer?.UndoAnnotation();
        UpdateChrome();
    }

    private void Redo()
    {
        ActiveViewer?.RedoAnnotation();
        UpdateChrome();
    }

    private void OnColourClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveViewer is not { } viewer) return;
        if ((sender as FrameworkElement)?.Tag is not string hex) return;
        if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint packed)) return;

        viewer.AnnotationStyle = viewer.AnnotationStyle with { Color = AnnotationColor.FromPacked(packed) };
        UpdateChrome();
    }

    private void OnWidthChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingPanel || ActiveViewer is not { } viewer) return;

        viewer.AnnotationStyle = viewer.AnnotationStyle with { WidthPt = (float)e.NewValue };
        UpdateChrome();
    }

    private void OnFillColourClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveViewer is not { } viewer) return;
        if ((sender as FrameworkElement)?.Tag is not string hex) return;
        if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint packed)) return;

        viewer.AnnotationStyle = viewer.AnnotationStyle with { Fill = AnnotationColor.FromPacked(packed) };
        UpdateChrome();
    }

    private void OnNoFillClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveViewer is not { } viewer) return;

        viewer.AnnotationStyle = viewer.AnnotationStyle with { Fill = null };
        UpdateChrome();
    }

    private void OnFontSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingPanel || ActiveViewer is not { } viewer) return;

        viewer.AnnotationStyle = viewer.AnnotationStyle with { FontSizePt = (float)e.NewValue };
        UpdateChrome();
    }

    private void OnFillOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingPanel || ActiveViewer is not { } viewer) return;

        viewer.AnnotationStyle = viewer.AnnotationStyle with { FillOpacity = (float)(e.NewValue / 100.0) };
        UpdateChrome();
    }

    private void OnNoteTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingPanel) return;
        ActiveViewer?.SetSelectedText(NoteText.Text);
    }

    private void OnDeleteMarkClicked(object sender, RoutedEventArgs e)
    {
        ActiveViewer?.DeleteSelectedAnnotation();
        UpdateChrome();

        // The button took the focus to be clicked; handing it back means the
        // next Supr goes to the drawing rather than to a button that no longer
        // has anything to delete.
        ActiveViewer?.TakeKeyboard();
    }

    /// <summary>
    /// Supr removes the mark in hand from anywhere in the window. The canvas
    /// handles the same key itself, but only while it holds the focus — and
    /// picking a mark up and then touching its colour, its width or its text
    /// leaves the focus in the properties panel, which is exactly when a
    /// reviewer reaches for Supr.
    /// </summary>
    private void OnDeleteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Not while someone is writing: there Supr is a character, not a mark.
        if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox or RichEditBox or AutoSuggestBox)
        {
            return;
        }

        if (ActiveViewer is not { SelectedAnnotation: not null } viewer) return;

        viewer.DeleteSelectedAnnotation();
        UpdateChrome();
        args.Handled = true;
    }

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
        UpdateStartPage();

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

        UpdateMarkTools(viewer, tool, interactive);
        UpdateViewMenu(viewer);
        UpdateSaveChrome(viewer, interactive);

        Thumbnails.Visibility = hasDocument && ThumbnailsButton.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

        // The panel is contextual: it appears for the tools that have options.
        bool markTool = tool.IsAnnotation();
        PropertiesPanel.Visibility = hasDocument && (textTool || markTool) ? Visibility.Visible : Visibility.Collapsed;
        TextToolPanel.Visibility = textTool ? Visibility.Visible : Visibility.Collapsed;
        MarkToolPanel.Visibility = markTool ? Visibility.Visible : Visibility.Collapsed;

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
    /// The marking half of the chrome: which tool is down, what the panel is
    /// showing, and whether there is anything to undo.
    ///
    /// The panel is filled from the viewer rather than kept in step by hand,
    /// which is what makes picking up a mark show that mark's colour and width
    /// without a second path through the code.
    /// </summary>
    private void UpdateMarkTools(PdfTiledViewer? viewer, ViewerTool tool, bool interactive)
    {
        foreach (var (button, owned) in MarkToolButtons(tool))
        {
            button.IsEnabled = interactive;
            button.IsChecked = owned;
        }

        UndoButton.IsEnabled = interactive && (viewer?.CanUndo ?? false);
        RedoButton.IsEnabled = interactive && (viewer?.CanRedo ?? false);

        PropertiesIconText.Visibility = tool.IsAnnotation() ? Visibility.Collapsed : Visibility.Visible;
        PropertiesIconMark.Visibility = tool.IsAnnotation() ? Visibility.Visible : Visibility.Collapsed;

        if (!tool.IsAnnotation())
        {
            if (tool == ViewerTool.SelectText)
            {
                PropertiesTitle.Text = "Selección de texto";
            }
            return;
        }

        PropertiesTitle.Text = ToolTitle(tool);

        var style = viewer?.AnnotationStyle ?? AnnotationStyle.Default;
        var selected = viewer?.SelectedAnnotation;

        // The fill belongs to the shapes that enclose an area; on a line it
        // would be a control with nothing to do.
        var kind = selected?.Kind ?? (tool == ViewerTool.SelectAnnotation ? AnnotationKind.Ink : tool.ToKind());
        bool takesFill = Annotation.TakesFill(kind);

        // A mark that cannot be changed shows none of the controls that would
        // change it: a swatch that does nothing when clicked is worse than no
        // swatch at all.
        bool changeable = selected is null || Annotation.CanBeChanged(selected.Kind);

        _syncingPanel = true;
        try
        {
            CurrentColourSwatch.Background = Swatch(style.Color);

            ColourSection.Visibility = changeable ? Visibility.Visible : Visibility.Collapsed;
            WidthSection.Visibility = changeable && Annotation.TakesWidth(kind)
                ? Visibility.Visible
                : Visibility.Collapsed;
            WidthSlider.Value = style.WidthPt;
            WidthValueText.Text = $"{style.WidthPt:0.##} pt";

            FontSizeSection.Visibility = changeable && Annotation.TakesFontSize(kind)
                ? Visibility.Visible
                : Visibility.Collapsed;
            FontSizeSlider.Value = Math.Clamp(Math.Round(style.FontSizePt), 6, 72);
            FontSizeText.Text = $"{style.FontSizePt:0} pt";

            FillSection.Visibility = changeable && takesFill ? Visibility.Visible : Visibility.Collapsed;
            FillOpacitySection.Visibility = changeable && takesFill && style.Fill is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
            FillStateText.Text = style.Fill is null ? "Sin relleno" : string.Empty;
            CurrentFillSwatch.Visibility = style.Fill is null ? Visibility.Collapsed : Visibility.Visible;
            if (style.Fill is { } fill)
            {
                CurrentFillSwatch.Background = Swatch(fill);
                FillOpacitySlider.Value = Math.Clamp(Math.Round(style.FillOpacity * 100.0), 5, 100);
                FillOpacityText.Text = $"{style.FillOpacity * 100:0} %";
            }

            bool typing = selected is not null && Annotation.TakesText(selected.Kind);
            NoteEditor.Visibility = typing ? Visibility.Visible : Visibility.Collapsed;
            if (typing)
            {
                NoteEditorLabel.Text = selected!.Kind == AnnotationKind.Note ? "Comentario" : "Texto";
                NoteText.PlaceholderText = selected.Kind == AnnotationKind.Note
                    ? "Qué hay que revisar"
                    : "Lo que se lee en el plano";

                if (NoteText.Text != selected.Text)
                {
                    NoteText.Text = selected.Text;
                }
            }
        }
        finally
        {
            _syncingPanel = false;
        }

        DeleteMarkButton.IsEnabled = selected is not null;

        int onSheet = viewer is null ? 0 : viewer.Annotations.CountForPage(viewer.CurrentPageIndex);
        bool placing = viewer?.IsPlacingVertices ?? false;

        MarkStatus.Text = placing
            ? "Clic para cada vértice. Intro o doble clic lo termina, Retroceso quita el último, Esc lo descarta."
            : selected is not null && !changeable
                ? $"El resaltado va con el texto que cubre: no se mueve ni cambia, solo se elimina. {Sheet(onSheet)}"
            : selected is not null
                ? $"Marca seleccionada. Arrástrala para moverla, tira de un tirador para estirarla o del pomo para girarla. {Sheet(onSheet)}"
                : tool switch
                {
                    ViewerTool.SelectAnnotation => $"Haz clic en una marca para cogerla. {Sheet(onSheet)}",
                    ViewerTool.Note => $"Haz clic en el plano para dejar un comentario. {Sheet(onSheet)}",
                    ViewerTool.FreeText => $"Haz clic en el plano y escribe en el panel. {Sheet(onSheet)}",
                    ViewerTool.Ink => $"Dibuja con el lápiz o el ratón. La otra punta del lápiz borra. {Sheet(onSheet)}",
                    ViewerTool.Highlight => $"Arrastra sobre el texto del plano para resaltarlo. {Sheet(onSheet)}",
                    ViewerTool.Cloud => $"Arrastra un recuadro, o haz clic en cada vértice. {Sheet(onSheet)}",
                    ViewerTool.Polyline or ViewerTool.Polygon =>
                        $"Haz clic en cada vértice; Intro o doble clic lo termina. {Sheet(onSheet)}",
                    _ => $"Arrastra sobre el plano para dibujar. {Sheet(onSheet)}",
                };

        static string Sheet(int count) => count switch
        {
            0 => "Esta hoja no tiene marcas.",
            1 => "Hay 1 marca en esta hoja.",
            _ => $"Hay {count} marcas en esta hoja.",
        };
    }

    private IEnumerable<(ToggleButton Button, bool Owns)> MarkToolButtons(ViewerTool tool)
    {
        yield return (SelectMarkToolButton, tool == ViewerTool.SelectAnnotation);
        yield return (InkToolButton, tool == ViewerTool.Ink);
        yield return (LineToolButton, tool == ViewerTool.Line);
        yield return (ArrowToolButton, tool == ViewerTool.Arrow);
        yield return (PolylineToolButton, tool == ViewerTool.Polyline);
        yield return (RectangleToolButton, tool == ViewerTool.Rectangle);
        yield return (EllipseToolButton, tool == ViewerTool.Ellipse);
        yield return (PolygonToolButton, tool == ViewerTool.Polygon);
        yield return (CloudToolButton, tool == ViewerTool.Cloud);
        yield return (HighlightToolButton, tool == ViewerTool.Highlight);
        yield return (TextToolMarkButton, tool == ViewerTool.FreeText);
        yield return (NoteToolButton, tool == ViewerTool.Note);
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush Swatch(AnnotationColor colour) =>
        new(Windows.UI.Color.FromArgb(255, colour.R, colour.G, colour.B));

    private static string ToolTitle(ViewerTool tool) => tool switch
    {
        ViewerTool.SelectAnnotation => "Marcas",
        ViewerTool.Ink => "Lápiz",
        ViewerTool.Line => "Línea",
        ViewerTool.Arrow => "Flecha",
        ViewerTool.Polyline => "Polilínea",
        ViewerTool.Rectangle => "Rectángulo",
        ViewerTool.Ellipse => "Elipse",
        ViewerTool.Polygon => "Polígono",
        ViewerTool.Cloud => "Nube de revisión",
        ViewerTool.Highlight => "Resaltar texto",
        ViewerTool.FreeText => "Texto",
        ViewerTool.Note => "Comentario",
        _ => "Herramienta",
    };

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
        FlattenItem.IsEnabled = hasDocument;
        DiscardChangesItem.IsEnabled = pending;
    }
}
