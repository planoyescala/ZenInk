namespace ZenInk.Core;

/// <summary>A page's placement within document space, in PDF points.</summary>
public readonly record struct PageBox(int Index, float XPt, float YPt, float WidthPt, float HeightPt)
{
    public float RightPt => XPt + WidthPt;
    public float BottomPt => YPt + HeightPt;
}

/// <summary>
/// One band of the strip: a single sheet, or a spread of them side by side.
/// Fitting a "whole page" to the viewport means fitting this, since in a
/// two-up view the pair is what the reader thinks of as the page.
/// </summary>
public readonly record struct RowBox(float XPt, float YPt, float WidthPt, float HeightPt)
{
    public float BottomPt => YPt + HeightPt;
}

/// <summary>
/// Stacks the pages of a document into one continuous vertical strip, in PDF
/// points, so the viewer can treat scrolling as a single coordinate space
/// instead of juggling per-page transforms. Rows are centered horizontally
/// against the widest row, which is what keeps a mixed set of sheet sizes
/// looking aligned rather than ragged.
///
/// A row holds <c>columns</c> sheets, so the same code lays out one-up and
/// two-up views. Sheets in a row share the row's top edge: that keeps the Y of
/// every page non-decreasing through <see cref="Pages"/>, which is the
/// invariant <see cref="PagesInBand"/> relies on to stop early.
/// </summary>
public sealed class DocumentLayout
{
    public const float PageGapPt = 16f;

    private readonly PageBox[] _pages;
    private readonly RowBox[] _rows;

    /// <summary>Row each laid-out page belongs to, indexed the same as <see cref="_pages"/>.</summary>
    private readonly int[] _rowOfPage;

    /// <summary>Every sheet stacked into one scrollable strip.</summary>
    public static DocumentLayout Continuous(IReadOnlyList<PdfPageSize> sizes) => Continuous(sizes, 1);

    /// <summary>Every sheet stacked into one scrollable strip, <paramref name="columns"/> at a time.</summary>
    public static DocumentLayout Continuous(IReadOnlyList<PdfPageSize> sizes, int columns) =>
        new(sizes, null, columns);

    /// <summary>
    /// Just one sheet, alone in document space, so scrolling can never carry
    /// the view onto a neighbouring page — moving between sheets becomes an
    /// explicit action instead of a side effect of panning.
    /// </summary>
    public static DocumentLayout SinglePage(IReadOnlyList<PdfPageSize> sizes, int pageIndex) =>
        SinglePage(sizes, pageIndex, 1);

    /// <summary>
    /// One row at a time: a single sheet, or the spread that contains
    /// <paramref name="pageIndex"/> when <paramref name="columns"/> is two.
    /// </summary>
    public static DocumentLayout SinglePage(IReadOnlyList<PdfPageSize> sizes, int pageIndex, int columns) =>
        new(sizes, pageIndex, columns);

    private DocumentLayout(IReadOnlyList<PdfPageSize> sizes, int? onlyPageIndex, int columns)
    {
        DocumentPageCount = sizes.Count;
        Columns = Math.Max(1, columns);

        int[] order = BuildOrder(sizes.Count, onlyPageIndex, Columns);
        if (order.Length == 0)
        {
            _pages = [];
            _rows = [];
            _rowOfPage = [];
            return;
        }

        _pages = new PageBox[order.Length];
        _rowOfPage = new int[order.Length];
        int rowCount = (order.Length + Columns - 1) / Columns;
        _rows = new RowBox[rowCount];

        // First pass: each row's own extent, so the strip can be centred
        // against the widest row rather than against the widest single sheet.
        var rowWidths = new float[rowCount];
        var rowHeights = new float[rowCount];
        float widest = 0f;

        for (int i = 0; i < order.Length; i++)
        {
            int row = i / Columns;
            var size = sizes[order[i]];
            rowWidths[row] += size.WidthPt;
            if (i % Columns != 0)
            {
                rowWidths[row] += PageGapPt;
            }
            rowHeights[row] = Math.Max(rowHeights[row], size.HeightPt);
            widest = Math.Max(widest, rowWidths[row]);
        }

        float y = 0f;
        for (int row = 0; row < rowCount; row++)
        {
            float x = (widest - rowWidths[row]) / 2f;
            _rows[row] = new RowBox(x, y, rowWidths[row], rowHeights[row]);

            int first = row * Columns;
            int last = Math.Min(first + Columns, order.Length);
            for (int i = first; i < last; i++)
            {
                var size = sizes[order[i]];
                // The PageBox keeps the real PDF page index, so tile keys and
                // text layers stay valid across a mode switch.
                _pages[i] = new PageBox(order[i], x, y, size.WidthPt, size.HeightPt);
                _rowOfPage[i] = row;
                x += size.WidthPt + PageGapPt;
            }

            y += rowHeights[row];
            if (row < rowCount - 1)
            {
                y += PageGapPt;
            }
        }

        WidthPt = widest;
        HeightPt = y;
    }

    /// <summary>
    /// Which PDF pages this layout holds: all of them, or just the row that
    /// contains the requested one. Rows are aligned to the document's start,
    /// so page 1 always shares a spread with page 2 rather than sliding as the
    /// reader pages through.
    /// </summary>
    private static int[] BuildOrder(int pageCount, int? onlyPageIndex, int columns)
    {
        if (pageCount == 0) return [];

        if (onlyPageIndex is not { } only)
        {
            var all = new int[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                all[i] = i;
            }
            return all;
        }

        int index = Math.Clamp(only, 0, pageCount - 1);
        int first = index - (index % columns);
        int count = Math.Min(columns, pageCount - first);

        var row = new int[count];
        for (int i = 0; i < count; i++)
        {
            row[i] = first + i;
        }
        return row;
    }

    public IReadOnlyList<PageBox> Pages => _pages;

    /// <summary>The bands the pages are laid out in — one sheet each, or one spread each.</summary>
    public IReadOnlyList<RowBox> Rows => _rows;

    /// <summary>Sheets side by side in a row.</summary>
    public int Columns { get; }

    public float WidthPt { get; }

    public float HeightPt { get; }

    /// <summary>Pages actually laid out — the whole document, or one row of it.</summary>
    public int PageCount => _pages.Length;

    /// <summary>Pages in the PDF, regardless of how many are laid out.</summary>
    public int DocumentPageCount { get; }

    /// <summary>
    /// The row holding the given PDF page, or null if this layout does not hold
    /// that page. Used to fit a whole page — or a whole spread — to the viewport.
    /// </summary>
    public RowBox? RowOfPage(int pageIndex)
    {
        for (int i = 0; i < _pages.Length; i++)
        {
            if (_pages[i].Index == pageIndex) return _rows[_rowOfPage[i]];
        }
        return null;
    }

    /// <summary>
    /// Pages overlapping the given document-space band. Pages are laid out in
    /// rows of increasing Y, so this walks the sorted array and stops once past
    /// the band rather than scanning every page of a large set each frame.
    /// </summary>
    public IEnumerable<PageBox> PagesInBand(double topPt, double bottomPt)
    {
        foreach (var page in _pages)
        {
            if (page.BottomPt < topPt) continue;
            if (page.YPt > bottomPt) yield break;
            yield return page;
        }
    }

    /// <summary>Index of the page occupying most of the given band, for the page indicator.</summary>
    public int DominantPageIndex(double topPt, double bottomPt)
    {
        int best = 0;
        double bestOverlap = double.NegativeInfinity;

        foreach (var page in PagesInBand(topPt, bottomPt))
        {
            double overlap = Math.Min(bottomPt, page.BottomPt) - Math.Max(topPt, page.YPt);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = page.Index;
            }
        }

        return best;
    }
}
