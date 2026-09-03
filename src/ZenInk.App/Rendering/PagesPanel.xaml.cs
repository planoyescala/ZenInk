//-----------------------------------------------------------------------------------------
// <copyright file="PagesPanel.xaml.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

using ZenInk.Core;

namespace ZenInk_App.Rendering;

/// <summary>
/// Everything that can be done to the sheets. The first six the panel does
/// itself; the rest need a dialog or a file picker, and go out to the window.
/// </summary>
public enum PageAction
{
    MoveUp,
    MoveDown,
    MoveTo,
    RotateLeft,
    RotateRight,
    Duplicate,
    Delete,
    InsertFromFile,
    InsertBlank,
    Extract,
    Split,
}

/// <summary>
/// A request out to the page that owns the window. <see cref="Destination"/> is
/// where new sheets would go — behind the last one marked, or at the end when
/// nothing is.
/// </summary>
public sealed record PageRequest(PageAction Action, IReadOnlyList<int> Sheets, int Destination);

/// <summary>
/// The sheets of the document, and the index the file carries.
///
/// Previews are rendered on demand for the slots actually scrolled into view
/// and never for the rest: a dense A0 sheet costs seconds of PDFium time
/// whatever size it is drawn at, so rendering a whole set up front would stall
/// the document the reader is looking at. They are also kept once made, keyed
/// by the page they came from rather than by where it sits — which is what lets
/// a sheet be dragged, undone and dragged again without re-rendering anything.
/// </summary>
public sealed partial class PagesPanel : UserControl
{
    private const double FrameWidth = 188;
    private const int PreviewMaxEdge = 220;

    /// <summary>
    /// How many previews are held. A hundred and sixty of them at this size is
    /// about thirty megabytes, which is nothing next to the tile cache and
    /// covers any set someone scrolls through in one sitting.
    /// </summary>
    private const int MaxPreviews = 160;

    private readonly record struct PreviewKey(int DocumentId, int PageIndex, int Rotation);

    private readonly Dictionary<PreviewKey, BitmapSource> _previews = [];
    private readonly Queue<PreviewKey> _previewOrder = new();
    private readonly HashSet<PreviewKey> _requested = [];

    /// <summary>
    /// Observable, and it has to be: a ListView reorders by changing its source,
    /// and over a plain list it quietly does nothing at all — the sheet lifts,
    /// the list scrolls, and the drop lands back where it started.
    /// </summary>
    private readonly ObservableCollection<PageThumbnail> _thumbnails = [];

    private PdfTiledViewer? _viewer;
    private int _generation;
    private bool _syncing;

    /// <summary>
    /// Where the marked sheets will be after the rearrangement now under way.
    ///
    /// Without it the relist puts the selection back at the same *positions*,
    /// which for a move means it lands on whatever was pushed aside — so
    /// pressing "up" twice moves a sheet up and then straight back down. What
    /// stays marked has to be the sheet, not the slot.
    /// </summary>
    private IReadOnlyList<int>? _marksAfterMove;

    /// <summary>
    /// The sheet to bring into view after the relist, or −1.
    ///
    /// A move can send a sheet a hundred rows away. Without this the list stays
    /// where it was and the reader is told nothing at all — which is exactly
    /// the case that made saying the number worth having.
    /// </summary>
    private int _showAfterMove = -1;

    public PagesPanel()
    {
        InitializeComponent();

        // A collapsed panel lays nothing out, so revealing it is the moment to
        // ask for the previews it now has room for. Without this the panel
        // opens on a column of empty frames.
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => QueueVisiblePreviews());
        Sheets.ContainerContentChanging += OnContainerContentChanging;
    }

    /// <summary>Raised for what needs a dialog or the file picker: the window's business, not the panel's.</summary>
    public event EventHandler<PageRequest>? ActionRequested;

    /// <summary>
    /// True while the sheet list has the keyboard. A key that means one thing
    /// over the drawing and another over the sheets has to be able to tell
    /// which one it is standing on.
    /// </summary>
    public bool HasFocus =>
        Visibility == Visibility.Visible
        && XamlRoot is { } root
        && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is DependencyObject focused
        && IsInside(focused);

    private bool IsInside(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, Sheets)) return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    public void Attach(PdfTiledViewer? viewer)
    {
        if (ReferenceEquals(_viewer, viewer)) return;

        if (_viewer is { } previous)
        {
            previous.DocumentOpened -= OnDocumentOpened;
            previous.PagesChanged -= OnPagesChanged;
            previous.ViewChanged -= OnViewChanged;
        }

        _viewer = viewer;

        if (_viewer is { } current)
        {
            current.DocumentOpened += OnDocumentOpened;
            current.PagesChanged += OnPagesChanged;
            current.ViewChanged += OnViewChanged;
        }

        _previews.Clear();
        _previewOrder.Clear();
        Rebuild();
        _ = LoadOutlineAsync();
    }

    private void OnDocumentOpened(object? sender, EventArgs e)
    {
        // Another file entirely: nothing that was rendered still means anything.
        _previews.Clear();
        _previewOrder.Clear();
        Rebuild();
        _ = LoadOutlineAsync();
    }

    /// <summary>
    /// Sheets moved, were turned or came and went. The previews survive — they
    /// belong to pages, not to positions — so this is a relist, not a re-render.
    /// </summary>
    private void OnPagesChanged(object? sender, EventArgs e) => Rebuild();

    private void OnViewChanged(object? sender, EventArgs e) => UpdateCurrent();

    // --- the list of sheets ----------------------------------------------

    private void Rebuild()
    {
        _generation++;
        _requested.Clear();

        var kept = _marksAfterMove ?? SelectedSheets();
        int show = _showAfterMove;
        _marksAfterMove = null;
        _showAfterMove = -1;

        _thumbnails.Clear();
        _syncing = true;
        Sheets.ItemsSource = null;

        if (_viewer is not { } viewer || viewer.PageCount == 0)
        {
            _syncing = false;
            UpdateActions();
            return;
        }

        foreach (var sheet in viewer.Sheets)
        {
            var thumbnail = new PageThumbnail(_thumbnails.Count, sheet, FrameWidth);
            if (!sheet.IsBlank && _previews.TryGetValue(KeyOf(sheet), out var ready))
            {
                thumbnail.Image = ready;
            }
            _thumbnails.Add(thumbnail);
        }

        Sheets.ItemsSource = _thumbnails;
        RestoreSelection(kept);
        _syncing = false;

        // After the layout pass: the list has no rows to scroll to until it has
        // measured them.
        if (show >= 0 && show < _thumbnails.Count)
        {
            var landed = _thumbnails[show];
            DispatcherQueue.TryEnqueue(() => Sheets.ScrollIntoView(landed));
        }

        UpdateCurrent();
        UpdateActions();
        QueueVisiblePreviews();
    }

    private static PreviewKey KeyOf(SheetInfo sheet) =>
        new(sheet.DocumentId, sheet.PageIndex, sheet.QuarterTurns);

    /// <summary>
    /// Puts the marked sheets back after a relist — where a move said they
    /// would land, or at the same positions when nothing moved them.
    /// </summary>
    private void RestoreSelection(IReadOnlyList<int> sheets)
    {
        Sheets.SelectedItems.Clear();
        foreach (int index in sheets)
        {
            if (index >= 0 && index < _thumbnails.Count)
            {
                Sheets.SelectedItems.Add(_thumbnails[index]);
            }
        }
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

    private void OnSheetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateActions();
        if (_syncing) return;

        // One sheet marked is the reader saying "show me that one". Several is
        // them lining up an operation, and jumping the view around while they
        // do it would fight them.
        if (Sheets.SelectedItems.Count == 1 && Sheets.SelectedItem is PageThumbnail thumbnail)
        {
            _viewer?.GoToPage(thumbnail.PageIndex);
        }
    }

    // --- previews ---------------------------------------------------------

    /// <summary>Asks after the current layout pass, when the slots exist to measure.</summary>
    private void QueueVisiblePreviews() => DispatcherQueue.TryEnqueue(RequestVisiblePreviews);

    /// <summary>
    /// The list realising a container is the one dependable signal that a slot
    /// is about to be seen. Scrolling, revealing the panel and relisting all
    /// end here.
    /// </summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        RequestPreview(args.ItemIndex);
    }

    private void RequestVisiblePreviews()
    {
        for (int i = 0; i < _thumbnails.Count; i++)
        {
            if (Sheets.ContainerFromIndex(i) is not null) RequestPreview(i);
        }
    }

    private void RequestPreview(int index)
    {
        if (_viewer is not { } viewer || index < 0 || index >= _thumbnails.Count) return;

        var sheets = viewer.Sheets;
        if (index >= sheets.Count) return;

        var sheet = sheets[index];
        if (sheet.IsBlank || sheet.DocumentId < 0) return;

        var key = KeyOf(sheet);
        if (_previews.TryGetValue(key, out var ready))
        {
            _thumbnails[index].Image = ready;
            return;
        }

        if (!_requested.Add(key)) return;
        _ = LoadPreviewAsync(key, _generation);
    }

    private async Task LoadPreviewAsync(PreviewKey key, int generation)
    {
        try
        {
            var preview = await PdfRenderQueue.Shared.RequestPagePreviewAsync(
                key.DocumentId, key.PageIndex, PreviewMaxEdge, key.Rotation);

            if (generation != _generation || preview is not { } image) return;

            var bitmap = new WriteableBitmap(image.Width, image.Height);
            image.Bgra.CopyTo(0, bitmap.PixelBuffer, 0, image.Bgra.Length);
            bitmap.Invalidate();

            Remember(key, bitmap);

            // Whichever slots this page is sitting in — a sheet can be in two
            // places at once once it has been copied.
            for (int i = 0; i < _thumbnails.Count; i++)
            {
                if (_thumbnails[i].Preview == key.PageIndex && _thumbnails[i].Document == key.DocumentId)
                {
                    _thumbnails[i].Image = bitmap;
                }
            }
        }
        catch (Exception ex)
        {
            // A preview is a convenience; losing one must not disturb the viewer.
            System.Diagnostics.Debug.WriteLine($"ZenInk: fallo al generar la miniatura de {key.PageIndex}: {ex}");
            _requested.Remove(key);
        }
    }

    private void Remember(PreviewKey key, BitmapSource bitmap)
    {
        if (_previews.TryAdd(key, bitmap))
        {
            _previewOrder.Enqueue(key);
        }

        while (_previewOrder.Count > MaxPreviews)
        {
            var oldest = _previewOrder.Dequeue();
            _previews.Remove(oldest);
            _requested.Remove(oldest);
        }
    }

    // --- what the reader does to the sheets -------------------------------

    /// <summary>The sheets marked, in document order. Empty when nothing is.</summary>
    private List<int> SelectedSheets()
    {
        var sheets = new List<int>(Sheets.SelectedItems.Count);
        foreach (var item in Sheets.SelectedItems)
        {
            if (item is PageThumbnail thumbnail) sheets.Add(thumbnail.PageIndex);
        }
        sheets.Sort();
        return sheets;
    }

    /// <summary>
    /// What an action works on: the sheets marked, or the one on screen when
    /// none are. Reaching for "turn this sheet" without marking it first is the
    /// ordinary way to use the panel.
    /// </summary>
    private List<int> Target()
    {
        var marked = Marked();
        if (marked.Count > 0) return marked;

        return _viewer is { PageCount: > 0 } viewer ? [viewer.CurrentPageIndex] : [];
    }

    private int Destination()
    {
        var marked = Marked();
        return marked.Count > 0 ? marked[^1] + 1 : _viewer?.PageCount ?? 0;
    }

    /// <summary>
    /// What is marked, but only while the panel is on screen. Now that the
    /// same commands sit in the ribbon, a closed panel would still be holding
    /// whatever was marked the last time it was open — and «girar» would turn
    /// a sheet the reader cannot see instead of the one they are looking at.
    /// </summary>
    private List<int> Marked() => Visibility == Visibility.Visible ? SelectedSheets() : [];

    private void OnMoveUpClicked(object sender, RoutedEventArgs e) => Run(PageAction.MoveUp);

    private void OnMoveDownClicked(object sender, RoutedEventArgs e) => Run(PageAction.MoveDown);

    private void OnMoveToClicked(object sender, RoutedEventArgs e) => Run(PageAction.MoveTo);

    private void OnMoveUpAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Run(PageAction.MoveUp);
    }

    private void OnMoveDownAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Run(PageAction.MoveDown);
    }

    private void OnRotateLeftClicked(object sender, RoutedEventArgs e) => Run(PageAction.RotateLeft);

    private void OnRotateRightClicked(object sender, RoutedEventArgs e) => Run(PageAction.RotateRight);

    private void OnDuplicateClicked(object sender, RoutedEventArgs e) => Run(PageAction.Duplicate);

    private void OnDeleteClicked(object sender, RoutedEventArgs e) => Run(PageAction.Delete);

    private void OnInsertFromFileClicked(object sender, RoutedEventArgs e) => Run(PageAction.InsertFromFile);

    private void OnInsertBlankClicked(object sender, RoutedEventArgs e) => Run(PageAction.InsertBlank);

    private void OnExtractClicked(object sender, RoutedEventArgs e) => Run(PageAction.Extract);

    private void OnSplitClicked(object sender, RoutedEventArgs e) => Run(PageAction.Split);

    /// <summary>
    /// Does one of the panel's jobs, whether it was asked for here or typed
    /// into the command finder. Both roads lead to the same place, which is
    /// what keeps the two from drifting apart.
    /// </summary>
    public void Run(PageAction action)
    {
        if (_viewer is not { PageCount: > 0 } viewer) return;

        var target = Target();
        if (target.Count == 0) return;

        switch (action)
        {
            // A step at a time, by button or by keyboard. Dragging is the
            // gesture, but a set of two hundred sheets is exactly where
            // dragging stops being usable, and where a keyboard does not.
            case PageAction.MoveUp:
                if (target[0] > 0) MoveTo(target[0] - 1);
                return;
            case PageAction.MoveDown:
                if (target[0] + target.Count < viewer.PageCount) MoveTo(target[0] + 1);
                return;
            case PageAction.RotateLeft:
                viewer.RotatePages(target, -1);
                return;
            case PageAction.RotateRight:
                viewer.RotatePages(target, 1);
                return;
            case PageAction.Duplicate:
                viewer.DuplicatePages(target);
                return;
            case PageAction.Delete:
                // A document with no sheets is not a document. The button is
                // already off in that case; this is the belt to that pair of
                // braces, since the command finder does not read buttons.
                if (target.Count < viewer.PageCount) viewer.RemovePages(target);
                return;
            default:
                ActionRequested?.Invoke(this, new PageRequest(action, target, Destination()));
                return;
        }
    }

    /// <summary>
    /// Moves the marked sheets and keeps them marked where they land — which,
    /// because a move gathers a scattered selection together, is one block
    /// starting at the gap they were dropped into once the sheets lifted out
    /// from above it are accounted for.
    /// </summary>
    /// <summary>
    /// Moves the marked sheets so the first of them becomes sheet
    /// <paramref name="landing"/>, and keeps them marked where they land —
    /// which, because a move gathers a scattered selection together, is one
    /// block starting there.
    /// </summary>
    public void MoveTo(int landing)
    {
        if (_viewer is not { } viewer) return;

        var sheets = Target();
        if (sheets.Count == 0) return;

        int at = viewer.Plan.LandingFor(sheets, landing);

        _marksAfterMove = [.. Enumerable.Range(at, sheets.Count)];
        _showAfterMove = at;
        viewer.MovePagesTo(sheets, at);

        // The reader moved a sheet; the sheet is what they want to be looking
        // at, not whatever slid into the place they were looking at before.
        viewer.GoToPage(at);
    }

    private void OnSheetsReordered(object sender, DragItemsCompletedEventArgs args)
    {
        if (_viewer is not { } viewer) return;

        // The list has already put itself in the order the reader let go of, so
        // reading it back beats working out which gap they aimed at.
        var order = new List<int>(_thumbnails.Count);
        foreach (var item in Sheets.Items)
        {
            if (item is PageThumbnail thumbnail) order.Add(thumbnail.PageIndex);
        }

        // Where the sheets that were marked have ended up, so the relist marks
        // the sheets rather than the slots they used to be in.
        var moved = new HashSet<int>(SelectedSheets());
        _marksAfterMove = [.. Enumerable.Range(0, order.Count).Where(i => moved.Contains(order[i]))];
        _showAfterMove = _marksAfterMove.Count > 0 ? _marksAfterMove[0] : -1;

        viewer.ReorderPages(order);
    }

    private void OnSheetMenuOpening(object? sender, object e) => UpdateActions();

    private void UpdateActions()
    {
        bool hasSheets = _viewer is { PageCount: > 0 };
        int marked = SelectedSheets().Count;
        bool canDelete = hasSheets && marked < (_viewer?.PageCount ?? 0);

        foreach (var button in new[] { RotateLeftButton, RotateRightButton, DuplicateButton })
        {
            button.IsEnabled = hasSheets;
        }

        // A sheet already at the top has nowhere to go up to, and the button
        // says so rather than doing nothing when pressed.
        var target = Target();
        MoveUpButton.IsEnabled = target.Count > 0 && target[0] > 0;
        MoveDownButton.IsEnabled = target.Count > 0 && target[0] + target.Count < (_viewer?.PageCount ?? 0);
        MenuMoveUp.IsEnabled = MoveUpButton.IsEnabled;
        MenuMoveDown.IsEnabled = MoveDownButton.IsEnabled;

        // Asking where to send a sheet only means something when there is
        // somewhere else to send it to.
        MenuMoveTo.IsEnabled = hasSheets && (_viewer?.PageCount ?? 0) > 1;

        InsertButton.IsEnabled = hasSheets;
        DeleteButton.IsEnabled = canDelete;

        MenuRotateLeft.IsEnabled = hasSheets;
        MenuRotateRight.IsEnabled = hasSheets;
        MenuDuplicate.IsEnabled = hasSheets;
        MenuDelete.IsEnabled = canDelete;
        MenuInsertFile.IsEnabled = hasSheets;
        MenuInsertBlank.IsEnabled = hasSheets;
        MenuExtract.IsEnabled = hasSheets;
        MenuSplit.IsEnabled = _viewer is { PageCount: > 1 };
    }

    // --- the index the file carries ---------------------------------------

    private async Task LoadOutlineAsync()
    {
        int generation = _generation;
        IReadOnlyList<OutlineEntry> entries = [];

        if (_viewer is { } viewer)
        {
            try
            {
                entries = await viewer.ReadOutlineAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ZenInk: no se pudo leer el índice del PDF: {ex.Message}");
            }
        }

        if (generation != _generation) return;

        Outline.ItemsSource = entries.Count == 0 ? null : OutlineNode.Tree(entries);

        // A set of plans usually brings an index and a lone drawing never does,
        // so the tab appears only when there is something behind it.
        OutlineTab.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (entries.Count == 0) ShowSheets();
    }

    private void OnSheetsTabClicked(object sender, RoutedEventArgs e) => ShowSheets();

    private void OnOutlineTabClicked(object sender, RoutedEventArgs e)
    {
        SheetsTab.IsChecked = false;
        OutlineTab.IsChecked = true;
        Sheets.Visibility = Visibility.Collapsed;
        Actions.Visibility = Visibility.Collapsed;
        Outline.Visibility = Visibility.Visible;
    }

    private void ShowSheets()
    {
        SheetsTab.IsChecked = true;
        OutlineTab.IsChecked = false;
        Sheets.Visibility = Visibility.Visible;
        Actions.Visibility = Visibility.Visible;
        Outline.Visibility = Visibility.Collapsed;
        QueueVisiblePreviews();
    }

    private void OnOutlineInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is OutlineNode { PageIndex: >= 0 } node)
        {
            _viewer?.GoToPage(node.PageIndex);
        }
    }
}
