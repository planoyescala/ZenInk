//-----------------------------------------------------------------------------------------
// <copyright file="PdfTiledViewer.xaml.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Numerics;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.System;
using Windows.Storage.Streams;
using Windows.UI;

using ZenInk.Core;

namespace ZenInk_App.Rendering;

/// <summary>Where a visible signature was asked to go: the sheet, and the box on it.</summary>
public sealed record SignatureSpot(int PageIndex, RectPt SheetRect);

/// <summary>The piece of one sheet the reader dragged a box around, in sheet points.</summary>
public sealed record CaptureSpot(int PageIndex, RectPt SheetRect);

/// <summary>
/// A drag over something whose real length the reader knows, waiting to be told
/// what that length is. The paper's own measurement in points is all the viewer
/// can say; the rest of the calibration is an answer, not a gesture.
/// </summary>
public sealed record CalibrationDrag(int PageIndex, float PaperPt);

/// <summary>A finished capture: the PNG, and how big it came out — which is what says whether it is worth pasting.</summary>
public sealed record CaptureImage(InMemoryRandomAccessStream Png, int Width, int Height);

/// <summary>Sheets to bring in from one file: which of its pages, and how to open it.</summary>
public sealed record PageInsertion(string Path, string? Password, IReadOnlyList<int> Pages);

/// <summary>
/// The other revision, open alongside the document and laid over it. It is a
/// document of its own in the queue — never a source of the plan — because
/// nothing about it is ever written: comparing must not be able to change the
/// drawing being compared.
/// </summary>
public sealed record ComparisonSource(string Path, string Name, int DocumentId, IReadOnlyList<PdfPageSize> Pages);

/// <summary>
/// Which part of counting the changes on a sheet is under way. The two renders
/// are the whole of the wait — fifteen seconds of it on a dense A0 — and they
/// are what the reader is told about, because a count that arrives without
/// warning after a still "Buscando…" is indistinguishable from one that never
/// arrives.
/// </summary>
public enum CompareSweepStage
{
    /// <summary>Nothing under way: either not comparing, or the count is in.</summary>
    Idle,

    /// <summary>Rasterizing the sheet being read.</summary>
    Sheet,

    /// <summary>Rasterizing the revision, onto the sheet's own grid.</summary>
    Revision,

    /// <summary>Both are in hand, and the disagreements are being flooded.</summary>
    Looking,
}

/// <summary>
/// One sheet as the pages panel sees it: where its picture comes from, how it
/// stands, and how big it is. Blank paper has no document behind it and no
/// preview to ask for.
/// </summary>
public readonly record struct SheetInfo(
    int DocumentId, int PageIndex, int QuarterTurns, PdfPageSize Size, bool IsBlank);

/// <summary>
/// A signature that has been placed but not yet written.
///
/// It exists so that placing and signing are two steps and not one. Signing
/// writes the file and cannot be taken back, so the box gets a moment on screen
/// first — to be looked at, dragged somewhere better, or thrown away.
/// </summary>
public sealed record PendingSignature(
    int PageIndex,
    RectPt SheetRect,
    IReadOnlyList<(string Text, bool Strong)> Lines);

public enum ViewerTool
{
    Pan,

    SelectText,

    /// <summary>
    /// Drag a rectangle and the view goes there. On an A0 this is the fastest
    /// way from the whole sheet to a legible dimension — one gesture instead of
    /// a dozen wheel notches and a drag to recentre.
    /// </summary>
    ZoomRectangle,

    /// <summary>
    /// Drag a rectangle and that piece of the drawing goes to the clipboard.
    ///
    /// It is not a screen grab: the region is rendered again from the PDF at
    /// print resolution, so a detail picked out at 29 % still arrives sharp in
    /// whatever it is pasted into.
    /// </summary>
    CaptureRegion,

    /// <summary>Pick a mark up: click to select it, drag to move it.</summary>
    SelectAnnotation,

    /// <summary>Freehand, pen or mouse.</summary>
    Ink,

    Line,

    Arrow,

    Rectangle,

    Ellipse,

    /// <summary>An open run of segments, placed vertex by vertex.</summary>
    Polyline,

    /// <summary>A closed run of segments, placed vertex by vertex.</summary>
    Polygon,

    /// <summary>A revision cloud, round a dragged box or round placed vertices.</summary>
    Cloud,

    /// <summary>Drag over text to wash it in colour.</summary>
    Highlight,

    /// <summary>Write words straight onto the sheet.</summary>
    FreeText,

    /// <summary>Place a comment; the text is typed in the panel.</summary>
    Note,

    /// <summary>
    /// Drag over something whose real length is known — a scale bar, a
    /// dimension already on the drawing, a known bay — and say what it is. It
    /// is the only tool that leaves nothing on the sheet: what it changes is
    /// what every measurement on that sheet means.
    /// </summary>
    Calibrate,

    /// <summary>Drag a line and it says how long it is.</summary>
    Distance,

    /// <summary>Place vertices round something and it says how far round it is.</summary>
    Perimeter,

    /// <summary>Place vertices round something and it says how much it covers.</summary>
    Area,

    /// <summary>Place an arm, the corner and the other arm, and it says the angle.</summary>
    Angle,
}

public static class ViewerToolExtensions
{
    /// <summary>True for the tools that put something on the sheet.</summary>
    public static bool Draws(this ViewerTool tool) =>
        tool is ViewerTool.Ink or ViewerTool.Line or ViewerTool.Arrow
            or ViewerTool.Rectangle or ViewerTool.Ellipse or ViewerTool.Polyline
            or ViewerTool.Polygon or ViewerTool.Cloud
            or ViewerTool.Highlight or ViewerTool.FreeText or ViewerTool.Note
            || tool.Measures();

    /// <summary>
    /// True for the tools that leave a number on the sheet. Calibrating is not
    /// one of them: it leaves nothing, it only says what the sheet is drawn to.
    /// </summary>
    public static bool Measures(this ViewerTool tool) =>
        tool is ViewerTool.Distance or ViewerTool.Perimeter
            or ViewerTool.Area or ViewerTool.Angle;

    /// <summary>True for the tools of the measuring tab, calibrating included.</summary>
    public static bool IsMeasurement(this ViewerTool tool) =>
        tool.Measures() || tool == ViewerTool.Calibrate;

    /// <summary>True for the tools whose panel is about marks — drawing them or picking them up.</summary>
    public static bool IsAnnotation(this ViewerTool tool) => tool.Draws() || tool == ViewerTool.SelectAnnotation;

    /// <summary>
    /// True for the tools worked by dragging on the drawing itself. Calibrating
    /// is one of them and is not a mark: the drag is the question, and the
    /// sheet keeps nothing of it.
    /// </summary>
    public static bool DragsOnSheet(this ViewerTool tool) =>
        tool.IsAnnotation() || tool == ViewerTool.Calibrate;

    /// <summary>The kind of mark a drawing tool makes.</summary>
    public static AnnotationKind ToKind(this ViewerTool tool) => tool switch
    {
        ViewerTool.Line => AnnotationKind.Line,
        ViewerTool.Arrow => AnnotationKind.Arrow,
        ViewerTool.Rectangle => AnnotationKind.Rectangle,
        ViewerTool.Ellipse => AnnotationKind.Ellipse,
        ViewerTool.Polyline => AnnotationKind.Polyline,
        ViewerTool.Polygon => AnnotationKind.Polygon,
        ViewerTool.Cloud => AnnotationKind.Cloud,
        ViewerTool.Highlight => AnnotationKind.Highlight,
        ViewerTool.FreeText => AnnotationKind.FreeText,
        ViewerTool.Note => AnnotationKind.Note,
        // The calibration drag is drawn as the dimension line it is measuring
        // along, so the reader can see what they are about to call a length.
        // Nothing of it is kept.
        ViewerTool.Calibrate or ViewerTool.Distance => AnnotationKind.Distance,
        ViewerTool.Perimeter => AnnotationKind.Perimeter,
        ViewerTool.Area => AnnotationKind.Area,
        ViewerTool.Angle => AnnotationKind.Angle,
        _ => AnnotationKind.Ink,
    };
}

public enum ViewerLayoutMode
{
    /// <summary>Every sheet stacked in one scrollable strip.</summary>
    Continuous,

    /// <summary>One sheet at a time; scrolling stays on it and pages change explicitly.</summary>
    SinglePage,
}

/// <summary>
/// How the zoom follows the window. Width and Page are standing choices, not
/// one-off actions: they are reapplied when the window resizes or the sheet
/// changes, which is what makes "ancho completo" mean something after the
/// reader drags the window wider.
/// </summary>
public enum ViewerFitMode
{
    /// <summary>Whatever zoom the reader set.</summary>
    Free,

    /// <summary>The strip's full width fills the viewport.</summary>
    Width,

    /// <summary>A whole sheet — or a whole spread — fits inside the viewport.</summary>
    Page,
}

/// <summary>
/// Scrolls a whole PDF as one continuous vertical strip of tiled pages.
///
/// Coordinates live in "document space" — PDF points, y down, every page
/// stacked by <see cref="DocumentLayout"/> — so scrolling is a single vector
/// and the math never depends on which discrete zoom level currently backs the
/// tiles on screen. <see cref="_origin"/> is the document point sitting at the
/// viewport's top-left corner; <see cref="_scale"/> is DIPs per point.
///
/// Drawing is progressive: coarser cached tiles are painted first (so panning
/// into new territory shows a soft, already-seen version immediately) and the
/// current level is painted over them, requesting whatever is missing.
/// </summary>
public sealed partial class PdfTiledViewer : UserControl
{
    private const double MinScale = 0.02;
    private const double MaxScale = 16.0;
    private const int FallbackLevels = 5;
    private const long CacheBudgetBytes = 384L * 1024 * 1024;
    private const double WheelScrollDips = 90.0;
    private const double ZoomStep = 1.15;
    private const int MaxTextLayers = 24;

    /// <summary>How far off a glyph the pointer may be and still select it, in points.</summary>
    private const float SelectionSnapPt = 24f;

    /// <summary>Below this, a zoom-rectangle drag was a click and is treated as one.</summary>
    private const double MinZoomBandDips = 12.0;

    /// <summary>Where a capture stops growing and starts getting softer instead. About 20 megapixels.</summary>
    private const long MaxCapturePixels = 20_000_000;

    /// <summary>Band size, so a big capture never asks PDFium for one enormous buffer.</summary>
    private const int MaxCaptureBandPixels = 4_000_000;

    /// <summary>
    /// How far off the line a captured stroke may be thinned, in points. Well
    /// under the width of anything drawn, and it cuts the point count of a pen
    /// gesture by an order of magnitude.
    /// </summary>
    private const float InkSimplifyPt = 0.35f;

    /// <summary>
    /// Below this, a drag with a shape tool was a click. A rectangle a point
    /// wide is not something anyone meant to draw.
    /// </summary>
    private const double MinShapeDips = 4.0;

    /// <summary>Arrow-key step, as a fraction of the viewport.</summary>
    private const double ArrowStepFraction = 0.12;

    /// <summary>
    /// How quickly the zoom closes the distance to where it was sent, in
    /// seconds. The scale eases towards its target rather than jumping there:
    /// each frame covers the same *fraction* of what is left, so the move
    /// starts at once and settles softly instead of stopping dead.
    ///
    /// At 55 ms most of the travel is over in about a tenth of a second and the
    /// last of it in a quarter — fast enough not to be a wait, gradual enough
    /// that the eye keeps hold of what it was looking at. Notches that arrive
    /// while it is still moving push the same target further out, so spinning
    /// the wheel gives one long glide and not a stack of jumps.
    /// </summary>
    private const double ZoomEaseSeconds = 0.055;

    /// <summary>
    /// How close to the target counts as arrived, as a proportion of the scale.
    /// A quarter of a percent is a fifth of a pixel on a 4K-wide sheet: past
    /// this the frames are costing more than they show.
    /// </summary>
    private const double ZoomSettleRatio = 0.0025;

    /// <summary>
    /// How often the chrome hears about a zoom that is still moving. Every
    /// frame would run the whole toolbar sixty times a second to animate a
    /// percentage readout; a dozen times a second reads as live and costs
    /// nothing. The end of the glide always reports, whatever this says.
    /// </summary>
    private const double ZoomReportSeconds = 0.08;

    private static readonly Color ZoomBandFill = Color.FromArgb(46, 0, 103, 192);

    private static readonly Color ZoomBandStroke = Color.FromArgb(210, 0, 103, 192);

    private static readonly Color SelectionFill = Color.FromArgb(70, 0, 103, 192);

    /// <summary>
    /// Search highlights are drawn over white paper and black ink, so they are
    /// translucent enough to read the drawing through and saturated enough to
    /// spot at a glance. The current hit is a different hue, not just a
    /// stronger one, so it stands out among its neighbours.
    /// </summary>
    /// <summary>Air between a change and the ring around it, so the ring never sits on the ink it points at.</summary>
    private const double ChangeRingInsetDips = 5.0;

    /// <summary>
    /// Above this many changes on a sheet, only the one being stood on is
    /// ringed. Seen on two real issues of a plan: a hundred and fifty-eight
    /// boxes is not a map of where to look, it is a mesh over the drawing.
    /// </summary>
    private const int MostRingsWorthDrawing = 24;

    /// <summary>
    /// The rings round the changes. Grey and not a third colour: the sheet
    /// already spends red and blue on saying which drawing an ink belongs to,
    /// and a ring in a colour of its own would compete with the only thing the
    /// picture is about. The one being stepped through is orange, which is what
    /// the search already means by "this is the one you are on".
    /// </summary>
    private static readonly Color ChangeRingStroke = Color.FromArgb(150, 90, 90, 90);

    private static readonly Color ChangeRingCurrent = Color.FromArgb(230, 255, 122, 0);

    /// <summary>
    /// The ring round a point that was pulled onto the drawing. Green, and not
    /// the selection's blue or the change's orange: it is a different kind of
    /// statement — this is where the point actually is — and it appears while
    /// the reader is looking straight at it.
    /// </summary>
    private static readonly Color SnapRing = Color.FromArgb(235, 30, 160, 90);

    private const float SnapRingDips = 6f;

    private static readonly Color SearchFill = Color.FromArgb(105, 255, 214, 0);

    private static readonly Color SearchCurrentFill = Color.FromArgb(150, 255, 122, 0);

    private static readonly Color SearchCurrentStroke = Color.FromArgb(220, 190, 70, 0);

    private readonly PdfRenderQueue _queue = PdfRenderQueue.Shared;
    private readonly TileCache _cache = new(CacheBudgetBytes);
    private readonly HashSet<TileKey> _inFlight = new();

    /// <summary>
    /// Extracted text, keyed by sheet *and* the turn it was extracted at: the
    /// glyph boxes live in the same space the tiles are drawn in, so turning a
    /// sheet makes the old boxes wrong.
    /// </summary>
    private readonly Dictionary<TextLayerKey, PageTextLayer> _textLayers = new();

    private readonly LinkedList<TextLayerKey> _textLru = new();
    private readonly HashSet<TextLayerKey> _textInFlight = new();

    /// <summary>Search hits in document order, so next/previous is a walk along one list.</summary>
    private readonly List<SearchHit> _hits = new();

    /// <summary>Where each sheet's hits sit in <see cref="_hits"/>; the scan fills it in page order.</summary>
    private readonly Dictionary<int, (int Start, int Count)> _hitsByPage = new();

    private DocumentLayout? _layout;
    private int _documentId = -1;
    private bool _isActive = true;

    /// <summary>Sheet sizes as laid out, with the axes swapped on a quarter turn.</summary>
    private IReadOnlyList<PdfPageSize> _pageSizes = [];

    /// <summary>
    /// The open document behind each file the plan reads from, by path. Sheets
    /// brought in from elsewhere are drawn straight out of their own file, so
    /// they are on screen before anything is written — which is what lets the
    /// reader see what they are about to save.
    ///
    /// Keyed by path rather than by the plan's own source numbers because those
    /// are renumbered when a source falls out of use, and undo can bring one
    /// back.
    /// </summary>
    private readonly Dictionary<string, int> _openSources = new(StringComparer.OrdinalIgnoreCase);

    private ViewerLayoutMode _layoutMode = ViewerLayoutMode.Continuous;
    private int _columns = 1;
    private int _currentPageIndex;
    private double _scale = 1.0;
    private Vector2 _origin;
    private ViewerFitMode _fitMode = ViewerFitMode.Width;
    private bool _fitDirty;
    private bool _fitSnap;
    private bool _isPanning;
    private bool _isSelecting;
    private bool _isZoomBanding;
    private Point _zoomBandStart;
    private Point _zoomBandEnd;

    /// <summary>
    /// Set while the reader is dragging out where a visible signature goes. It
    /// borrows the zoom rectangle's band rather than growing a second one: the
    /// gesture is the same, and only what happens on release differs.
    ///
    /// It is a passing mode and not a tool on the rail. The rail is what the
    /// reader draws with and stays chosen; this is one rectangle asked for by a
    /// dialog, and it ends the moment it is given.
    /// </summary>
    private bool _bandIsForSignature;
    private bool _bandIsForCapture;

    /// <summary>Set while the placed-but-unwritten signature is being dragged somewhere else.</summary>
    private bool _draggingPending;

    private Vector2 _pendingGrabbedAt;
    private Point _lastPointerPosition;
    private ViewerTool _tool = ViewerTool.Pan;
    private bool _thinLines;
    private bool _suppressScrollEvents;

    /// <summary>
    /// The zoom in flight: where it is heading, and the point it turns about.
    /// The anchor is kept in both spaces — where it is on the canvas and what
    /// sheet point sits under it — so every frame can put that same point back
    /// under the pointer instead of interpolating a corner, which is what would
    /// make the drawing slide away from the cursor as it grows.
    /// </summary>
    private double _zoomTarget;
    private Point _zoomAnchor;
    private Vector2 _zoomAnchorDoc;
    private bool _zoomGliding;
    private long _zoomLastTick;
    private double _zoomSinceReport;

    /// <summary>Every mark on this document, and the history to take them back.</summary>
    private readonly AnnotationStore _annotations = new();

    /// <summary>
    /// Colour and width per kind of mark, remembered separately.
    ///
    /// A reviewer does not want one colour for everything: the highlighter is
    /// yellow and the pencil is red, and having to set that again on every
    /// switch is the sort of friction that ends with everything being one
    /// colour.
    /// </summary>
    private readonly Dictionary<AnnotationKind, AnnotationStyle> _styles = new();

    /// <summary>The mark the reader has hold of, and the sheet it is on.</summary>
    private Annotation? _selected;
    private int _selectedPage = -1;

    /// <summary>The stroke being drawn right now, in sheet space, and its sheet.</summary>
    private readonly List<Vector2> _draft = new();
    private int _draftPage = -1;
    private bool _isDrawing;

    /// <summary>Set while a mark is being dragged; holds where it started so undo has one step.</summary>
    private Annotation? _movingFrom;
    private Vector2 _moveAnchorSheet;

    /// <summary>The grip being pulled, if the drag started on one.</summary>
    private MarkGrip _handle = MarkGrip.None;

    /// <summary>
    /// Vertices placed so far, for the shapes that are built click by click,
    /// and where the pointer is now — the segment that follows the cursor.
    /// </summary>
    private readonly List<Vector2> _vertices = new();
    private Vector2 _rubber;
    private bool _placingVertices;

    /// <summary>Where the pointer went down, to tell a click from a drag.</summary>
    private Point _pressPosition;

    /// <summary>The pointer that owns the gesture, so a resting palm cannot start a second one.</summary>
    private uint _gesturePointerId;

    /// <summary>Bumped on every open, so results for a previous document are discarded.</summary>
    private int _documentGeneration;

    private int _selectionPage = -1;
    private int _selectionAnchor = -1;
    private int _selectionFocus = -1;

    private string _searchQuery = string.Empty;
    private TextSearchOptions _searchOptions;
    private int _searchGeneration;
    private int _searchScanned;
    private bool _searchRunning;
    private int _searchCursor = -1;

    /// <summary>
    /// The revision laid over this document, when one is. Its tiles are kept
    /// apart from the plain ones rather than sharing a key: the same square of
    /// the same sheet is a different picture once something is laid over it,
    /// and the two must never be handed to each other.
    /// </summary>
    private ComparisonSource? _revision;
    private readonly TileCache _compareCache = new(CacheBudgetBytes / 2);
    private readonly HashSet<TileKey> _compareInFlight = new();
    private CompareFit _compareFit = CompareFit.Fit;
    private ComparePalette _comparePalette = ComparePalette.Default;
    private int _comparePageOffset;

    /// <summary>
    /// Bumped whenever anything about the comparison changes — the revision,
    /// the pairing, the fit, the colours. Every composed tile and every sweep
    /// already under way was made under the old answer, so this is what stops
    /// one of them landing on top of the new one.
    /// </summary>
    private int _compareGeneration;

    /// <summary>
    /// The same idea for the sweep, and separate from it because the two go
    /// stale for different reasons. Recolouring throws away every composed tile
    /// and none of the measurements; repairing the sheets throws away both.
    /// </summary>
    private int _sweepGeneration;

    private readonly Dictionary<int, IReadOnlyList<ChangeRegion>> _changes = new();
    private readonly HashSet<int> _changesInFlight = new();

    /// <summary>
    /// How far along the sweep of each sheet is. Measured on a 51 MB A0 the
    /// sweep is fifteen seconds, nearly all of it the two full-page renders, so
    /// a reader watching "Buscando…" sit there has no way to tell work from a
    /// hang. This is what the wait is made of.
    /// </summary>
    private readonly Dictionary<int, CompareSweepStage> _sweepStage = new();

    /// <summary>
    /// Which change is being stood on, and on which sheet. The sheet is kept
    /// alongside because in continuous view the current one changes as the
    /// reader scrolls, and a cursor left pointing at a number would ring the
    /// wrong change on the next sheet down.
    /// </summary>
    private int _changeSheet = -1;
    private int _changeCursor = -1;

    public PdfTiledViewer()
    {
        InitializeComponent();
        UpdateCursor();
    }

    /// <summary>Raised when the visible page, zoom level or selection changes.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>Raised when a document finishes opening, so page-dependent UI can rebuild.</summary>
    public event EventHandler? DocumentOpened;

    /// <summary>Raised when the sheets change shape — a turn — so previews can be redrawn.</summary>
    public event EventHandler? PagesChanged;

    /// <summary>
    /// Raised when a mark has just been placed that is waiting to be typed
    /// into. The words live in the panel, so the panel is where the caret has
    /// to go — otherwise the reader clicks, sees a marker appear, and types
    /// into nothing.
    /// </summary>
    public event EventHandler? TextWanted;

    /// <summary>
    /// A text layer belongs to a page of a file, not to a place in the plan:
    /// two copies of one sheet read the same words, and moving a sheet does not
    /// change them.
    /// </summary>
    private readonly record struct TextLayerKey(int DocumentId, int PageIndex, int Rotation);

    /// <summary>
    /// Where a sheet of the plan is read from. A blank sheet has no document
    /// behind it, which is what <see cref="IsBlank"/> means — there is nothing
    /// to ask PDFium for, and the canvas draws the paper anyway.
    /// </summary>
    private readonly record struct SheetOrigin(int Source, int DocumentId, int PageIndex)
    {
        public bool IsBlank => DocumentId < 0;
    }

    /// <summary>
    /// One occurrence of the search term. The glyph range is what makes it
    /// navigable; the runs are the rectangles to paint, kept alongside because
    /// the text layer they came from may well be evicted before the reader
    /// scrolls back to this hit.
    /// </summary>
    private sealed record SearchHit(int PageIndex, TextMatch Range, IReadOnlyList<TextRun> Runs);

    /// <summary>
    /// Claims all the space on offer. A CanvasControl reports no size of its
    /// own, so in a host that arranges children at their desired size — a
    /// TabView's content presenter, for one — this control would otherwise
    /// collapse to zero height and never draw.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = base.MeasureOverride(availableSize);

        double width = double.IsInfinity(availableSize.Width) ? desired.Width : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? desired.Height : availableSize.Height;
        return new Size(width, height);
    }

    public int PageCount => _layout?.DocumentPageCount ?? 0;

    /// <summary>Identifies this viewer's document to the shared render queue.</summary>
    public int DocumentId => _documentId;

    /// <summary>The file this document was opened from, which is where a save writes back.</summary>
    public string? SourcePath { get; private set; }

    /// <summary>Every sheet's size as laid out, so the thumbnail strip can take its shape before any rendering.</summary>
    public IReadOnlyList<PdfPageSize> PageSizes => _pageSizes;

    /// <summary>The reader's quarter-turn on a sheet, for anyone rendering it themselves.</summary>
    public int RotationOf(int pageIndex) =>
        pageIndex >= 0 && pageIndex < Plan.Count ? Plan[pageIndex].QuarterTurns & 3 : 0;

    /// <summary>How the sheets stand right now: the order, and where each one comes from.</summary>
    public PagePlan Plan => _annotations.Plan;

    /// <summary>Anything on screen that is not yet in the file: sheets moved, turns, marks.</summary>
    public bool HasUnsavedChanges => HasUnsavedRotations || HasPageChanges || _annotations.IsDirty;

    /// <summary>True while sheets have been moved, removed, copied or brought in but not written.</summary>
    public bool HasPageChanges => Plan.IsRearranged;

    /// <summary>
    /// Which open document a sheet is drawn from, and which of its pages. A
    /// sheet that came from another file is read from that file directly.
    /// </summary>
    private SheetOrigin OriginOf(int sheet)
    {
        if (sheet < 0 || sheet >= Plan.Count) return new SheetOrigin(0, -1, -1);

        var slot = Plan[sheet];
        if (slot.IsBlank) return new SheetOrigin(PageSlot.BlankSource, -1, -1);
        if (slot.Source == 0) return new SheetOrigin(0, _documentId, slot.PageIndex);

        string path = Plan.Sources[slot.Source].Path;
        return _openSources.TryGetValue(path, out int documentId)
            ? new SheetOrigin(slot.Source, documentId, slot.PageIndex)
            : new SheetOrigin(slot.Source, -1, -1);
    }

    /// <summary>A sheet's size as its file reports it, before the reader's own turn.</summary>
    private PdfPageSize SheetSize(int sheet, PdfPageSize fallback) =>
        sheet >= 0 && sheet < Plan.Count ? Plan[sheet].Size : fallback;

    private TextLayerKey TextKeyFor(int sheet)
    {
        var origin = OriginOf(sheet);
        return new TextLayerKey(origin.DocumentId, origin.PageIndex, RotationOf(sheet));
    }

    /// <summary>The marks on this document. The panel reads it; nothing outside changes it.</summary>
    public AnnotationStore Annotations => _annotations;

    public bool CanUndo => _annotations.CanUndo;

    public bool CanRedo => _annotations.CanRedo;

    /// <summary>The mark the reader has hold of, if any.</summary>
    public Annotation? SelectedAnnotation => _selected;

    /// <summary>
    /// Which kind of mark the panel is talking about: the one the tool makes,
    /// or the one in hand when the reader is picking marks up.
    /// </summary>
    private AnnotationKind StyleKind =>
        _tool == ViewerTool.SelectAnnotation ? _selected?.Kind ?? AnnotationKind.Ink : _tool.ToKind();

    /// <summary>
    /// Colour and width for the next mark of the current kind. Setting it while
    /// a mark is selected changes that mark too: picking one up and then
    /// choosing a colour can only mean recolouring it.
    /// </summary>
    public AnnotationStyle AnnotationStyle
    {
        get => StyleFor(StyleKind);
        set
        {
            _styles[StyleKind] = value;

            // A mark that cannot be changed is not recoloured by picking a
            // colour for the next one.
            if (_selected is { } selected && _selectedPage >= 0 && Annotation.CanBeChanged(selected.Kind))
            {
                var restyled = selected.WithStyle(value);
                _annotations.Replace(_selectedPage, selected, restyled);
                _selected = restyled;
            }

            Canvas.Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The style a kind of mark is drawn with, falling back to what that kind
    /// should look like before anyone has chosen: red for a drawn line, and a
    /// highlighter that is yellow, because that is what a highlighter is.
    /// </summary>
    private AnnotationStyle StyleFor(AnnotationKind kind)
    {
        if (_styles.TryGetValue(kind, out var chosen)) return chosen;

        return kind == AnnotationKind.Highlight
            ? new AnnotationStyle(new AnnotationColor(255, 200, 0), 1f)
            : AnnotationStyle.Default;
    }

    /// <summary>
    /// Rewrites the selected mark's words, letter by letter, as either writer
    /// is typed into — the box on the sheet or the panel.
    ///
    /// Live, so the history does not fill up with one step per keystroke. The
    /// mark as it stood before the first letter is kept here, and
    /// <see cref="CommitText"/> turns the whole run into one thing to take
    /// back. That is the same bargain a drag makes; typing just has no pointer
    /// coming up to say when it is over.
    /// </summary>
    public void SetSelectedText(string text)
    {
        if (_selected is not { } selected || _selectedPage < 0) return;
        if (selected.Text == text) return;

        _textFrom ??= selected;
        _textFromPage = _selectedPage;

        var edited = selected.WithText(text);
        _annotations.ReplaceLive(_selectedPage, selected, edited);
        _selected = edited;

        // The mark grew or shrank by a letter, so the box being typed into has
        // to follow it: written words size their own frame.
        if (_writing is not null && _writing.Id == edited.Id)
        {
            _writing = edited;
            PositionWriter();
        }

        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Closes off a run of typing as one step in the history.
    ///
    /// Called when the caret leaves the words: the box on the sheet closing,
    /// the panel losing focus, another mark being picked up. Harmless when
    /// nothing was typed, and it refuses to record across two different marks —
    /// a step whose two halves are not the same mark is not a change to it.
    /// </summary>
    public void CommitText()
    {
        if (_textFrom is not { } before) return;

        int page = _textFromPage;
        _textFrom = null;
        _textFromPage = -1;

        if (page < 0 || _selected is not { } after || after.Id != before.Id) return;

        _annotations.RememberEdit(page, before, after);
    }

    // --- escribir sobre la propia hoja ------------------------------------

    /// <summary>
    /// True while a label is being typed on the sheet.
    ///
    /// The chrome asks, because the tool letters are accelerators: they fire on
    /// the window before the key reaches whatever has the caret. There is
    /// already a guard for that which asks the focus manager whether a text box
    /// holds it, and it does not catch this box — the letter still got through,
    /// changed the tool, dropped the selection and closed the very box it was
    /// being typed into. Asking the viewer what it is doing needs no such
    /// answer to be right.
    /// </summary>
    public bool IsWriting => _writing is not null;

    /// <summary>The written mark the on-sheet box is open on, if any.</summary>
    private Annotation? _writing;

    private int _writingPage = -1;

    /// <summary>
    /// The mark as it stood before the first letter of the run being typed, and
    /// the sheet it is on. Shared by both writers, because both go through
    /// <see cref="SetSelectedText"/>.
    /// </summary>
    private Annotation? _textFrom;

    private int _textFromPage = -1;

    /// <summary>
    /// Opens the box for typing straight onto the sheet, over the written mark
    /// that is selected.
    ///
    /// Written text only. A comment is a marker whose words live in the panel —
    /// that is what makes it a comment and not a label — and a box over the pin
    /// would cover the very drawing it points at.
    /// </summary>
    public void BeginWriting()
    {
        if (_selected is not { Kind: AnnotationKind.FreeText } mark || _selectedPage < 0) return;

        _writing = mark;
        _writingPage = _selectedPage;

        // Put in before the box is shown, so the TextChanged that raises finds
        // the same words already on the mark and writes nothing back.
        if (WriteBox.Text != mark.Text) WriteBox.Text = mark.Text;

        Show(true);
        PositionWriter();

        // Straight away, from inside the press that placed the mark — the same
        // point at which a comment hands the caret to the panel, which has
        // always worked.
        WriteBox.Focus(FocusState.Programmatic);
        WriteBox.SelectAll();

        // And again once the layout has settled, in case the first attempt was
        // made before the box had been measured. It costs nothing when the
        // caret is already here, and what it buys is worth having: a caret that
        // lands nowhere means the letters reach the window as tool shortcuts,
        // and the tool they choose drops the selection and shuts the very box
        // they were meant for.
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (_writing is null || WriteBox.FocusState is not FocusState.Unfocused) return;

                WriteBox.Focus(FocusState.Programmatic);
                WriteBox.SelectAll();
            });
    }

    /// <summary>
    /// Shows or hides the box without taking it out of the tree.
    ///
    /// Collapsing it would be the obvious way and it is the wrong one: a
    /// control that is not laid out cannot be given the caret, and Focus says
    /// so by quietly returning false rather than failing. Kept realised and
    /// merely transparent, it is ready the instant it is asked for. Idle it is
    /// also out of the tab order and deaf to the pointer, so an invisible text
    /// box cannot catch anything meant for the drawing.
    /// </summary>
    private void Show(bool writing)
    {
        WriteBox.Opacity = writing ? 1 : 0;
        WriteBox.IsHitTestVisible = writing;
        WriteBox.IsTabStop = writing;

        if (!writing)
        {
            // Parked off the canvas so a stray caret cannot blink over the
            // sheet, and narrow so it never widens the layout.
            WriteBox.Margin = new Thickness(-4000, -4000, 0, 0);
        }
    }

    /// <summary>
    /// Puts the box over the words it is writing, at the size they will be
    /// drawn at.
    ///
    /// Driven from <see cref="ClampOrigin"/>, which every scroll, zoom and
    /// resize goes through: the box has to travel with the sheet, or it hangs
    /// in the air over a drawing that has moved out from under it.
    /// </summary>
    private void PositionWriter()
    {
        if (_writing is not { } mark || _writingPage < 0) return;

        if (PageBoxOf(_writingPage) is not { } page)
        {
            // The sheet has been scrolled out of the view, so there is nothing
            // left to sit over. The words are not lost: the mark holds them.
            Show(false);
            return;
        }

        Show(true);

        var box = PlacementOf(page).ToScreen(mark.FrameBox);

        WriteBox.Margin = new Thickness(box.X, box.Y, 0, 0);
        WriteBox.MinWidth = Math.Max(52, box.Width);

        // A label can be turned, so the box it is typed in turns with it.
        //
        // About its own top-left corner, which is the mark's anchor and the
        // very point the words are rotated about when they are drawn. Degrees
        // pass straight through: both this and the engine turn clockwise in a
        // y-down space, so there is no sign to get wrong.
        WriteBox.RenderTransform = mark.RotationDeg == 0f
            ? null
            : new Microsoft.UI.Xaml.Media.RotateTransform { Angle = mark.RotationDeg };

        // Clamped at both ends: far out, an unclamped size rounds to nothing
        // and the caret disappears; far in, it would be a control taller than
        // the window.
        WriteBox.FontSize = Math.Clamp(mark.Style.FontSizePt * _scale, 8, 96);
    }

    /// <summary>
    /// Shuts the box and records the typing as one step.
    ///
    /// Not driven by the focus leaving it, which is what the first attempt did
    /// and why the box shut itself the instant it opened: the canvas takes the
    /// focus on the very press that places the mark, so the box was made
    /// visible, handed the caret, and closed again before a key could reach it.
    /// It closes on the things a reader actually does instead — Escape, a press
    /// somewhere else on the sheet, or picking up another mark.
    /// </summary>
    private void EndWriting()
    {
        if (_writing is null) return;

        _writing = null;
        _writingPage = -1;
        Show(false);

        CommitText();
        Canvas.Invalidate();
    }

    private void OnWriteBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_writing is null) return;

        SetSelectedText(WriteBox.Text);
    }

    private void OnWriteBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Escape finishes the label. Return does not: a written mark is several
        // lines as often as one, and this box is where those get typed.
        if (e.Key != VirtualKey.Escape) return;

        e.Handled = true;
        EndWriting();
        Canvas.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Puts the focus back on the drawing, for the chrome to call after a
    /// button of its own has taken it. The keys that move around the sheet
    /// live on the canvas, so leaving the focus on a toolbar button silently
    /// turns half of them off.
    /// </summary>
    public void TakeKeyboard() => Canvas.Focus(FocusState.Programmatic);

    public void DeleteSelectedAnnotation()
    {
        if (_selected is not { } selected || _selectedPage < 0) return;

        _annotations.Remove(_selectedPage, selected);
        ClearAnnotationSelection();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes back the last change — a mark, or the sheets moving — and shows where it was.</summary>
    public void UndoAnnotation() => StepHistory(_annotations.Undo());

    public void RedoAnnotation() => StepHistory(_annotations.Redo());

    private void StepHistory(EditStep step)
    {
        if (!step.Happened) return;

        if (step.Rearranged)
        {
            // The sheets are somewhere else now, so everything measured against
            // the old order has to be measured again.
            AdoptPlan();
            Canvas.Invalidate();
            return;
        }

        int pageIndex = step.PageIndex;

        // The mark that was selected may be the one that just vanished.
        if (_selected is { } selected && !_annotations.TryFind(selected.Id, out _, out _))
        {
            ClearAnnotationSelection();
        }
        else if (_selected is { } current && _annotations.TryFind(current.Id, out int page, out var refreshed))
        {
            _selected = refreshed;
            _selectedPage = page;
        }

        // Undoing something on a sheet you cannot see would look like nothing
        // happening at all.
        if (pageIndex != CurrentPageIndex)
        {
            GoToPage(pageIndex);
        }

        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearAnnotationSelection()
    {
        // Before the selection goes: shutting the box records what was typed,
        // and it reads the selection to know what to record it against.
        EndWriting();
        CommitText();

        _selected = null;
        _selectedPage = -1;
    }

    /// <summary>True while a turn is on screen but not yet written to the file.</summary>
    public bool HasUnsavedRotations => Plan.HasTurns;

    /// <summary>
    /// In single-sheet mode the page is whichever one is laid out; in
    /// continuous mode it is whichever fills most of the viewport.
    /// </summary>
    public int CurrentPageIndex
    {
        get
        {
            if (_layout is not { } layout) return 0;
            if (_layoutMode == ViewerLayoutMode.SinglePage) return _currentPageIndex;

            var view = ViewportInDocSpace();
            return layout.DominantPageIndex(view.Top, view.Bottom);
        }
    }

    public int CurrentPageNumber => _layout is null ? 0 : CurrentPageIndex + 1;

    public bool CanGoPrevious => _layout is not null && CurrentPageIndex > 0;

    public bool CanGoNext => _layout is not null && CurrentPageIndex < PageCount - 1;

    public ViewerLayoutMode LayoutMode
    {
        get => _layoutMode;
        set
        {
            if (_layoutMode == value) return;

            // Carry the sheet you were looking at across the switch.
            int page = CurrentPageIndex;
            _layoutMode = value;
            _currentPageIndex = page;
            RebuildLayout();
        }
    }

    /// <summary>Sheets side by side: one, or a two-up spread.</summary>
    public int Columns
    {
        get => _columns;
        set
        {
            int columns = Math.Clamp(value, 1, 2);
            if (_columns == columns) return;

            int page = CurrentPageIndex;
            _columns = columns;
            _currentPageIndex = page;
            RebuildLayout();
        }
    }

    /// <summary>
    /// How the zoom follows the window. Setting it re-fits at once; the reader
    /// zooming by hand drops it back to <see cref="ViewerFitMode.Free"/>.
    /// </summary>
    public ViewerFitMode FitMode
    {
        get => _fitMode;
        set
        {
            _fitMode = value;
            if (value == ViewerFitMode.Free) return;

            _fitDirty = true;
            _fitSnap = true;
            Canvas.Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void GoToPage(int pageIndex)
    {
        if (_pageSizes.Count == 0 || _layout is null) return;

        StopZoomGlide();

        int target = Math.Clamp(pageIndex, 0, _pageSizes.Count - 1);
        if (target == CurrentPageIndex && _layoutMode == ViewerLayoutMode.SinglePage) return;

        _currentPageIndex = target;

        if (_layoutMode == ViewerLayoutMode.SinglePage)
        {
            RebuildLayout();
            return;
        }

        foreach (var page in _layout.Pages)
        {
            if (page.Index != target) continue;
            _origin = new Vector2(_origin.X, page.YPt);
            break;
        }

        if (_fitMode == ViewerFitMode.Page)
        {
            _fitDirty = true;
            _fitSnap = true;
        }

        ClampOrigin();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NextPage() => GoToPage(CurrentPageIndex + 1);

    public void PreviousPage() => GoToPage(CurrentPageIndex - 1);

    /// <summary>Rebuilds document space for the current mode, keeping the zoom level.</summary>
    private void RebuildLayout()
    {
        if (_pageSizes.Count == 0)
        {
            _layout = null;
            return;
        }

        ClearSelection();

        _layout = _layoutMode == ViewerLayoutMode.SinglePage
            ? DocumentLayout.SinglePage(_pageSizes, _currentPageIndex, _columns)
            : DocumentLayout.Continuous(_pageSizes, _columns);

        if (_layoutMode == ViewerLayoutMode.SinglePage)
        {
            _origin = Vector2.Zero;
        }
        else
        {
            foreach (var page in _layout.Pages)
            {
                if (page.Index != _currentPageIndex) continue;
                _origin = new Vector2(_origin.X, page.YPt);
                break;
            }
        }

        if (_fitMode != ViewerFitMode.Free)
        {
            _fitDirty = true;
            _fitSnap = true;
        }

        ClampOrigin();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public double ZoomPercent => _scale * 100.0;

    public bool HasSelection => _selectionPage >= 0 && _selectionAnchor >= 0 && _selectionFocus >= 0;

    /// <summary>
    /// Draws every stroke as a hairline instead of at its authored width.
    /// Every cached tile is discarded, since they were rasterized with the
    /// other setting.
    /// </summary>
    public bool ThinLines
    {
        get => _thinLines;
        set
        {
            if (_thinLines == value) return;
            _thinLines = value;

            if (_documentId >= 0)
            {
                _queue.SetThinLines(_documentId, value);
            }

            _cache.Clear();
            ForgetSnapPixels();
            _inFlight.Clear();

            // The composed tiles were rasterized at the other setting too, and
            // a comparison drawn half at each weight is a page full of changes
            // that are not there.
            if (_revision is not null)
            {
                RestartComparison();
            }

            Canvas.Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ViewerTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;

            // A shape half built is finished by the change of tool if it has
            // enough corners to be a shape, and dropped if it has not: leaving
            // it hanging on an invisible tool would lose it either way.
            if (_placingVertices) FinishVertices();

            _tool = value;
            if (value == ViewerTool.Pan)
            {
                ClearSelection();
            }

            // Reaching for a drawing tool means the mark in hand is done with.
            // Left selected, its halo would sit over the sheet being drawn on,
            // and the next colour chosen would recolour it by surprise.
            if (value != ViewerTool.SelectAnnotation)
            {
                ClearAnnotationSelection();
            }
            UpdateCursor();
            Canvas.Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task OpenAsync(string path, string? password = null)
    {
        var info = await _queue.OpenDocumentAsync(path, password);

        if (_documentId >= 0)
        {
            _ = _queue.CloseDocumentAsync(_documentId);
        }
        _documentId = info.DocumentId;
        SourcePath = path;
        _documentGeneration++;
        StopZoomGlide();
        StopComparing();
        _cache.Clear();
        ForgetSnapPixels();
        _inFlight.Clear();
        _textLayers.Clear();
        _textLru.Clear();
        _textInFlight.Clear();
        ClearSelection();
        ClearAnnotationSelection();
        _annotations.Clear();
        CloseExtraSources();
        ResetSearch();

        _annotations.LoadPlan(PagePlan.Identity(info.Pages, path, password));
        _pageSizes = Plan.EffectiveSizes();
        _currentPageIndex = 0;
        _layout = _layoutMode == ViewerLayoutMode.SinglePage
            ? DocumentLayout.SinglePage(_pageSizes, 0, _columns)
            : DocumentLayout.Continuous(_pageSizes, _columns);
        _origin = Vector2.Zero;
        _fitDirty = true;
        _fitSnap = true;

        Canvas.Invalidate();
        // Opening a drawing is the reader saying they want to move around it,
        // so the canvas takes the keyboard rather than making them click first.
        Canvas.Focus(FocusState.Programmatic);
        DocumentOpened?.Invoke(this, EventArgs.Empty);
        ViewChanged?.Invoke(this, EventArgs.Empty);

        // Awaited, not left running: a mark made in the moment before the
        // file's own marks arrived would be wiped by them landing.
        await LoadAnnotationsAsync(_documentId, _documentGeneration);
    }

    /// <summary>
    /// Picks up the marks the file already carries. They come back as the
    /// baseline — nothing to save, nothing to undo — which is what makes
    /// "there are changes" mean something.
    /// </summary>
    private async Task LoadAnnotationsAsync(int documentId, int generation)
    {
        try
        {
            var marks = await _queue.ReadAnnotationsAsync(documentId);
            if (generation != _documentGeneration) return;

            _annotations.Load(marks);
            Canvas.Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A drawing whose annotations cannot be read is still a drawing
            // worth showing, so this does not take the document down with it.
            System.Diagnostics.Debug.WriteLine($"ZenInk: no se pudieron leer las anotaciones: {ex.Message}");
        }
    }

    /// <summary>Re-fits to the current mode; falls back to fitting the width.</summary>
    public void RefitNow()
    {
        if (_fitMode == ViewerFitMode.Free)
        {
            _fitMode = ViewerFitMode.Width;
        }

        _fitDirty = true;
        _fitSnap = true;
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }


    /// <summary>
    /// Back to the zoom the readout calls 100 %. Reading a drawing means moving
    /// between "the whole sheet" and "this detail" all day, and a known
    /// magnification to return to is what keeps that from becoming guesswork.
    /// </summary>
    public void ZoomToActualSize() => ZoomTo(ViewportCentre(), 1.0);

    /// <summary>Zooms about the centre of the viewport, for the toolbar's zoom buttons.</summary>
    public void ZoomBy(double factor) => ZoomAt(ViewportCentre(), factor);

    private Point ViewportCentre() => new(Canvas.ActualWidth / 2, Canvas.ActualHeight / 2);

    // --- sheets ---------------------------------------------------------

    /// <summary>Turns the sheet in view. Positive is clockwise, in quarter turns.</summary>
    public void RotateCurrentPage(int quarterTurns) => RotatePages([CurrentPageIndex], quarterTurns);

    /// <summary>Turns every sheet in the document by the same amount.</summary>
    public void RotateAllPages(int quarterTurns) => RotatePages([.. Enumerable.Range(0, Plan.Count)], quarterTurns);

    /// <summary>
    /// Turns a set of sheets. The tiles of the old orientation stay cached
    /// under their own key, so turning a sheet back is instant; only the text
    /// of the turned sheets is dropped, since its boxes are the one thing a
    /// turn genuinely invalidates.
    /// </summary>
    public void RotatePages(IReadOnlyCollection<int> sheets, int quarterTurns) =>
        Rearrange(Plan.Rotate(sheets, quarterTurns));

    /// <summary>
    /// Moves sheets so they sit together starting at
    /// <paramref name="destination"/>, which is read against the order as it is
    /// now — the gap the reader is pointing at.
    /// </summary>
    public void MovePages(IReadOnlyCollection<int> sheets, int destination) =>
        Rearrange(Plan.Move(sheets, destination));

    /// <summary>
    /// Moves sheets so the first of them becomes sheet
    /// <paramref name="landing"/> — a sheet number in, not a gap.
    /// </summary>
    public void MovePagesTo(IReadOnlyCollection<int> sheets, int landing) =>
        Rearrange(Plan.MoveTo(sheets, landing));

    /// <summary>
    /// The sheets in exactly this order, named by where they are now. This is
    /// what a drag in the pages panel leaves behind.
    /// </summary>
    public void ReorderPages(IReadOnlyList<int> order) => Rearrange(Plan.Reorder(order));

    /// <summary>Takes sheets out. The last sheet of a document cannot go: the caller checks.</summary>
    public void RemovePages(IReadOnlyCollection<int> sheets) => Rearrange(Plan.Remove(sheets));

    /// <summary>
    /// Every sheet as the pages panel needs it: which open document and page it
    /// is drawn from, how it is turned, and how big it ends up. One list rather
    /// than a question per sheet, because the panel rebuilds all of it at once.
    /// </summary>
    public IReadOnlyList<SheetInfo> Sheets
    {
        get
        {
            var sheets = new SheetInfo[Plan.Count];
            for (int i = 0; i < sheets.Length; i++)
            {
                var origin = OriginOf(i);
                var slot = Plan[i];
                sheets[i] = new SheetInfo(
                    origin.DocumentId, origin.PageIndex, slot.QuarterTurns, slot.EffectiveSize, slot.IsBlank);
            }
            return sheets;
        }
    }

    /// <summary>The document's own index of bookmarks, or an empty list.</summary>
    public Task<IReadOnlyList<OutlineEntry>> ReadOutlineAsync() =>
        _documentId < 0
            ? Task.FromResult<IReadOnlyList<OutlineEntry>>([])
            : _queue.ReadOutlineAsync(_documentId);

    /// <summary>Puts a copy of each sheet straight behind the last of them, marks included.</summary>
    public void DuplicatePages(IReadOnlyCollection<int> sheets) => Rearrange(Plan.Duplicate(sheets));

    /// <summary>Puts blank paper in.</summary>
    public void InsertBlankPages(int destination, PdfPageSize size, int count = 1) =>
        Rearrange(Plan.InsertBlank(destination, size, count));

    /// <summary>
    /// Brings sheets in from another PDF. The file is opened and stays open, so
    /// its sheets are on screen straight away rather than after a save — and so
    /// their own marks come across editable.
    /// </summary>
    public Task InsertPagesAsync(int destination, string path, IReadOnlyList<int> pages, string? password = null) =>
        InsertPagesAsync(destination, [new PageInsertion(path, password, pages)]);

    /// <summary>
    /// Brings sheets in from several PDFs at once, in the order given — which
    /// is the ordinary way to build a set out of a folder of one-sheet
    /// drawings.
    ///
    /// One rearrangement for the whole batch, not one per file: picking
    /// fourteen drawings was one act, so one step back has to undo it.
    /// </summary>
    public async Task InsertPagesAsync(int destination, IReadOnlyList<PageInsertion> batch)
    {
        var batches = new List<PageBatch>(batch.Count);
        var opened = new List<(int DocumentId, int Count)>(batch.Count);

        foreach (var insertion in batch)
        {
            var info = await _queue.OpenDocumentAsync(insertion.Path, insertion.Password);

            // Opened twice — a second batch from the same file — means letting
            // the newcomer go: the sheets already on screen are drawn from the
            // first.
            if (!_openSources.TryAdd(insertion.Path, info.DocumentId))
            {
                _ = _queue.CloseDocumentAsync(info.DocumentId);
            }

            var brought = new List<(int, PdfPageSize)>(insertion.Pages.Count);
            foreach (int page in insertion.Pages)
            {
                if (page >= 0 && page < info.Pages.Count) brought.Add((page, info.Pages[page]));
            }
            if (brought.Count == 0) continue;

            batches.Add(new PageBatch(new PagePlanSource(insertion.Path, insertion.Password), brought));
            opened.Add((_openSources[insertion.Path], brought.Count));
        }

        if (batches.Count == 0) return;

        int at = Math.Clamp(destination, 0, Plan.Count);
        Rearrange(Plan.InsertMany(at, batches));

        // Marks the other files already carried, so a reviewed sheet arrives
        // reviewed. Read after the sheets are placed, since where they landed
        // is what says which slots to put them on.
        int generation = _documentGeneration;
        foreach (var (documentId, count) in opened)
        {
            await AdoptForeignMarksAsync(documentId, at, count, generation);
            at += count;
        }
    }

    private async Task AdoptForeignMarksAsync(int documentId, int firstSheet, int count, int generation)
    {
        try
        {
            var marks = await _queue.ReadAnnotationsAsync(documentId);
            if (generation != _documentGeneration || marks.Count == 0) return;

            for (int i = 0; i < count; i++)
            {
                int sheet = firstSheet + i;
                if (sheet >= Plan.Count) break;
                if (!marks.TryGetValue(Plan[sheet].PageIndex, out var forPage) || forPage.Count == 0) continue;

                foreach (var mark in forPage)
                {
                    _annotations.Add(sheet, mark);
                }
            }

            Canvas.Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A sheet whose marks cannot be read is still a sheet worth having.
            System.Diagnostics.Debug.WriteLine($"ZenInk: no se pudieron leer las marcas de las hojas traídas: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the chosen sheets to a file of their own, with their turns and
    /// their marks. Nothing here touches the document in hand.
    /// </summary>
    public Task ExtractPagesAsync(IReadOnlyCollection<int> sheets, string targetPath)
    {
        var edit = Plan.Keep(sheets);
        return _queue.SaveChangesCopyAsync(
            edit.Plan.Compacted(),
            targetPath,
            RemapMarks(edit),
            password: Plan.Sources[0].Password);
    }

    /// <summary>The marks as they would be keyed if the plan were rearranged this way.</summary>
    private Dictionary<int, IReadOnlyList<Annotation>> RemapMarks(PageEdit edit)
    {
        var marks = new Dictionary<int, IReadOnlyList<Annotation>>();
        for (int i = 0; i < edit.OriginOfNew.Length; i++)
        {
            int origin = edit.OriginOfNew[i];
            if (origin < 0) continue;

            var forPage = _annotations.ForPage(origin);
            if (forPage.Count > 0) marks[i] = forPage;
        }
        return marks;
    }

    /// <summary>
    /// Takes a rearrangement on, and puts back everything that was measured
    /// against the old order: the layout, the text selection, the search.
    /// </summary>
    private void Rearrange(PageEdit edit)
    {
        if (edit.Plan.Count == 0 || ReferenceEquals(edit.Plan, Plan)) return;

        _annotations.Rearrange(edit);
        AdoptPlan();
    }

    private void AdoptPlan()
    {
        StopZoomGlide();
        _pageSizes = Plan.EffectiveSizes();
        ClearSelection();
        ClearAnnotationSelection();

        // Turning a sheet changes how the revision has to lie on it, and moving
        // sheets renumbers what each one is paired with. Both are measured
        // against the old order, so both are thrown away.
        if (_revision is not null)
        {
            RestartComparison();
        }

        // Hit rectangles are in page-local space, so a turn moves them and a
        // move renumbers them. The ranges would survive a turn, but rescanning
        // is simpler than repairing them and rearranging is a deliberate,
        // occasional act.
        if (_searchQuery.Length > 0)
        {
            RestartSearch();
        }

        _currentPageIndex = Math.Clamp(_currentPageIndex, 0, Math.Max(0, Plan.Count - 1));
        RebuildLayout();
        PagesChanged?.Invoke(this, EventArgs.Empty);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lets go of the files sheets were brought from. The document's own is not one of them.</summary>
    private void CloseExtraSources()
    {
        foreach (int documentId in _openSources.Values)
        {
            _ = _queue.CloseDocumentAsync(documentId);
        }
        _openSources.Clear();
    }

    /// <summary>
    /// Writes the pending turns and marks into the file the document came from,
    /// then picks the reopened document back up where the reader left off.
    /// Returns the failure to report, or null when it worked.
    /// </summary>
    public async Task<string?> SaveChangesAsync()
    {
        if (SourcePath is not { } path || _documentId < 0) return "El documento no tiene un archivo asociado.";

        bool turned = HasUnsavedRotations;
        var outcome = await _queue.ApplyChangesInPlaceAsync(_documentId, path, Plan, _annotations.Snapshot());
        AdoptReopenedDocument(outcome.Document);

        if (!outcome.Saved) return outcome.Error;

        if (turned)
        {
            // A saved turn moves into the page's own /Rotate, and with it the
            // sheet space the marks are held in. Reading them back is how they
            // arrive in the new one — and it is why the history goes: a step
            // recorded in the old space would put a mark somewhere else.
            await LoadAnnotationsAsync(_documentId, _documentGeneration);
        }
        else
        {
            _annotations.MarkSaved();
        }

        ViewChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>
    /// Writes everything pending and then burns the marks into the drawing.
    ///
    /// This is the one change that cannot be taken back: what was an annotation
    /// becomes part of the page, so nobody can move it, recolour it or delete
    /// it — which is the point. The marks are written first, so a flatten of a
    /// review that was never saved still burns in what is on screen.
    /// </summary>
    public async Task<string?> FlattenAsync()
    {
        if (SourcePath is not { } path || _documentId < 0) return "El documento no tiene un archivo asociado.";

        var outcome = await _queue.ApplyChangesInPlaceAsync(
            _documentId, path, Plan, _annotations.Snapshot(), flatten: true);

        AdoptReopenedDocument(outcome.Document);
        if (!outcome.Saved) return outcome.Error;

        // Nothing of ours is left in the file to pick up again: the marks are
        // the drawing now, and the tiles are where they show from here on.
        ClearAnnotationSelection();
        _annotations.Clear();

        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>
    /// Writes the turns and marks to another file, leaving this document as it
    /// is. With <paramref name="flatten"/>, the copy is the flattened one and
    /// the document in hand keeps its marks editable.
    /// </summary>
    public Task SaveChangesCopyAsync(string targetPath, bool flatten = false)
    {
        if (SourcePath is null) throw new InvalidOperationException("El documento no tiene un archivo asociado.");
        return _queue.SaveChangesCopyAsync(Plan, targetPath, _annotations.Snapshot(), flatten);
    }

    /// <summary>
    /// True while the reader is being asked to drag out where the signature
    /// goes. Setting it changes the pointer and nothing else; the gesture ends
    /// by raising <see cref="SignaturePlaced"/>.
    /// </summary>
    public bool IsPlacingSignature
    {
        get;
        set
        {
            if (field == value) return;

            field = value;
            _bandIsForSignature = false;
            _isZoomBanding = false;
            UpdateCursor();
            Canvas.Invalidate();
        }
    }

    /// <summary>
    /// The box the reader drew, or null if they gave up on it. In sheet points,
    /// which is the same space marks are held in — the caller turns it into the
    /// page's own coordinates through the queue.
    /// </summary>
    public event EventHandler<SignatureSpot?>? SignaturePlaced;

    /// <summary>Raised when a capture box is drawn — or with null when the gesture was too small to mean one.</summary>
    public event EventHandler<CaptureSpot?>? RegionCaptured;

    /// <summary>Raised when a calibration drag ends long enough to be worth asking about.</summary>
    public event EventHandler<CalibrationDrag>? CalibrationDragged;

    /// <summary>
    /// Whether a measurement is pulled onto the drawing's own lines. On by
    /// default: measuring by eye at 30 % zoom is how a room comes out a
    /// centimetre short, and the reader who wants a point in mid-air can say
    /// so.
    /// </summary>
    public bool SnapToInk { get; set; } = true;

    /// <summary>
    /// Whether a measuring gesture is held to the horizontal or the vertical.
    /// Off by default: it is the reader who knows whether what they are about
    /// to measure is square, and a lock nobody asked for is a wrong number
    /// nobody questions.
    /// </summary>
    public bool AxisLock { get; set; }

    /// <summary>
    /// Whether the next measuring point is held to an axis right now — the
    /// button, with Shift meaning the opposite of whatever it says.
    ///
    /// Shift is what a hand reaches for when the lock is on and this one line
    /// runs at an angle, and what it reaches for when the lock is off and this
    /// one line has to be square. Inverting rather than forcing is what makes
    /// it work both ways round.
    ///
    /// Never for an angle: an angle held to the axes can only ever read a
    /// right angle or a straight one, which is not a measurement, it is the
    /// tool refusing to answer.
    /// </summary>
    private bool AxisHeld(bool shift) =>
        _tool.IsMeasurement() && _tool != ViewerTool.Angle && (AxisLock ^ shift);

    /// <summary>
    /// The point a measuring gesture really takes, once both helps have had
    /// their say.
    ///
    /// The axis comes last, after the pull onto the ink. The other order looks
    /// equivalent and is not: snapping a point that was already square moves it
    /// off square again, and then the lock is a promise the drawing does not
    /// keep. This way the locked coordinate is exact and the free one is still
    /// free to land on a line.
    /// </summary>
    private Vector2 Placed(PageBox page, Point position, Vector2? anchor, bool shift)
    {
        var point = Snapped(page, SheetPointClamped(page, position));

        return anchor is { } from && AxisHeld(shift)
            ? AnnotationGeometry.OnAxis(from, point)
            : point;
    }

    /// <summary>
    /// How far the pointer may be from a line and still be pulled onto it, in
    /// dips. About four millimetres on screen: near enough that it only catches
    /// what was aimed at, far enough that it catches it.
    /// </summary>
    private const float SnapReachDips = 14f;

    /// <summary>
    /// Tiles kept as pixels for snapping. Sixteen of them is a screenful at a
    /// reading zoom, which is the only place a measurement is being taken.
    /// </summary>
    private const int MostSnapTiles = 16;

    private readonly Dictionary<TileKey, byte[]> _snapPixels = new();
    private readonly List<TileKey> _snapOrder = new();

    /// <summary>Where the last point was pulled to, for drawing the ring that says it was.</summary>
    private Vector2? _snappedAt;

    private void KeepForSnapping(TileKey key, TileBitmapData tile)
    {
        if (_snapPixels.ContainsKey(key)) return;

        _snapPixels[key] = tile.Bgra;
        _snapOrder.Add(key);

        while (_snapOrder.Count > MostSnapTiles)
        {
            _snapPixels.Remove(_snapOrder[0]);
            _snapOrder.RemoveAt(0);
        }
    }

    private void ForgetSnapPixels()
    {
        _snapPixels.Clear();
        _snapOrder.Clear();
        _snappedAt = null;
    }

    /// <summary>
    /// The point a measurement should actually take, pulled onto the drawing
    /// when there is a line within reach.
    ///
    /// It looks at the tile under the point — the very picture on screen — so
    /// it is as precise as the zoom is and no slower than a dictionary lookup.
    /// A point over a tile that has not arrived yet is left alone, which is the
    /// same answer as no ink.
    /// </summary>
    private Vector2 Snapped(PageBox page, Vector2 sheetPoint)
    {
        _snappedAt = null;
        if (!SnapToInk || !_tool.IsMeasurement()) return sheetPoint;

        var origin = OriginOf(page.Index);
        if (origin.IsBlank) return sheetPoint;

        var sheet = SheetSize(page.Index, new PdfPageSize(page.WidthPt, page.HeightPt));
        int rotation = RotationOf(page.Index);
        var local = SheetTurn.ToDisplay(sheetPoint, sheet.WidthPt, sheet.HeightPt, rotation);

        double dpiScale = Canvas.Dpi / 96.0;
        int level = ZoomLevels.LevelForScale(_scale * dpiScale);
        double levelScale = ZoomLevels.ScaleForLevel(level);
        double tilePt = ZoomLevels.TileSize / levelScale;
        if (tilePt <= 0) return sheetPoint;

        int col = (int)Math.Floor(local.X / tilePt);
        int row = (int)Math.Floor(local.Y / tilePt);

        var key = new TileKey(origin.PageIndex, level, col, row, rotation, origin.DocumentId);
        if (!_snapPixels.TryGetValue(key, out var pixels)) return sheetPoint;

        var inTile = new Vector2(
            (float)((local.X - (col * tilePt)) * levelScale),
            (float)((local.Y - (row * tilePt)) * levelScale));

        // The reach is given on screen and searched in the tile's own pixels,
        // which are a different size whenever the zoom sits between two levels.
        int reach = Math.Max(2, (int)Math.Round(SnapReachDips / Math.Max(_scale, 0.01) * levelScale));

        if (InkSnap.Snap(pixels, ZoomLevels.TileSize, ZoomLevels.TileSize, inTile, reach) is not { } found)
        {
            return sheetPoint;
        }

        var pulled = new Vector2(
            (float)((col * tilePt) + (found.X / levelScale)),
            (float)((row * tilePt) + (found.Y / levelScale)));

        var snapped = SheetTurn.ToSheet(pulled, sheet.WidthPt, sheet.HeightPt, rotation);
        _snappedAt = snapped;
        return snapped;
    }

    /// <summary>What a sheet is drawn to, or null while it has never been calibrated.</summary>
    public SheetScale? ScaleOf(int pageIndex) => _annotations.ScaleOf(pageIndex);

    /// <summary>The scale of the sheet in view, which is the one the panel talks about.</summary>
    public SheetScale? ScaleHere => ScaleOf(CurrentPageIndex);

    /// <summary>
    /// What every calibrated sheet is drawn to. The print path needs them all:
    /// a set can carry a site plan at 1:500 and a detail at 1:20, and printing
    /// both at one drawing scale is two different factors.
    /// </summary>
    public IReadOnlyDictionary<int, SheetScale> Scales()
    {
        var scales = new Dictionary<int, SheetScale>();
        for (int sheet = 0; sheet < _pageSizes.Count; sheet++)
        {
            if (_annotations.ScaleOf(sheet) is { } scale) scales[sheet] = scale;
        }
        return scales;
    }

    /// <summary>
    /// Sets what a sheet is drawn to, bringing the measurements already on it
    /// up to it. Null takes the calibration away again.
    /// </summary>
    public void CalibrateSheet(int pageIndex, SheetScale? scale)
    {
        if (pageIndex < 0) return;

        _annotations.SetScale(pageIndex, scale);
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The signature waiting to be written, drawn on the sheet where it will land.</summary>
    public PendingSignature? Pending { get; private set; }

    public void ShowPendingSignature(PendingSignature pending)
    {
        Pending = pending;
        Canvas.Invalidate();
    }

    /// <summary>
    /// Puts a stamp on a sheet: a box with a few lines in it, and a mark like
    /// any other — it can be moved, recoloured, taken back and rubbed out.
    ///
    /// It is picked up straight away, because a stamp is nearly always dragged
    /// a little after it lands, and because the panel then says what it is.
    /// </summary>
    public void PlaceStamp(int pageIndex, RectPt box, string text)
    {
        if (pageIndex < 0 || pageIndex >= _pageSizes.Count) return;

        var mark = new Annotation(
            AnnotationKind.Stamp,
            [new Vector2(box.Left, box.Top), new Vector2(box.Right, box.Bottom)],
            AnnotationStyle,
            text: text,
            author: Author);

        _annotations.Add(pageIndex, mark);
        _selected = mark;
        _selectedPage = pageIndex;
        Tool = ViewerTool.SelectAnnotation;

        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearPendingSignature()
    {
        if (Pending is null) return;

        Pending = null;
        _draggingPending = false;
        Canvas.Invalidate();
    }

    /// <summary>The signatures the file carries, and whether each of them adds up.</summary>
    public IReadOnlyList<PdfSignatureInfo> ReadSignatures() =>
        SourcePath is { } path && File.Exists(path) ? PdfSignatures.Read(path) : [];

    /// <summary>
    /// Turns a box the reader drew on the sheet into the page's own coordinates,
    /// and reports the page's /Rotate with it so the stamp can be drawn level.
    /// </summary>
    public Task<(RectPt Rect, int QuarterTurns)> ToPdfRectAsync(int pageIndex, RectPt sheetRect) =>
        _queue.ToPdfRectAsync(_documentId, pageIndex, sheetRect);

    /// <summary>
    /// Signs the file this document came from, then picks the reopened document
    /// back up.
    ///
    /// Nothing pending is written here on purpose. A signature covers the file
    /// as it stands, so saving has to have happened first and visibly — see the
    /// caller, which says so before it signs.
    /// </summary>
    public async Task<string?> SignAsync(
        IPdfSigner signer, PdfSignatureOptions options, IRevocationSource? validation = null)
    {
        if (SourcePath is not { } path || _documentId < 0) return "El documento no tiene un archivo asociado.";

        var outcome = await _queue.SignInPlaceAsync(_documentId, path, signer, options, validation);
        AdoptReopenedDocument(outcome.Document);

        if (!outcome.Saved) return outcome.Error;

        // The signature's own form field is now an annotation on the page, and
        // the marks were reloaded with the document: nothing of ours changed,
        // but the tiles have.
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>
    /// Signs into another file, leaving this one unsigned and its marks still
    /// editable.
    ///
    /// The copy is written first and signed second, in that order and not the
    /// other way round: a signature covers the file it sits in, so a mark that
    /// was not in the file yet would not be signed.
    /// </summary>
    public async Task SignCopyAsync(string targetPath, IPdfSigner signer, PdfSignatureOptions options)
    {
        if (SourcePath is null) throw new InvalidOperationException("El documento no tiene un archivo asociado.");

        await SaveChangesCopyAsync(targetPath);
        await PdfRenderQueue.SignCopyAsync(targetPath, targetPath, signer, options);
    }

    /// <summary>
    /// Takes over the document handle a save handed back. The sheets now carry
    /// their turns in the file, so the reader's own turns reset to none and the
    /// layout comes out identical — which is what lets the view stay put across
    /// a save.
    /// </summary>
    private void AdoptReopenedDocument(PdfDocumentInfo info)
    {
        _documentId = info.DocumentId;
        _documentGeneration++;
        _cache.Clear();
        ForgetSnapPixels();
        _inFlight.Clear();
        _textLayers.Clear();
        _textLru.Clear();
        _textInFlight.Clear();

        // The sheets now stand in the file the way they stand on screen, so the
        // plan starts over from what came back: nothing moved, nothing turned.
        // That is what lets the view stay put across a save.
        _annotations.LoadPlan(PagePlan.Identity(info.Pages, SourcePath ?? string.Empty, Plan.Sources[0].Password));
        CloseExtraSources();
        _pageSizes = Plan.EffectiveSizes();

        if (_thinLines)
        {
            _queue.SetThinLines(_documentId, true);
        }

        if (_searchQuery.Length > 0)
        {
            RestartSearch();
        }

        // A save that moved sheets gives back a different number of them, so
        // the strip has to be measured again rather than only redrawn.
        _currentPageIndex = Math.Clamp(_currentPageIndex, 0, Math.Max(0, Plan.Count - 1));
        RebuildLayout();

        Canvas.Invalidate();
        PagesChanged?.Invoke(this, EventArgs.Empty);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // --- selection ------------------------------------------------------

    /// <summary>Selects all the text on the sheet in view, if its text layer is ready.</summary>
    public void SelectCurrentPage()
    {
        if (_layout is null) return;

        int pageIndex = CurrentPageIndex;
        EnsureTextLayer(pageIndex);

        if (!_textLayers.TryGetValue(TextKeyFor(pageIndex), out var layer)
            || layer.Count == 0)
        {
            return;
        }

        _selectionPage = pageIndex;
        _selectionAnchor = 0;
        _selectionFocus = layer.Count - 1;
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public string? GetSelectedText()
    {
        if (!HasSelection) return null;
        if (!_textLayers.TryGetValue(TextKeyFor(_selectionPage), out var layer))
        {
            return null;
        }

        string text = layer.GetText(_selectionAnchor, _selectionFocus);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public void CopySelection()
    {
        if (GetSelectedText() is not { } text) return;

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    /// <summary>
    /// Called when this viewer's tab is shown or hidden. A hidden tab drops its
    /// rasterized tiles and its parsed pages: only one document is ever on
    /// screen, and re-rendering the visible tiles on return is fast, whereas
    /// keeping every tab's tiles would multiply the memory budget by the tab
    /// count. It also sidesteps holding GPU bitmaps across the unload/reload
    /// the TabView performs when switching tabs.
    /// </summary>
    public void SetActive(bool active)
    {
        _isActive = active;

        if (active)
        {
            Canvas.Invalidate();
            return;
        }

        // The glide is driven by a static event: a tab that goes away without
        // unhooking would keep drawing itself off screen, and keep itself
        // alive doing it.
        StopZoomGlide();
        _cache.Clear();
        ForgetSnapPixels();
        _inFlight.Clear();
        _compareCache.Clear();
        _compareInFlight.Clear();
        if (_documentId >= 0)
        {
            _queue.ReleasePages(_documentId);
        }
        if (_revision is { } revision)
        {
            _queue.ReleasePages(revision.DocumentId);
        }
    }

    /// <summary>
    /// Releases the document and its GPU resources. Called explicitly when the
    /// tab closes — not from Unloaded, which a TabView also raises merely for
    /// switching away from this tab.
    /// </summary>
    public void CloseDocument()
    {
        StopZoomGlide();
        StopComparing();
        _cache.Clear();
        ForgetSnapPixels();
        _inFlight.Clear();
        _textLayers.Clear();
        _textLru.Clear();
        _textInFlight.Clear();
        ClearSelection();
        ClearAnnotationSelection();
        _annotations.Clear();
        ResetSearch();

        if (_documentId >= 0)
        {
            _ = _queue.CloseDocumentAsync(_documentId);
            _documentId = -1;
        }
        CloseExtraSources();

        _layout = null;
        _pageSizes = [];
        Canvas.RemoveFromVisualTree();
    }

    private void UpdateCursor() => ProtectedCursor = InputSystemCursor.Create(
        IsPlacingSignature ? InputSystemCursorShape.Cross : CursorForTool());

    private InputSystemCursorShape CursorForTool() => _tool switch
    {
        // The highlighter picks out text, so it wears the text cursor.
        ViewerTool.SelectText or ViewerTool.Highlight => InputSystemCursorShape.IBeam,
        ViewerTool.ZoomRectangle or ViewerTool.CaptureRegion => InputSystemCursorShape.Cross,
        ViewerTool.SelectAnnotation => InputSystemCursorShape.Arrow,
        // Drawing wants a cursor whose hot spot you can aim: a hand would hide
        // the very corner the mark is meant to start on.
        _ when _tool.DragsOnSheet() => InputSystemCursorShape.Cross,
        _ => InputSystemCursorShape.Hand,
    };

    private void ClearSelection()
    {
        _selectionPage = -1;
        _selectionAnchor = -1;
        _selectionFocus = -1;
    }

    private void OnCopyAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        CopySelection();
        args.Handled = true;
    }

    private void OnSelectAllAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_tool != ViewerTool.SelectText) return;
        SelectCurrentPage();
        args.Handled = true;
    }

    // --- viewport -------------------------------------------------------

    private Rect ViewportInDocSpace()
    {
        double width = Canvas.ActualWidth / _scale;
        double height = Canvas.ActualHeight / _scale;
        return new Rect(_origin.X, _origin.Y, Math.Max(width, 0), Math.Max(height, 0));
    }

    /// <summary>
    /// Applies a deferred fit once the canvas actually has a size. Fitting
    /// straight after the document loads is unreliable — the canvas may still
    /// be zero-sized or mid-layout, which is what produced inconsistent side
    /// margins before.
    ///
    /// A fit the reader asked for jumps to the top of what it fitted; a fit
    /// merely re-run because the window changed size keeps the middle of the
    /// view where it was, so dragging a window edge does not lose their place.
    /// </summary>
    private void ApplyPendingFit()
    {
        if (!_fitDirty || _fitMode == ViewerFitMode.Free || _layout is not { } layout) return;
        if (Canvas.ActualWidth <= 0 || Canvas.ActualHeight <= 0) return;

        // A fit is a scale chosen by something other than the reader's hand —
        // a menu, or the window changing size — and it replaces whatever the
        // hand had asked for.
        StopZoomGlide();

        var centre = _origin + new Vector2(
            (float)(Canvas.ActualWidth / _scale / 2.0),
            (float)(Canvas.ActualHeight / _scale / 2.0));

        RowBox? row = _fitMode == ViewerFitMode.Page ? layout.RowOfPage(CurrentPageIndex) : null;

        if (_fitMode == ViewerFitMode.Page && row is { } box && box.WidthPt > 0 && box.HeightPt > 0)
        {
            _scale = Math.Clamp(
                Math.Min(Canvas.ActualWidth / box.WidthPt, Canvas.ActualHeight / box.HeightPt),
                MinScale,
                MaxScale);
        }
        else if (layout.WidthPt > 0)
        {
            _scale = Math.Clamp(Canvas.ActualWidth / layout.WidthPt, MinScale, MaxScale);
        }
        else
        {
            return;
        }

        if (_fitSnap)
        {
            _origin = row is { } target
                ? new Vector2(target.XPt, target.YPt)
                : new Vector2(0f, _origin.Y);
        }
        else
        {
            _origin = centre - new Vector2(
                (float)(Canvas.ActualWidth / _scale / 2.0),
                (float)(Canvas.ActualHeight / _scale / 2.0));
        }

        _fitDirty = false;
        _fitSnap = false;
        ClampOrigin();

        // The fit lands during a draw, after whatever raised ViewChanged has
        // already been handled, so the zoom readout would otherwise show the
        // scale from before the fit. Deferred, because a draw is no place to
        // run someone else's handler.
        DispatcherQueue.TryEnqueue(() => ViewChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Keeps the viewport inside the document, and centers whichever axis has
    /// slack instead of letting the content drift to one side.
    /// </summary>
    private void ClampOrigin()
    {
        if (_layout is null) return;

        _origin = ClampedOrigin(_origin, Canvas.ActualWidth / _scale, Canvas.ActualHeight / _scale);
        SyncScrollBars();
        PositionWriter();
    }

    /// <summary>
    /// The same rule without moving anything, so a glide can ask where it is
    /// going to end up before it gets there.
    /// </summary>
    private Vector2 ClampedOrigin(Vector2 origin, double viewWidth, double viewHeight)
    {
        if (_layout is not { } layout) return origin;

        float x = layout.WidthPt <= viewWidth
            ? (float)((layout.WidthPt - viewWidth) / 2.0)
            : (float)Math.Clamp(origin.X, 0, layout.WidthPt - viewWidth);

        float y = layout.HeightPt <= viewHeight
            ? (float)((layout.HeightPt - viewHeight) / 2.0)
            : (float)Math.Clamp(origin.Y, 0, layout.HeightPt - viewHeight);

        return new Vector2(x, y);
    }

    private void ScrollBy(Vector2 deltaPt)
    {
        // Moving the drawing takes the view over from any zoom still gliding:
        // finishing that glide underneath the reader would slide the sheet
        // again after they had already put it where they wanted it.
        StopZoomGlide();

        _origin += deltaPt;
        ClampOrigin();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pushes the viewport into the scroll bars. Every path that moves or
    /// resizes the view ends up in ClampOrigin, so syncing from there is what
    /// keeps the bars from drifting out of step with the canvas.
    /// </summary>
    private void SyncScrollBars()
    {
        if (_layout is not { } layout)
        {
            VerticalScroll.Visibility = Visibility.Collapsed;
            HorizontalScroll.Visibility = Visibility.Collapsed;
            return;
        }

        _suppressScrollEvents = true;
        try
        {
            double viewWidth = Canvas.ActualWidth / _scale;
            double viewHeight = Canvas.ActualHeight / _scale;

            double verticalRange = Math.Max(0, layout.HeightPt - viewHeight);
            VerticalScroll.Maximum = verticalRange;
            VerticalScroll.ViewportSize = viewHeight;
            VerticalScroll.SmallChange = viewHeight * 0.1;
            VerticalScroll.LargeChange = viewHeight * 0.9;
            VerticalScroll.Value = Math.Clamp(_origin.Y, 0, verticalRange);
            VerticalScroll.Visibility = verticalRange > 0.5 ? Visibility.Visible : Visibility.Collapsed;

            double horizontalRange = Math.Max(0, layout.WidthPt - viewWidth);
            HorizontalScroll.Maximum = horizontalRange;
            HorizontalScroll.ViewportSize = viewWidth;
            HorizontalScroll.SmallChange = viewWidth * 0.1;
            HorizontalScroll.LargeChange = viewWidth * 0.9;
            HorizontalScroll.Value = Math.Clamp(_origin.X, 0, horizontalRange);
            HorizontalScroll.Visibility = horizontalRange > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _suppressScrollEvents = false;
        }
    }

    private void OnVerticalScroll(object sender, ScrollEventArgs e)
    {
        if (_suppressScrollEvents || _layout is null) return;
        StopZoomGlide();
        _origin = new Vector2(_origin.X, (float)e.NewValue);
        ClampOrigin();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnHorizontalScroll(object sender, ScrollEventArgs e)
    {
        if (_suppressScrollEvents || _layout is null) return;
        StopZoomGlide();
        _origin = new Vector2((float)e.NewValue, _origin.Y);
        ClampOrigin();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Zooms about a point on the canvas, by a factor. A gesture that arrives
    /// while the last one is still moving multiplies what is already on its
    /// way, not what is on screen — so five quick notches of the wheel are one
    /// glide to five notches away, and never five glides fighting each other.
    /// </summary>
    private void ZoomAt(Point anchor, double factor)
    {
        double from = _zoomGliding ? _zoomTarget : _scale;
        ZoomTo(anchor, from * factor);
    }

    /// <summary>
    /// Sends the zoom to a scale, turning about a point on the canvas. The
    /// sheet point under that anchor is worked out once, here, and held for the
    /// whole glide.
    /// </summary>
    private void ZoomTo(Point anchor, double target, Vector2? anchorDoc = null)
    {
        double clamped = Math.Clamp(target, MinScale, MaxScale);

        _zoomAnchor = anchor;
        _zoomAnchorDoc = anchorDoc
            ?? _origin + new Vector2((float)anchor.X, (float)anchor.Y) / (float)_scale;
        _zoomTarget = clamped;

        // Zooming by hand is the reader overriding the standing fit.
        _fitMode = ViewerFitMode.Free;
        _fitDirty = false;

        if (Math.Abs(clamped - _scale) <= _scale * ZoomSettleRatio)
        {
            SettleZoom();
            return;
        }

        StartZoomGlide();
    }

    private void StartZoomGlide()
    {
        if (_zoomGliding) return;

        _zoomGliding = true;
        _zoomLastTick = System.Diagnostics.Stopwatch.GetTimestamp();
        _zoomSinceReport = 0;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnZoomFrame;
    }

    /// <summary>
    /// Puts the zoom exactly where it was heading and stops the glide — what
    /// the last frame does, and what a target too close to bother moving to
    /// does straight away.
    /// </summary>
    private void SettleZoom()
    {
        StopZoomGlide();
        ApplyZoomScale(_zoomTarget);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Leaves the zoom where it has got to. Panning, changing sheet or turning
    /// one all take over the view, and finishing a glide underneath them would
    /// move the drawing after the reader had already moved it themselves.
    /// </summary>
    private void StopZoomGlide()
    {
        if (!_zoomGliding) return;

        _zoomGliding = false;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnZoomFrame;
    }

    /// <summary>
    /// One frame of the glide. Each tick closes the same fraction of whatever
    /// distance is left — worked out from the real elapsed time, so a dropped
    /// frame slows nothing down — and the distance is measured in log space,
    /// because zoom is a ratio: going 100 % → 200 % has to look like the same
    /// move as 200 % → 400 %.
    /// </summary>
    private void OnZoomFrame(object? sender, object e)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsed = (now - _zoomLastTick) / (double)System.Diagnostics.Stopwatch.Frequency;
        _zoomLastTick = now;

        // A frame after the app was busy elsewhere would otherwise jump the
        // whole way in one step, which is the very thing this is here to avoid.
        elapsed = Math.Clamp(elapsed, 0, 0.1);

        double remaining = Math.Log(_zoomTarget) - Math.Log(_scale);
        if (Math.Abs(remaining) <= ZoomSettleRatio)
        {
            SettleZoom();
            return;
        }

        double closed = 1.0 - Math.Exp(-elapsed / ZoomEaseSeconds);
        ApplyZoomScale(Math.Exp(Math.Log(_scale) + (remaining * closed)));

        _zoomSinceReport += elapsed;
        if (_zoomSinceReport >= ZoomReportSeconds)
        {
            _zoomSinceReport = 0;
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Moves the view to a scale, keeping the anchored sheet point under the
    /// same place on the canvas.
    /// </summary>
    private void ApplyZoomScale(double scale)
    {
        _scale = scale;
        _origin = _zoomAnchorDoc - new Vector2((float)_zoomAnchor.X, (float)_zoomAnchor.Y) / (float)_scale;
        ClampOrigin();
        Canvas.Invalidate();
    }

    /// <summary>Where the viewport will be once the glide in flight settles.</summary>
    private Rect ViewportAt(double scale)
    {
        double width = Canvas.ActualWidth / scale;
        double height = Canvas.ActualHeight / scale;

        var origin = ClampedOrigin(
            _zoomAnchorDoc - new Vector2((float)_zoomAnchor.X, (float)_zoomAnchor.Y) / (float)scale,
            width,
            height);

        return new Rect(origin.X, origin.Y, Math.Max(width, 0), Math.Max(height, 0));
    }

    /// <summary>Maps a pointer position to the page under it and the point in that page's local space.</summary>
    private bool TryHitPage(Point position, out PageBox page, out float localX, out float localY)
    {
        page = default;
        localX = localY = 0;
        if (_layout is not { } layout) return false;

        double docX = _origin.X + position.X / _scale;
        double docY = _origin.Y + position.Y / _scale;

        foreach (var candidate in layout.Pages)
        {
            if (docY < candidate.YPt || docY > candidate.BottomPt) continue;
            if (docX < candidate.XPt || docX > candidate.RightPt) continue;
            page = candidate;
            localX = (float)(docX - candidate.XPt);
            localY = (float)(docY - candidate.YPt);
            return true;
        }

        return false;
    }

    // --- input ----------------------------------------------------------

    /// <summary>
    /// Moving around the sheet from the keyboard. Handled on the canvas rather
    /// than on the control, so an arrow key typed into the find bar moves the
    /// caret and not the drawing.
    ///
    /// In single-sheet mode the page keys change sheet, because there is
    /// nowhere else for them to go; in continuous mode they step a viewport at
    /// a time through the strip.
    /// </summary>
    private void OnCanvasKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_layout is null) return;

        bool control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        double stepX = Canvas.ActualWidth / _scale * ArrowStepFraction;
        double stepY = Canvas.ActualHeight / _scale * ArrowStepFraction;
        double pageY = Canvas.ActualHeight / _scale * 0.9;

        switch (e.Key)
        {
            // Intro closes off a shape being built corner by corner, the way it
            // does in every drawing program.
            case VirtualKey.Enter when _placingVertices:
                FinishVertices();
                break;

            // While placing corners, back takes one off rather than deleting
            // the mark in hand: it is the more useful of the two just then.
            case VirtualKey.Back when _placingVertices && _vertices.Count > 0:
                _vertices.RemoveAt(_vertices.Count - 1);
                if (_vertices.Count == 0) CancelMark();
                Canvas.Invalidate();
                break;

            // Supr belongs to the mark in hand when there is one; with nothing
            // selected the key has nothing to do here.
            case VirtualKey.Delete when _selected is not null:
            case VirtualKey.Back when _selected is not null:
                DeleteSelectedAnnotation();
                break;

            case VirtualKey.Escape when IsPlacingSignature:
                IsPlacingSignature = false;
                SignaturePlaced?.Invoke(this, null);
                break;

            case VirtualKey.Escape when _isDrawing || _placingVertices || _selected is not null:
                CancelMark();
                break;

            case VirtualKey.Left:
                ScrollBy(new Vector2((float)-stepX, 0f));
                break;
            case VirtualKey.Right:
                ScrollBy(new Vector2((float)stepX, 0f));
                break;
            case VirtualKey.Up:
                ScrollBy(new Vector2(0f, (float)-stepY));
                break;
            case VirtualKey.Down:
                ScrollBy(new Vector2(0f, (float)stepY));
                break;

            case VirtualKey.PageUp:
                StepPage(-1, pageY);
                break;
            case VirtualKey.PageDown:
                StepPage(1, pageY);
                break;

            // Space reads on, the way it does in every document reader.
            case VirtualKey.Space:
                StepPage(shift ? -1 : 1, pageY);
                break;

            case VirtualKey.Home:
                if (control)
                {
                    GoToPage(0);
                }
                else
                {
                    _origin = new Vector2(_origin.X, 0f);
                    ClampOrigin();
                    Canvas.Invalidate();
                    ViewChanged?.Invoke(this, EventArgs.Empty);
                }
                break;

            case VirtualKey.End:
                if (control)
                {
                    GoToPage(PageCount - 1);
                }
                else
                {
                    _origin = new Vector2(_origin.X, _layout.HeightPt);
                    ClampOrigin();
                    Canvas.Invalidate();
                    ViewChanged?.Invoke(this, EventArgs.Empty);
                }
                break;

            case VirtualKey.Add:
            case (VirtualKey)0xBB:   // '+' on the main row
                ZoomBy(ZoomStep);
                break;
            case VirtualKey.Subtract:
            case (VirtualKey)0xBD:   // '-' on the main row
                ZoomBy(1.0 / ZoomStep);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// A viewport-sized step, which in single-sheet mode means changing sheet
    /// once there is no more of this one to show.
    /// </summary>
    private void StepPage(int direction, double viewportPt)
    {
        if (_layout is not { } layout) return;

        if (_layoutMode == ViewerLayoutMode.SinglePage)
        {
            double room = layout.HeightPt - Canvas.ActualHeight / _scale;
            bool atEnd = direction > 0
                ? _origin.Y >= room - 0.5
                : _origin.Y <= 0.5;

            if (atEnd || room <= 0.5)
            {
                GoToPage(CurrentPageIndex + direction * _columns);
                return;
            }
        }

        ScrollBy(new Vector2(0f, (float)(direction * viewportPt)));
    }

    private void OnCanvasSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        // A standing fit follows the window, without losing the reader's place.
        if (_fitMode != ViewerFitMode.Free)
        {
            _fitDirty = true;
        }

        ClampOrigin();
        Canvas.Invalidate();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_layout is null) return;

        var point = e.GetCurrentPoint(Canvas);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        var modifiers = e.KeyModifiers;
        double notches = delta / 120.0;
        bool shift = modifiers.HasFlag(VirtualKeyModifiers.Shift);

        // On a single sheet the wheel zooms, the way a CAD viewport behaves —
        // there is nowhere to scroll to, since no other page shares the space.
        // In continuous view the wheel has to scroll the strip, so zooming
        // moves to Ctrl+wheel.
        bool zoom = modifiers.HasFlag(VirtualKeyModifiers.Control)
            || (_layoutMode == ViewerLayoutMode.SinglePage && !shift);

        if (zoom)
        {
            // By how much the wheel turned, not merely which way. A notch is
            // 120, but a high-resolution wheel or a precision touchpad reports
            // the same physical turn as a burst of much smaller deltas — and a
            // whole step for each of them is a zoom that lurches away from
            // under the reader, on exactly the machines whose wheel is the
            // finer one. Raising the step to the fraction turned leaves a plain
            // wheel where it was and turns the burst back into one glide.
            ZoomAt(point.Position, Math.Pow(ZoomStep, notches));
        }
        else if (shift)
        {
            ScrollBy(new Vector2((float)(-notches * WheelScrollDips / _scale), 0f));
        }
        else
        {
            ScrollBy(new Vector2(0f, (float)(-notches * WheelScrollDips / _scale)));
        }

        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_layout is null) return;

        var point = e.GetCurrentPoint(Canvas);
        bool left = point.Properties.IsLeftButtonPressed;
        bool middle = point.Properties.IsMiddleButtonPressed;
        if (!left && !middle) return;

        Canvas.Focus(Microsoft.UI.Xaml.FocusState.Pointer);

        // A press on the sheet finishes whatever label was being typed. This
        // press cannot be inside the box — that is a control of its own and
        // takes its own pointer events — so reaching here means the reader has
        // gone somewhere else, which is what ends a label. Placing the next one
        // still works: this closes the old before the tool opens the new.
        EndWriting();

        // Middle-drag always pans, whichever tool is active — so the zoom
        // rectangle can stay selected while you still move around freely.
        // The highlighter works on text, not on the sheet: it takes the same
        // drag the text tool does, and turns what was picked out into a wash.
        if (left && (_tool == ViewerTool.SelectText || _tool == ViewerTool.Highlight))
        {
            BeginSelection(point.Position);
        }
        // A signature that is placed but not yet written can be dragged
        // somewhere better, whatever tool happens to be chosen: it is not a
        // mark, and it is only on screen for as long as it takes to decide.
        else if (left && Pending is { } waiting && HitsPending(point.Position, waiting, out var grabbed))
        {
            _draggingPending = true;
            _pendingGrabbedAt = grabbed;
        }
        else if (left && (IsPlacingSignature || _tool is ViewerTool.ZoomRectangle or ViewerTool.CaptureRegion))
        {
            _isZoomBanding = true;
            _bandIsForSignature = IsPlacingSignature;
            _bandIsForCapture = !IsPlacingSignature && _tool == ViewerTool.CaptureRegion;
            _zoomBandStart = point.Position;
            _zoomBandEnd = point.Position;
        }
        else if (left && _tool.DragsOnSheet() && CanMark(e))
        {
            _gesturePointerId = e.Pointer.PointerId;
            BeginMark(point.Position, point.Properties.IsEraser || point.Properties.IsRightButtonPressed);
        }
        else
        {
            _isPanning = true;
            _lastPointerPosition = point.Position;
        }

        Canvas.CapturePointer(e.Pointer);
    }

    /// <summary>
    /// Whether this pointer should be drawing at all. A finger never draws: on
    /// a pen tablet the hand that holds the drawing is on the glass, and a
    /// finger that leaves ink would be a stray line across the sheet every
    /// time. Touch pans instead, which is what a finger is for here.
    /// </summary>
    private static bool CanMark(PointerRoutedEventArgs e) =>
        e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch;

    private void BeginMark(Point position, bool erase)
    {
        _pressPosition = position;

        if (!TryHitSheet(position, out var page, out var sheetPoint)) return;

        // The back of the pen rubs marks out, the way it does on paper.
        if (erase)
        {
            EraseAt(page.Index, sheetPoint);
            return;
        }

        if (_placingVertices)
        {
            // Another corner of the shape being built. It goes down on release,
            // so that a press that turns into a drag is still a drag.
            return;
        }

        if (_tool == ViewerTool.SelectAnnotation)
        {
            BeginSelectOrHandle(page, sheetPoint, position);
            return;
        }

        // A comment and a written mark are both placed rather than dragged, and
        // both are selected at once so the panel can take the words straight
        // away — for the written one that is the whole of it.
        if (_tool is ViewerTool.Note or ViewerTool.FreeText)
        {
            var typed = new Annotation(_tool.ToKind(), [sheetPoint], AnnotationStyle, author: Author);
            _annotations.Add(page.Index, typed);
            _selected = typed;
            _selectedPage = page.Index;
            Canvas.Invalidate();

            // A label is typed where it will be read; a comment is typed in the
            // panel, because its marker is a pin with no room for words. Either
            // way the caret has to land somewhere, or the reader clicks, sees a
            // mark appear, and the keys they press go nowhere.
            if (_tool == ViewerTool.FreeText)
            {
                BeginWriting();
            }
            else
            {
                TextWanted?.Invoke(this, EventArgs.Empty);
            }

            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        ClearAnnotationSelection();
        _isDrawing = true;
        _draftPage = page.Index;
        _draft.Clear();

        // A measurement starts on the drawing, not next to it.
        var start = Snapped(page, sheetPoint);
        _draft.Add(start);
        _rubber = start;
        Canvas.Invalidate();
    }

    /// <summary>
    /// A press with the selection tool: a grip of the mark in hand first, then
    /// a mark under the pointer, and failing both the drawing moves instead.
    /// The grips win because they sit on top of the mark's own edge, where a
    /// click would otherwise be ambiguous.
    /// </summary>
    private void BeginSelectOrHandle(PageBox page, Vector2 sheetPoint, Point position)
    {
        float slop = (float)(Annotation.HitSlopPt / Math.Max(_scale, 0.05));

        if (_selected is { } current && _selectedPage == page.Index)
        {
            var grip = AnnotationHandles.At(current, sheetPoint, slop * 2.5f, HandleOffsetPt);
            if (grip.Exists)
            {
                _handle = grip;
                _movingFrom = current;
                Canvas.Invalidate();
                return;
            }
        }

        var hit = _annotations.HitTest(page.Index, sheetPoint, slop);
        if (hit is null)
        {
            ClearAnnotationSelection();
            _isPanning = true;
            _lastPointerPosition = position;
        }
        else
        {
            _selected = hit;
            _selectedPage = page.Index;
            _styles[hit.Kind] = hit.Style;

            // A highlight is picked up only to be deleted, so the drag that
            // would move anything else does nothing here.
            if (AnnotationHandles.CanMove(hit))
            {
                _movingFrom = hit;
                _moveAnchorSheet = sheetPoint;
            }
        }

        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// How far above the mark the turn knob floats, in points at this zoom. The
    /// knob is interface, so it keeps its distance on screen rather than
    /// growing with the drawing.
    /// </summary>
    private float HandleOffsetPt => (float)(AnnotationHandles.RotateOffsetPt / Math.Max(_scale, 0.05));

    private void EraseAt(int pageIndex, Vector2 sheetPoint)
    {
        var hit = _annotations.HitTest(pageIndex, sheetPoint, (float)(Annotation.HitSlopPt / Math.Max(_scale, 0.05)));
        if (hit is null) return;

        _annotations.Remove(pageIndex, hit);
        if (_selected?.Id == hit.Id) ClearAnnotationSelection();

        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The mark being drawn, as it stands — the stroke under the pen, or the
    /// vertices placed so far with the one the pointer is holding. Built
    /// through the same constructor as a finished mark, so the preview cannot
    /// differ from the result.
    /// </summary>
    private Annotation? BuildDraft()
    {
        var kind = _tool.ToKind();

        if (_placingVertices)
        {
            if (_vertices.Count == 0) return null;

            var placed = new List<Vector2>(_vertices) { _rubber };
            return Make(kind, placed, _draftPage);
        }

        if (_draft.Count == 0) return null;

        IReadOnlyList<Vector2> points = kind == AnnotationKind.Ink ? _draft : [_draft[0], _draft[^1]];
        if (kind != AnnotationKind.Ink && points.Count < 2) return null;

        return Make(kind, points, _draftPage);
    }

    /// <summary>
    /// A new mark on a sheet, carrying that sheet's scale.
    ///
    /// Every mark goes through here so that a measurement cannot be made
    /// without one: the scale is not something the reader sets on the mark, it
    /// is what the sheet is drawn to, and a measurement made on a sheet
    /// calibrated afterwards is brought up to it by the store.
    /// </summary>
    private Annotation Make(AnnotationKind kind, IReadOnlyList<Vector2> points, int page) =>
        new(kind, points, AnnotationStyle, author: Author,
            // While calibrating, the sheet's own scale is the one being
            // replaced: showing a number from it under the drag would be the
            // old answer following the reader's hand.
            scale: page >= 0 && _tool != ViewerTool.Calibrate ? _annotations.ScaleOf(page) : null);

    /// <summary>Who the marks are attributed to, which is what a PDF reader shows as the author.</summary>
    private static string Author
    {
        get
        {
            try
            {
                return Environment.UserName;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// A pointer position as a point on a sheet, in the marks' own space. The
    /// reader's turn comes off here, which is why nothing downstream has to
    /// know about it.
    /// </summary>
    private bool TryHitSheet(Point position, out PageBox page, out Vector2 sheetPoint)
    {
        sheetPoint = default;
        if (!TryHitPage(position, out page, out float localX, out float localY)) return false;

        sheetPoint = ToSheetPoint(page, new Vector2(localX, localY));
        return true;
    }

    /// <summary>
    /// The same conversion for a pointer that has wandered off the sheet
    /// mid-drag: the point is pulled back onto the paper rather than dropped,
    /// so a stroke that overshoots the edge still ends on the sheet.
    /// </summary>
    private Vector2 SheetPointClamped(PageBox page, Point position)
    {
        double docX = _origin.X + position.X / _scale;
        double docY = _origin.Y + position.Y / _scale;

        var local = new Vector2(
            (float)Math.Clamp(docX - page.XPt, 0, page.WidthPt),
            (float)Math.Clamp(docY - page.YPt, 0, page.HeightPt));

        return ToSheetPoint(page, local);
    }

    private Vector2 ToSheetPoint(PageBox page, Vector2 local)
    {
        var sheet = Plan.Count > page.Index
            ? Plan[page.Index].Size
            : new PdfPageSize(page.WidthPt, page.HeightPt);

        return SheetTurn.ToSheet(local, sheet.WidthPt, sheet.HeightPt, RotationOf(page.Index));
    }

    private PageBox? PageBoxOf(int pageIndex)
    {
        if (_layout is not { } layout) return null;

        foreach (var candidate in layout.Pages)
        {
            if (candidate.Index == pageIndex) return candidate;
        }
        return null;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_layout is null) return;
        var position = e.GetCurrentPoint(Canvas).Position;

        if (_isDrawing && e.Pointer.PointerId == _gesturePointerId)
        {
            ExtendMark(position, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift));
            return;
        }

        if (_handle.Exists && e.Pointer.PointerId == _gesturePointerId)
        {
            DragHandle(position, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift));
            return;
        }

        if (_movingFrom is not null && e.Pointer.PointerId == _gesturePointerId)
        {
            DragMark(position);
            return;
        }

        // The segment that follows the cursor while a shape is being built.
        if (_placingVertices && PageBoxOf(_draftPage) is { } vertexPage)
        {
            _rubber = Placed(
                vertexPage,
                position,
                _vertices.Count > 0 ? _vertices[^1] : null,
                e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift));
            Canvas.Invalidate();
            return;
        }

        if (_isSelecting)
        {
            ExtendSelection(position);
            return;
        }

        if (_draggingPending && Pending is { } dragged)
        {
            var page = PageAt(dragged.PageIndex);
            if (page is not null)
            {
                var now = SheetPointClamped(page.Value, position);
                var moved = dragged.SheetRect;
                Pending = dragged with
                {
                    SheetRect = new RectPt(
                        moved.X + (now.X - _pendingGrabbedAt.X),
                        moved.Y + (now.Y - _pendingGrabbedAt.Y),
                        moved.Width,
                        moved.Height),
                };
                _pendingGrabbedAt = now;
                Canvas.Invalidate();
            }
            return;
        }

        if (_isZoomBanding)
        {
            _zoomBandEnd = position;
            Canvas.Invalidate();
            return;
        }

        if (!_isPanning) return;

        var deltaDips = new Vector2(
            (float)(position.X - _lastPointerPosition.X),
            (float)(position.Y - _lastPointerPosition.Y));
        _lastPointerPosition = position;

        ScrollBy(-deltaDips / (float)_scale);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingPending)
        {
            _draggingPending = false;
            Canvas.ReleasePointerCapture(e.Pointer);
            return;
        }

        if (_isZoomBanding)
        {
            _isZoomBanding = false;
            var where = e.GetCurrentPoint(Canvas).Position;

            if (_bandIsForSignature)
            {
                _bandIsForSignature = false;
                ApplySignatureBand(where);
            }
            else if (_bandIsForCapture)
            {
                _bandIsForCapture = false;
                ApplyCaptureBand(where);
            }
            else
            {
                ApplyZoomBand(where);
            }

            Canvas.ReleasePointerCapture(e.Pointer);
            return;
        }

        if (_handle.Exists)
        {
            if (_movingFrom is { } before && _selected is { } after && _selectedPage >= 0)
            {
                _annotations.Replace(_selectedPage, before, after);
            }
            _handle = MarkGrip.None;
            _movingFrom = null;
            Canvas.ReleasePointerCapture(e.Pointer);
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_placingVertices)
        {
            PlaceVertex(e.GetCurrentPoint(Canvas).Position, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift));
            Canvas.ReleasePointerCapture(e.Pointer);
            return;
        }

        if (_isDrawing)
        {
            var released = e.GetCurrentPoint(Canvas).Position;

            // A shape built vertex by vertex starts as a press: if the pointer
            // never moved, the reader is placing corners rather than dragging a
            // box, and the gesture becomes the first vertex.
            if (Annotation.TakesVertices(_tool.ToKind()) && !MovedEnough(released))
            {
                StartPlacingVertices();
                Canvas.ReleasePointerCapture(e.Pointer);
                return;
            }

            ExtendMark(released, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift));
            FinishMark();
            Canvas.ReleasePointerCapture(e.Pointer);
            return;
        }

        if (_movingFrom is { } origin)
        {
            // One step for the whole drag: the frames in between were the mark
            // following the pointer, not a hundred decisions.
            if (_selected is { } moved && _selectedPage >= 0)
            {
                _annotations.Replace(_selectedPage, origin, moved);
            }
            _movingFrom = null;
            Canvas.ReleasePointerCapture(e.Pointer);
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // The capture is taken on every press, so it has to be given back on
        // every release — including the press that only picked a mark up and
        // started neither a pan nor a selection.
        if (!_isPanning && !_isSelecting)
        {
            Canvas.ReleasePointerCapture(e.Pointer);
            return;
        }

        if (_isSelecting && _tool == ViewerTool.Highlight)
        {
            FinishHighlight();
        }

        _isPanning = false;
        _isSelecting = false;
        Canvas.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>
    /// Turns the text just picked out into a highlight.
    ///
    /// One mark, not one per line: a phrase that wraps is a single thing the
    /// reviewer highlighted, and the PDF says so too — a highlight annotation
    /// carries a box per line in its quad points. The boxes come from the same
    /// text layer that draws the selection, so the wash lands exactly where the
    /// blue was a moment ago.
    /// </summary>
    private void FinishHighlight()
    {
        if (_selectionPage < 0 || _selectionAnchor < 0) return;
        if (!_textLayers.TryGetValue(TextKeyFor(_selectionPage), out var layer))
        {
            return;
        }

        var sheet = Plan.Count > _selectionPage
            ? Plan[_selectionPage].Size
            : new PdfPageSize(0, 0);
        int turn = RotationOf(_selectionPage);

        var corners = new List<Vector2>();
        foreach (var run in layer.BuildRuns(_selectionAnchor, _selectionFocus))
        {
            corners.Add(SheetTurn.ToSheet(new Vector2(run.Left, run.Top), sheet.WidthPt, sheet.HeightPt, turn));
            corners.Add(SheetTurn.ToSheet(new Vector2(run.Right, run.Bottom), sheet.WidthPt, sheet.HeightPt, turn));
        }

        if (corners.Count >= 2)
        {
            _annotations.Add(
                _selectionPage,
                new Annotation(AnnotationKind.Highlight, corners, AnnotationStyle, author: Author));
        }

        ClearSelection();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ExtendMark(Point position, bool shift)
    {
        if (PageBoxOf(_draftPage) is not { } page) return;

        // Held to the first point of the drag, not to the previous sample: a
        // measurement is the line from where it started to where it ends, and
        // only the first point is the one that will survive into the mark.
        var sheetPoint = Placed(page, position, _draft.Count > 0 ? _draft[0] : null, shift);

        // A pen reports far more samples than the drawing needs; the ones that
        // land on top of each other are dropped here rather than kept and
        // thinned later.
        if (_draft.Count > 0 && Vector2.Distance(_draft[^1], sheetPoint) * _scale < 0.75) return;

        _draft.Add(sheetPoint);
        Canvas.Invalidate();
    }

    /// <summary>
    /// A double click closes off a shape being built corner by corner. The
    /// second click has already placed a vertex on the same spot, and
    /// <see cref="PlaceVertex"/> drops that one for exactly this reason.
    /// </summary>
    private void OnCanvasDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        // A written mark is opened again by double clicking it, which is what a
        // hand does without being told — and the only way back into one that
        // was typed and left.
        if (!_placingVertices)
        {
            if (_selected is { Kind: AnnotationKind.FreeText }) BeginWriting();
            return;
        }

        FinishVertices();
        e.Handled = true;
    }

    private bool MovedEnough(Point position) =>
        Math.Abs(position.X - _pressPosition.X) > MinShapeDips
        || Math.Abs(position.Y - _pressPosition.Y) > MinShapeDips;

    /// <summary>
    /// Turns the press that has just happened into the first corner of a shape
    /// built click by click.
    /// </summary>
    private void StartPlacingVertices()
    {
        if (_draft.Count == 0) return;

        _placingVertices = true;
        _isDrawing = false;
        _vertices.Clear();
        _vertices.Add(_draft[0]);
        _rubber = _draft[0];
        _draft.Clear();

        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PlaceVertex(Point position, bool shift)
    {
        if (PageBoxOf(_draftPage) is not { } page) return;

        // Each vertex is held to the one before it, so a run of segments comes
        // out as a staircase rather than everything on one line through the
        // first corner.
        var sheetPoint = Placed(page, position, _vertices.Count > 0 ? _vertices[^1] : null, shift);

        // Two clicks in the same spot end the shape, which is what a reader
        // does without being told.
        if (_vertices.Count > 0 && Vector2.Distance(_vertices[^1], sheetPoint) * _scale < MinShapeDips)
        {
            FinishVertices();
            return;
        }

        _vertices.Add(sheetPoint);
        _rubber = sheetPoint;

        // An angle is an arm, a corner and the other arm. There is nothing to
        // add a fourth point to, so it closes itself rather than waiting for a
        // double click that would only mean the same thing.
        if (_tool == ViewerTool.Angle && _vertices.Count == 3)
        {
            FinishVertices();
            return;
        }

        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Closes off a shape built click by click. Too few corners to be a shape
    /// and nothing is left behind — a stray click should not litter the sheet.
    /// </summary>
    public void FinishVertices()
    {
        if (!_placingVertices) return;

        var kind = _tool.ToKind();
        int least = kind == AnnotationKind.Polyline ? 2 : 3;

        if (_vertices.Count >= least && _draftPage >= 0)
        {
            _annotations.Add(_draftPage, Make(kind, [.. _vertices], _draftPage));
        }

        _placingVertices = false;
        _vertices.Clear();
        _draftPage = -1;

        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True while a shape is being built corner by corner, so the chrome can say so.</summary>
    public bool IsPlacingVertices => _placingVertices;

    private void DragHandle(Point position, bool snap)
    {
        if (_movingFrom is not { } original || _selectedPage < 0) return;
        if (PageBoxOf(_selectedPage) is not { } page) return;

        var sheetPoint = SheetPointClamped(page, position);

        var changed = _handle.Which == MarkHandle.Rotate
            ? original.WithRotation(snap
                ? AnnotationHandles.Snap(AnnotationHandles.RotationFor(original, sheetPoint))
                : AnnotationHandles.RotationFor(original, sheetPoint))
            : AnnotationHandles.Drag(original, _handle, sheetPoint);

        if (_selected is { } previous)
        {
            _annotations.ReplaceLive(_selectedPage, previous, changed);
        }
        _selected = changed;
        Canvas.Invalidate();
    }

    private void DragMark(Point position)
    {
        if (_selected is not { } selected || _selectedPage < 0) return;
        if (PageBoxOf(_selectedPage) is not { } page) return;

        var sheetPoint = SheetPointClamped(page, position);
        var moved = selected.MovedBy(sheetPoint - _moveAnchorSheet);
        _moveAnchorSheet = sheetPoint;

        _annotations.ReplaceLive(_selectedPage, selected, moved);
        _selected = moved;
        Canvas.Invalidate();
    }

    /// <summary>
    /// Turns the stroke on screen into a mark on the sheet. A drag too small to
    /// be a shape is thrown away rather than left as a speck the reader then
    /// has to find and delete.
    /// </summary>
    private void FinishMark()
    {
        _isDrawing = false;

        var kind = _tool.ToKind();
        if (_draft.Count == 0 || _draftPage < 0)
        {
            _draft.Clear();
            Canvas.Invalidate();
            return;
        }

        bool tooSmall = kind != AnnotationKind.Ink
            && (_draft.Count < 2 || Vector2.Distance(_draft[0], _draft[^1]) * _scale < MinShapeDips);

        // Calibrating leaves nothing behind: the drag was a question about the
        // sheet, and the answer is typed. It goes out as an event so the sheet
        // is never left half-calibrated by a dialog the reader dismissed.
        if (_tool == ViewerTool.Calibrate)
        {
            int page = _draftPage;
            float measured = _draft.Count >= 2 ? Vector2.Distance(_draft[0], _draft[^1]) : 0f;

            _draft.Clear();
            _draftPage = -1;
            Canvas.Invalidate();

            if (measured >= SheetScale.ShortestCalibrationPt && page >= 0)
            {
                CalibrationDragged?.Invoke(this, new CalibrationDrag(page, measured));
            }
            return;
        }

        if (!tooSmall)
        {
            IReadOnlyList<Vector2> points = kind == AnnotationKind.Ink
                ? AnnotationGeometry.Simplify(_draft, InkSimplifyPt)
                : [_draft[0], _draft[^1]];

            _annotations.Add(_draftPage, Make(kind, points, _draftPage));
        }

        _draft.Clear();
        _draftPage = -1;
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops whatever gesture is in flight, without leaving a mark behind.</summary>
    public void CancelMark()
    {
        bool anything = _isDrawing || _placingVertices || _movingFrom is not null || _selected is not null;
        if (!anything) return;

        if (_movingFrom is { } origin && _selected is not null && _selectedPage >= 0)
        {
            _annotations.ReplaceLive(_selectedPage, _selected, origin);
        }

        _isDrawing = false;
        _placingVertices = false;
        _vertices.Clear();
        _handle = MarkGrip.None;
        _movingFrom = null;
        _draft.Clear();
        _draftPage = -1;
        ClearAnnotationSelection();

        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts the dragged rectangle in the viewport. A drag too small to be
    /// deliberate is treated as a click, and just zooms in a step about that
    /// point — otherwise a stray click would fling the view to a random
    /// magnification.
    /// </summary>
    private void ApplyZoomBand(Point end)
    {
        _zoomBandEnd = end;

        double width = Math.Abs(_zoomBandEnd.X - _zoomBandStart.X);
        double height = Math.Abs(_zoomBandEnd.Y - _zoomBandStart.Y);

        if (width < MinZoomBandDips || height < MinZoomBandDips)
        {
            ZoomAt(_zoomBandStart, ZoomStep * ZoomStep);
            Canvas.Invalidate();
            return;
        }

        double left = Math.Min(_zoomBandStart.X, _zoomBandEnd.X);
        double top = Math.Min(_zoomBandStart.Y, _zoomBandEnd.Y);

        // The centre of the band, in document space, before the scale changes.
        var centre = _origin + new Vector2(
            (float)((left + width / 2.0) / _scale),
            (float)((top + height / 2.0) / _scale));

        double target = Math.Min(Canvas.ActualWidth / (width / _scale), Canvas.ActualHeight / (height / _scale));

        // The band's centre ends up in the middle of the viewport, so that is
        // the pair of points the glide turns about.
        ZoomTo(ViewportCentre(), target, centre);
    }

    /// <summary>
    /// Turns the band the reader just dragged into the box a visible signature
    /// will occupy, in sheet points on the page it was drawn over.
    ///
    /// A band too small to hold a stamp is a cancelled gesture, not a tiny
    /// signature: nobody means to sign inside four points of paper.
    /// </summary>
    private void ApplySignatureBand(Point end)
    {
        _zoomBandEnd = end;
        IsPlacingSignature = false;
        Canvas.Invalidate();

        double width = Math.Abs(end.X - _zoomBandStart.X);
        double height = Math.Abs(end.Y - _zoomBandStart.Y);

        if (width < MinZoomBandDips || height < MinZoomBandDips
            || !TryHitSheet(_zoomBandStart, out var page, out var corner))
        {
            SignaturePlaced?.Invoke(this, null);
            return;
        }

        var opposite = SheetPointClamped(page, end);
        var box = new RectPt(
            Math.Min(corner.X, opposite.X),
            Math.Min(corner.Y, opposite.Y),
            Math.Abs(opposite.X - corner.X),
            Math.Abs(opposite.Y - corner.Y));

        SignaturePlaced?.Invoke(this, new SignatureSpot(page.Index, box));
    }

    /// <summary>
    /// Turns the band into the piece of sheet to capture, and hands it out.
    ///
    /// A band too small is a cancelled gesture and not a tiny capture: a click
    /// that slipped must not put a four-point stamp on the clipboard.
    /// </summary>
    private void ApplyCaptureBand(Point end)
    {
        _zoomBandEnd = end;
        Canvas.Invalidate();

        double width = Math.Abs(end.X - _zoomBandStart.X);
        double height = Math.Abs(end.Y - _zoomBandStart.Y);

        if (width < MinZoomBandDips || height < MinZoomBandDips
            || !TryHitSheet(_zoomBandStart, out var page, out var corner))
        {
            RegionCaptured?.Invoke(this, null);
            return;
        }

        var opposite = SheetPointClamped(page, end);
        var box = new RectPt(
            Math.Min(corner.X, opposite.X),
            Math.Min(corner.Y, opposite.Y),
            Math.Abs(opposite.X - corner.X),
            Math.Abs(opposite.Y - corner.Y));

        RegionCaptured?.Invoke(this, new CaptureSpot(page.Index, box));
    }

    /// <summary>
    /// Draws a piece of one sheet — the drawing and the marks on it — into a
    /// PNG, at <paramref name="dpi"/> rather than at whatever the screen
    /// happened to be showing.
    ///
    /// That is the whole point of the feature. A screen grab of an A0 seen at
    /// 29 % is 29 % of the detail; this asks PDFium for the same region again
    /// at print resolution, so a dimension unreadable on screen is readable in
    /// the paste. The marks go on afterwards as vectors, for the same reason
    /// printing does it that way.
    /// </summary>
    public async Task<CaptureImage?> RenderRegionPngAsync(int pageIndex, RectPt box, double dpi)
    {
        if (_documentId < 0 || box.Width <= 0 || box.Height <= 0) return null;

        var origin = OriginOf(pageIndex);
        double scale = dpi / 72.0;

        int width = Math.Max(1, (int)Math.Round(box.Width * scale));
        int height = Math.Max(1, (int)Math.Round(box.Height * scale));

        // A whole A0 asked for at 200 dpi is a third of a gigapixel. Rather
        // than refuse, the scale drops until it fits: a slightly softer capture
        // is a better answer than an error, and the reader picked the region,
        // not the resolution.
        long pixels = (long)width * height;
        if (pixels > MaxCapturePixels)
        {
            double shrink = Math.Sqrt(MaxCapturePixels / (double)pixels);
            scale *= shrink;
            width = Math.Max(1, (int)Math.Round(box.Width * scale));
            height = Math.Max(1, (int)Math.Round(box.Height * scale));
        }

        int startX = (int)Math.Floor(box.X * scale);
        int startY = (int)Math.Floor(box.Y * scale);
        int rotation = RotationOf(pageIndex);

        var device = CanvasDevice.GetSharedDevice();

        // 96 dpi on the target so that one DIP is one pixel: every rectangle
        // below is then in output pixels and there is no second scale to get
        // wrong.
        using var target = new CanvasRenderTarget(device, width, height, 96);

        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Colors.White);

            // Aliased and on whole pixels, as everywhere else: two abutting
            // antialiased bands leave a pale seam across the capture.
            ds.Antialiasing = CanvasAntialiasing.Aliased;

            int bandHeight = (int)Math.Clamp(MaxCaptureBandPixels / Math.Max(1, width), 1, height);

            for (int offset = 0; offset < height; offset += bandHeight)
            {
                int slice = Math.Min(bandHeight, height - offset);

                // Comparing and capturing are one gesture in a meeting: what
                // gets pasted has to be the picture that was on screen, so the
                // overlay travels with the band.
                var band = await _queue.RequestPrintBandAsync(
                    origin.DocumentId, origin.PageIndex, rotation, scale,
                    startX, startY + offset, width, slice, monochrome: false, OverlayFor(pageIndex));

                if (band is not { } data) return null;

                using var bitmap = CanvasBitmap.CreateFromBytes(
                    device, data.Bgra, data.Width, data.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized);

                ds.DrawImage(bitmap, new Rect(0, offset, width, slice));
            }

            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            DrawCapturedMarks(ds, pageIndex, box, scale);
        }

        var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        return new CaptureImage(stream, width, height);
    }

    /// <summary>
    /// Lays the sheet's marks over the captured region, clipped to it — the
    /// same geometry the canvas and the printer use, so a nube captured here
    /// is the nube that was on screen.
    /// </summary>
    private void DrawCapturedMarks(CanvasDrawingSession ds, int pageIndex, RectPt box, double scale)
    {
        var marks = _annotations.ForPage(pageIndex);
        if (marks.Count == 0) return;

        // The placement wants the sheet as the file draws it, and Plan holds it
        // that way already — the reader's turn travels separately.
        var sheet = SheetSize(pageIndex, new PdfPageSize(box.Width, box.Height));

        var placement = new SheetPlacement(
            sheet.WidthPt,
            sheet.HeightPt,
            RotationOf(pageIndex),
            -box.X * scale,
            -box.Y * scale,
            scale);

        foreach (var mark in marks)
        {
            AnnotationRenderer.Draw(ds, mark, placement);
        }
    }

    private void BeginSelection(Point position)
    {
        if (!TryHitPage(position, out var page, out float localX, out float localY)) return;
        if (!_textLayers.TryGetValue(TextKeyFor(page.Index), out var layer)) return;

        int index = layer.HitTest(localX, localY, SelectionSnapPt);
        if (index < 0)
        {
            ClearSelection();
        }
        else
        {
            _selectionPage = page.Index;
            _selectionAnchor = index;
            _selectionFocus = index;
            _isSelecting = true;
        }

        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ExtendSelection(Point position)
    {
        if (_selectionPage < 0) return;
        if (!TryHitPage(position, out var page, out float localX, out float localY)) return;

        // Selection stays within the page it started on.
        if (page.Index != _selectionPage) return;
        if (!_textLayers.TryGetValue(TextKeyFor(_selectionPage), out var layer))
        {
            return;
        }

        int index = layer.HitTest(localX, localY, float.MaxValue);
        if (index < 0 || index == _selectionFocus) return;

        _selectionFocus = index;
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // --- drawing --------------------------------------------------------

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        ApplyPendingFit();
        if (_layout is not { } layout) return;

        var ds = args.DrawingSession;
        var view = ViewportInDocSpace();

        // Tiles are rasterized in physical device pixels, so the level has to
        // account for the display's scaling factor. Choosing it from DIPs alone
        // renders every tile short of the panel's real resolution, which is what
        // made a 150%-scaled display look permanently soft.
        double dpiScale = sender.Dpi / 96.0;
        int level = ZoomLevels.LevelForScale(_scale * dpiScale);

        // While the zoom is gliding, what is asked of PDFium is what the view
        // will need when it lands — not what each frame on the way happens to
        // cross. A glide of a few notches passes through two or three tile
        // levels, and rasterizing a dense A0 at each of them is work that
        // reaches the screen for one frame and is thrown away. On the way there
        // the frames are drawn from what is already cached, which is what makes
        // the movement smooth in the first place.
        bool gliding = _zoomGliding;

        foreach (var page in layout.PagesInBand(view.Top, view.Bottom))
        {
            var pageRect = ToScreenRect(page);
            if (pageRect.Width <= 0 || pageRect.Height <= 0) continue;

            ds.FillRectangle(pageRect, Colors.White);

            using (ds.CreateLayer(1f, pageRect))
            {
                // Tiles abut, so their edges must not be antialiased: two
                // touching edges at half coverage each compose to three
                // quarters, and that shortfall is the pale seam that shows up
                // across a solid fill. Snapping the edges to whole device
                // pixels (below) is what makes drawing them aliased safe.
                var antialiasing = ds.Antialiasing;
                ds.Antialiasing = CanvasAntialiasing.Aliased;

                int floor = Math.Max(ZoomLevels.MinLevel, level - FallbackLevels);
                for (int fallback = floor; fallback < level; fallback++)
                {
                    DrawPageTiles(ds, page, fallback, view, dpiScale, requestMissing: false);
                }

                DrawPageTiles(ds, page, level, view, dpiScale, requestMissing: !gliding);

                ds.Antialiasing = antialiasing;

                DrawSearchHits(ds, page);
                DrawSelection(ds, page);
                DrawChangeRings(ds, page);
                DrawAnnotations(ds, page);
                DrawPendingSignature(ds, page);
            }

            // Measured from the draw, like the text layers, so the sheet on
            // screen is the one whose changes get counted first.
            EnsureChanges(page.Index);

            // The highlighter needs the text as much as the text tool does:
            // both work by picking words out of the sheet.
            if (_tool is ViewerTool.SelectText or ViewerTool.Highlight)
            {
                EnsureTextLayer(page.Index);
            }
        }

        if (gliding)
        {
            var landing = ViewportAt(_zoomTarget);
            int landingLevel = ZoomLevels.LevelForScale(_zoomTarget * dpiScale);

            foreach (var page in layout.PagesInBand(landing.Top, landing.Bottom))
            {
                RequestPageTiles(page, landingLevel, landing);
            }
        }

        // Outside the per-page clip: the band is a piece of interface, and it
        // may well be dragged across the gap between two sheets.
        DrawZoomBand(ds);
    }

    /// <summary>
    /// Asks for the tiles a page needs to be drawn at a level, without drawing
    /// any of it. This is how the render for where a zoom is heading gets under
    /// way while the view is still travelling there, so the sharp version is
    /// waiting rather than starting once the movement stops.
    /// </summary>
    private void RequestPageTiles(PageBox page, int level, Rect view)
    {
        double tilePt = ZoomLevels.TileSize / ZoomLevels.ScaleForLevel(level);
        if (tilePt <= 0) return;

        double left = Math.Max(0, view.Left - page.XPt);
        double top = Math.Max(0, view.Top - page.YPt);
        double right = Math.Min(page.WidthPt, view.Right - page.XPt);
        double bottom = Math.Min(page.HeightPt, view.Bottom - page.YPt);
        if (right <= left || bottom <= top) return;

        var origin = OriginOf(page.Index);
        if (origin.IsBlank) return;

        var overlay = OverlayFor(page.Index);
        var cache = overlay is null ? _cache : _compareCache;

        int rotation = RotationOf(page.Index);
        int colStart = (int)Math.Floor(left / tilePt);
        int colEnd = (int)Math.Floor((right - 1e-6) / tilePt);
        int rowStart = (int)Math.Floor(top / tilePt);
        int rowEnd = (int)Math.Floor((bottom - 1e-6) / tilePt);

        for (int row = rowStart; row <= rowEnd; row++)
        {
            for (int col = colStart; col <= colEnd; col++)
            {
                var key = new TileKey(origin.PageIndex, level, col, row, rotation, origin.DocumentId);
                if (cache.TryGet(key, out _)) continue;

                RequestTile(key, origin.DocumentId, overlay);
            }
        }
    }

    private void DrawZoomBand(CanvasDrawingSession ds)
    {
        if (!_isZoomBanding) return;

        var band = new Rect(
            Math.Min(_zoomBandStart.X, _zoomBandEnd.X),
            Math.Min(_zoomBandStart.Y, _zoomBandEnd.Y),
            Math.Abs(_zoomBandEnd.X - _zoomBandStart.X),
            Math.Abs(_zoomBandEnd.Y - _zoomBandStart.Y));

        if (band.Width <= 0 || band.Height <= 0) return;

        ds.FillRectangle(band, ZoomBandFill);
        ds.DrawRectangle(band, ZoomBandStroke, 1.2f);
    }

    private Rect ToScreenRect(PageBox page) => new(
        (page.XPt - _origin.X) * _scale,
        (page.YPt - _origin.Y) * _scale,
        Math.Max(0, page.WidthPt * _scale),
        Math.Max(0, page.HeightPt * _scale));

    private void DrawPageTiles(
        CanvasDrawingSession ds, PageBox page, int level, Rect view, double dpiScale, bool requestMissing)
    {
        int tileSize = ZoomLevels.TileSize;
        double levelScale = ZoomLevels.ScaleForLevel(level);
        double tilePt = tileSize / levelScale;
        if (tilePt <= 0) return;

        // Visible slice of this page, in page-local points.
        double left = Math.Max(0, view.Left - page.XPt);
        double top = Math.Max(0, view.Top - page.YPt);
        double right = Math.Min(page.WidthPt, view.Right - page.XPt);
        double bottom = Math.Min(page.HeightPt, view.Bottom - page.YPt);
        if (right <= left || bottom <= top) return;

        // Blank paper has no file behind it: the sheet itself is already drawn,
        // and there is nothing to ask PDFium for.
        var origin = OriginOf(page.Index);
        if (origin.IsBlank) return;

        // A comparison draws from its own cache, one square at a time, exactly
        // where the plain tile would have gone. That is what leaves panning,
        // zoom, capture and print as they were: there is no second layer on
        // screen to keep lined up with the first.
        var overlay = OverlayFor(page.Index);
        var cache = overlay is null ? _cache : _compareCache;

        int rotation = RotationOf(page.Index);
        int colStart = (int)Math.Floor(left / tilePt);
        int colEnd = (int)Math.Floor((right - 1e-6) / tilePt);
        int rowStart = (int)Math.Floor(top / tilePt);
        int rowEnd = (int)Math.Floor((bottom - 1e-6) / tilePt);

        for (int row = rowStart; row <= rowEnd; row++)
        {
            // Each boundary is derived from its grid index alone and snapped
            // the same way, so the bottom edge of one tile and the top edge of
            // the next are the very same number rather than two roundings of
            // it. That is what leaves no gap and no overlap between them.
            double destTop = SnapToPixel((page.YPt + row * tilePt - _origin.Y) * _scale, dpiScale);
            double destBottom = SnapToPixel((page.YPt + (row + 1) * tilePt - _origin.Y) * _scale, dpiScale);

            for (int col = colStart; col <= colEnd; col++)
            {
                var key = new TileKey(origin.PageIndex, level, col, row, rotation, origin.DocumentId);

                if (!cache.TryGet(key, out var bitmap))
                {
                    if (requestMissing)
                    {
                        RequestTile(key, origin.DocumentId, overlay);
                    }
                    continue;
                }

                double destLeft = SnapToPixel((page.XPt + col * tilePt - _origin.X) * _scale, dpiScale);
                double destRight = SnapToPixel((page.XPt + (col + 1) * tilePt - _origin.X) * _scale, dpiScale);

                ds.DrawImage(bitmap, new Rect(
                    destLeft,
                    destTop,
                    Math.Max(0, destRight - destLeft),
                    Math.Max(0, destBottom - destTop)));
            }
        }
    }

    /// <summary>
    /// Rounds a position in DIPs to the display's own pixel grid. Tiles are
    /// rasterized in physical pixels, so this is the grid their edges have to
    /// land on to stay crisp and to meet each other exactly.
    /// </summary>
    private static double SnapToPixel(double dips, double dpiScale) =>
        dpiScale <= 0 ? dips : Math.Round(dips * dpiScale, MidpointRounding.AwayFromZero) / dpiScale;

    private void DrawSelection(CanvasDrawingSession ds, PageBox page)
    {
        if (!HasSelection || page.Index != _selectionPage) return;
        if (!_textLayers.TryGetValue(TextKeyFor(_selectionPage), out var layer))
        {
            return;
        }

        foreach (var run in layer.BuildRuns(_selectionAnchor, _selectionFocus))
        {
            ds.FillRectangle(ToScreenRect(page, run), SelectionFill);
        }
    }

    /// <summary>
    /// Paints this sheet's search hits: every one in amber, the one the reader
    /// is on in orange with an outline, so it reads as "here" among the rest
    /// the way a browser's find does.
    /// </summary>
    private void DrawSearchHits(CanvasDrawingSession ds, PageBox page)
    {
        if (_hits.Count == 0) return;
        if (!_hitsByPage.TryGetValue(page.Index, out var range)) return;

        for (int i = range.Start; i < range.Start + range.Count && i < _hits.Count; i++)
        {
            bool current = i == _searchCursor;

            foreach (var run in _hits[i].Runs)
            {
                var rect = ToScreenRect(page, run);
                // A hit on a tiny label can land under a pixel; widen it so it
                // is still findable when the whole sheet is on screen.
                if (rect.Width < 3) rect = new Rect(rect.X - 1.5, rect.Y, 3, rect.Height);
                if (rect.Height < 3) rect = new Rect(rect.X, rect.Y - 1.5, rect.Width, 3);

                ds.FillRectangle(rect, current ? SearchCurrentFill : SearchFill);
                if (current)
                {
                    ds.DrawRectangle(rect, SearchCurrentStroke, 1.4f);
                }
            }
        }
    }

    /// <summary>
    /// Where this sheet's marks sit on the canvas. Built per page and per
    /// frame, because it depends on the scroll position, the zoom and the
    /// sheet's own turn — and it is the only thing that knows all three.
    /// </summary>
    private SheetPlacement PlacementOf(PageBox page)
    {
        var sheet = SheetSize(page.Index, new PdfPageSize(page.WidthPt, page.HeightPt));

        return new SheetPlacement(
            sheet.WidthPt,
            sheet.HeightPt,
            RotationOf(page.Index),
            (page.XPt - _origin.X) * _scale,
            (page.YPt - _origin.Y) * _scale,
            _scale);
    }

    /// <summary>
    /// Paints the sheet's marks over the drawing, and the stroke being made
    /// right now on top of them. The mark in progress is drawn through the very
    /// same code as a finished one, so nothing shifts at the moment the pen
    /// comes up.
    /// </summary>
    private void DrawAnnotations(CanvasDrawingSession ds, PageBox page)
    {
        var marks = _annotations.ForPage(page.Index);
        bool drafting = _draftPage == page.Index
            && ((_isDrawing && _draft.Count > 0) || (_placingVertices && _vertices.Count > 0));

        if (marks.Count == 0 && !drafting) return;

        var placement = PlacementOf(page);

        foreach (var mark in marks)
        {
            AnnotationRenderer.Draw(ds, mark, placement);
        }

        if (_selected is { } selected && _selectedPage == page.Index)
        {
            AnnotationRenderer.DrawSelection(ds, selected, placement, HandleOffsetPt);
        }

        if (drafting && BuildDraft() is { } preview)
        {
            AnnotationRenderer.Draw(ds, preview, placement);
        }

        // The ring that says the point went where the drawing is, not where the
        // pointer was. Without it a snap is invisible, and a measurement that
        // silently moved the reader's click is the one thing a measuring tool
        // must not do.
        if (_snappedAt is { } pulled && _tool.IsMeasurement() && page.Index == _draftPage)
        {
            var at = placement.ToScreen(pulled);
            ds.DrawCircle(at, SnapRingDips, SnapRing, 1.6f);
            ds.DrawCircle(at, 1.5f, SnapRing, 1.6f);
        }
    }

    /// <summary>The page with a given index, as it is laid out right now.</summary>
    private PageBox? PageAt(int index)
    {
        if (_layout is not { } layout) return null;

        foreach (var page in layout.Pages)
        {
            if (page.Index == index) return page;
        }
        return null;
    }

    private bool HitsPending(Point position, PendingSignature pending, out Vector2 sheetPoint)
    {
        sheetPoint = default;

        if (!TryHitSheet(position, out var hit, out var point) || hit.Index != pending.PageIndex) return false;

        sheetPoint = point;

        var box = pending.SheetRect;
        return point.X >= box.X && point.X <= box.X + box.Width
            && point.Y >= box.Y && point.Y <= box.Y + box.Height;
    }

    /// <summary>
    /// Draws the signature that is placed but not yet written, as it will look
    /// once it is.
    ///
    /// The lines and the type size come from the engine — the same
    /// <see cref="PdfSignatureStamp"/> that writes the appearance into the file —
    /// so what is on screen is not a sketch of the stamp but the stamp itself,
    /// measured the same way.
    /// </summary>
    private void DrawPendingSignature(CanvasDrawingSession ds, PageBox page)
    {
        if (Pending is not { } pending || pending.PageIndex != page.Index) return;

        var placement = PlacementOf(page);
        var box = placement.ToScreen(pending.SheetRect);
        if (box.Width < 1 || box.Height < 1) return;

        // Blue and dashed while it is only a proposal, so it cannot be mistaken
        // for something already written into the drawing.
        ds.FillRectangle(box, Color.FromArgb(26, 60, 110, 200));
        ds.DrawRectangle(box, Color.FromArgb(220, 60, 110, 200), 1.4f);

        float size = PdfSignatureStamp.FitSize(
            pending.Lines, pending.SheetRect.Width, pending.SheetRect.Height) * placement.Scale;

        if (size < 3f) return;

        float padding = 4f * placement.Scale;
        float room = (float)box.Width - padding * 2f;

        // The engine sizes the type with Helvetica's metrics, and the canvas
        // draws it in Arial. They are metrically the same face, but "the same"
        // is not "identical", and the preview has to be the promise the file
        // keeps — so what is about to be drawn is measured, and shrunk if the
        // last few thousandths do not fit.
        float widest = 0f;
        foreach (var (text, strong) in pending.Lines)
        {
            using var trial = Face(size, strong);
            using var laid = new CanvasTextLayout(ds, text, trial, 0f, 0f);
            widest = Math.Max(widest, (float)laid.LayoutBounds.Width);
        }

        if (widest > room && widest > 0f) size *= room / widest;
        if (size < 3f) return;

        // Baselines exactly where the appearance stream puts them: one type size
        // below the top, then a line and a quarter apart.
        float baseline = (float)box.Top + padding + size;

        foreach (var (text, strong) in pending.Lines)
        {
            using var format = Face(size, strong);
            using var layout = new CanvasTextLayout(ds, text, format, 0f, 0f);

            ds.DrawTextLayout(
                layout,
                new Vector2((float)box.Left + padding, baseline - layout.LineMetrics[0].Baseline),
                Color.FromArgb(255, 30, 30, 36));

            baseline += size * 1.25f;
        }
    }

    private static CanvasTextFormat Face(float size, bool strong) => new()
    {
        FontFamily = "Arial",
        FontSize = size,
        FontWeight = strong ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
        WordWrapping = CanvasWordWrapping.NoWrap,
    };

    private Rect ToScreenRect(PageBox page, TextRun run) => new(
        (page.XPt + run.Left - _origin.X) * _scale,
        (page.YPt + run.Top - _origin.Y) * _scale,
        Math.Max(0, (run.Right - run.Left) * _scale),
        Math.Max(0, (run.Bottom - run.Top) * _scale));

    private void RequestTile(TileKey key, int documentId, OverlaySheet? overlay = null)
    {
        var pending = overlay is null ? _inFlight : _compareInFlight;
        if (documentId < 0 || !pending.Add(key)) return;

        _ = LoadTileAsync(key, documentId, overlay, _documentGeneration, _compareGeneration);
    }

    private async Task LoadTileAsync(
        TileKey key, int documentId, OverlaySheet? overlay, int generation, int compareGeneration)
    {
        try
        {
            var data = overlay is { } laid
                ? await _queue.RequestComparisonTileAsync(documentId, key, ZoomLevels.TileSize, laid)
                : await _queue.RequestTileAsync(documentId, key, ZoomLevels.TileSize);

            if (generation != _documentGeneration || !_isActive) return;

            // A composed tile was made with the colours and the pairing of the
            // moment it was asked for. Caching it against a comparison that has
            // since been set up differently is how the old picture ends up on
            // top of the new one.
            if (overlay is not null && compareGeneration != _compareGeneration) return;

            if (data is { } tile)
            {
                var bitmap = CanvasBitmap.CreateFromBytes(
                    Canvas,
                    tile.Bgra,
                    tile.Width,
                    tile.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    Canvas.Dpi);
                (overlay is null ? _cache : _compareCache).Add(key, bitmap);

                // The same pixels, kept on this side of the graphics card, so a
                // measurement can be pulled onto the drawing without asking
                // PDFium anything: on a dense A0 that question costs a second
                // and this costs a lookup. Only the plain tiles — a comparison
                // is coloured by what changed, which is not what a reader is
                // aiming at.
                if (overlay is null) KeepForSnapping(key, tile);

                Canvas.Invalidate();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ZenInk: fallo al renderizar tile {key}: {ex}");
        }
        finally
        {
            (overlay is null ? _inFlight : _compareInFlight).Remove(key);
        }
    }

    private void EnsureTextLayer(int pageIndex)
    {
        var key = TextKeyFor(pageIndex);
        if (key.DocumentId < 0 || _textLayers.ContainsKey(key) || !_textInFlight.Add(key)) return;
        _ = LoadTextLayerAsync(key, key.DocumentId, _documentGeneration);
    }

    private async Task LoadTextLayerAsync(TextLayerKey key, int documentId, int generation)
    {
        try
        {
            var layer = await _queue.RequestTextLayerAsync(documentId, key.PageIndex, key.Rotation);
            if (generation != _documentGeneration) return;

            RememberTextLayer(key, layer);
            Canvas.Invalidate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ZenInk: fallo al extraer texto de la página {key.PageIndex}: {ex}");
        }
        finally
        {
            _textInFlight.Remove(key);
        }
    }

    private void RememberTextLayer(TextLayerKey key, PageTextLayer layer)
    {
        _textLayers[key] = layer;
        _textLru.Remove(key);
        _textLru.AddLast(key);

        while (_textLru.Count > MaxTextLayers)
        {
            var oldest = _textLru.First!.Value;
            _textLru.RemoveFirst();
            if (oldest.PageIndex != _selectionPage)
            {
                _textLayers.Remove(oldest);
            }
        }
    }

    // --- comparar revisiones ---------------------------------------------
    //
    // Un BIM Manager no lee un plano: lee qué ha cambiado entre la revisión J
    // y la K. La revisión se abre como documento aparte y se dibuja dentro del
    // propio tile, así que moverse, ampliar, capturar e imprimir siguen siendo
    // lo de siempre — y no hay un segundo lienzo que mantener en su sitio.

    /// <summary>Raised when the comparison starts, ends, or is set up differently.</summary>
    public event EventHandler? ComparisonChanged;

    /// <summary>The revision laid over the document, or null when nothing is.</summary>
    public ComparisonSource? Revision => _revision;

    public bool IsComparing => _revision is not null;

    /// <summary>
    /// Which page of the revision is paired with which sheet, as a shift. A
    /// re-issued set usually pairs one to one; a cover sheet added or dropped
    /// along the way is the case this exists for.
    /// </summary>
    public int ComparePageOffset
    {
        get => _comparePageOffset;
        set => SetComparison(() => _comparePageOffset = value);
    }

    public CompareFit CompareFit
    {
        get => _compareFit;
        set => SetComparison(() => _compareFit = value);
    }

    /// <summary>
    /// The colours the two files are read in. Changing them redraws, but it
    /// does not send the sweep round again: where the two drawings disagree has
    /// nothing to do with what colour that is shown in, and re-measuring a
    /// dense A0 to recolour it would take seconds and lose the reader's place
    /// in the list.
    /// </summary>
    public ComparePalette ComparePalette
    {
        get => _comparePalette;
        set => SetComparison(() => _comparePalette = value, resweep: false);
    }

    /// <summary>Exchanges the two files' colours, which is the whole of saying which one is the newer.</summary>
    public void SwapCompareColours() => ComparePalette = _comparePalette.Swapped();

    /// <summary>
    /// Opens the other revision and lays it over the document.
    ///
    /// It is opened as a document of its own and never joins the plan: nothing
    /// about it is written, and the drawing being compared must not be able to
    /// pick up sheets from it by accident.
    /// </summary>
    public async Task CompareWithAsync(string path, string name, string? password = null)
    {
        var info = await _queue.OpenDocumentAsync(path, password);

        StopComparing();
        _revision = new ComparisonSource(path, name, info.DocumentId, info.Pages);
        _comparePageOffset = 0;

        RestartComparison();
    }

    /// <summary>Puts the drawing back the way it was read, and lets go of the revision.</summary>
    public void StopComparing()
    {
        if (_revision is { } revision)
        {
            _ = _queue.CloseDocumentAsync(revision.DocumentId);
        }

        _revision = null;
        RestartComparison();
    }

    /// <summary>
    /// Throws away everything that was composed or measured under the previous
    /// answer. Every setting goes through here, which is what keeps a tile made
    /// with the old colours from landing on top of the new ones.
    /// </summary>
    private void RestartComparison(bool resweep = true)
    {
        _compareGeneration++;
        _compareCache.Clear();
        _compareInFlight.Clear();

        if (resweep)
        {
            _sweepGeneration++;
            _changes.Clear();
            _changesInFlight.Clear();
            _sweepStage.Clear();
            _changeSheet = -1;
            _changeCursor = -1;

            // Off the moment the comparison is set up, rather than waiting for
            // the sheet to be drawn. It is the same fifteen seconds either way
            // on a dense A0, and starting it here is the part of them that can
            // be spent while the reader is still looking at the file dialog
            // closing.
            EnsureChanges(CurrentPageIndex);
        }

        Canvas.Invalidate();
        ComparisonChanged?.Invoke(this, EventArgs.Empty);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetComparison(Action change, bool resweep = true)
    {
        change();
        if (_revision is null) return;

        RestartComparison(resweep);
    }

    /// <summary>
    /// How the revision sits on one sheet, or null when that sheet has no
    /// counterpart. A sheet without one is drawn as itself rather than as an
    /// empty comparison: the set gained or lost a drawing, and saying so by
    /// showing the sheet in one colour would be a lie about its content.
    /// </summary>
    public OverlaySheet? OverlayFor(int sheet)
    {
        if (_revision is not { } revision) return null;
        if (sheet < 0 || sheet >= _pageSizes.Count) return null;

        int page = sheet + _comparePageOffset;
        if (page < 0 || page >= revision.Pages.Count) return null;

        var alignment = SheetAlignment.For(_pageSizes[sheet], revision.Pages[page], RotationOf(sheet), _compareFit);
        return new OverlaySheet(revision.DocumentId, page, alignment, _comparePalette);
    }

    /// <summary>The revision's page paired with a sheet, numbered as a reader would say it, or 0 for none.</summary>
    public int PairedPageNumber(int sheet)
    {
        if (_revision is not { } revision) return 0;

        int page = sheet + _comparePageOffset;
        return page >= 0 && page < revision.Pages.Count ? page + 1 : 0;
    }

    /// <summary>The changes found on a sheet. Empty both while the sweep runs and when it found none.</summary>
    public IReadOnlyList<ChangeRegion> ChangesOn(int sheet) =>
        _changes.TryGetValue(sheet, out var found) ? found : [];

    /// <summary>Whether the sweep of a sheet has finished, which is what tells "none" from "not yet".</summary>
    public bool ChangesReady(int sheet) => _changes.ContainsKey(sheet);

    /// <summary>Which change the reader is standing on, counted from one, or 0 before they have stepped onto any.</summary>
    public int ChangeNumber => _changeSheet == CurrentPageIndex ? _changeCursor + 1 : 0;

    /// <summary>How many changes there are on the sheet in view.</summary>
    public int ChangeCount => ChangesOn(CurrentPageIndex).Count;

    /// <summary>Whether the sheet in view has been swept — what tells "no changes" from "still looking".</summary>
    public bool ChangesReadyHere => _revision is not null && ChangesReady(CurrentPageIndex);

    /// <summary>
    /// Walks to the next or previous change on the sheet, wrapping round it.
    ///
    /// Within the sheet and not across the document on purpose: pairing is per
    /// sheet, and jumping to another one would move the reader off the drawing
    /// they were checking without their asking.
    /// </summary>
    public void StepChange(int direction)
    {
        int sheet = CurrentPageIndex;
        var found = ChangesOn(sheet);
        if (found.Count == 0) return;

        if (_changeSheet != sheet)
        {
            _changeSheet = sheet;
            _changeCursor = -1;
        }

        _changeCursor = _changeCursor < 0
            ? (direction > 0 ? 0 : found.Count - 1)
            : ((((_changeCursor + direction) % found.Count) + found.Count) % found.Count);

        GoToChange(sheet, found[_changeCursor]);
    }

    private void GoToChange(int sheet, ChangeRegion change)
    {
        if (PageBoxOf(sheet) is not { } page) return;

        StopZoomGlide();

        var centre = new Vector2(
            page.XPt + change.Box.X + (change.Box.Width / 2f),
            page.YPt + change.Box.Y + (change.Box.Height / 2f));

        _origin = centre - new Vector2(
            (float)(Canvas.ActualWidth / _scale / 2.0),
            (float)(Canvas.ActualHeight / _scale / 2.0));

        ClampOrigin();
        Canvas.Invalidate();
        ComparisonChanged?.Invoke(this, EventArgs.Empty);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Starts the sweep for a sheet if it has not been swept yet. Called from
    /// the draw, so what is on screen is what gets measured first — the same
    /// rule the tiles and the text layers already follow.
    /// </summary>
    private void EnsureChanges(int sheet)
    {
        if (_revision is null || _changes.ContainsKey(sheet)) return;
        if (OverlayFor(sheet) is not { } overlay) return;

        var origin = OriginOf(sheet);
        if (origin.IsBlank || !_changesInFlight.Add(sheet)) return;

        _ = SweepAsync(sheet, origin.DocumentId, origin.PageIndex, overlay, _documentGeneration, _sweepGeneration);
    }

    /// <summary>
    /// Renders both revisions of one sheet onto a single coarse grid and asks
    /// the engine where they disagree.
    ///
    /// It goes down the print band lane, which is served below the tiles: the
    /// count of changes can arrive a moment late, but the drawing the reader is
    /// looking at cannot.
    /// </summary>
    private async Task SweepAsync(
        int sheet, int documentId, int pageIndex, OverlaySheet overlay, int generation, int sweepGeneration)
    {
        try
        {
            Reached(sheet, CompareSweepStage.Sheet, sweepGeneration);

            var laid = _pageSizes[sheet];
            double scale = RevisionInk.DetectionDpi / 72.0;

            // A wall-sized sheet drops its resolution rather than its memory.
            long pixels = (long)Math.Ceiling(laid.WidthPt * scale) * (long)Math.Ceiling(laid.HeightPt * scale);
            if (pixels > RevisionInk.MaxDetectionPixels)
            {
                scale *= Math.Sqrt(RevisionInk.MaxDetectionPixels / (double)pixels);
            }

            int width = Math.Max(1, (int)Math.Ceiling(laid.WidthPt * scale));
            int height = Math.Max(1, (int)Math.Ceiling(laid.HeightPt * scale));

            var alignment = overlay.Alignment;
            var sheetBand = await _queue.RequestPrintBandAsync(
                documentId, pageIndex, RotationOf(sheet), scale, 0, 0, width, height, monochrome: false);

            Reached(sheet, CompareSweepStage.Revision, sweepGeneration);

            var revisionBand = await _queue.RequestBandAsync(
                overlay.DocumentId,
                overlay.PageIndex,
                alignment.QuarterTurns,
                scale * alignment.ScaleX,
                scale * alignment.ScaleY,
                -(int)Math.Round(alignment.OffsetXPt * scale),
                -(int)Math.Round(alignment.OffsetYPt * scale),
                width,
                height,
                monochrome: false);

            if (sheetBand is not { } a || revisionBand is not { } b) return;

            Reached(sheet, CompareSweepStage.Looking, sweepGeneration);

            // Off the UI thread: flooding three million pixels is not much, but
            // it is more than a frame, and it would be felt as the drawing
            // sticking under the hand.
            var found = await Task.Run(
                () => RevisionInk.FindChanges(a.Bgra, b.Bgra, width, height, 1.0 / scale));

            if (generation != _documentGeneration || sweepGeneration != _sweepGeneration) return;

            _changes[sheet] = found;
            Canvas.Invalidate();
            ComparisonChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A sheet whose changes cannot be counted is still a sheet worth
            // showing the comparison of.
            System.Diagnostics.Debug.WriteLine($"ZenInk: no se pudo comparar la hoja {sheet}: {ex.Message}");
        }
        finally
        {
            _changesInFlight.Remove(sheet);
            _sweepStage.Remove(sheet);
        }
    }

    /// <summary>
    /// Says where a sheet's sweep has got to, unless the answer is already
    /// stale — the reader may have repaired the sheets or picked another
    /// revision while PDFium was busy with this one.
    /// </summary>
    private void Reached(int sheet, CompareSweepStage stage, int sweepGeneration)
    {
        if (sweepGeneration != _sweepGeneration) return;

        _sweepStage[sheet] = stage;
        ComparisonChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>How far along the sweep of the sheet in view is, for saying so while it runs.</summary>
    public CompareSweepStage SweepStageHere =>
        _sweepStage.TryGetValue(CurrentPageIndex, out var stage) ? stage : CompareSweepStage.Idle;

    /// <summary>
    /// Rings the places the two revisions disagree. Drawn over the composed
    /// tiles and not into them, so that they can be stepped through and so that
    /// a capture of the drawing carries the difference itself rather than a
    /// box drawn around it.
    ///
    /// Past a certain number only the one being stood on is ringed. A dozen
    /// rings are a map of where to look; a hundred and fifty are a mesh laid
    /// over the drawing, and the drawing is what the reader came for — on a
    /// sheet that changed everywhere the colour already says so, which is what
    /// the rings were there to add.
    /// </summary>
    private void DrawChangeRings(CanvasDrawingSession ds, PageBox page)
    {
        var found = ChangesOn(page.Index);
        if (found.Count == 0) return;

        bool current = page.Index == _changeSheet;
        float inset = (float)(ChangeRingInsetDips / Math.Max(_scale, 0.02));
        bool onlyCurrent = found.Count > MostRingsWorthDrawing;

        for (int i = 0; i < found.Count; i++)
        {
            if (onlyCurrent && !(current && i == _changeCursor)) continue;

            var box = found[i].Box;
            var ring = new Rect(
                (page.XPt + box.X - inset - _origin.X) * _scale,
                (page.YPt + box.Y - inset - _origin.Y) * _scale,
                Math.Max(1.0, (box.Width + (inset * 2)) * _scale),
                Math.Max(1.0, (box.Height + (inset * 2)) * _scale));

            bool here = current && i == _changeCursor;
            ds.DrawRoundedRectangle(
                ring, 3f, 3f,
                here ? ChangeRingCurrent : ChangeRingStroke,
                here ? 2.4f : 1.2f);
        }
    }

    // --- find -----------------------------------------------------------

    /// <summary>Shows the find bar and puts the caret in it, keeping any term already typed.</summary>
    public void OpenFind()
    {
        FindBar.Visibility = Visibility.Visible;

        // Closing the bar drops the results but keeps the term, so reopening
        // has to look for it again rather than show an empty count.
        if (FindInput.Text.Length > 0 && _searchQuery != FindInput.Text)
        {
            _searchQuery = FindInput.Text;
            RestartSearch();
        }

        FindInput.Focus(FocusState.Programmatic);
        FindInput.SelectAll();
    }

    /// <summary>Opens the find bar already looking for a given term.</summary>
    public void FindText(string term)
    {
        FindInput.Text = term;
        OpenFind();
    }

    public void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        ResetSearch();
        Canvas.Invalidate();
        Canvas.Focus(FocusState.Programmatic);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsFindOpen => FindBar.Visibility == Visibility.Visible;

    private void OnFindCloseClicked(object sender, RoutedEventArgs e) => CloseFind();

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = FindInput.Text;
        RestartSearch();
    }

    private void OnFindOptionChanged(object sender, RoutedEventArgs e)
    {
        _searchOptions = new TextSearchOptions(
            FindMatchCase.IsChecked == true,
            FindWholeWord.IsChecked == true);
        RestartSearch();
    }

    private void OnFindInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                // Shift walks backwards, the way a browser's find bar does.
                bool back = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                StepFind(back ? -1 : 1);
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                CloseFind();
                e.Handled = true;
                break;
        }
    }

    private void OnFindNextClicked(object sender, RoutedEventArgs e) => StepFind(1);

    private void OnFindPreviousClicked(object sender, RoutedEventArgs e) => StepFind(-1);

    /// <summary>Moves to the next or previous hit, wrapping round the document.</summary>
    public void StepFind(int direction)
    {
        if (_hits.Count == 0) return;

        int next = _searchCursor < 0
            ? (direction >= 0 ? 0 : _hits.Count - 1)
            : (_searchCursor + direction + _hits.Count) % _hits.Count;

        GoToHit(next);
    }

    /// <summary>
    /// Drops the search entirely — no term, no hits, no highlights. The text
    /// box keeps what was typed, because reopening the bar on the same term is
    /// the common case; the *query* has to go, or a later turn would rebuild
    /// highlights for a search the reader had closed.
    /// </summary>
    private void ResetSearch()
    {
        _searchGeneration++;
        _searchRunning = false;
        _searchScanned = 0;
        _searchCursor = -1;
        _searchQuery = string.Empty;
        _hits.Clear();
        _hitsByPage.Clear();
        UpdateFindChrome();
    }

    private void RestartSearch()
    {
        _searchGeneration++;
        _searchRunning = false;
        _searchScanned = 0;
        _searchCursor = -1;
        _hits.Clear();
        _hitsByPage.Clear();

        Canvas.Invalidate();

        if (_searchQuery.Length == 0 || _documentId < 0 || PageCount == 0)
        {
            UpdateFindChrome();
            return;
        }

        _searchRunning = true;
        UpdateFindChrome();
        _ = ScanAsync(_searchGeneration, _documentGeneration, _searchQuery, _searchOptions);
    }

    /// <summary>
    /// Walks the document a sheet at a time, in page order, so hits arrive
    /// already sorted and the count can climb while the reader waits. One sheet
    /// is in flight at a time on purpose: text extraction sits below tiles in
    /// the queue, and flooding it would still leave a dense sheet's tiles
    /// queued behind a hundred text requests.
    /// </summary>
    private async Task ScanAsync(int generation, int documentGeneration, string query, TextSearchOptions options)
    {
        int startPage = CurrentPageIndex;

        for (int pageIndex = 0; pageIndex < PageCount; pageIndex++)
        {
            if (generation != _searchGeneration || documentGeneration != _documentGeneration) return;

            var key = TextKeyFor(pageIndex);
            PageTextLayer layer;

            // Blank paper carries no words; there is nothing to ask for.
            if (key.DocumentId < 0) continue;

            if (_textLayers.TryGetValue(key, out var cached))
            {
                layer = cached;
            }
            else
            {
                try
                {
                    layer = await _queue.RequestTextLayerAsync(key.DocumentId, key.PageIndex, key.Rotation);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ZenInk: fallo al buscar en la hoja {pageIndex}: {ex}");
                    continue;
                }

                if (generation != _searchGeneration || documentGeneration != _documentGeneration) return;
                RememberTextLayer(key, layer);
            }

            var matches = layer.Find(query, options);
            if (matches.Count > 0)
            {
                _hitsByPage[pageIndex] = (_hits.Count, matches.Count);
                foreach (var match in matches)
                {
                    _hits.Add(new SearchHit(pageIndex, match, layer.BuildRuns(match.StartIndex, match.EndIndex)));
                }

                // Land on the first hit at or after the sheet in view, so a
                // search starts where the reader is rather than at page one.
                if (_searchCursor < 0 && pageIndex >= startPage)
                {
                    GoToHit(_hitsByPage[pageIndex].Start);
                }
            }

            _searchScanned = pageIndex + 1;
            UpdateFindChrome();
            Canvas.Invalidate();
        }

        if (generation != _searchGeneration) return;

        _searchRunning = false;

        // Nothing after the reader's sheet: wrap to the first hit in the file.
        if (_searchCursor < 0 && _hits.Count > 0)
        {
            GoToHit(0);
        }

        UpdateFindChrome();
    }

    /// <summary>Selects a hit and brings it into view, centred, without changing the zoom.</summary>
    private void GoToHit(int index)
    {
        if (index < 0 || index >= _hits.Count) return;

        _searchCursor = index;
        var hit = _hits[index];

        // In single-sheet mode — and in a two-up spread — the hit may be on a
        // sheet that is not currently laid out at all.
        if (_layout?.RowOfPage(hit.PageIndex) is null)
        {
            GoToPage(hit.PageIndex);
        }

        if (_layout is { } layout)
        {
            foreach (var page in layout.Pages)
            {
                if (page.Index != hit.PageIndex) continue;
                CentreOn(page, hit.Runs);
                break;
            }
        }

        UpdateFindChrome();
        Canvas.Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CentreOn(PageBox page, IReadOnlyList<TextRun> runs)
    {
        if (runs.Count == 0) return;

        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        foreach (var run in runs)
        {
            left = Math.Min(left, run.Left);
            top = Math.Min(top, run.Top);
            right = Math.Max(right, run.Right);
            bottom = Math.Max(bottom, run.Bottom);
        }

        var centre = new Vector2(
            page.XPt + (left + right) / 2f,
            page.YPt + (top + bottom) / 2f);

        _origin = centre - new Vector2(
            (float)(Canvas.ActualWidth / _scale / 2.0),
            (float)(Canvas.ActualHeight / _scale / 2.0));

        ClampOrigin();
    }

    /// <summary>
    /// Keeps the find bar honest while a scan is still running: the count is a
    /// running total, not a final one, so it says so rather than looking like a
    /// document with fewer hits than it has.
    /// </summary>
    private void UpdateFindChrome()
    {
        bool any = _hits.Count > 0;

        FindNextButton.IsEnabled = any;
        FindPreviousButton.IsEnabled = any;

        if (_searchQuery.Length == 0)
        {
            FindCount.Text = string.Empty;
            return;
        }

        if (!any)
        {
            FindCount.Text = _searchRunning ? "Buscando…" : "Sin resultados";
            return;
        }

        string position = _searchCursor >= 0 ? $"{_searchCursor + 1}/{_hits.Count}" : $"{_hits.Count}";
        FindCount.Text = _searchRunning && _searchScanned < PageCount ? $"{position}…" : position;
    }
}
