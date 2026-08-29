using ZenInk.Core;
using static ZenInk.Tests.TestRunner;

namespace ZenInk.Tests;

/// <summary>The list of drawings opened lately: order, duplicates, cap, and the store.</summary>
public static class RecentDocumentsTests
{
    public static void Run()
    {
        Ordering();
        Store();
    }

    private static void Ordering()
    {
        Section("RecentDocuments — order and duplicates");

        var list = RecentDocuments.Promote([], @"C:\planos\A-01.pdf");
        Check("the first drawing opens the list", list.Count == 1 && list[0] == @"C:\planos\A-01.pdf");

        list = RecentDocuments.Promote(list, @"C:\planos\A-02.pdf");
        Check("the newest goes to the head", list[0] == @"C:\planos\A-02.pdf");
        Check("and the previous one stays behind it", list[1] == @"C:\planos\A-01.pdf");

        list = RecentDocuments.Promote(list, @"C:\planos\A-01.pdf");
        Check("reopening moves rather than duplicates", list.Count == 2);
        Check("and it comes back to the head", list[0] == @"C:\planos\A-01.pdf");

        list = RecentDocuments.Promote(list, @"c:\PLANOS\a-02.PDF");
        Check("a Windows path is the same path in any case", list.Count == 2, $"got {list.Count}");

        var many = new List<string>();
        for (int i = 0; i < RecentDocuments.Limit + 6; i++)
        {
            many = [.. RecentDocuments.Promote(many, $@"C:\planos\hoja-{i}.pdf")];
        }
        Check("the list is capped", many.Count == RecentDocuments.Limit, $"got {many.Count}");
        Check("and it is the oldest that falls off", many[^1] == $@"C:\planos\hoja-{6}.pdf", many[^1]);

        var without = RecentDocuments.Remove(many, many[3]);
        Check("a drawing that is gone can be dropped", without.Count == many.Count - 1);

        Check("an empty path is not an entry", RecentDocuments.Promote(many, "  ").Count == many.Count);
    }

    private static void Store()
    {
        Section("RecentDocuments — store");

        string store = Path.Combine(Path.GetTempPath(), $"zenink-recientes-{Guid.NewGuid():N}.txt");
        try
        {
            Check("a store that is not there is an empty history", RecentDocuments.Load(store).Count == 0);

            RecentDocuments.Save(store, [@"C:\planos\A-01.pdf", @"C:\planos\A-02.pdf"]);
            var read = RecentDocuments.Load(store);
            Check("what was written comes back", read.Count == 2 && read[0] == @"C:\planos\A-01.pdf");

            File.WriteAllLines(store, ["", @"C:\planos\A-01.pdf", "   ", @"c:\planos\a-01.pdf"]);
            read = RecentDocuments.Load(store);
            Check("blank lines and repeats are ignored on the way in", read.Count == 1, $"got {read.Count}");
        }
        finally
        {
            try { File.Delete(store); } catch (Exception) { /* the temp file is not the point */ }
        }

        // A store under a folder that does not exist yet is the first run.
        string nested = Path.Combine(
            Path.GetTempPath(), $"zenink-{Guid.NewGuid():N}", "recientes.txt");
        try
        {
            RecentDocuments.Save(nested, [@"C:\planos\A-01.pdf"]);
            Check("the first save makes its own folder", RecentDocuments.Load(nested).Count == 1);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(nested)!, recursive: true); } catch (Exception) { }
        }
    }
}
