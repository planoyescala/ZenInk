namespace ZenInk.Tests;

/// <summary>
/// Minimal check collector. The suite runs as a console app rather than under a
/// test framework because every check drives PDFium, which is single-threaded
/// and process-global — a sequential run is the honest shape for it.
/// </summary>
public static class TestRunner
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = [];

    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} ".PadRight(72, '─'));
    }

    public static void Check(string label, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  PASS  {label}");
            return;
        }

        _failed++;
        string message = detail is null ? label : $"{label} — {detail}";
        Failures.Add(message);
        Console.WriteLine($"  FAIL  {message}");
    }

    public static void CheckClose(string label, double actual, double expected, double tolerance)
    {
        bool ok = Math.Abs(actual - expected) <= tolerance;
        Check(label, ok, ok ? null : $"expected {expected:0.###} ± {tolerance:0.###}, got {actual:0.###}");
    }

    public static int Summarise()
    {
        Console.WriteLine();
        Console.WriteLine(new string('─', 72));

        if (_failed == 0)
        {
            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }

        Console.WriteLine($"{_failed} of {_passed + _failed} checks FAILED:");
        foreach (string failure in Failures)
        {
            Console.WriteLine($"  · {failure}");
        }
        return 1;
    }
}
