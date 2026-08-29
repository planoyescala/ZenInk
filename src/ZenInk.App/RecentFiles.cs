using Windows.Storage;
using ZenInk.Core;

namespace ZenInk_App;

/// <summary>
/// One drawing in the "opened lately" list, as the start page shows it: the
/// file's name on top and the folder it came from underneath, because a
/// reviewer's sheets are called A-01 in four different projects.
///
/// Both come from the path string alone. The file is never touched to build
/// this — see <see cref="RecentDocuments"/> for why.
/// </summary>
public sealed class RecentEntry
{
    public RecentEntry(string fullPath)
    {
        FullPath = fullPath;

        string name = System.IO.Path.GetFileName(fullPath);
        Name = name.Length > 0 ? name : fullPath;

        string? folder = System.IO.Path.GetDirectoryName(fullPath);
        Folder = string.IsNullOrEmpty(folder) ? fullPath : folder;
    }

    public string FullPath { get; }

    public string Name { get; }

    public string Folder { get; }
}

/// <summary>Where the history file lives. Everything it does with it is in <see cref="RecentDocuments"/>.</summary>
public static class RecentFiles
{
    private static string? _storePath;

    public static string StorePath => _storePath ??= Locate();

    private static string Locate()
    {
        try
        {
            return System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "recientes.txt");
        }
        catch (Exception)
        {
            // Without package identity there is no per-app folder, so the
            // history falls back to a named one of its own rather than
            // disappearing.
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZenInk",
                "recientes.txt");
        }
    }
}
