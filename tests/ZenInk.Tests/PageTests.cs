using System.Numerics;

using PDFiumCore;
using ZenInk.Core;
using static ZenInk.Tests.TestRunner;

namespace ZenInk.Tests;

/// <summary>
/// Milestone 3: the sheets themselves — moved, dropped, copied, brought in from
/// elsewhere, taken out to a file of their own — and the index a set carries.
///
/// The arrangement is pure arithmetic and is checked as such; the writing is
/// checked against real files, because what a rearrangement has to prove is
/// that the right sheet came out in the right place, and only a written file
/// can say that.
/// </summary>
public static class PageTests
{
    public static void Run()
    {
        Arranging();
        MarksFollowSheets();
        SteppingBack();
        Ranges();
        Ordering();
    }

    private static void Ordering()
    {
        Section("Ordenar por nombre como se lee");

        static string Sorted(params string[] names) =>
            string.Join(' ', names.OrderBy(n => n, NaturalOrder.Comparer));

        Check($"a number reads as a number ({Sorted("HOJA-10", "HOJA-2", "HOJA-1")})",
            Sorted("HOJA-10", "HOJA-2", "HOJA-1") == "HOJA-1 HOJA-2 HOJA-10");
        Check("and plain text order would have it backwards",
            string.Join(' ', new[] { "HOJA-10", "HOJA-2" }.OrderBy(n => n, StringComparer.Ordinal)) == "HOJA-10 HOJA-2");

        Check($"leading zeros do not change what it counts to ({Sorted("h-007", "h-8", "h-06")})",
            Sorted("h-007", "h-8", "h-06") == "h-06 h-007 h-8");
        Check("several numbers in one name, each in its place",
            Sorted("02AR-010", "02AR-002", "01GN-003") == "01GN-003 02AR-002 02AR-010");
        Check("a number longer than any integer still orders",
            Sorted("x99999999999999999999", "x100000000000000000000")
                == "x99999999999999999999 x100000000000000000000");

        // Two spellings of one name stay together, and neither jumps the queue
        // over a name that really does come later. They are still told apart,
        // so the order is one order and not a coin toss.
        Check($"case does not reorder a name ({Sorted("banco", "ALZADOS", "Alzados")})",
            Sorted("banco", "ALZADOS", "Alzados").EndsWith("banco")
            && Sorted("banco", "ALZADOS", "Alzados").Split(' ')[0].Equals("alzados", StringComparison.OrdinalIgnoreCase));
        Check("but they are still told apart", NaturalOrder.Compare("Alzados", "ALZADOS") != 0);
        Check("accents read as the letters they are", NaturalOrder.Compare("PRODUCCIÓN", "produccion") != 0);
        Check("a number comes before a letter", NaturalOrder.Compare("2", "b") < 0);
        Check("a shorter name comes first when it is a prefix", NaturalOrder.Compare("plano", "plano-2") < 0);
        Check("the same name is the same name", NaturalOrder.Compare("igual", "igual") == 0);
        Check("nothing is not a name, and does not throw", NaturalOrder.Compare(null, "algo") < 0);

        // What the reader actually picks is a folder of drawings, and what they
        // see in the picker is the file name, not the path.
        var paths = new[] { @"C:\z\02AR-010.pdf", @"C:\a\02AR-002.pdf" };
        Check("paths order by the name at the end of them",
            string.Join(' ', paths.OrderBy(p => p, NaturalOrder.ByFileName).Select(Path.GetFileName))
                == "02AR-002.pdf 02AR-010.pdf");

        Section("Insertar varios archivos de una vez");

        var plan = Four();
        var batches = new List<PageBatch>
        {
            new(new PagePlanSource(@"C:\planos\a.pdf"), [(0, new PdfPageSize(841, 1189))]),
            new(new PagePlanSource(@"C:\planos\b.pdf"), [(0, new PdfPageSize(841, 1189)), (1, new PdfPageSize(841, 1189))]),
        };

        var many = plan.InsertMany(1, batches);
        Check($"every batch lands, in the order given ({Order(many.Plan)})", Order(many.Plan) == "0,0,0,1,1,2,3");
        Check("each file joins the plan's sources once", many.Plan.Sources.Count == 3);
        Check("and its sheets read from it",
            many.Plan[1].Source == 1 && many.Plan[2].Source == 2 && many.Plan[3].Source == 2);
        Check("the sheets that were there are untouched", many.Plan[0].Source == 0 && many.Plan[4].Source == 0);
        Check("none of the new ones came from a sheet that existed",
            many.OriginOfNew[1] == -1 && many.OriginOfNew[2] == -1 && many.OriginOfNew[3] == -1);
        Check("and the old ones still know where they were", many.OriginOfNew[4] == 1);

        var twice = plan.InsertMany(0,
        [
            new PageBatch(new PagePlanSource(@"C:\planos\a.pdf"), [(0, new PdfPageSize(1, 1))]),
            new PageBatch(new PagePlanSource(@"C:\planos\A.PDF"), [(1, new PdfPageSize(1, 1))]),
        ]);
        Check("the same file spelt two ways is opened once", twice.Plan.Sources.Count == 2);

        Check("an empty batch changes nothing", plan.InsertMany(0, []).Plan.Count == 4);
    }

    private static void Ranges()
    {
        Section("Qué hojas se piden");

        static string Read(string? text, int pages) =>
            PageRange.Parse(text, pages) is { } sheets ? string.Join(',', sheets) : "—";

        Check("a single sheet", Read("3", 10) == "2");
        Check("a list", Read("1,3,5", 10) == "0,2,4");
        Check("a range", Read("2-4", 10) == "1,2,3");
        Check("the two mixed, with spaces", Read(" 1, 4 - 6 ", 10) == "0,3,4,5");
        Check("repeats come back once", Read("2,2,1-3", 10) == "0,1,2");
        Check("a range given backwards is still that range", Read("6-4", 10) == "3,4,5");
        Check("past the end is clamped to it", Read("8-20", 10) == "7,8,9");
        Check("an en dash is a dash", Read("2–3", 10) == "1,2");
        Check("nothing typed means every sheet", Read("", 3) == "0,1,2");
        Check("and so does a box of spaces", Read("   ", 3) == "0,1,2");
        Check("words are not a range", Read("todas", 10) == "—");
        Check("nor is a number in the middle of one", Read("1,x,3", 10) == "—");
        Check("nor is sheet zero", Read("0-2", 10) == "—");
        Check("a document with no sheets asks for none", Read("1-3", 0) == "");
    }

    public static async Task RunAsync()
    {
        await WritingAsync();
        await BringingInAsync();
        await TakingOutAsync();
        await MarksThroughASaveAsync();
        await OutlineAsync();
    }

    private const string Source = @"C:\planos\revision-j.pdf";

    private static PagePlan Four() => PagePlan.Identity(
        [new PdfPageSize(400, 600), new PdfPageSize(410, 600), new PdfPageSize(420, 600), new PdfPageSize(430, 600)],
        Source);

    private static string Order(PagePlan plan) =>
        string.Join(',', plan.Slots.Select(s => s.IsBlank ? "b" : s.PageIndex.ToString()));

    // --- the arrangement itself -----------------------------------------

    private static void Arranging()
    {
        Section("PagePlan — moviendo hojas");

        var plan = Four();
        Check("a document as it came is not rearranged", !plan.IsRearranged);
        Check("and carries no turns", !plan.HasTurns);
        Check("its sizes are the sheets' own", plan.EffectiveSizes()[2].WidthPt == 420);

        var moved = plan.Move([0], 3);
        Check($"a sheet moves to where it was dropped ({Order(moved.Plan)})", Order(moved.Plan) == "1,2,0,3");
        Check("and the plan says so", moved.Plan.IsRearranged);
        Check("its origin travels with it", moved.OriginOfNew[2] == 0);
        Check("and the map back agrees", moved.DestinationOfOld(4)[0] == 2);

        var block = plan.Move([1, 3], 0);
        Check($"several sheets keep their order between them ({Order(block.Plan)})", Order(block.Plan) == "1,3,0,2");

        var down = plan.Move([2], 1);
        Check($"a sheet moved up lands above the one it was dropped on ({Order(down.Plan)})", Order(down.Plan) == "0,2,1,3");

        Section("PagePlan — decir a qué hoja va");

        Check($"a sheet goes to the number asked for ({Order(plan.MoveTo([0], 2).Plan)})",
            Order(plan.MoveTo([0], 2).Plan) == "1,2,0,3");
        Check($"and back the other way ({Order(plan.MoveTo([3], 0).Plan)})",
            Order(plan.MoveTo([3], 0).Plan) == "3,0,1,2");
        Check("a sheet asked to stay where it is stays where it is",
            Order(plan.MoveTo([2], 2).Plan) == "0,1,2,3");

        Check($"several go together, in their own order ({Order(plan.MoveTo([3, 1], 0).Plan)})",
            Order(plan.MoveTo([3, 1], 0).Plan) == "1,3,0,2");
        Check($"a scattered pair gathers where it lands ({Order(plan.MoveTo([0, 3], 1).Plan)})",
            Order(plan.MoveTo([0, 3], 1).Plan) == "1,0,3,2");

        // Past the end is not an error: it is someone asking for the back.
        Check($"past the end lands at the end ({Order(plan.MoveTo([0], 99).Plan)})",
            Order(plan.MoveTo([0], 99).Plan) == "1,2,3,0");
        Check($"and a block stops where it still fits ({Order(plan.MoveTo([0, 1], 99).Plan)})",
            Order(plan.MoveTo([0, 1], 99).Plan) == "2,3,0,1");
        Check("before the start lands at the start", Order(plan.MoveTo([2], -5).Plan) == "2,0,1,3");

        Check("what it says it will do is what it does", plan.LandingFor([0, 1], 99) == 2);
        Check("and a landing inside the document is left alone", plan.LandingFor([0], 2) == 2);

        // A drag names the gap it was dropped on; typing a number names the
        // sheet. The two agree only once the lifted sheets are accounted for.
        Check("a drop gap and a sheet number are not the same thing",
            Order(plan.Move([0], 2).Plan) == "1,0,2,3" && Order(plan.MoveTo([0], 2).Plan) == "1,2,0,3");

        var dropped = plan.Remove([1, 2]);
        Check($"sheets come out ({Order(dropped.Plan)})", Order(dropped.Plan) == "0,3");
        Check("and what is gone says so", dropped.DestinationOfOld(4)[1] == -1);

        var copied = plan.Duplicate([0, 1]);
        Check($"copies go straight behind the last of them ({Order(copied.Plan)})", Order(copied.Plan) == "0,1,0,1,2,3");
        Check("a copy remembers the sheet it came from", copied.OriginOfNew[2] == 0);
        Check("and the original is still the one the old number points at", copied.DestinationOfOld(4)[0] == 0);

        var turned = plan.Rotate([1], 1).Plan;
        Check("a turn is a turn and not a rearrangement", turned.HasTurns && !turned.IsRearranged);
        Check("and it swaps that sheet's sides", turned.EffectiveSizes()[1].WidthPt == 600);
        Check("leaving the others alone", turned.EffectiveSizes()[0].WidthPt == 400);

        var turnedThenMoved = turned.Move([1], 3).Plan;
        Check("a sheet that moves takes its turn with it",
            turnedThenMoved[2].QuarterTurns == 1 && turnedThenMoved[0].QuarterTurns == 0);

        var blank = plan.InsertBlank(2, new PdfPageSize(595, 842));
        Check($"blank paper goes in where it was asked for ({Order(blank.Plan)})", Order(blank.Plan) == "0,1,b,2,3");
        Check("it comes from nowhere", blank.OriginOfNew[2] == -1);
        Check("and it is the paper size asked for", blank.Plan[2].Size.HeightPt == 842);

        var kept = plan.Keep([3, 1]);
        Check($"keeping a few leaves them in the document's order ({Order(kept.Plan)})", Order(kept.Plan) == "1,3");

        Section("PagePlan — hojas de otro archivo");

        const string other = @"C:\planos\revision-k.pdf";
        var brought = plan.Insert(1, new PagePlanSource(other), [(0, new PdfPageSize(841, 1189))]);

        Check("the other file joins the plan's sources", brought.Plan.Sources.Count == 2);
        Check("its sheet reads from it", brought.Plan[1].Source == 1 && brought.Plan[1].PageIndex == 0);
        Check("and the document's own sheets stay on source zero", brought.Plan[0].Source == 0);

        var twice = brought.Plan.Insert(0, new PagePlanSource(other), [(1, new PdfPageSize(841, 1189))]);
        Check("a second batch from the same file does not open it again", twice.Plan.Sources.Count == 2);

        var without = twice.Plan.Remove([0, 2]).Plan.Compacted();
        Check("dropping the last sheet from a file drops the file", without.Sources.Count == 1);
        Check("and what is left is the document again", !without.IsRearranged);
    }

    // --- marks travelling with their sheets ------------------------------

    private static Annotation Mark(string text) => new(
        AnnotationKind.Note, [new Vector2(100, 100)], new AnnotationStyle(AnnotationColor.Red, 2f), text);

    private static void MarksFollowSheets()
    {
        Section("Las marcas viajan con su hoja");

        var store = new AnnotationStore();
        store.LoadPlan(Four());

        var onFirst = Mark("hoja 0");
        var onThird = Mark("hoja 2");
        store.Add(0, onFirst);
        store.Add(2, onThird);

        store.Rearrange(store.Plan.Move([0], 3));

        Check($"the sheets moved ({Order(store.Plan)})", Order(store.Plan) == "1,2,0,3");
        Check("the mark went with its sheet", store.ForPage(2).Count == 1 && store.ForPage(2)[0].Text == "hoja 0");
        Check("and so did the other one", store.ForPage(1).Count == 1 && store.ForPage(1)[0].Text == "hoja 2");
        Check("nothing was left at the old number", store.CountForPage(0) == 0);

        store.Rearrange(store.Plan.Duplicate([2]));
        Check("a copied sheet gets a copy of its marks", store.ForPage(3).Count == 1);
        Check("with its own identity", store.ForPage(3)[0].Id != store.ForPage(2)[0].Id);
        Check("and the same words", store.ForPage(3)[0].Text == "hoja 0");

        store.Rearrange(store.Plan.Remove([2]));
        Check("a sheet taken out takes its marks with it", store.CountForPage(2) == 1 && store.Count == 2);
    }

    private static void SteppingBack()
    {
        Section("Deshacer alcanza a las hojas y a las marcas por igual");

        var store = new AnnotationStore();
        store.LoadPlan(Four());

        var mark = Mark("cota");
        store.Add(1, mark);
        store.Rearrange(store.Plan.Remove([1]));

        Check("the sheet is gone", store.Plan.Count == 3);
        Check("and its mark with it", store.Count == 0);

        var step = store.Undo();
        Check("undo says the sheets moved", step.Rearranged);
        Check("the sheet is back", store.Plan.Count == 4 && Order(store.Plan) == "0,1,2,3");
        Check("and the mark that was on it", store.ForPage(1).Count == 1 && store.ForPage(1)[0].Text == "cota");

        var again = store.Undo();
        Check("a further step back reaches the mark itself", !again.Rearranged && again.PageIndex == 1);
        Check("and takes it away", store.Count == 0);

        store.Redo();
        Check("redo puts the mark back", store.Count == 1);
        store.Redo();
        Check("and redo again takes the sheet out", store.Plan.Count == 3 && store.Count == 0);

        // A mark made after stepping back must clear what was ahead, sheets
        // included: the future it belonged to no longer happened.
        store.Undo();
        store.Add(0, Mark("nueva"));
        Check("a new mark drops what was undone", !store.CanRedo);
    }

    // --- writing it out ---------------------------------------------------

    private static PagePlan PlanOf(string path, PdfDocumentInfo info) => PagePlan.Identity(info.Pages, path);

    private static async Task<(PdfRenderQueue Queue, string Path, PagePlan Plan, int DocumentId)> OpenSetAsync(
        PdfRenderQueue queue, string name, int sheets)
    {
        string path = TestPdf.WriteSheets(name, sheets);
        var info = await queue.OpenDocumentAsync(path);
        return (queue, path, PlanOf(path, info), info.DocumentId);
    }

    /// <summary>The widths of a written file's sheets, which say which sheet each one is.</summary>
    private static async Task<string> WidthsOfAsync(PdfRenderQueue queue, string path)
    {
        var info = await queue.OpenDocumentAsync(path);
        string widths = string.Join(',', info.Pages.Select(p => Math.Round(p.WidthPt)));
        await queue.CloseDocumentAsync(info.DocumentId);
        return widths;
    }

    private static async Task WritingAsync()
    {
        Section("Guardar una reorganización");

        var queue = PdfRenderQueue.Shared;
        var set = await OpenSetAsync(queue, "zenink-pages-write", 4);
        string moved = Path.Combine(Path.GetTempPath(), "zenink-pages-moved.pdf");

        // 400, 410, 420, 430 in the file; the plan asks for 430, 400, 420.
        var plan = set.Plan.Move([3], 0).Plan.Remove([2]).Plan;
        await queue.SaveChangesCopyAsync(plan, moved);

        Check($"the sheets come out in the order asked for ({await WidthsOfAsync(queue, moved)})",
            await WidthsOfAsync(queue, moved) == "430,400,420");

        // The widths say which page object landed where; this says the drawing
        // came with it, which is the half a page tree cannot fake.
        var written = await queue.OpenDocumentAsync(moved);
        var first = await queue.RequestPagePreviewAsync(written.DocumentId, 0, 300);
        Check("and the ink of the sheet that moved came with it",
            first is { } image && Bitmap.IsDark(image.Bgra, image.Width, image.Width / 6, (int)(image.Height * 0.60)),
            "the fourth sheet draws its band a little below the middle");
        await queue.CloseDocumentAsync(written.DocumentId);

        Check("the original is left exactly as it was",
            await WidthsOfAsync(queue, set.Path) == "400,410,420,430");

        string copies = Path.Combine(Path.GetTempPath(), "zenink-pages-copies.pdf");
        await queue.SaveChangesCopyAsync(set.Plan.Duplicate([1]).Plan, copies);
        Check($"a copied sheet is written twice ({await WidthsOfAsync(queue, copies)})",
            await WidthsOfAsync(queue, copies) == "400,410,410,420,430");

        string blank = Path.Combine(Path.GetTempPath(), "zenink-pages-blank.pdf");
        await queue.SaveChangesCopyAsync(set.Plan.InsertBlank(1, new PdfPageSize(595, 842)).Plan, blank);
        Check($"blank paper is written at its own size ({await WidthsOfAsync(queue, blank)})",
            await WidthsOfAsync(queue, blank) == "400,595,410,420,430");

        string turned = Path.Combine(Path.GetTempPath(), "zenink-pages-turned.pdf");
        await queue.SaveChangesCopyAsync(set.Plan.Move([2], 0).Plan.Rotate([0], 1).Plan, turned);
        Check($"a turn lands on the sheet that was moved, not on the number ({await WidthsOfAsync(queue, turned)})",
            await WidthsOfAsync(queue, turned) == "600,400,410,430");

        await queue.CloseDocumentAsync(set.DocumentId);
    }

    private static async Task BringingInAsync()
    {
        Section("Traer hojas de otro archivo");

        var queue = PdfRenderQueue.Shared;
        var set = await OpenSetAsync(queue, "zenink-pages-host", 2);

        string otherPath = TestPdf.WriteRectangle("zenink-pages-guest", "0 450 100 150");
        var other = await queue.OpenDocumentAsync(otherPath);

        var plan = set.Plan.Insert(1, new PagePlanSource(otherPath), [(0, other.Pages[0])]).Plan;
        string mixed = Path.Combine(Path.GetTempPath(), "zenink-pages-mixed.pdf");
        await queue.SaveChangesCopyAsync(plan, mixed);

        Check($"the sheet from the other file goes where it was put ({await WidthsOfAsync(queue, mixed)})",
            await WidthsOfAsync(queue, mixed) == "400,400,410");

        // Both sheets are 400 wide, so only the drawing tells them apart: the
        // guest's ink sits high, the host's low.
        var written = await queue.OpenDocumentAsync(mixed);
        var guest = await queue.RequestPagePreviewAsync(written.DocumentId, 1, 300);
        Check("and it is that file's drawing that came across",
            guest is { } image && Bitmap.IsDark(image.Bgra, image.Width, image.Width / 8, (int)(image.Height * 0.12)));

        await queue.CloseDocumentAsync(written.DocumentId);
        await queue.CloseDocumentAsync(other.DocumentId);
        await queue.CloseDocumentAsync(set.DocumentId);
    }

    private static async Task TakingOutAsync()
    {
        Section("Sacar hojas a su propio archivo");

        var queue = PdfRenderQueue.Shared;
        var set = await OpenSetAsync(queue, "zenink-pages-extract", 5);
        byte[] before = await File.ReadAllBytesAsync(set.Path);

        string taken = Path.Combine(Path.GetTempPath(), "zenink-pages-taken.pdf");
        await queue.SaveChangesCopyAsync(set.Plan.Keep([1, 3]).Plan, taken);

        Check($"only the chosen sheets are written ({await WidthsOfAsync(queue, taken)})",
            await WidthsOfAsync(queue, taken) == "410,430");
        Check("and the document they came from is not touched at all",
            (await File.ReadAllBytesAsync(set.Path)).SequenceEqual(before));

        await queue.CloseDocumentAsync(set.DocumentId);
    }

    private static async Task MarksThroughASaveAsync()
    {
        Section("Las marcas sobreviven a que la hoja se mueva");

        var queue = PdfRenderQueue.Shared;
        var set = await OpenSetAsync(queue, "zenink-pages-marks", 3);

        var store = new AnnotationStore();
        store.LoadPlan(set.Plan);
        store.Add(0, Mark("de la primera"));
        store.Rearrange(store.Plan.Move([0], 3));

        string saved = Path.Combine(Path.GetTempPath(), "zenink-pages-marks-out.pdf");
        await queue.SaveChangesCopyAsync(store.Plan, saved, store.Snapshot());

        Check($"the sheets are written in the new order ({await WidthsOfAsync(queue, saved)})",
            await WidthsOfAsync(queue, saved) == "410,420,400");

        var reopened = await queue.OpenDocumentAsync(saved);
        var readBack = await queue.ReadAnnotationsAsync(reopened.DocumentId);

        Check("the mark comes back on the sheet it was made on, wherever that is now",
            readBack.TryGetValue(1, out var wrong) is false || wrong.Count == 0);
        Check("which is the last one",
            readBack.TryGetValue(2, out var marks) && marks.Count == 1 && marks[0].Text == "de la primera");

        await queue.CloseDocumentAsync(reopened.DocumentId);
        await queue.CloseDocumentAsync(set.DocumentId);
    }

    // --- the index the file carries ---------------------------------------

    private static async Task OutlineAsync()
    {
        Section("El índice de marcadores del PDF");

        var queue = PdfRenderQueue.Shared;
        string path = TestPdf.WriteOutlined("zenink-pages-outline");
        var document = await queue.OpenDocumentAsync(path);

        var outline = await queue.ReadOutlineAsync(document.DocumentId);

        Check($"the top of the tree is read ({outline.Count} entries)", outline.Count == 2);
        Check("with its titles", outline.Count == 2 && outline[0].Title == "Planta baja");
        Check("pointing at the sheet they name", outline.Count == 2 && outline[0].PageIndex == 0);
        Check("siblings come in order", outline.Count == 2 && outline[1].Title == "Planta primera");
        Check("and their sheets with them", outline.Count == 2 && outline[1].PageIndex == 2);

        var children = outline.Count > 0 ? outline[0].Children : [];
        Check("a nested entry is read under its parent", children.Count == 1);
        Check("with its own title", children.Count == 1 && children[0].Title == "Detalle de escalera");
        Check("and its own sheet", children.Count == 1 && children[0].PageIndex == 1);
        Check("an entry that goes somewhere says so", children.Count == 1 && children[0].CanGo);

        await queue.CloseDocumentAsync(document.DocumentId);

        var plain = await queue.OpenDocumentAsync(TestPdf.WriteSheets("zenink-pages-noindex", 2));
        Check("a document with no index gives an empty one",
            (await queue.ReadOutlineAsync(plain.DocumentId)).Count == 0);
        await queue.CloseDocumentAsync(plain.DocumentId);
    }
}
