namespace ZenInk.Compare;

/// <summary>
/// Where the measuring happens, which is never where the drawing lives. The
/// plans this tool exists for are clients' and sit on the desktop; a copy in
/// %TEMP% costs a second on a fifty-megabyte A0 and takes the whole question of
/// what this might write away.
/// </summary>
public static class Workbench
{
    public static string Stage(string path)
    {
        string full = Path.GetFullPath(path);
        string temp = Path.GetFullPath(Path.GetTempPath());

        // Already in %TEMP% — the fixtures, or a second run over the same copy.
        if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return full;

        string copy = Path.Combine(temp, $"zenink-medir-{Path.GetFileName(full)}");
        File.Copy(full, copy, overwrite: true);
        return copy;
    }

    public static string Size(string path) => $"{new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB";
}

/// <summary>
/// What the process is holding at each step of a comparison.
///
/// The number worth knowing is not the tile cache — that one is budgeted and
/// known — but what a second open document costs. A dense A0's parsed page is
/// PDFium's, it does not show up in managed memory, and a comparison has two of
/// them.
/// </summary>
public static class Footprint
{
    private static readonly List<(string When, long Bytes)> Marks = [];

    public static void Mark(string when)
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        self.Refresh();
        Marks.Add((when, self.WorkingSet64));
    }

    public static void Report()
    {
        long previous = 0;
        foreach (var (when, bytes) in Marks)
        {
            long grown = previous == 0 ? 0 : bytes - previous;
            Console.WriteLine($"         {when,-22} {bytes / (1024 * 1024),6:N0} MB"
                              + (grown == 0 ? "" : $"   ({grown / (1024 * 1024):+#,##0;-#,##0;0} MB)"));
            previous = bytes;
        }
    }
}
