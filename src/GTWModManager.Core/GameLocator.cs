using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GTWModManager.Core;

/// <summary>Finds Steam and the Get To Work install, on Windows and on Linux (where the game runs through Proton).</summary>
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

    /// <summary>
    /// Windows Steam stores paths in lower case; rebuild the path with the folders' real names.
    /// Linux paths are case-sensitive and already exact.
    /// </summary>
    public static string RealCase(string path)
    {
        if (!Platform.IsWindows) return Path.GetFullPath(path);
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

    /// <summary>Folders where Steam itself is installed.</summary>
    public static IEnumerable<string> SteamRoots()
    {
        var roots = new List<string>();
        if (Platform.IsWindows)
        {
            AddIfDir(roots, Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string);
            AddIfDir(roots, Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string);
            AddIfDir(roots, @"C:\Program Files (x86)\Steam");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AddIfDir(roots, Path.Combine(home, ".steam", "steam"));
            AddIfDir(roots, Path.Combine(home, ".local", "share", "Steam"));
            AddIfDir(roots, Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")); // Flatpak Steam
            AddIfDir(roots, Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"));            // Snap Steam
        }

        // ~/.steam/steam is usually a symlink to ~/.local/share/Steam: list each real folder once.
        var seen = new HashSet<string>(Platform.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var root in roots)
            if (seen.Add(ResolveLinks(root)))
                yield return root;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var seen = new HashSet<string>(Platform.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var root in SteamRoots())
        {
            if (seen.Add(ResolveLinks(root)))
                yield return root;

            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            string text;
            try { text = File.ReadAllText(vdf); } catch (IOException) { continue; }
            foreach (Match m in LibraryPathRegex().Matches(text))
            {
                var path = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(path) && seen.Add(ResolveLinks(path)))
                    yield return path;
            }
        }
    }

    private static string ResolveLinks(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return Path.GetFullPath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName);
        }
        catch (IOException) { return Path.GetFullPath(path); }
    }

    private static void AddIfDir(List<string> list, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (Platform.IsWindows) path = path.Replace('/', '\\');
        if (Directory.Exists(path)) list.Add(path);
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPathRegex();
}
