//-----------------------------------------------------------------------------------------
// <copyright file="MainPage.xaml.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
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
    /// Shown where the busy message goes, but without locking anything: the
    /// reader is being asked to do something on the sheet, not waited on.
    /// </summary>
    private string? _hintMessage;

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

        // Fixed for the life of the process, so it is written once and not on
        // every refresh of the start page.
        AboutLineText.Text = $"ZenInk {PackageVersion} · software libre (GPL-3.0) · parte de ZenBIM";

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

        // Offered whenever Windows knows this copy opens PDFs, by package or by
        // the installer's registry entries. Asking about the package alone hid
        // the entry on every copy the installer had put there — which is every
        // copy that is not a developer's.
        DefaultPdfItem.Visibility = DefaultPdfApp.IsRegistered() ? Visibility.Visible : Visibility.Collapsed;

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

    // --- acerca de --------------------------------------------------------

    /// <summary>
    /// The version, asked of the package rather than written by hand: an MSIX
    /// carries its own, and a number typed into a dialog is one that goes stale
    /// the first time the manifest moves.
    /// </summary>
    private static string PackageVersion
    {
        get
        {
            // Asked before it is used: without a package there is nothing to
            // ask, and Package.Current answers that with an exception.
            if (DefaultPdfApp.IsPackaged())
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }

            // Installed from the Inno Setup installer: the assembly carries the
            // same number, put there by the project.
            return typeof(MainPage).Assembly.GetName().Version?.ToString() ?? "1.0.0.0";
        }
    }

    /// <summary>
    /// What the program is, who makes it, and under what licence it may be
    /// copied. Reached from the preferences menu, from the command palette, and
    /// from the line at the foot of the start page.
    /// </summary>
    private async void OnAboutClicked(object sender, RoutedEventArgs e)
    {
        var body = new StackPanel { Spacing = 10, Width = 420 };

        body.Children.Add(new TextBlock
        {
            Text = $"Versión {PackageVersion}",
            Opacity = 0.7,
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Visor y editor de planos PDF. Parte del proyecto ZenBIM, "
                 + "de plano y escala.",
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "© 2026 plano y escala.\n\n"
                 + "ZenInk es software libre: puedes redistribuirlo y modificarlo bajo "
                 + "los términos de la GNU General Public License publicada por la Free "
                 + "Software Foundation, en su versión 3 o cualquier posterior.\n\n"
                 + "Se distribuye con la esperanza de que sea útil, pero SIN GARANTÍA "
                 + "ALGUNA; ni siquiera la garantía implícita de comerciabilidad o de "
                 + "idoneidad para un propósito concreto. Ver la Licencia para más "
                 + "detalles.",
        });

        body.Children.Add(new HyperlinkButton
        {
            Content = "Leer la licencia completa (GPL-3.0)",
            NavigateUri = new Uri("https://www.gnu.org/licenses/gpl-3.0.html"),
            Padding = new Thickness(0),
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Text = "El texto de la licencia y los avisos de terceros viajan dentro del "
                 + "programa, junto al ejecutable.\n\n"
                 + "ZenInk dibuja los planos con PDFium (BSD-3-Clause) a través de "
                 + "PDFiumCore (Apache-2.0), sobre WinUI 3 y Win2D.",
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "ZenInk",
            Content = body,
            CloseButtonText = "Cerrar",
        };

        AppTheme.Dress(dialog);
        await Dialogs.ShowAsync(dialog);
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
                Text = "Todavía no has abierto ningún documento",
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

    /// <summary>
    /// Opens a document, asking for a password if the file wants one and
    /// letting the reader try again if they get it wrong.
    ///
    /// The password is never stored anywhere: it is held for as long as the
    /// document is open — a save closes and reopens the file, and without it the
    /// document would die on its first save — and goes when the tab does.
    /// </summary>
    private async Task OpenUnlockingAsync(PdfTiledViewer viewer, string path, string displayName)
    {
        string? password = null;

        while (true)
        {
            try
            {
                await viewer.OpenAsync(path, password);
                return;
            }
            catch (PdfPasswordRequiredException locked)
            {
                password = await AskForPasswordAsync(displayName, locked.WasTried)
                    ?? throw new OperationCanceledException();
            }
        }
    }

    private async Task<string?> AskForPasswordAsync(string displayName, bool wasWrong)
    {
        var box = new PasswordBox { PlaceholderText = "Contraseña" };

        var body = new StackPanel { Spacing = 10, Width = 380 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = wasWrong
                ? $"Esa contraseña no abre «{displayName}». Prueba otra vez."
                : $"«{displayName}» está protegido con contraseña.",
        });
        body.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Este PDF está protegido",
            Content = body,
            PrimaryButtonText = "Abrir",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        AppTheme.Dress(dialog);

        // Enter on the password box is what everyone does, and a dialog that
        // ignores it reads as broken.
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                dialog.Hide();
                _passwordAccepted = true;
            }
        };

        _passwordAccepted = false;
        var answer = await Dialogs.ShowAsync(dialog);

        return answer == ContentDialogResult.Primary || _passwordAccepted ? box.Password : null;
    }

    private bool _passwordAccepted;

    /// <summary>
    /// The tab already showing a drawing, if one is.
    ///
    /// Compared as full paths and without regard to case, because the same
    /// sheet reaches here spelled several ways: the shell hands over what was
    /// double-clicked, the shortcut list holds what was opened last time, and a
    /// mapped drive or a relative path is the same file written differently.
    /// A comparison of the strings as they arrive would answer "not open" for
    /// half of them, which is the duplicate tab this exists to prevent.
    /// </summary>
    private TabViewItem? TabShowing(string path)
    {
        string wanted;
        try
        {
            wanted = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            // Not a path this machine can make sense of. Opening it will fail
            // in a moment and say so properly; that is not this method's job.
            return null;
        }

        foreach (var item in Tabs.TabItems.OfType<TabViewItem>())
        {
            if (item.Tag is not PdfTiledViewer viewer || viewer.SourcePath is not { } open) continue;

            try
            {
                if (string.Equals(Path.GetFullPath(open), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    private async Task OpenPathInNewTabAsync(string path, string displayName)
    {
        // The same drawing twice is two tabs of the same name holding two sets
        // of marks, and only one of them can be saved. Whoever asks for a sheet
        // that is already open is asking to look at it, so they are taken to it.
        if (TabShowing(path) is { } already)
        {
            Tabs.SelectedItem = already;
            if (already.Tag is PdfTiledViewer shown) ShowViewer(shown);
            UpdateChrome();
            return;
        }

        OpenButton.IsEnabled = false;
        TabViewItem? tab = null;
        try
        {
            var viewer = new PdfTiledViewer();
            viewer.ViewChanged += OnViewerViewChanged;
            viewer.RegionCaptured += OnRegionCaptured;
            viewer.PagesChanged += OnViewerViewChanged;
            viewer.TextWanted += OnViewerTextWanted;
            viewer.ComparisonChanged += OnViewerComparisonChanged;
            viewer.CalibrationDragged += OnCalibrationDragged;

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

            await OpenUnlockingAsync(viewer, path, displayName);
            SetRecent(RecentDocuments.Promote(Recent, path));
        }
        catch (OperationCanceledException)
        {
            // The reader shut the password box. Nothing is wrong with the file,
            // so it stays in the shortcut list and nothing is reported.
            CloseTab(tab);
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
        Pages.Attach(viewer);
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
        await Dialogs.ShowAsync(dialog);
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

        var answer = await Dialogs.ShowAsync(dialog);
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
                Pages.Attach(null);
            }

            viewer.ViewChanged -= OnViewerViewChanged;
            viewer.RegionCaptured -= OnRegionCaptured;
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

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

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
        if (!await ConfirmRearrangementAsync(viewer)) return;

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

    // --- managing the sheets ---------------------------------------------

    /// <summary>Paper the reader can add. Sizes in points, upright; turning them is a click away.</summary>
    private static readonly (string Name, PdfPageSize Size)[] Papers =
    [
        ("A4 — 210 × 297 mm", new PdfPageSize(595f, 842f)),
        ("A3 — 297 × 420 mm", new PdfPageSize(842f, 1191f)),
        ("A2 — 420 × 594 mm", new PdfPageSize(1191f, 1684f)),
        ("A1 — 594 × 841 mm", new PdfPageSize(1684f, 2384f)),
        ("A0 — 841 × 1189 mm", new PdfPageSize(2384f, 3370f)),
    ];

    /// <summary>
    /// What the pages panel cannot finish on its own. Turning, copying,
    /// removing and reordering happen there and then; these four need a file,
    /// a folder or an answer, and those belong to the window.
    /// </summary>
    private async void OnPageActionRequested(object? sender, PageRequest request)
    {
        if (ActiveViewer is not { } viewer || viewer.PageCount == 0) return;

        switch (request.Action)
        {
            case PageAction.MoveTo:
                await MoveToAsync(viewer, request.Sheets);
                break;
            case PageAction.InsertFromFile:
                await InsertFromFileAsync(viewer, request.Destination);
                break;
            case PageAction.InsertBlank:
                await InsertBlankAsync(viewer, request.Destination);
                break;
            case PageAction.Extract:
                await ExtractAsync(viewer, request.Sheets);
                break;
            case PageAction.Split:
                await SplitAsync(viewer);
                break;
        }

        UpdateChrome();
    }

    /// <summary>
    /// Sends the marked sheets to a sheet number the reader types.
    ///
    /// Stepping up one at a time is fine for a nudge and useless for a set of
    /// two hundred, where the answer is already known — "this goes seventh" —
    /// and the only question is how to say it.
    /// </summary>
    private async Task MoveToAsync(PdfTiledViewer viewer, IReadOnlyList<int> sheets)
    {
        if (sheets.Count == 0 || viewer.PageCount < 2) return;

        // The highest number the block can start at, so the last of them still
        // fits on the document.
        int last = viewer.PageCount - sheets.Count + 1;

        var position = new NumberBox
        {
            Value = sheets[0] + 1,
            Minimum = 1,
            Maximum = last,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var body = new StackPanel { Spacing = 12, Width = 380 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = sheets.Count == 1
                ? $"La hoja {sheets[0] + 1} de {viewer.PageCount} se llevará a donde digas."
                : $"Las {sheets.Count} hojas marcadas se llevarán juntas, en su orden, "
                  + $"empezando en la hoja que digas (1 a {last}).",
        });
        body.Children.Add(Labelled("Llevarla a la hoja", position));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = sheets.Count == 1 ? "Mover la hoja" : "Mover las hojas",
            Content = body,
            PrimaryButtonText = "Mover",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        AppTheme.Dress(dialog);
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        // An empty box reads back as NaN, and rounding that would send the
        // sheets to nowhere in particular.
        if (double.IsNaN(position.Value)) return;

        Pages.MoveTo((int)Math.Round(position.Value) - 1);
    }

    /// <summary>
    /// Brings sheets in from another PDF. The file is opened to be asked how
    /// many sheets it has before anything is chosen — offering "which sheets?"
    /// without saying how many there are is a question nobody can answer.
    /// </summary>
    private async Task InsertFromFileAsync(PdfTiledViewer viewer, int destination)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));
        picker.FileTypeFilter.Add(".pdf");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;

        // Each one opened and let go of again, only to be asked how many sheets
        // it has: the viewer opens them for itself once they are chosen, and
        // holding a handle to fourteen files for the length of a dialog buys
        // nothing.
        var read = new List<(StorageFile File, int PageCount, string? Password)>(files.Count);
        int locked = 0;

        using (BusyScope(files.Count == 1 ? "Leyendo el PDF…" : $"Leyendo {files.Count} PDF…"))
        {
            foreach (var file in files)
            {
                var probed = await ProbeAsync(file);
                if (probed is { } ok) read.Add((file, ok.PageCount, ok.Password));
                else locked++;
            }
        }

        if (read.Count == 0)
        {
            if (locked > 0) await ShowMessageAsync("No se insertó nada", "No se pudo abrir ninguno de los PDF elegidos.");
            return;
        }

        // One file is a question about which of its sheets; several is a
        // question about what order they go in. Asking for a range of pages
        // once per file would be absurd for a folder of one-sheet drawings,
        // which is exactly the case this is for.
        List<PageInsertion> batch;

        if (read.Count == 1)
        {
            var (only, pageCount, password) = read[0];
            var chosen = await AskForSheetsAsync(only.Name, pageCount);
            if (chosen is null || chosen.Count == 0) return;

            batch = [new PageInsertion(only.Path, password, chosen)];
        }
        else
        {
            var ordered = await AskForOrderAsync(read);
            if (ordered is null) return;

            batch = [.. ordered.Select(entry =>
                new PageInsertion(entry.File.Path, entry.Password, [.. Enumerable.Range(0, entry.PageCount)]))];
        }

        int sheets = batch.Sum(insertion => insertion.Pages.Count);

        using (BusyScope("Insertando hojas…"))
        {
            try
            {
                await viewer.InsertPagesAsync(destination, batch);
                Hint(locked == 0
                    ? $"{Sheets(sheets)} insertadas."
                    : $"{Sheets(sheets)} insertadas; {locked} archivo(s) no se pudieron abrir.");
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("No se pudieron insertar las hojas", ex.Message);
            }
        }
    }

    /// <summary>
    /// Asks a file how many sheets it has, unlocking it if it asks to be. Null
    /// means it is not going to be inserted — the reader gave up on the
    /// password, or the file will not open at all.
    /// </summary>
    private async Task<(int PageCount, string? Password)?> ProbeAsync(StorageFile file)
    {
        string? password = null;

        while (true)
        {
            try
            {
                var probe = await PdfRenderQueue.Shared.OpenDocumentAsync(file.Path, password);
                int pageCount = probe.Pages.Count;
                await PdfRenderQueue.Shared.CloseDocumentAsync(probe.DocumentId);
                return (pageCount, password);
            }
            catch (PdfPasswordRequiredException wants)
            {
                password = await AskForPasswordAsync(file.Name, wants.WasTried);
                if (password is null) return null;
            }
            catch (Exception ex)
            {
                await ShowMessageAsync($"No se pudo leer «{file.Name}»", ex.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// What order a batch of files goes in. By name to start with, because a
    /// set of drawings is named by code and that is the order it belongs in —
    /// and by name means <c>HOJA-2</c> before <c>HOJA-10</c>, which plain text
    /// order gets backwards.
    /// </summary>
    private async Task<List<(StorageFile File, int PageCount, string? Password)>?> AskForOrderAsync(
        List<(StorageFile File, int PageCount, string? Password)> files)
    {
        var byName = files.OrderBy(entry => entry.File.Name, NaturalOrder.Comparer).ToList();
        var asPicked = new List<(StorageFile File, int PageCount, string? Password)>(files);

        var order = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        order.Items.Add("Por nombre de archivo");
        order.Items.Add("En el orden en que los elegí");

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 240,
            ItemsSource = byName.Select(Describe).ToList(),
        };

        order.SelectionChanged += (_, _) =>
        {
            // Never nothing: a box with no answer in it says nothing about what
            // pressing Insert is going to do.
            if (order.SelectedIndex < 0) order.SelectedIndex = 0;
            list.ItemsSource = (order.SelectedIndex == 1 ? asPicked : byName).Select(Describe).ToList();
        };

        int sheets = files.Sum(entry => entry.PageCount);

        var body = new StackPanel { Spacing = 12, Width = 420 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"{files.Count} archivos, {Sheets(sheets).ToLowerInvariant()} en total. "
                   + "Se pueden reordenar después en el panel de hojas.",
        });
        body.Children.Add(Labelled("Orden", order));
        body.Children.Add(list);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Insertar hojas",
            Content = body,
            PrimaryButtonText = "Insertar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        AppTheme.Dress(dialog);
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return null;

        return order.SelectedIndex == 1 ? asPicked : byName;

        static string Describe((StorageFile File, int PageCount, string? Password) entry) =>
            entry.PageCount == 1 ? entry.File.Name : $"{entry.File.Name}  ·  {entry.PageCount} hojas";
    }

    /// <summary>Which sheets of another file to bring, as a range. Empty means all of them.</summary>
    private async Task<IReadOnlyList<int>?> AskForSheetsAsync(string fileName, int pageCount)
    {
        var box = new TextBox { PlaceholderText = $"Todas (1-{pageCount})" };
        var complaint = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Text = "No se entiende. Por ejemplo: 1, 3, 5-8",
            Visibility = Visibility.Collapsed,
        };

        var body = new StackPanel { Spacing = 10, Width = 380 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"«{fileName}» tiene {(pageCount == 1 ? "una hoja" : $"{pageCount} hojas")}.",
        });
        body.Children.Add(Labelled("Qué hojas traer", box));
        body.Children.Add(complaint);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Insertar hojas",
            Content = body,
            PrimaryButtonText = "Insertar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        // Held open rather than closed on a bad range: throwing the dialog away
        // would throw away what they typed along with it.
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (PageRange.Parse(box.Text, pageCount) is not null) return;

            args.Cancel = true;
            complaint.Visibility = Visibility.Visible;
        };

        AppTheme.Dress(dialog);

        return await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary
            ? PageRange.Parse(box.Text, pageCount)
            : null;
    }

    private async Task InsertBlankAsync(PdfTiledViewer viewer, int destination)
    {
        var size = viewer.PageSizes.Count > 0
            ? viewer.PageSizes[Math.Clamp(viewer.CurrentPageIndex, 0, viewer.PageSizes.Count - 1)]
            : Papers[0].Size;

        var paper = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        paper.Items.Add($"Como la hoja en pantalla — {Math.Round(size.WidthPt / 72 * 25.4)} × {Math.Round(size.HeightPt / 72 * 25.4)} mm");
        foreach (var (name, _) in Papers)
        {
            paper.Items.Add(name);
        }

        var landscape = new CheckBox { Content = "Apaisada" };
        var count = new NumberBox
        {
            Value = 1,
            Minimum = 1,
            Maximum = 50,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var body = new StackPanel { Spacing = 12, Width = 380 };
        body.Children.Add(Labelled("Tamaño", paper));
        body.Children.Add(landscape);
        body.Children.Add(Labelled("Cuántas", count));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Insertar hojas en blanco",
            Content = body,
            PrimaryButtonText = "Insertar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        AppTheme.Dress(dialog);
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var chosen = paper.SelectedIndex <= 0 ? size : Papers[paper.SelectedIndex - 1].Size;
        if (landscape.IsChecked == true)
        {
            chosen = new PdfPageSize(Math.Max(chosen.WidthPt, chosen.HeightPt), Math.Min(chosen.WidthPt, chosen.HeightPt));
        }

        viewer.InsertBlankPages(destination, chosen, (int)Math.Round(count.Value));
    }

    private async Task ExtractAsync(PdfTiledViewer viewer, IReadOnlyList<int> sheets)
    {
        if (sheets.Count == 0 || viewer.SourcePath is not { } source) return;

        string stem = Path.GetFileNameWithoutExtension(source);
        var file = await PickPdfDestinationAsync(sheets.Count == 1
            ? $"{stem} hoja {sheets[0] + 1}"
            : $"{stem} {sheets.Count} hojas");
        if (file is null) return;

        // Writing over the document it is reading would pull the file out from
        // under the open handle, and the result would be neither document.
        if (IsSamePath(file.Path, source))
        {
            await ShowMessageAsync(
                "Ese es el documento abierto",
                "Las hojas extraídas necesitan un archivo propio. Elige otro nombre.");
            return;
        }

        using (BusyScope("Extrayendo hojas…"))
        {
            try
            {
                await viewer.ExtractPagesAsync(sheets, file.Path);
                Hint($"{Sheets(sheets.Count)} en «{file.Name}».");
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("No se pudieron extraer las hojas", ex.Message);
            }
        }
    }

    /// <summary>
    /// Cuts the document into files of so many sheets each. The originals are
    /// not touched: this writes new files beside each other in a folder the
    /// reader picks, which is what makes it safe to try.
    /// </summary>
    private async Task SplitAsync(PdfTiledViewer viewer)
    {
        if (viewer.SourcePath is not { } source || viewer.PageCount < 2) return;

        var every = new NumberBox
        {
            Value = 1,
            Minimum = 1,
            Maximum = viewer.PageCount - 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var body = new StackPanel { Spacing = 12, Width = 380 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"El documento tiene {viewer.PageCount} hojas. Se escribirán archivos nuevos; "
                   + "este no se toca.",
        });
        body.Children.Add(Labelled("Hojas por archivo", every));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Dividir el documento",
            Content = body,
            PrimaryButtonText = "Elegir carpeta…",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        AppTheme.Dress(dialog);
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        int chunk = Math.Max(1, (int)Math.Round(every.Value));

        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        string stem = Path.GetFileNameWithoutExtension(source);
        int written = 0;

        using (BusyScope("Dividiendo el documento…"))
        {
            try
            {
                for (int first = 0; first < viewer.PageCount; first += chunk)
                {
                    int last = Math.Min(first + chunk, viewer.PageCount);
                    var sheets = Enumerable.Range(first, last - first).ToList();

                    string name = chunk == 1
                        ? $"{stem} {first + 1:D3}.pdf"
                        : $"{stem} {first + 1:D3}-{last:D3}.pdf";

                    await viewer.ExtractPagesAsync(sheets, Path.Combine(folder.Path, name));
                    written++;
                }

                Hint($"{written} archivos en «{folder.Name}».");
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("No se pudo dividir el documento", ex.Message);
            }
        }
    }

    private static string Sheets(int count) => count == 1 ? "Una hoja" : $"{count} hojas";

    /// <summary>
    /// Says what just happened, in the status bar, and then gets out of the
    /// way. Writing files somewhere the reader is not looking deserves an
    /// answer; it does not deserve a dialog to dismiss.
    /// </summary>
    private void Hint(string message)
    {
        _hintMessage = message;
        UpdateChrome();

        _ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
        {
            // Only if it is still ours: a tool picked up in the meantime has
            // its own thing to say, and it outranks this.
            if (!ReferenceEquals(_hintMessage, message)) return;

            _hintMessage = null;
            UpdateChrome();
        }));
    }

    /// <summary>
    /// A rearrangement is the one pending change that costs the file something
    /// beyond what it says: the bytes a signature covers are no longer these
    /// bytes, so every signature the drawing carried stops checking out. Said
    /// before the write, because after it there is nothing to decide.
    /// </summary>
    private async Task<bool> ConfirmRearrangementAsync(PdfTiledViewer viewer)
    {
        if (!viewer.HasPageChanges) return true;

        int signatures = viewer.ReadSignatures().Count;
        if (signatures == 0) return true;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Este documento está firmado",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = signatures == 1
                    ? "Cambiar las hojas de sitio deja sin validez la firma que trae el documento: "
                      + "una firma cubre el archivo tal y como estaba. Se puede volver a firmar después."
                    : $"Cambiar las hojas de sitio deja sin validez las {signatures} firmas que trae el documento: "
                      + "una firma cubre el archivo tal y como estaba. Se puede volver a firmar después.",
            },
            PrimaryButtonText = "Guardar igualmente",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        };

        AppTheme.Dress(dialog);
        return await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
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
                + "que traía el archivo de otras herramientas.\n\nEsto no se puede deshacer: "
                + "si aplanas sobre este archivo, no hay vuelta atrás. Aplanar en una copia "
                + "deja este documento como está, con sus marcas todavía editables.",
            PrimaryButtonText = "Aplanar y guardar",
            SecondaryButtonText = "Aplanar en una copia…",
            CloseButtonText = "Cancelar",
            // The way out is the default: pressing Enter without reading should
            // land on the choice that keeps a version with live marks, not on
            // the one that cannot be undone.
            DefaultButton = ContentDialogButton.Secondary,
        };

        AppTheme.Dress(dialog);

        switch (await Dialogs.ShowAsync(dialog))
        {
            case ContentDialogResult.Primary:
                using (BusyScope("Aplanando las marcas…"))
                {
                    string? error = await viewer.FlattenAsync();
                    if (error is not null)
                    {
                        await ShowMessageAsync("No se pudieron aplanar las marcas", error);
                    }
                }
                break;

            case ContentDialogResult.Secondary:
                await FlattenToCopyAsync(viewer);
                break;

            default:
                return;
        }

        UpdateChrome();
    }

    /// <summary>
    /// Burns the marks into a copy. The document in hand is not touched, which
    /// is the whole reason this exists next to the flatten that cannot be undone.
    /// </summary>
    private async Task FlattenToCopyAsync(PdfTiledViewer viewer)
    {
        if (viewer.SourcePath is not { } source) return;

        var file = await PickPdfDestinationAsync($"{Path.GetFileNameWithoutExtension(source)} aplanado");
        if (file is null) return;

        if (IsSamePath(file.Path, source))
        {
            // Aiming the copy at the original is the flatten that cannot be
            // undone, and it should not happen by accident through this door.
            await ShowMessageAsync(
                "Elige otro archivo",
                "Ese es el documento que estás mirando. Para aplanarlo sobre sí mismo, usa «Aplanar y guardar».");
            return;
        }

        using (BusyScope("Aplanando en una copia…"))
        {
            try
            {
                await viewer.SaveChangesCopyAsync(file.Path, flatten: true);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("No se pudo aplanar en una copia", ex.Message);
            }
        }
    }

    /// <summary>Asks where a copy should go. The name is a suggestion, nothing more.</summary>
    private static async Task<StorageFile?> PickPdfDestinationAsync(string suggestedName)
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));
        picker.FileTypeChoices.Add("Documento PDF", [".pdf"]);
        picker.SuggestedFileName = suggestedName;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        return await picker.PickSaveFileAsync();
    }

    private async void OnSaveCopyClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveViewer is not { } viewer || viewer.SourcePath is not { } source) return;

        // The document's own name: this saves whatever the document carries,
        // and naming it after one kind of change would go stale the moment
        // there is another.
        var file = await PickPdfDestinationAsync(Path.GetFileNameWithoutExtension(source));
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

    /// <summary>
    /// Signing the plan with a certificate.
    ///
    /// A signature is appended, never written over the drawing, so a signature
    /// that came with the plan stays valid and this one can sit on top of it.
    /// That is the whole reason the app can offer signing at all — see
    /// <see cref="PdfSignatures"/>.
    /// </summary>
    private async void OnSignClicked(object sender, RoutedEventArgs e) => await AskAndSignAsync();

    private async Task AskAndSignAsync()
    {
        if (ActiveViewer is not { } viewer || viewer.SourcePath is not { } source) return;

        var certificates = PdfSignatures.AvailableCertificates();
        if (certificates.Count == 0)
        {
            var nothing = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "No hay ningún certificado",
                Content = "Windows no encuentra ningún certificado con el que firmar. "
                        + "Si tienes el tuyo en un archivo —el de la FNMT viene en un .pfx— "
                        + "puedes instalarlo ahora: lo abre el asistente de Windows, que es "
                        + "quien te pide la contraseña.",
                PrimaryButtonText = "Importar un certificado…",
                SecondaryButtonText = "Poner solo un sello",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary,
            };
            AppTheme.Dress(nothing);

            var without = await Dialogs.ShowAsync(nothing);

            if (without == ContentDialogResult.Primary && await ImportCertificateAsync())
            {
                await AskAndSignAsync();
            }
            else if (without == ContentDialogResult.Secondary)
            {
                // Nothing to sign with, and a drawing that still has to say who
                // reviewed it. The stamp is offered here rather than hidden
                // behind importing a certificate the reader may not have.
                await AskForStampAsync(viewer);
            }
            return;
        }

        // The name alone in the list, and who issued it underneath: a name plus
        // an expiry date on one line is longer than the box, and what got cut
        // off was the year.
        var picker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0,
            ItemsSource = certificates
                .Select(c => c.GetNameInfo(X509NameType.SimpleName, false))
                .ToList(),
        };

        var issuer = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
        var who = new TextBox { PlaceholderText = "Nombre y apellidos" };
        var papers = new TextBox { PlaceholderText = "DNI" };

        // The name and the identity number come out of the certificate, and stay
        // editable: what a certificate calls someone — surnames first, all in
        // capitals — is not always what belongs on a drawing.
        void DescribeChosen()
        {
            var chosen = certificates[Math.Max(0, picker.SelectedIndex)];
            issuer.Text = $"Emitido por {chosen.GetNameInfo(X509NameType.SimpleName, forIssuer: true)}"
                        + $" · caduca el {chosen.NotAfter:dd/MM/yyyy}";

            var (name, id) = IdentityIn(chosen);
            who.Text = name;
            papers.Text = id;
        }

        picker.SelectionChanged += (_, _) => DescribeChosen();
        DescribeChosen();

        // Empty by default: a reason is a claim about why this was signed, and
        // one that was never typed is worse than none at all.
        var reason = new TextBox { PlaceholderText = "Motivo de la firma" };
        var heading = new TextBox { Text = "Firmado digitalmente por" };
        var visible = new CheckBox { Content = "Ponerla a la vista sobre el documento", IsChecked = true };
        var withDate = new CheckBox { Content = "Poner la fecha", IsChecked = true };
        var importer = new HyperlinkButton { Content = "Importar o añadir un certificado…", Padding = new Thickness(0) };

        // The way out for a drawing that is being reviewed rather than issued.
        // It is offered here, beside the real thing, and named for what it is:
        // the reader should not have to discover halfway through that what they
        // put on the sheet proves nothing.
        var justStamp = new HyperlinkButton
        {
            Content = "Poner solo un sello, sin firma digital…",
            Padding = new Thickness(0),
        };

        // The timestamp. Off unless the reader turns it on, and with nowhere to
        // ask until they say where: whose clock a signature leans on is their
        // decision, and a default authority here would be a stranger vouching
        // for every drawing they sign.
        var timestamp = new CheckBox
        {
            Content = "Sellar la hora con una autoridad (TSA)",
            IsChecked = TimestampSetting.Wanted,
        };

        var authority = new TextBox
        {
            PlaceholderText = "https://…",
            Text = TimestampSetting.Url,
            Visibility = timestamp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed,
        };

        var authorityWhy = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
            Text = "Con sello, la firma sigue valiendo cuando el certificado caduque. "
                 + "Sale a internet: viaja un resumen de la firma, ni el plano ni quién firma.",
            Visibility = authority.Visibility,
        };

        // The proof the drawing carries with it. Its own switch, because it is
        // its own question — and because a reader may want the time vouched for
        // without asking every authority in the chain about every certificate.
        var validation = new CheckBox
        {
            Content = "Guardar los datos de validación en el archivo",
            IsChecked = TimestampSetting.KeepValidationData,
        };

        var validationWhy = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
            Text = "Para poder comprobar la firma dentro de años sin preguntarle a nadie. "
                 + "Pregunta ahora a las autoridades que nombra tu certificado; si no contestan, "
                 + "la firma se hace igual y se queda sin ellos.",
            Visibility = validation.IsChecked == true ? Visibility.Visible : Visibility.Collapsed,
        };

        validation.Checked += (_, _) => validationWhy.Visibility = Visibility.Visible;
        validation.Unchecked += (_, _) => validationWhy.Visibility = Visibility.Collapsed;

        void ShowAuthority()
        {
            var showing = timestamp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            authority.Visibility = showing;
            authorityWhy.Visibility = showing;
        }

        timestamp.Checked += (_, _) => ShowAuthority();
        timestamp.Unchecked += (_, _) => ShowAuthority();

        var stampFields = new StackPanel { Spacing = 6 };

        // What the stamp will say, kept in step as it is typed.
        //
        // It is here because it replaces a row of switches. There used to be one
        // tick per line, which is two things to understand — the field and the
        // switch — and they could disagree. Now an empty field is a line that
        // does not appear, and this shows that without having to be explained.
        var sample = new StackPanel { Spacing = 2 };
        var preview = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 76, 107, 158)),
            Child = sample,
        };

        void Redraw()
        {
            var stamp = new PdfSignatureAppearance(
                who.Text.Trim(), papers.Text.Trim(), heading.Text.Trim(), withDate.IsChecked == true);

            sample.Children.Clear();
            foreach (var (text, strong) in PdfSignatureStamp.Lines(
                         stamp, reason.Text.Trim(), "", DateTimeOffset.Now))
            {
                sample.Children.Add(new TextBlock
                {
                    Text = text,
                    FontWeight = strong ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            bool showing = visible.IsChecked == true;
            preview.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
            stampFields.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var field in new[] { who, papers, reason, heading })
        {
            field.TextChanged += (_, _) => Redraw();
        }
        withDate.Checked += (_, _) => Redraw();
        withDate.Unchecked += (_, _) => Redraw();
        visible.Checked += (_, _) => Redraw();
        visible.Unchecked += (_, _) => Redraw();

        bool wantsImport = false;

        var body = new StackPanel { Spacing = 8, Width = 460 };

        // Only said when there is something to say. «This document carries no
        // signatures yet» is a line that costs two rows to tell the reader
        // nothing they will act on.
        var already = viewer.ReadSignatures();
        if (already.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = DescribeSignatures(already),
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        body.Children.Add(new TextBlock { Text = "Firmar con" });
        body.Children.Add(picker);
        body.Children.Add(issuer);
        body.Children.Add(importer);
        body.Children.Add(justStamp);
        body.Children.Add(timestamp);
        body.Children.Add(authority);
        body.Children.Add(authorityWhy);
        body.Children.Add(validation);
        body.Children.Add(validationWhy);
        body.Children.Add(visible);

        stampFields.Children.Add(Labelled("Encabezado", heading));

        // Name and identity number share a row: they are short, they belong
        // together, and the dialog is already as tall as a laptop screen holds.
        var identity = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(150) } },
        };
        var namePair = Labelled("Nombre", who);
        var idPair = Labelled("DNI", papers);
        Grid.SetColumn(idPair, 1);
        identity.Children.Add(namePair);
        identity.Children.Add(idPair);

        stampFields.Children.Add(identity);
        stampFields.Children.Add(Labelled("Motivo", reason));
        stampFields.Children.Add(withDate);
        stampFields.Children.Add(new TextBlock
        {
            Opacity = 0.75,
            Margin = new Thickness(0, 2, 0, 2),
            Text = "Lo que dejes en blanco no aparece:",
        });
        stampFields.Children.Add(preview);

        body.Children.Add(stampFields);

        if (viewer.HasUnsavedChanges)
        {
            // A signature covers the file, so anything still only on screen has
            // to be written first — and the reader should know that before the
            // signing, not after.
            body.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Text = "Hay cambios sin guardar. Se guardarán antes de firmar: "
                     + "una firma cubre el archivo, y lo que no esté escrito no queda firmado.",
            });
        }

        // Signing a copy is the primary way out. A signature cannot be taken
        // back and the original often has to survive it — for a second signer,
        // for a correction — so the safe one is the one under the cursor, and
        // the dialog says the name it will use rather than springing it.
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 4, 0, 0),
            Text = $"La copia: «{Path.GetFileName(SignedCopyPath(source))}», junto al original. "
                 + "Después dibujarás dónde va la firma.",
        });

        Redraw();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Firmar el documento",
            Content = new ScrollViewer { Content = body, MaxHeight = 560, HorizontalContentAlignment = HorizontalAlignment.Stretch },
            PrimaryButtonText = "Firmar una copia",
            SecondaryButtonText = "Firmar este archivo",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        // Importing needs the dialog out of the way — Windows' wizard is its own
        // window, and the list of certificates has to be read again afterwards.
        importer.Click += (_, _) => { wantsImport = true; dialog.Hide(); };

        bool wantsStamp = false;
        justStamp.Click += (_, _) => { wantsStamp = true; dialog.Hide(); };

        AppTheme.Dress(dialog);

        var answer = await Dialogs.ShowAsync(dialog);

        if (wantsImport)
        {
            await ImportCertificateAsync();
            await AskAndSignAsync();
            return;
        }

        if (wantsStamp)
        {
            await StampWithoutSigningAsync(
                viewer,
                new PdfSignatureAppearance(
                    who.Text.Trim(), papers.Text.Trim(), StampHeading(heading.Text), withDate.IsChecked == true),
                reason.Text.Trim());
            return;
        }

        if (answer == ContentDialogResult.None) return;

        var certificate = certificates[Math.Max(0, picker.SelectedIndex)];

        // Remembered whatever the reader does next, including cancelling: it is
        // a preference about how they sign, not part of this signature.
        TimestampSetting.Url = authority.Text;
        TimestampSetting.Wanted = timestamp.IsChecked == true;
        TimestampSetting.KeepValidationData = validation.IsChecked == true;

        ITimestamper? clock = TimestampSetting.Wanted ? new HttpTimestamper(TimestampSetting.Url) : null;

        var signer = new CertificateSigner(certificate, null, clock);
        var options = new PdfSignatureOptions(Reason: reason.Text.Trim());

        if (visible.IsChecked == true)
        {
            var stamp = new PdfSignatureAppearance(
                who.Text.Trim(), papers.Text.Trim(), heading.Text.Trim(), withDate.IsChecked == true);

            if (await PlaceSignatureAsync(viewer, stamp, options.Reason) is not { } placed) return;
            options = placed with { Reason = options.Reason };
        }

        if (answer == ContentDialogResult.Secondary)
        {
            await SignInPlaceAsync(viewer, signer, options);
        }
        else
        {
            await SignSignedCopyAsync(viewer, source, signer, options);
        }

        UpdateChrome();
    }

    /// <summary>A label over its field, which is the shape every field in this dialog takes.</summary>
    private static StackPanel Labelled(string label, FrameworkElement field)
    {
        var pair = new StackPanel { Spacing = 2 };
        pair.Children.Add(new TextBlock { Text = label });
        pair.Children.Add(field);
        return pair;
    }

    /// <summary>
    /// Where a signed copy goes: beside the original, with «signed» on the end.
    /// A number is added rather than a file overwritten — a signed document is
    /// not something to quietly replace with another signed document.
    /// </summary>
    private static string SignedCopyPath(string source)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(source)) ?? "";
        string stem = Path.GetFileNameWithoutExtension(source);

        string candidate = Path.Combine(directory, $"{stem} signed.pdf");
        for (int n = 2; File.Exists(candidate); n++)
        {
            candidate = Path.Combine(directory, $"{stem} signed {n}.pdf");
        }
        return candidate;
    }

    /// <summary>
    /// Signs into «… signed.pdf» beside the original and opens it, so the
    /// signature can be seen rather than taken on trust.
    /// </summary>
    private async Task SignSignedCopyAsync(
        PdfTiledViewer viewer, string source, IPdfSigner signer, PdfSignatureOptions options)
    {
        string target = SignedCopyPath(source);

        using (BusyScope("Firmando una copia…"))
        {
            try
            {
                await viewer.SignCopyAsync(target, signer, options);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("No se pudo firmar la copia", ex.Message);
                return;
            }
        }

        await AddValidationDataAsync(target);
        await OpenPathInNewTabAsync(target, Path.GetFileName(target));
    }

    /// <summary>
    /// Puts the proof that the certificates were good into the signed file, if
    /// the reader asked for it.
    ///
    /// It runs after the signature and never instead of it: the drawing is
    /// already signed by the time this is tried, so a responder that is down
    /// costs the proof and not the signature. That is why a failure here is a
    /// hint and not an error box — what the reader asked for happened.
    /// </summary>
    private async Task AddValidationDataAsync(string signedPath)
    {
        if (!TimestampSetting.KeepValidationData) return;

        string staged = signedPath + ".ltv";

        try
        {
            using (BusyScope("Guardando los datos de validación…"))
            {
                int answers = await Task.Run(
                    () => PdfSignatures.AddValidationData(signedPath, staged, new HttpRevocationSource()));

                if (answers == 0 && !File.Exists(staged))
                {
                    Hint("No se pudo preguntar por los certificados: la firma queda sin datos de validación.");
                    return;
                }

                File.Move(staged, signedPath, overwrite: true);
                Hint($"Firma con datos de validación: {answers} respuesta(s) de las autoridades.");
            }
        }
        catch (Exception ex)
        {
            TryDeleteFile(staged);
            Hint($"La firma está hecha, pero sin datos de validación: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Asks the reader to drag out where the stamp goes, and turns what they
    /// drew into the page's own coordinates.
    ///
    /// The conversion is not arithmetic that can be done here: a sheet with a
    /// /Rotate, or a crop box that does not start at the origin, needs the
    /// page's own matrix, and that lives behind the render queue.
    /// </summary>
    /// <summary>
    /// The stamp with no signature under it: the same box, drawn as a mark.
    ///
    /// It is not a lesser signature, it is a different thing — nothing is
    /// sealed, nothing can be verified, and anybody with a PDF editor can move
    /// it or take it off. What it is for is the drawing that is being reviewed
    /// rather than issued, where what matters is that the sheet says who looked
    /// at it.
    /// </summary>
    /// <summary>Puts a stamp on the drawing in view, without going near a certificate.</summary>
    private async Task StampHereAsync()
    {
        if (ActiveViewer is { PageCount: > 0 } viewer)
        {
            await AskForStampAsync(viewer);
        }
    }

    /// <summary>
    /// Asks what the stamp should say, for the reader who has no certificate —
    /// or does not want one on this drawing.
    /// </summary>
    private async Task AskForStampAsync(PdfTiledViewer viewer)
    {
        var who = new TextBox { PlaceholderText = "Nombre y apellidos" };
        var papers = new TextBox { PlaceholderText = "DNI" };
        var reason = new TextBox { PlaceholderText = "Motivo, si hace falta" };
        var heading = new TextBox { Text = "Revisado por" };
        var withDate = new CheckBox { Content = "Poner la fecha", IsChecked = true };

        var body = new StackPanel { Spacing = 8, Width = 420 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Un sello es una marca sobre el dibujo: dice quién lo ha visto, pero no lo firma. "
                 + "No queda constancia criptográfica y cualquiera puede moverlo o quitarlo.",
        });

        body.Children.Add(Labelled("Encabezado", heading));
        body.Children.Add(Labelled("Nombre", who));
        body.Children.Add(Labelled("DNI", papers));
        body.Children.Add(Labelled("Motivo", reason));
        body.Children.Add(withDate);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Poner un sello",
            Content = body,
            PrimaryButtonText = "Colocarlo",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        AppTheme.Dress(dialog);

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        await StampWithoutSigningAsync(
            viewer,
            new PdfSignatureAppearance(
                who.Text.Trim(), papers.Text.Trim(), StampHeading(heading.Text), withDate.IsChecked == true),
            reason.Text.Trim());
    }

    /// <summary>
    /// The heading a stamp with nothing under it may carry.
    ///
    /// "Firmado digitalmente por" is a claim about cryptography, and a box that
    /// makes it without any is the one way this feature could deceive the
    /// person receiving the drawing. Anything else the reader typed is theirs
    /// to keep — this only refuses to repeat the one sentence that would be a
    /// lie.
    /// </summary>
    private static string StampHeading(string typed)
    {
        string heading = typed.Trim();

        return heading.Contains("digital", StringComparison.OrdinalIgnoreCase)
            ? "Firmado por"
            : heading;
    }

    private async Task StampWithoutSigningAsync(
        PdfTiledViewer viewer, PdfSignatureAppearance stamp, string reason)
    {
        var lines = PdfSignatureStamp.Lines(stamp, reason, "", DateTimeOffset.Now);
        if (await PlaceStampAsync(viewer, lines, forSigning: false) is not { } placed) return;

        viewer.PlaceStamp(placed.Page, placed.Box, string.Join('\n', lines.Select(line => line.Text)));
        Hint("Sello puesto. Es una marca: se mueve, se cambia y se borra como cualquier otra.");
        UpdateChrome();
    }

    private async Task<PdfSignatureOptions?> PlaceSignatureAsync(
        PdfTiledViewer viewer, PdfSignatureAppearance stamp, string reason)
    {
        var lines = PdfSignatureStamp.Lines(stamp, reason, "", DateTimeOffset.Now);

        if (await PlaceStampAsync(viewer, lines) is not { } placed) return null;

        var (rect, turns) = await viewer.ToPdfRectAsync(placed.Page, placed.Box);

        return new PdfSignatureOptions(
            PageIndex: placed.Page,
            Rectangle: rect,
            PageQuarterTurns: turns,
            Appearance: stamp);
    }

    /// <summary>
    /// Drags out where a stamp goes and hands back the box in sheet points —
    /// the space marks live in. Both the signature and the plain stamp go
    /// through it, so the two are placed by the same gesture and land in the
    /// same place.
    /// </summary>
    private async Task<(int Page, RectPt Box)?> PlaceStampAsync(
        PdfTiledViewer viewer, IReadOnlyList<(string Text, bool Strong)> lines, bool forSigning = true)
    {
        // The strip says what pressing it will do. A stamp that offered
        // "Firmar aquí" would be promising a signature it is not going to make.
        SignStripLabel.Text = forSigning
            ? "Arrastra la firma para moverla"
            : "Arrastra el sello para moverlo";
        SignConfirmButton.Content = forSigning ? "Firmar aquí" : "Ponerlo aquí";
        SignRedrawButton.Content = forSigning ? "Dibujarla otra vez" : "Dibujarlo otra vez";
        SignDropButton.Content = forSigning ? "Quitarla" : "Quitarlo";

        while (true)
        {
            _hintMessage = forSigning
                ? "Dibuja un rectángulo para colocar la firma · Esc para dejarlo"
                : "Dibuja un rectángulo para colocar el sello · Esc para dejarlo";
            UpdateChrome();

            var spot = await AskForSpotAsync(viewer);

            _hintMessage = null;
            UpdateChrome();

            if (spot is null) return null;

            // Placed, not written. From here the reader can drag it, draw it
            // again somewhere else, or throw it away — and nothing has touched
            // the file yet.
            viewer.ShowPendingSignature(new PendingSignature(spot.PageIndex, spot.SheetRect, lines));
            SignatureStrip.Visibility = Visibility.Visible;

            var decision = await WaitForSignatureDecisionAsync();
            var placed = viewer.Pending;

            SignatureStrip.Visibility = Visibility.Collapsed;
            viewer.ClearPendingSignature();

            if (decision == SignatureDecision.Drop || placed is null) return null;
            if (decision == SignatureDecision.Redraw) continue;

            return (placed.PageIndex, placed.SheetRect);
        }
    }

    private enum SignatureDecision { Sign, Redraw, Drop }

    /// <summary>Set while the strip is up; the three buttons answer through it.</summary>
    private TaskCompletionSource<SignatureDecision>? _signatureDecision;

    private Task<SignatureDecision> WaitForSignatureDecisionAsync()
    {
        _signatureDecision = new TaskCompletionSource<SignatureDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        return _signatureDecision.Task;
    }

    private void OnSignConfirmClicked(object sender, RoutedEventArgs e) =>
        _signatureDecision?.TrySetResult(SignatureDecision.Sign);

    private void OnSignRedrawClicked(object sender, RoutedEventArgs e) =>
        _signatureDecision?.TrySetResult(SignatureDecision.Redraw);

    private void OnSignDropClicked(object sender, RoutedEventArgs e) =>
        _signatureDecision?.TrySetResult(SignatureDecision.Drop);

    private static Task<SignatureSpot?> AskForSpotAsync(PdfTiledViewer viewer)
    {
        var waiting = new TaskCompletionSource<SignatureSpot?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Placed(object? sender, SignatureSpot? spot)
        {
            viewer.SignaturePlaced -= Placed;
            waiting.TrySetResult(spot);
        }

        viewer.SignaturePlaced += Placed;
        viewer.IsPlacingSignature = true;
        return waiting.Task;
    }

    /// <summary>
    /// Hands a certificate file to Windows' own import wizard.
    ///
    /// Deliberately not done here: a .pfx is opened with a password, and ZenInk
    /// has no business asking for one, holding one, or being the thing that gets
    /// it wrong. The wizard asks, and the key lands in the user's store where
    /// signing can reach it without anyone seeing it.
    /// </summary>
    private static async Task<bool> ImportCertificateAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        foreach (string kind in new[] { ".pfx", ".p12", ".cer", ".crt", ".p7b" })
        {
            picker.FileTypeFilter.Add(kind);
        }

        var file = await picker.PickSingleFileAsync();
        if (file is null) return false;

        string entry = Path.GetExtension(file.Path).ToLowerInvariant() switch
        {
            ".pfx" or ".p12" => "CryptExtAddPFX",
            ".p7b" => "CryptExtAddPKCS7",
            _ => "CryptExtAddCER",
        };

        try
        {
            using var wizard = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"cryptext.dll,{entry} \"{file.Path}\"",
                UseShellExecute = true,
            });

            // Waiting matters: the list of certificates is read again straight
            // after, and reading it while the wizard is still open would find
            // the store exactly as it was.
            if (wizard is not null) await wizard.WaitForExitAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The name and identity number a certificate carries. The FNMT writes the
    /// DNI twice — in SERIALNUMBER as <c>IDCES-00000000X</c>, and again on the
    /// end of the common name — and other issuers write neither, so both are
    /// tried and neither is required.
    /// </summary>
    private static (string Name, string Id) IdentityIn(X509Certificate2 certificate)
    {
        string simple = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

        var serial = System.Text.RegularExpressions.Regex.Match(
            certificate.Subject, @"SERIALNUMBER=(?:IDC[A-Z]{2}-)?([0-9A-Za-z]+)");
        string id = serial.Success ? serial.Groups[1].Value : "";

        var trailing = System.Text.RegularExpressions.Regex.Match(simple, @"^(.*?)\s*-\s*(\d{7,8}[A-Za-z])$");
        if (!trailing.Success) return (simple, id);

        return (trailing.Groups[1].Value.Trim(), id.Length > 0 ? id : trailing.Groups[2].Value);
    }

    private async Task SignInPlaceAsync(PdfTiledViewer viewer, IPdfSigner signer, PdfSignatureOptions options)
    {
        using (BusyScope("Firmando…"))
        {
            if (viewer.HasUnsavedChanges && await viewer.SaveChangesAsync() is { } failure)
            {
                await ShowMessageAsync("No se pudo guardar antes de firmar", failure);
                return;
            }

            var proof = TimestampSetting.KeepValidationData ? new HttpRevocationSource() : null;

            if (await viewer.SignAsync(signer, options, proof) is { } error)
            {
                await ShowMessageAsync("No se pudo firmar el documento", error);
            }
        }
    }

    /// <summary>
    /// What the plan already carries. An earlier signature covering only part of
    /// the file is normal and not a fault: it covers the document as it stood
    /// when it was signed. What matters is whether it still adds up.
    /// </summary>
    private static string DescribeSignatures(IReadOnlyList<PdfSignatureInfo> signatures)
    {
        if (signatures.Count == 0)
        {
            return "Este documento no lleva ninguna firma todavía.";
        }

        var lines = signatures.Select(s =>
            $"· {(s.Signer.Length > 0 ? s.Signer : "firmante desconocido")}"
            + (s.SignedAt is { } when ? $", {when:d} {when:t}" : "")
            // The time an authority vouched for outranks the one the signer's
            // own clock claimed, so it is the one shown when there is one.
            + (s.Timestamp is { } stamp
                ? $" · sellada {stamp.Stamped:d} {stamp.Stamped:t}"
                  + (stamp.CoversSignature ? "" : " (el sello no es de esta firma)")
                : "")
            + (s.DigestMatches ? "" : " — NO cuadra con el archivo"));

        string heading = signatures.Count == 1
            ? "Este documento ya lleva una firma:"
            : $"Este documento ya lleva {signatures.Count} firmas:";

        return $"{heading}\n{string.Join("\n", lines)}\n\nLa tuya se añade encima sin tocarlas.";
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
    /// Hands the drawing to Windows' print pane, arranged the way it is on
    /// screen. Sheets moved and sheets turned still print before they are
    /// saved: what is on paper should be what is on screen, and being made to
    /// save first would be a strange price for a printout.
    /// </summary>
    private async Task PrintAsync()
    {
        if (ActiveViewer is not { } viewer || viewer.SourcePath is null) return;
        if (viewer.PageCount == 0) return;

        string title = (Tabs.SelectedItem as TabViewItem)?.Header as string ?? "Documento";
        PdfPrintSource? source = null;

        try
        {
            using (BusyScope("Preparando la impresión…"))
            {
                var job = await PdfPrintJob.OpenAsync(viewer.Plan);
                // Marks print whether or not they have been saved, for the same
                // reason unsaved turns do: the paper should be what is on screen.
                job.Marks = viewer.Annotations.Snapshot();

                // A comparison prints as the comparison. What goes to the
                // meeting has to be the picture that was on screen.
                job.Overlay = viewer.OverlayFor;

                // What each sheet is drawn to, which is what lets the dialog
                // offer 1:100 at all. Sheets that were never calibrated simply
                // are not in it, and the dialog does not promise them a scale.
                if (viewer.Scales() is { Count: > 0 } scales)
                {
                    job.Configure(job.Settings with { Scales = scales }, job.Paper);
                }
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

    // --- comparar revisiones -----------------------------------------------
    //
    // Un plano no se lee entero cada vez: se lee qué cambió entre la revisión J
    // y la K. La revisión se abre aparte y se dibuja dentro de los mismos
    // tiles, así que moverse, ampliar y capturar siguen siendo lo de siempre.

    private async void OnCompareOpenClicked(object sender, RoutedEventArgs e) => await PickRevisionAsync();

    private async Task PickRevisionAsync()
    {
        if (ActiveViewer is not { } viewer || viewer.PageCount == 0) return;

        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));
        picker.FileTypeFilter.Add(".pdf");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            using (BusyScope("Abriendo la revisión…"))
            {
                await viewer.CompareWithAsync(file.Path, file.Name);
            }

            RibbonTabs.SelectedItem = TabComparar;
            Hint($"Comparando con {file.Name}");
        }
        catch (PdfPasswordRequiredException)
        {
            // Asked for rather than guessed at: the revision is someone else's
            // file as often as not, and a locked one is a normal thing to meet.
            if (await AskForPasswordAsync(file.Name, wasWrong: false) is not { } password) return;

            try
            {
                using (BusyScope("Abriendo la revisión…"))
                {
                    await viewer.CompareWithAsync(file.Path, file.Name, password);
                }

                RibbonTabs.SelectedItem = TabComparar;
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(file.Name, ex);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(file.Name, ex);
        }
        finally
        {
            UpdateChrome();
        }
    }

    private void OnCompareStopClicked(object sender, RoutedEventArgs e)
    {
        ActiveViewer?.StopComparing();
        UpdateChrome();
    }

    private void OnNextChangeClicked(object sender, RoutedEventArgs e) => StepChange(1);

    private void OnPreviousChangeClicked(object sender, RoutedEventArgs e) => StepChange(-1);

    private void OnNextChangeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = ActiveViewer?.IsComparing == true;
        if (args.Handled) StepChange(1);
    }

    private void OnPreviousChangeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = ActiveViewer?.IsComparing == true;
        if (args.Handled) StepChange(-1);
    }

    private void StepChange(int direction)
    {
        ActiveViewer?.StepChange(direction);
        UpdateChrome();
    }

    private void OnComparePairForwardClicked(object sender, RoutedEventArgs e) => ShiftPairing(1);

    private void OnComparePairBackClicked(object sender, RoutedEventArgs e) => ShiftPairing(-1);

    private void OnComparePairResetClicked(object sender, RoutedEventArgs e) => SetPairing(0);

    private void ShiftPairing(int by)
    {
        if (ActiveViewer is not { } viewer) return;

        SetPairing(viewer.ComparePageOffset + by);
    }

    private void SetPairing(int offset)
    {
        if (ActiveViewer is not { } viewer || !viewer.IsComparing) return;

        viewer.ComparePageOffset = offset;
        UpdateChrome();
    }

    private void OnCompareFitClicked(object sender, RoutedEventArgs e) => SetCompareFit(CompareFit.Fit);

    private void OnCompareStretchClicked(object sender, RoutedEventArgs e) => SetCompareFit(CompareFit.Stretch);

    private void SetCompareFit(CompareFit fit)
    {
        if (ActiveViewer is not { } viewer) return;

        viewer.CompareFit = fit;
        UpdateChrome();
    }

    private void OnCompareSwapClicked(object sender, RoutedEventArgs e)
    {
        ActiveViewer?.SwapCompareColours();
        UpdateChrome();
    }

    private void OnCompareCommonChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingPanel || ActiveViewer is not { } viewer || !viewer.IsComparing) return;

        viewer.ComparePalette = viewer.ComparePalette with { Common = (byte)Math.Clamp(e.NewValue, 0, 255) };
        UpdateChrome();
    }

    private void OnViewerComparisonChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, ActiveViewer)) return;

        UpdateChrome();
    }

    /// <summary>
    /// The comparison's half of the chrome. Everything here reads from the
    /// viewer rather than being kept in step by hand, so a comparison set up
    /// from the palette or ended by closing the document says the same thing as
    /// one set up from the ribbon.
    /// </summary>
    private void UpdateCompareChrome(PdfTiledViewer? viewer, ViewerTool tool, bool interactive)
    {
        bool comparing = interactive && viewer is { IsComparing: true };

        // The header goes on naming the active tool whenever there is one with
        // options — that is what makes the ribbon safe to use. It is only when
        // no tool owns the panel that the comparison takes the title.
        bool ownsHeader = comparing && !tool.IsAnnotation()
            && tool != ViewerTool.SelectText && tool != ViewerTool.Calibrate;
        PropertiesIconCompare.Visibility = ownsHeader ? Visibility.Visible : Visibility.Collapsed;
        if (ownsHeader)
        {
            PropertiesIconText.Visibility = Visibility.Collapsed;
            PropertiesIconMark.Visibility = Visibility.Collapsed;
            PropertiesTitle.Text = "Comparación";
        }

        CompareOpenButton.IsEnabled = interactive;
        CompareStopButton.IsEnabled = comparing;
        ComparePairButton.IsEnabled = comparing;
        CompareFitButton.IsEnabled = comparing;
        CompareSwapButton.IsEnabled = comparing;

        int changes = comparing ? viewer!.ChangeCount : 0;
        PreviousChangeButton.IsEnabled = changes > 0;
        NextChangeButton.IsEnabled = changes > 0;

        if (!comparing)
        {
            ChangeIndicator.Text = string.Empty;
            ComparePairLabel.Text = "Emparejar";
            return;
        }

        // "Sin cambios" and "still looking" are different answers, and saying
        // the first while the sweep is running is the one way this feature can
        // lie outright.
        //
        // While it looks, it says what it is doing. The wait is two full-page
        // renders — fifteen seconds of them on a dense A0 — and a word that
        // never changes for that long is read as a hang.
        //
        // And at the ceiling the count becomes a floor: two hundred places is
        // where the list stops being one, and "200 cambios" would be the one
        // number here that is not what it says.
        // "200+" and not "más de 200": this row is the one that overflows first
        // when the window narrows, and the long form would be the widest thing
        // in it.
        string all = changes >= RevisionInk.MaxRegions ? $"{RevisionInk.MaxRegions}+" : changes.ToString();

        ChangeIndicator.Text = !viewer!.ChangesReadyHere
            ? viewer.SweepStageHere switch
            {
                CompareSweepStage.Sheet => "Leyendo 1 de 2…",
                CompareSweepStage.Revision => "Leyendo 2 de 2…",
                CompareSweepStage.Looking => "Buscando cambios…",
                _ => "Buscando…",
            }
            : changes == 0
                ? "Sin cambios"
                : viewer.ChangeNumber > 0
                    ? $"Cambio {viewer.ChangeNumber} de {all}"
                    : changes == 1 ? "1 cambio" : $"{all} cambios";

        int paired = viewer.PairedPageNumber(viewer.CurrentPageIndex);
        ComparePairLabel.Text = paired > 0 ? $"Con la hoja {paired}" : "Sin pareja";

        var palette = viewer.ComparePalette;
        CompareSheetSwatch.Background = Swatch(palette.Sheet);
        CompareRevisionSwatch.Background = Swatch(palette.Revision);

        string name = (Tabs.SelectedItem as TabViewItem)?.Header as string ?? "Este documento";
        CompareSheetName.Text = $"Solo en {name}";
        CompareRevisionName.Text = $"Solo en {viewer.Revision!.Name}";

        ComparePairing.Text = paired > 0
            ? $"Hoja {viewer.CurrentPageNumber} sobre la hoja {paired} de la revisión."
            : "Esta hoja no tiene pareja en la revisión, así que se ve tal cual.";

        CompareStatus.Text = changes > 0
            ? "F4 lleva al cambio siguiente; Mayús+F4, al anterior."
            : string.Empty;

        _syncingPanel = true;
        CompareCommonSlider.Value = palette.Common;
        _syncingPanel = false;
    }

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

    private void OnCaptureToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.CaptureRegion);

    /// <summary>Tells the toast that is fading out that a newer one took its place.</summary>
    private object? _toastToken;

    /// <summary>
    /// Says what just happened, over the drawing, and then goes away.
    ///
    /// This is for the things that leave no trace on screen — a copy to the
    /// clipboard being the whole reason it exists. Without it the reader
    /// drags a box, the box vanishes and nothing visibly happens, which reads
    /// as a gesture that failed.
    /// </summary>
    private void ShowToast(string message, string detail = "")
    {
        ToastText.Text = message;
        ToastDetail.Text = detail;
        ToastDetail.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        Toast.Visibility = Visibility.Visible;
        FadeToast(1, 140);

        var token = new object();
        _toastToken = token;

        _ = Task.Delay(TimeSpan.FromSeconds(3.5)).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
        {
            // Only if it is still ours: a second capture while this one is up
            // replaces the message rather than being cut short by it.
            if (!ReferenceEquals(_toastToken, token)) return;

            FadeToast(0, 320);
            _ = Task.Delay(TimeSpan.FromMilliseconds(340)).ContinueWith(__ => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_toastToken, token)) return;
                Toast.Visibility = Visibility.Collapsed;
            }));
        }));
    }

    private void FadeToast(double to, int milliseconds)
    {
        var fade = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
        };
        Storyboard.SetTarget(fade, Toast);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var story = new Storyboard();
        story.Children.Add(fade);
        story.Begin();
    }

    /// <summary>
    /// The resolution a capture is rendered at. 200 dpi is what a drawing
    /// pasted into a report wants: enough that a dimension stays legible when
    /// the page is printed, without turning a modest region into a file nobody
    /// can mail.
    /// </summary>
    private const double CaptureDpi = 200;

    /// <summary>
    /// Turns the box the reader drew into a picture on the clipboard.
    ///
    /// One shot: the hand comes back afterwards. A capture is a thing you do
    /// once and then carry on reading, and a tool that stayed armed would put
    /// the next pan gesture on the clipboard.
    /// </summary>
    private async void OnRegionCaptured(object? sender, CaptureSpot? spot)
    {
        if (sender is not PdfTiledViewer viewer || !ReferenceEquals(viewer, ActiveViewer)) return;

        SetTool(ViewerTool.Pan);

        if (spot is null)
        {
            ShowToast("Captura cancelada", "el recuadro se quedó demasiado pequeño");
            return;
        }

        Hint("Preparando la captura…");

        CaptureImage? shot;
        try
        {
            shot = await viewer.RenderRegionPngAsync(spot.PageIndex, spot.SheetRect, CaptureDpi);
        }
        catch (Exception error)
        {
            ShowToast("No se pudo preparar la captura", error.Message);
            return;
        }

        if (shot is null)
        {
            ShowToast("No se pudo preparar la captura");
            return;
        }

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(shot.Png));
            Clipboard.SetContent(package);

            // Flush, and it is not optional. Without it the clipboard keeps a
            // reference to the stream and only reads it when something pastes,
            // so the picture dies with this stream — and worse, with ZenInk:
            // copy, close the app, paste into a mail, and nothing arrives.
            // This hands the pixels over now.
            Clipboard.Flush();

            // The size is not decoration: it is what says whether the capture
            // is worth pasting, and it is the only way to notice that the
            // twenty-megapixel ceiling softened this one.
            ShowToast("Imagen copiada al portapapeles", $"{shot.Width} × {shot.Height} px");
        }
        catch (Exception error)
        {
            ShowToast("No se pudo copiar al portapapeles", error.Message);
        }
        finally
        {
            shot.Png.Dispose();
        }
    }

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

    // --- medir --------------------------------------------------------------
    //
    // Una medida es una marca con un número calculado, así que todo lo de
    // arriba vale también aquí: se dibuja, se coge, se recolorea y se guarda
    // igual. Lo único que estas herramientas necesitan y las demás no es que la
    // hoja sepa a qué escala está.

    private void OnCalibrateToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Calibrate);

    private void OnDistanceToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Distance);

    private void OnPerimeterToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Perimeter);

    private void OnAreaToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Area);

    private void OnAngleToolClicked(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Angle);

    private void OnSnapClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveViewer is not { } viewer) return;

        viewer.SnapToInk = SnapToolButton.IsChecked == true;
        Hint(viewer.SnapToInk
            ? "Los puntos se pegan a las líneas del plano."
            : "Los puntos van donde sueltes, sin pegarse a nada.");
    }

    private void OnOrthoClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveViewer is not { } viewer) return;

        viewer.AxisLock = OrthoToolButton.IsChecked == true;
        Hint(viewer.AxisLock
            ? "Las medidas salen horizontales o verticales. Mayús para una suelta en diagonal."
            : "Las medidas van donde las lleves. Mayús para una suelta a escuadra.");
    }

    private void OnClearScaleClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveViewer is not { } viewer) return;

        viewer.CalibrateSheet(viewer.CurrentPageIndex, null);
        Hint("Hoja sin calibrar. Las medidas que haya se quedan sin número.");
        UpdateChrome();
    }

    /// <summary>
    /// The other half of calibrating: the drag said how much paper, and this
    /// asks what that paper is. Nothing is set until the answer comes back, so
    /// a dialog dismissed leaves the sheet exactly as it was.
    /// </summary>
    private async void OnCalibrationDragged(object? sender, CalibrationDrag drag)
    {
        if (!ReferenceEquals(sender, ActiveViewer) || ActiveViewer is not { } viewer) return;

        var box = new TextBox { PlaceholderText = "Por ejemplo 5,40", Width = 140 };
        var units = new ComboBox
        {
            ItemsSource = new[] { "mm", "cm", "m", "km", "in", "ft" },
            SelectedIndex = 2,
            Width = 90,
        };

        // The unit last used is the one this drawing is in, and a set of plans
        // is in one unit throughout.
        units.SelectedIndex = (int)_lastMeasureUnit switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 4,
            _ => 5,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(box);
        row.Children.Add(units);

        var body = new StackPanel { Spacing = 10, Width = 380 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "¿Cuánto mide de verdad lo que acabas de recorrer?",
        });
        body.Children.Add(row);
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            FontSize = 12,
            Text = "Con esto quedan calibradas todas las medidas de esta hoja, también las que ya estén hechas.",
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Calibrar la hoja",
            Content = body,
            PrimaryButtonText = "Calibrar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        AppTheme.Dress(dialog);

        box.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;

            e.Handled = true;
            _calibrationAccepted = true;
            dialog.Hide();
        };

        _calibrationAccepted = false;
        var answer = await Dialogs.ShowAsync(dialog);
        if (answer != ContentDialogResult.Primary && !_calibrationAccepted) return;

        // Typed in a Spanish keyboard, so a comma is a decimal mark and not a
        // thousands separator.
        if (!double.TryParse(
            box.Text.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double length) || length <= 0)
        {
            await ShowMessageAsync("No se pudo calibrar", "Escribe la distancia como un número, por ejemplo 5,40.");
            return;
        }

        var unit = (MeasureUnit)Math.Clamp(units.SelectedIndex, 0, 5);
        _lastMeasureUnit = unit;

        if (SheetScale.From(drag.PaperPt, length, unit) is not { } scale)
        {
            await ShowMessageAsync(
                "No se pudo calibrar",
                "El arrastre es demasiado corto para calibrar con él: recorre una distancia más larga del plano.");
            return;
        }

        viewer.CalibrateSheet(drag.PageIndex, scale);

        // Straight on to measuring: calibrating is never the point, it is what
        // has to happen first.
        SetTool(ViewerTool.Distance);
        Hint($"Hoja calibrada a {scale.RatioLabel}. {scale.FormatLength(drag.PaperPt)} de lo que recorriste.");
        UpdateChrome();
    }

    /// <summary>Says what the sheet in view is drawn to, in the ribbon and in the panel.</summary>
    private void UpdateMeasureChrome(PdfTiledViewer? viewer, bool interactive)
    {
        var scale = interactive ? viewer?.ScaleHere : null;

        ScaleIndicator.Text = !interactive ? string.Empty : scale is { } known ? known.RatioLabel : "Sin calibrar";
        ClearScaleButton.IsEnabled = scale is not null;

        // What a centimetre of paper stands for, which is the sanity check a
        // reader can make against the drawing in their hand — the ratio alone
        // is right and says nothing you can hold a rule up to.
        const double PointsPerCentimetre = 72.0 / 2.54;

        SheetScaleText.Text = scale is { } sheet
            ? $"Calibrada a {sheet.RatioLabel} · 1 cm de papel = {sheet.FormatLength(PointsPerCentimetre)}"
            : "Sin calibrar. Ninguna medida puede dar un número todavía.";

        // The measuring tools are only honest on a calibrated sheet. An angle
        // is the exception and stays available: paper and building agree about
        // angles whatever the scale.
        bool calibrated = scale is not null;
        DistanceToolButton.IsEnabled = interactive && calibrated;
        PerimeterToolButton.IsEnabled = interactive && calibrated;
        AreaToolButton.IsEnabled = interactive && calibrated;

        SnapToolButton.IsEnabled = interactive;
        SnapToolButton.IsChecked = viewer?.SnapToInk ?? true;

        // Not tied to the sheet being calibrated, unlike the tools themselves:
        // an angle is measurable on any sheet, and holding a gesture square is
        // worth having before there is a number on the end of it.
        OrthoToolButton.IsEnabled = interactive;
        OrthoToolButton.IsChecked = viewer?.AxisLock ?? false;

        string detail = interactive && viewer?.SelectedAnnotation is { } chosen ? Detail(chosen) : string.Empty;
        MeasureDetailText.Text = detail;
        MeasureDetailSection.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Everything the chosen measurement can say, which is more than its label
    /// does. A room measured for its area is asked for its perimeter next, and
    /// one measured round its walls is asked what it encloses.
    /// </summary>
    private static string Detail(Annotation mark)
    {
        if (!Measures.Is(mark.Kind)) return string.Empty;

        if (mark.Kind == AnnotationKind.Angle)
        {
            return mark.Text.Length > 0 ? $"Ángulo {mark.Text}" : string.Empty;
        }

        if (mark.Scale is not { } scale) return string.Empty;

        double around = AnnotationGeometry.TotalLength(mark.Points, closed: mark.Kind != AnnotationKind.Distance);

        return mark.Kind switch
        {
            AnnotationKind.Distance => $"Distancia {scale.FormatLength(around)}",

            AnnotationKind.Area =>
                $"Área {scale.FormatArea(AnnotationGeometry.PolygonArea(mark.Points))}"
                + $" · perímetro {scale.FormatLength(around)}",

            // A closed run of segments encloses something whether or not that
            // is what it was drawn for.
            _ => $"Perímetro {scale.FormatLength(around)}"
                 + $" · encierra {scale.FormatArea(AnnotationGeometry.PolygonArea(mark.Points))}",
        };
    }

    /// <summary>The unit of the last calibration; a set of drawings is in one unit throughout.</summary>
    private MeasureUnit _lastMeasureUnit = MeasureUnit.Metre;

    private bool _calibrationAccepted;

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

        viewer.AnnotationStyle = viewer.AnnotationStyle with { WidthPt = StrokeWidths.At((int)e.NewValue) };
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

    /// <summary>
    /// The caret has left the panel, so the run of typing becomes one step in
    /// the history rather than one per letter. The viewer keeps the two halves;
    /// this only says when the run is over.
    /// </summary>
    private void OnNoteTextLostFocus(object sender, RoutedEventArgs e) => ActiveViewer?.CommitText();

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

        // In the pages panel, Supr is about the sheets marked there — which is
        // the only thing it could sensibly mean with the focus in that list.
        if (Pages.HasFocus)
        {
            Pages.Run(PageAction.Delete);
            UpdateChrome();
            args.Handled = true;
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
        // The ribbon follows the tool and not the other way round, so that
        // every route in — a click, a letter, the command palette — leaves the
        // strip agreeing with the drawing.
        ShowTabFor(tool);
        UpdateChrome();
    }

    /// <summary>
    /// The tool letters. One key, no modifier, and the ribbon follows along so
    /// that what is on screen never disagrees with what the drawing is doing.
    /// </summary>
    private void OnToolAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Not while someone is writing: there these are letters, not tools.
        if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox or RichEditBox or AutoSuggestBox)
        {
            return;
        }

        // And not while a label is being typed on the sheet itself. The focus
        // manager does not report that box the way it reports the panel's, so
        // the guard above lets the letter through — and the tool it chose drops
        // the selection, which closes the box the letter was meant for. The
        // viewer knows what it is doing without being asked about focus.
        if (ActiveViewer is { IsWriting: true }) return;

        if (ActiveViewer is not { PageCount: > 0 } || _busyMessage is not null) return;

        var tool = sender.Key switch
        {
            VirtualKey.M => ViewerTool.Pan,
            VirtualKey.V => ViewerTool.SelectAnnotation,
            VirtualKey.L => ViewerTool.Line,
            VirtualKey.F => ViewerTool.Arrow,
            VirtualKey.P => ViewerTool.Polyline,
            VirtualKey.R => ViewerTool.Rectangle,
            VirtualKey.E => ViewerTool.Ellipse,
            VirtualKey.G => ViewerTool.Polygon,
            VirtualKey.N => ViewerTool.Cloud,
            VirtualKey.S => ViewerTool.Highlight,
            VirtualKey.T => ViewerTool.FreeText,
            VirtualKey.C => ViewerTool.Note,
            VirtualKey.A => ViewerTool.Ink,
            VirtualKey.Z => ViewerTool.ZoomRectangle,
            VirtualKey.X => ViewerTool.SelectText,
            VirtualKey.K => ViewerTool.CaptureRegion,
            _ => (ViewerTool?)null,
        };
        if (tool is not { } chosen) return;

        SetTool(chosen);
        args.Handled = true;
    }

    /// <summary>The ribbon's labels, found once: the rows never change shape.</summary>
    private List<TextBlock>? _ribbonLabels;

    /// <summary>Set while the ribbon is showing icons without their names.</summary>
    private bool _ribbonCompact;

    /// <summary>
    /// Below this the widest row — «Anotar», with its eleven tools — no longer
    /// fits, so the names come off and the icons stay. Measured against the
    /// running app, not guessed: that row asks for about 1100 px, and the rest
    /// is the margin that keeps the labels from flickering on and off while
    /// the window is being dragged.
    /// </summary>
    private const double RibbonLabelWidth = 1120;

    private void OnRibbonRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < RibbonLabelWidth;
        if (compact == _ribbonCompact) return;
        _ribbonCompact = compact;

        _ribbonLabels ??= new Panel[] { RibbonInicio, RibbonAnotar, RibbonOrganizador, RibbonComparar }
            .SelectMany(RibbonLabelsIn)
            .ToList();

        foreach (var label in _ribbonLabels)
        {
            label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// The name inside each ribbon button. Every one of them is the TextBlock
    /// that sits beside a <see cref="Controls.ToolIcon"/>, which is what tells
    /// a label apart from the readouts — the page number and the zoom — that
    /// live in the fixed row and must never be hidden.
    ///
    /// This reads the buttons' content and not the visual tree: three of the
    /// four rows start collapsed, and a collapsed control has not applied its
    /// template yet, so a visual walk would come back empty for them.
    /// </summary>
    private static IEnumerable<TextBlock> RibbonLabelsIn(Panel row)
    {
        foreach (var child in row.Children)
        {
            if (child is ContentControl { Content: StackPanel { Children: [Controls.ToolIcon, TextBlock label] } })
            {
                yield return label;
            }
        }
    }

    /// <summary>
    /// Shows the chosen tab's row of commands and hides the other three.
    ///
    /// The four rows all stay in the tree: which tool is down lives in the
    /// button itself, and rebuilding the row on every tab change would mean
    /// keeping that state somewhere else and putting it back by hand.
    /// </summary>
    private void OnRibbonTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var chosen = sender.SelectedItem;
        RibbonInicio.Visibility = Show(chosen == TabInicio);
        RibbonAnotar.Visibility = Show(chosen == TabAnotar);
        RibbonOrganizador.Visibility = Show(chosen == TabOrganizador);
        RibbonComparar.Visibility = Show(chosen == TabComparar);
        RibbonMedir.Visibility = Show(chosen == TabMedir);

        // The panel is what a ribbon needs to stay safe, and comparing is the
        // one activity whose panel is not a tool's: the legend saying which
        // colour is which file only makes sense while this tab is up.
        UpdateChrome();

        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Brings the tab that owns a tool to the front, so that picking one with
    /// the keyboard leaves the ribbon showing where it came from. Without this
    /// the letter would work and the ribbon would silently disagree with it.
    ///
    /// The four navigation modes sit outside the tabs, so choosing one leaves
    /// the ribbon where it was: reaching for the hand in the middle of sorting
    /// sheets should not throw away the tab you were working in.
    /// </summary>
    private void ShowTabFor(ViewerTool tool)
    {
        // Measuring has a tab of its own, and calibrating belongs to it even
        // though it leaves no mark: what the reader is doing there is telling
        // the sheet what it is drawn to, and the tools that need that answer
        // are the ones beside it.
        if (tool.IsMeasurement())
        {
            if (RibbonTabs.SelectedItem != TabMedir)
            {
                RibbonTabs.SelectedItem = TabMedir;
            }
            return;
        }

        if (!tool.Draws() && tool != ViewerTool.CaptureRegion) return;

        if (RibbonTabs.SelectedItem != TabAnotar)
        {
            RibbonTabs.SelectedItem = TabAnotar;
        }
    }

    // --- hojas, desde la cinta ---------------------------------------------
    //
    // Los mismos comandos que el panel. Van todos por `PagesPanel.Run`, que es
    // donde vive la regla de sobre qué hojas actúan; duplicarla aquí es como
    // las dos rutas se separan.

    private void OnInsertFromFileClicked(object sender, RoutedEventArgs e) => RunPageAction(PageAction.InsertFromFile);

    private void OnInsertBlankClicked(object sender, RoutedEventArgs e) => RunPageAction(PageAction.InsertBlank);

    private void OnDuplicatePageClicked(object sender, RoutedEventArgs e) => RunPageAction(PageAction.Duplicate);

    private void OnDeletePageClicked(object sender, RoutedEventArgs e) => RunPageAction(PageAction.Delete);

    private void OnExtractPagesClicked(object sender, RoutedEventArgs e) => RunPageAction(PageAction.Extract);

    private void OnSplitPagesClicked(object sender, RoutedEventArgs e) => RunPageAction(PageAction.Split);

    private void RunPageAction(PageAction action)
    {
        Pages.Run(action);
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
        InsertPagesButton.IsEnabled = interactive;
        DuplicatePageButton.IsEnabled = interactive;
        ExtractPagesButton.IsEnabled = interactive;
        SplitPagesButton.IsEnabled = interactive;

        // Quitar es el único que puede quedarse sin nada que hacer: un
        // documento sin hojas no es un documento.
        DeletePageButton.IsEnabled = interactive && viewer!.PageCount > 1;
        OpenButton.IsEnabled = !busy;
        PreviousPageButton.IsEnabled = interactive && (viewer?.CanGoPrevious ?? false);
        NextPageButton.IsEnabled = interactive && (viewer?.CanGoNext ?? false);

        var tool = viewer?.Tool ?? ViewerTool.Pan;
        bool textTool = tool == ViewerTool.SelectText;
        PanToolButton.IsChecked = tool == ViewerTool.Pan;
        TextToolButton.IsChecked = textTool;
        ZoomToolButton.IsChecked = tool == ViewerTool.ZoomRectangle;
        ZoomToolButton.IsEnabled = interactive;
        CaptureToolButton.IsChecked = tool == ViewerTool.CaptureRegion;
        CaptureToolButton.IsEnabled = interactive;
        LineWeightButton.IsChecked = viewer?.ThinLines != true;

        UpdateMarkTools(viewer, tool, interactive);
        UpdateViewMenu(viewer);
        UpdateSaveChrome(viewer, interactive);
        UpdateCompareChrome(viewer, tool, interactive);
        UpdateMeasureChrome(viewer, interactive);

        Pages.Visibility = hasDocument && ThumbnailsButton.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

        // The panel is contextual: it appears for the tools that have options,
        // and for the comparison, whose legend is the only thing that says
        // which colour on the drawing is which file.
        bool markTool = tool.IsAnnotation();
        bool comparing = interactive && viewer is { IsComparing: true };

        // Calibrating leaves no mark, so it owns no mark panel — but the active
        // tool has to be visible somewhere, and what it needs to say is what
        // the sheet is drawn to. That is the panel it gets.
        bool calibrating = tool == ViewerTool.Calibrate;

        PropertiesPanel.Visibility = hasDocument && (textTool || markTool || comparing || calibrating)
            ? Visibility.Visible
            : Visibility.Collapsed;
        TextToolPanel.Visibility = textTool ? Visibility.Visible : Visibility.Collapsed;
        MarkToolPanel.Visibility = markTool ? Visibility.Visible : Visibility.Collapsed;
        ComparePanel.Visibility = comparing ? Visibility.Visible : Visibility.Collapsed;
        // The measuring panel also comes up for a measurement picked with the
        // selection tool: what it says about the mark in hand is the reason to
        // pick one up.
        bool measuring = calibrating || tool.Measures()
            || (viewer?.SelectedAnnotation is { } picked && Measures.Is(picked.Kind));

        MeasurePanel.Visibility = measuring ? Visibility.Visible : Visibility.Collapsed;

        bool hasSelection = viewer?.HasSelection ?? false;
        CopySelectionButton.IsEnabled = hasSelection;
        SelectAllButton.IsEnabled = hasDocument;
        SelectionStatus.Text = hasSelection
            ? "Texto seleccionado. Ctrl+C también copia."
            : "Arrastra sobre la página para seleccionar.";

        if ((_busyMessage ?? _hintMessage) is { } message)
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

        PageIndicator.Text = $"Página {viewer.CurrentPageNumber} de {viewer.PageCount}";
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

        // Calibrating counts as a tool of the sheet here: it has no mark, but
        // it has a panel and it has to be named in it like every other tool.
        bool ownsPanel = tool.IsAnnotation() || tool == ViewerTool.Calibrate;

        PropertiesIconText.Visibility = ownsPanel ? Visibility.Collapsed : Visibility.Visible;
        PropertiesIconMark.Visibility = ownsPanel ? Visibility.Visible : Visibility.Collapsed;

        if (!tool.IsAnnotation())
        {
            PropertiesTitle.Text = tool switch
            {
                ViewerTool.SelectText => "Selección de texto",
                ViewerTool.Calibrate => ToolTitle(tool),
                _ => PropertiesTitle.Text,
            };
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
            // El tope sale de la escalera, no de una cifra escrita en el XAML:
            // añadir una parada no puede dejar el riel corto.
            WidthSlider.Maximum = StrokeWidths.Stops.Count - 1;
            WidthSlider.Value = StrokeWidths.IndexOf(style.WidthPt);
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
                    : "Escribe texto...";

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
                    ViewerTool.Note => $"Haz clic en la página para dejar un comentario. {Sheet(onSheet)}",
                    ViewerTool.FreeText =>
                        $"Haz clic en la página y escribe ahí mismo; Esc lo cierra. {Sheet(onSheet)}",
                    ViewerTool.Ink => $"Dibuja con el lápiz o el ratón. La otra punta del lápiz borra. {Sheet(onSheet)}",
                    ViewerTool.Highlight => $"Arrastra sobre el texto para resaltarlo. {Sheet(onSheet)}",
                    ViewerTool.Cloud => $"Arrastra un recuadro, o haz clic en cada vértice. {Sheet(onSheet)}",
                    ViewerTool.Polyline or ViewerTool.Polygon =>
                        $"Haz clic en cada vértice; Intro o doble clic lo termina. {Sheet(onSheet)}",
                    _ => $"Arrastra sobre la página para dibujar. {Sheet(onSheet)}",
                };

        static string Sheet(int count) => count switch
        {
            0 => "Esta página no tiene marcas.",
            1 => "Hay 1 marca en esta página.",
            _ => $"Hay {count} marcas en esta página.",
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
        yield return (CalibrateToolButton, tool == ViewerTool.Calibrate);
        yield return (DistanceToolButton, tool == ViewerTool.Distance);
        yield return (PerimeterToolButton, tool == ViewerTool.Perimeter);
        yield return (AreaToolButton, tool == ViewerTool.Area);
        yield return (AngleToolButton, tool == ViewerTool.Angle);
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
        ViewerTool.Calibrate => "Calibrar la hoja",
        ViewerTool.Distance => "Distancia",
        ViewerTool.Perimeter => "Perímetro",
        ViewerTool.Area => "Área",
        ViewerTool.Angle => "Ángulo",
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
        string flow = continuous ? "Continuo" : "Página a página";
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
        DiscardChangesItem.IsEnabled = pending;

        FlattenButton.IsEnabled = hasDocument;
        SignButton.IsEnabled = hasDocument;
    }
}
