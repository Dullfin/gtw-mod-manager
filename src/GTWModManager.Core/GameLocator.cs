using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GTWModManager.Core;

/// <summary>Finds the Get To Work install through Steam's registry keys and library list.</summary>
public static partial class GameLocator
{
    public static string? Find()
    {
        foreach (var library in SteamLibraries())
        {
            var candidate = Path.Combine(library, "steamapps", "common", "Get To Work");
            if (File.Exists(Path.Combine(candidate, GamePaths.ExeName)))
                return RealCase(candidate);
        }
        return null;
    }

    /// <summary>Steam stores paths in lower case; rebuild the path with the folders' real names.</summary>
    public static string RealCase(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!.ToUpperInvariant();
        var result = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = new DirectoryInfo(result).EnumerateDirectories(part).FirstOrDefault();
            result = Path.Combine(result, match?.Name ?? part);
        }
        return result;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var steamRoots = new List<string>();
        AddIfDir(steamRoots, Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string);
        AddIfDir(steamRoots, Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string);
        AddIfDir(steamRoots, @"C:\Program Files (x86)\Steam");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots)
        {
            if (seen.Add(Path.GetFullPath(root)))
                yield return root;

            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            string text;
            try { text = File.ReadAllText(vdf); } catch (IOException) { continue; }
            foreach (Match m in LibraryPathRegex().Matches(text))
            {
                var path = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(path) && seen.Add(Path.GetFullPath(path)))
                    yield return path;
            }
        }
    }

    private static void AddIfDir(List<string> list, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            list.Add(path.Replace('/', '\\'));
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPathRegex();
}
