namespace ZenInk.Core;

/// <summary>How the revision's sheet is laid over the one being read.</summary>
public enum CompareFit
{
    /// <summary>
    /// Scaled to fit and centred, proportions kept. What two plots of the same
    /// drawing need: an A1 print and an A0 print of one plan differ by a
    /// factor, not by a shape.
    /// </summary>
    Fit,

    /// <summary>
    /// Each axis scaled on its own until the revision fills the sheet. For the
    /// case Fit cannot reach — a re-plot whose margins changed, so the drawing
    /// sits on paper of a slightly different proportion.
    /// </summary>
    Stretch,
}

/// <summary>
/// Where the revision's page lands on the sheet being read. The transform is
/// only ever a quarter turn, a scale per axis and a translation — which is not
/// a simplification but the shape FPDF_RenderPageBitmap accepts, so the
/// revision can be rasterized straight onto the sheet's own pixel grid instead
/// of being rendered and then resampled.
///
/// <see cref="QuarterTurns"/> is absolute, not relative to the sheet: it is
/// what gets handed to PDFium, and it already carries the reader's own turn.
/// Offsets are in the sheet's points, y down, like everything else the viewer
/// lays out.
/// </summary>
public readonly record struct SheetAlignment(
    int QuarterTurns,
    double ScaleX,
    double ScaleY,
    double OffsetXPt,
    double OffsetYPt)
{
    public static SheetAlignment None => new(0, 1.0, 1.0, 0.0, 0.0);

    /// <summary>
    /// Below this the scale is taken to be exactly one. Two plots of the same
    /// sheet often differ by a rounding in the paper size, and a scale of
    /// 0.9997 would resample every pixel of the revision for nothing — which
    /// is what turns a clean overlay into one where every line is fringed.
    /// </summary>
    private const double SameSizeTolerance = 0.004;

    /// <summary>
    /// Lays <paramref name="revision"/> over <paramref name="sheet"/>.
    ///
    /// <paramref name="sheet"/> is the sheet as the viewer lays it out — the
    /// reader's turn already applied — and <paramref name="revision"/> is the
    /// other page as its own file reports it. <paramref name="sheetTurns"/> is
    /// that reader's turn, which the revision has to be given too: turning a
    /// sheet sideways and leaving what is laid over it upright compares
    /// nothing.
    ///
    /// The extra quarter turn on top of it is decided by shape alone. A set
    /// re-issued through a different plotter driver often arrives with its
    /// pages stored the other way round, and the reader should not have to
    /// notice.
    /// </summary>
    public static SheetAlignment For(PdfPageSize sheet, PdfPageSize revision, int sheetTurns, CompareFit fit)
    {
        int upright = sheetTurns & 3;
        int turned = (sheetTurns + 1) & 3;

        var asIs = Drawn(revision, upright);
        var sideways = Drawn(revision, turned);

        bool turn = AspectDistance(sideways, sheet) < AspectDistance(asIs, sheet);
        var drawn = turn ? sideways : asIs;

        double scaleX = sheet.WidthPt / Math.Max(drawn.WidthPt, 1f);
        double scaleY = sheet.HeightPt / Math.Max(drawn.HeightPt, 1f);

        if (fit == CompareFit.Fit)
        {
            scaleX = scaleY = Snap(Math.Min(scaleX, scaleY));
        }
        else
        {
            scaleX = Snap(scaleX);
            scaleY = Snap(scaleY);
        }

        return new SheetAlignment(
            turn ? turned : upright,
            scaleX,
            scaleY,
            (sheet.WidthPt - drawn.WidthPt * scaleX) / 2.0,
            (sheet.HeightPt - drawn.HeightPt * scaleY) / 2.0);
    }

    /// <summary>Moves the revision over the sheet, for two plots taken off different origins.</summary>
    public SheetAlignment Nudged(double dxPt, double dyPt) =>
        this with { OffsetXPt = OffsetXPt + dxPt, OffsetYPt = OffsetYPt + dyPt };

    /// <summary>The box a page occupies once a quarter turn is applied: the odd turns swap the axes.</summary>
    private static PdfPageSize Drawn(PdfPageSize page, int quarterTurns) =>
        (quarterTurns & 1) == 1 ? new PdfPageSize(page.HeightPt, page.WidthPt) : page;

    /// <summary>
    /// How far two boxes are from having the same proportions, measured as a
    /// ratio rather than a difference so that a wide sheet and a tall one come
    /// out the same distance apart whichever way round they are asked about.
    /// </summary>
    private static double AspectDistance(PdfPageSize a, PdfPageSize b) =>
        Math.Abs(Math.Log(
            (a.WidthPt / Math.Max(a.HeightPt, 1f)) /
            (b.WidthPt / Math.Max(b.HeightPt, 1f))));

    private static double Snap(double scale) =>
        Math.Abs(scale - 1.0) < SameSizeTolerance ? 1.0 : scale;
}

/// <summary>
/// The three colours a comparison is read in. They are named for the file each
/// one stands for and not for what changed, because which of the two is the
/// newer revision is the reader's to say — swapping them is a swap of these two
/// entries and nothing else.
/// </summary>
public readonly record struct ComparePalette(AnnotationColor Sheet, AnnotationColor Revision, byte Common)
{
    /// <summary>
    /// Blue for the document already open, red for the one laid over it, and
    /// the drawing they agree on in grey.
    ///
    /// Red against blue rather than the usual red against green: red and green
    /// are the pair a good number of readers cannot tell apart, and a picture
    /// whose entire content is the difference between two colours is the worst
    /// possible place to spend that.
    /// </summary>
    public static ComparePalette Default =>
        new(new AnnotationColor(25, 118, 210), new AnnotationColor(211, 47, 47), 140);

    public ComparePalette Swapped() => this with { Sheet = Revision, Revision = Sheet };
}

/// <summary>A patch of the sheet where the two revisions do not agree.</summary>
public readonly record struct ChangeRegion(RectPt Box, int Pixels);

/// <summary>
/// The pixel half of comparing two revisions: laying one render over another so
/// that what only one of them has stands out, and finding where those places
/// are.
///
/// It works on ink — how dark a pixel is — rather than on colour. A drawing
/// re-issued through a different plotter driver comes back in slightly
/// different greys and blues, and a comparison that called that a change would
/// call the whole sheet a change.
/// </summary>
public static class RevisionInk
{
    /// <summary>
    /// How far a pixel may look for its counterpart in the other render before
    /// it counts as being on its own.
    ///
    /// Without it every line in the drawing would come out fringed: two renders
    /// of the same geometry land on the pixel grid a fraction differently, and
    /// a line half a pixel to the left is, taken literally, one line removed
    /// and another added. A single pixel is the whole of that error — more than
    /// that starts hiding real changes.
    /// </summary>
    public const int Spread = 1;

    /// <summary>
    /// The resolution changes are hunted at. Generous on purpose: rendering a
    /// dense A0 costs walking its three million objects, which is the same
    /// whatever size the bitmap is, so a coarser sweep would be no faster and
    /// would miss the hairlines a plan is mostly made of.
    /// </summary>
    public const double DetectionDpi = 48.0;

    /// <summary>Ceiling on the sweep, so a wall-sized sheet drops its resolution rather than the memory.</summary>
    public const long MaxDetectionPixels = 4_000_000;

    /// <summary>Ink difference, out of 255, below which a pixel is taken to be unchanged.</summary>
    private const int ChangeThreshold = 32;

    /// <summary>Specks smaller than this are the pixel grid, not the drawing.</summary>
    private const int MinChangePixels = 3;

    /// <summary>
    /// How close two patches have to be to be read as one change. Eight pixels
    /// of the sweep is about a centimetre of paper: near enough that two boxes
    /// there would have been one revision cloud on a real drawing.
    /// </summary>
    private const int MergeDistancePixels = 8;

    /// <summary>
    /// Above this many patches the list stops being something to step through,
    /// so the largest are kept and the rest dropped. A sheet that reaches it
    /// has been redrawn rather than revised, and a reader told "200 changes"
    /// should be told that it is a floor — which is why this is not private.
    /// </summary>
    public const int MaxRegions = 200;

    /// <summary>
    /// How far across the paper one change may reach before it stops being a
    /// place to go to. About a sheet of A4 laid on the drawing: roughly what a
    /// reader takes in at once, and measured on the paper rather than as a
    /// share of the sheet, because that is what the eye works in — a change
    /// twenty-five centimetres across is a zone whether it is on an A0 or an
    /// A1.
    ///
    /// It exists because of two real issues of one drawing, where nothing came
    /// near the two hundred and yet one box covered 88 % of the paper: a
    /// boundary line that moved is a single connected patch running from one
    /// edge of the sheet to the other, and everything near it folds into it.
    /// "6 cambios", one of which is the drawing, is worse than no count at all.
    /// Past this a patch is cut on a grid, which is how a line that moved along
    /// a façade becomes somewhere to step through instead of one box around
    /// everything.
    /// </summary>
    private const double MaxRegionPoints = 720.0;

    /// <summary>
    /// Lays the revision's render over the sheet's and returns what comes out,
    /// cropped back from <paramref name="margin"/> pixels of surround.
    ///
    /// Both buffers cover the same pixel grid, wider than the result by that
    /// margin on every side, which is what lets <see cref="Spread"/> look at
    /// real neighbours right up to the edge. Without it every tile would carry
    /// a coloured seam along its border, drawn from the one row of pixels that
    /// had nothing to compare itself against.
    ///
    /// The composition is subtractive, as ink on paper is: what both drawings
    /// have absorbs every channel and comes out grey, and what only one of them
    /// has absorbs everything except its own colour.
    /// </summary>
    public static byte[] Compose(
        ReadOnlySpan<byte> sheet,
        ReadOnlySpan<byte> revision,
        int width,
        int height,
        int margin,
        ComparePalette palette)
    {
        var sheetInk = Ink(sheet, width, height);
        var revisionInk = Ink(revision, width, height);
        var sheetSpread = Spread == 0 ? sheetInk : Dilate(sheetInk, width, height, Spread);
        var revisionSpread = Spread == 0 ? revisionInk : Dilate(revisionInk, width, height, Spread);

        int outWidth = Math.Max(1, width - margin * 2);
        int outHeight = Math.Max(1, height - margin * 2);
        var packed = new byte[outWidth * outHeight * 4];

        for (int y = 0; y < outHeight; y++)
        {
            int source = ((y + margin) * width) + margin;
            int target = y * outWidth * 4;

            for (int x = 0; x < outWidth; x++, source++, target += 4)
            {
                int a = sheetInk[source];
                int b = revisionInk[source];

                // Ink counts as shared when the other drawing has some within
                // reach, and each side is asked separately: a line that moved
                // by a pixel is shared seen from either end, so it stays grey
                // instead of falling between the two tests and disappearing.
                int sharedBySheet = Math.Min(a, revisionSpread[source]);
                int sharedByRevision = Math.Min(b, sheetSpread[source]);

                int shared = Math.Max(sharedBySheet, sharedByRevision);
                int onlySheet = a - sharedBySheet;
                int onlyRevision = b - sharedByRevision;

                packed[target] = Channel(shared, onlySheet, onlyRevision, palette, palette.Sheet.B, palette.Revision.B);
                packed[target + 1] = Channel(shared, onlySheet, onlyRevision, palette, palette.Sheet.G, palette.Revision.G);
                packed[target + 2] = Channel(shared, onlySheet, onlyRevision, palette, palette.Sheet.R, palette.Revision.R);
                packed[target + 3] = 255;
            }
        }

        return packed;
    }

    /// <summary>
    /// Where the two renders disagree, as boxes over the area the sweep covers.
    ///
    /// The caller renders both onto the same grid — the sheet as it lies, the
    /// revision through its alignment — so a box here is already in the sheet's
    /// own geometry, and <paramref name="pointsPerPixel"/> is all it takes to
    /// put it back into points.
    /// </summary>
    public static IReadOnlyList<ChangeRegion> FindChanges(
        ReadOnlySpan<byte> sheet, ReadOnlySpan<byte> revision, int width, int height, double pointsPerPixel)
    {
        var sheetInk = Ink(sheet, width, height);
        var revisionInk = Ink(revision, width, height);
        var sheetSpread = Spread == 0 ? sheetInk : Dilate(sheetInk, width, height, Spread);
        var revisionSpread = Spread == 0 ? revisionInk : Dilate(revisionInk, width, height, Spread);

        var changed = new bool[width * height];
        for (int i = 0; i < changed.Length; i++)
        {
            int onlySheet = sheetInk[i] - Math.Min(sheetInk[i], revisionSpread[i]);
            int onlyRevision = revisionInk[i] - Math.Min(revisionInk[i], sheetSpread[i]);
            changed[i] = Math.Max(onlySheet, onlyRevision) >= ChangeThreshold;
        }

        // The cap goes to both steps, and for two different reasons: it stops
        // the fold from chaining half the drawing into one box, and it cuts up
        // the patches that were already that big when they came out of the
        // flood — a moved boundary line is one of those, and no merge is
        // involved in it at all.
        int reach = Math.Max(1, (int)(MaxRegionPoints / Math.Max(pointsPerPixel, 0.001)));
        int widest = Math.Min(width, reach);
        int tallest = Math.Min(height, reach);

        var found = Merge(Components(changed, width, height), MergeDistancePixels, widest, tallest);
        found = Split(found, changed, width, height, widest, tallest);

        if (found.Count > MaxRegions)
        {
            found = [.. found.OrderByDescending(box => (long)box.Width * box.Height).Take(MaxRegions)];
        }

        // Reading order, in bands: two changes side by side on the same row of
        // the drawing are stepped through left to right, and a few pixels of
        // difference in their tops must not reverse them.
        int band = Math.Max(1, (int)(72.0 / Math.Max(pointsPerPixel, 0.001)));

        return
        [
            .. found
                .OrderBy(box => box.Top / band)
                .ThenBy(box => box.Left)
                .Select(box => new ChangeRegion(
                    new RectPt(
                        (float)(box.Left * pointsPerPixel),
                        (float)(box.Top * pointsPerPixel),
                        (float)(box.Width * pointsPerPixel),
                        (float)(box.Height * pointsPerPixel)),
                    box.Pixels)),
        ];
    }

    /// <summary>One channel of the subtractive mix, saturating rather than wrapping where the layers pile up.</summary>
    private static byte Channel(
        int shared, int onlySheet, int onlyRevision, ComparePalette palette, byte sheetChannel, byte revisionChannel)
    {
        // A colour absorbs the channels it does not carry: red ink takes the
        // green and the blue out of the paper and leaves the red behind.
        int absorbed =
            (shared * palette.Common / 255) +
            (onlySheet * (255 - sheetChannel) / 255) +
            (onlyRevision * (255 - revisionChannel) / 255);

        return (byte)(255 - Math.Min(255, absorbed));
    }

    /// <summary>
    /// How dark each pixel is, which is what a comparison is actually about.
    /// Rec. 601 luma, the same weighting the print path greys with, so a red
    /// line and a yellow one of the same weight carry the same ink.
    /// </summary>
    private static byte[] Ink(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var ink = new byte[width * height];
        for (int i = 0; i < ink.Length; i++)
        {
            int at = i * 4;
            if (at + 2 >= bgra.Length) break;

            int luma = ((bgra[at + 2] * 299) + (bgra[at + 1] * 587) + (bgra[at] * 114)) / 1000;
            ink[i] = (byte)(255 - luma);
        }
        return ink;
    }

    /// <summary>
    /// Spreads ink by <paramref name="radius"/> pixels — the maximum over the
    /// square around each pixel. Done as two passes, across and then down,
    /// because a square maximum separates that way and the whole point of it is
    /// to cost nothing next to the render it follows.
    /// </summary>
    private static byte[] Dilate(byte[] ink, int width, int height, int radius)
    {
        var across = new byte[ink.Length];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                byte most = 0;
                int from = Math.Max(0, x - radius);
                int to = Math.Min(width - 1, x + radius);
                for (int at = from; at <= to; at++)
                {
                    if (ink[row + at] > most) most = ink[row + at];
                }
                across[row + x] = most;
            }
        }

        var down = new byte[ink.Length];
        for (int y = 0; y < height; y++)
        {
            int from = Math.Max(0, y - radius);
            int to = Math.Min(height - 1, y + radius);
            for (int x = 0; x < width; x++)
            {
                byte most = 0;
                for (int at = from; at <= to; at++)
                {
                    byte value = across[(at * width) + x];
                    if (value > most) most = value;
                }
                down[(y * width) + x] = most;
            }
        }

        return down;
    }

    private readonly record struct Box(int Left, int Top, int Right, int Bottom, int Pixels)
    {
        public int Width => Right - Left + 1;

        public int Height => Bottom - Top + 1;

        public Box Union(Box other) => new(
            Math.Min(Left, other.Left),
            Math.Min(Top, other.Top),
            Math.Max(Right, other.Right),
            Math.Max(Bottom, other.Bottom),
            Pixels + other.Pixels);

        public bool Near(Box other, int distance) =>
            Left - distance <= other.Right && other.Left - distance <= Right &&
            Top - distance <= other.Bottom && other.Top - distance <= Bottom;
    }

    /// <summary>
    /// The connected patches of changed pixels, eight-way. Flooded from an
    /// explicit stack rather than by recursion: a sheet whose background
    /// changed is one patch of millions of pixels, and that is a stack
    /// overflow, not a deep call.
    /// </summary>
    private static List<Box> Components(bool[] changed, int width, int height)
    {
        var boxes = new List<Box>();
        var seen = new bool[changed.Length];
        var stack = new Stack<int>();

        for (int start = 0; start < changed.Length; start++)
        {
            if (!changed[start] || seen[start]) continue;

            seen[start] = true;
            stack.Push(start);

            int left = start % width, right = left, top = start / width, bottom = top, count = 0;

            while (stack.Count > 0)
            {
                int at = stack.Pop();
                int x = at % width, y = at / width;
                count++;

                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;

                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= height) continue;

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= width) continue;

                        int next = (ny * width) + nx;
                        if (!changed[next] || seen[next]) continue;

                        seen[next] = true;
                        stack.Push(next);
                    }
                }
            }

            if (count >= MinChangePixels)
            {
                boxes.Add(new Box(left, top, right, bottom, count));
            }
        }

        return boxes;
    }

    /// <summary>
    /// Folds patches that sit close together into one. A revised dimension
    /// arrives as its arrow, its line and each of its digits; a reader stepping
    /// through changes wants to be taken to the dimension.
    ///
    /// Two patches are left apart when folding them would make something
    /// bigger than <paramref name="widest"/> by <paramref name="tallest"/>. A
    /// fold has no floor to stop it: on a drawing with changes everywhere,
    /// each fold brings the next patch within reach, and the sheet ends up as
    /// one box.
    /// </summary>
    private static List<Box> Merge(List<Box> boxes, int distance, int widest, int tallest)
    {
        bool merged = true;
        while (merged && boxes.Count > 1)
        {
            merged = false;
            var folded = new List<Box>(boxes.Count);

            foreach (var box in boxes)
            {
                bool joined = false;
                for (int i = 0; i < folded.Count; i++)
                {
                    if (!folded[i].Near(box, distance)) continue;

                    var union = folded[i].Union(box);
                    if (union.Width > widest || union.Height > tallest) continue;

                    folded[i] = union;
                    joined = true;
                    merged = true;
                    break;
                }

                if (!joined) folded.Add(box);
            }

            boxes = folded;
        }

        return boxes;
    }

    /// <summary>
    /// Cuts the patches that span too much of the sheet into pieces a reader
    /// can be taken to, on a grid of at most <paramref name="widest"/> by
    /// <paramref name="tallest"/>.
    ///
    /// Each piece is tightened back onto the changed pixels inside it, so what
    /// comes out is still a box around a change and not a box around a cell of
    /// the grid. Cells with nothing in them are dropped, which is most of them:
    /// a line that moved along a façade covers a wide box and very little of
    /// its area.
    /// </summary>
    private static List<Box> Split(List<Box> boxes, bool[] changed, int width, int height, int widest, int tallest)
    {
        if (!boxes.Any(box => box.Width > widest || box.Height > tallest)) return boxes;

        var cut = new List<Box>(boxes.Count);

        foreach (var box in boxes)
        {
            if (box.Width <= widest && box.Height <= tallest)
            {
                cut.Add(box);
                continue;
            }

            for (int top = box.Top; top <= box.Bottom; top += tallest)
            {
                int bottom = Math.Min(box.Bottom, top + tallest - 1);

                for (int left = box.Left; left <= box.Right; left += widest)
                {
                    int right = Math.Min(box.Right, left + widest - 1);
                    if (Tighten(changed, width, height, left, top, right, bottom) is { } piece)
                    {
                        cut.Add(piece);
                    }
                }
            }
        }

        return cut;
    }

    /// <summary>The box around the changed pixels inside a cell, or null when there are too few to be a change.</summary>
    private static Box? Tighten(bool[] changed, int width, int height, int left, int top, int right, int bottom)
    {
        int foundLeft = int.MaxValue, foundTop = int.MaxValue;
        int foundRight = int.MinValue, foundBottom = int.MinValue;
        int count = 0;

        for (int y = Math.Max(0, top); y <= Math.Min(height - 1, bottom); y++)
        {
            int row = y * width;
            for (int x = Math.Max(0, left); x <= Math.Min(width - 1, right); x++)
            {
                if (!changed[row + x]) continue;

                count++;
                if (x < foundLeft) foundLeft = x;
                if (x > foundRight) foundRight = x;
                if (y < foundTop) foundTop = y;
                if (y > foundBottom) foundBottom = y;
            }
        }

        return count >= MinChangePixels ? new Box(foundLeft, foundTop, foundRight, foundBottom, count) : null;
    }
}
