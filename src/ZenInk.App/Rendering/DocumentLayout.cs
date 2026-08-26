namespace ZenInk_App.Rendering;

/// <summary>A page's placement within document space, in PDF points.</summary>
public readonly record struct PageBox(int Index, float XPt, float YPt, float WidthPt, float HeightPt)
{
    public float RightPt => XPt + WidthPt;
    public float BottomPt => YPt + HeightPt;
}

/// <summary>
/// Stacks every page of a document into one continuous vertical strip, in PDF
/// points, so the viewer can treat scrolling as a single coordinate space
/// instead of juggling per-page transforms. Pages are centered horizontally
/// against the widest page in the document, which is what keeps a mixed set of
/// sheet sizes looking aligned rather than ragged.
/// </summary>
public sealed class DocumentLayout
{
    public const float PageGapPt = 16f;

    private readonly PageBox[] _pages;

    public DocumentLayout(IReadOnlyList<PdfPageSize> sizes)
    {
        _pages = new PageBox[sizes.Count];

        float widest = 0f;
        foreach (var size in sizes)
        {
            widest = Math.Max(widest, size.WidthPt);
        }

        float y = 0f;
        for (int i = 0; i < sizes.Count; i++)
        {
            var size = sizes[i];
            float x = (widest - size.WidthPt) / 2f;
            _pages[i] = new PageBox(i, x, y, size.WidthPt, size.HeightPt);
            y += size.HeightPt;
            if (i < sizes.Count - 1)
            {
                y += PageGapPt;
            }
        }

        WidthPt = widest;
        HeightPt = y;
    }

    public IReadOnlyList<PageBox> Pages => _pages;

    public float WidthPt { get; }

    public float HeightPt { get; }

    public int PageCount => _pages.Length;

    /// <summary>
    /// Pages overlapping the given document-space band. Pages are stacked in
    /// increasing Y, so this walks the sorted array and stops once past the
    /// band rather than scanning every page of a large set each frame.
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
