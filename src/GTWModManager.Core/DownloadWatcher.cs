using System.Runtime.InteropServices;

namespace GTWModManager.Core;

/// <summary>
/// Waits for a file to finish downloading into the user's Downloads folder, for mods that can
/// only be downloaded in the browser (Nexus "Manual download").
/// </summary>
public static class DownloadWatcher
{
    public static string DownloadsFolder
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Platform.IsWindows)
            {
                var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
                if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) == 0)
                {
                    try { return Marshal.PtrToStringUni(ptr)!; }
                    finally { Marshal.FreeCoTaskMem(ptr); }
                }
            }
            else if (XdgDownloadDir(home) is { } xdg)
            {
                return xdg;
            }
            return Path.Combine(home, "Downloads");
        }
    }

    /// <summary>Linux desktops record the (possibly translated) Downloads folder in ~/.config/user-dirs.dirs.</summary>
    public static string? XdgDownloadDir(string home, string? userDirsFile = null)
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config");
        var file = userDirsFile ?? Path.Combine(configHome, "user-dirs.dirs");
        if (!File.Exists(file)) return null;
        foreach (var line in File.ReadLines(file))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal)) continue;
            var value = trimmed["XDG_DOWNLOAD_DIR=".Length..].Trim('"').Replace("$HOME", home);
            return value.Length > 0 ? value : null;
        }
        return null;
    }

    /// <summary>
    /// Returns the first mod archive that appears in <paramref name="folder"/> after
    /// <paramref name="since"/> and has stopped growing. Browsers write to a temporary name
    /// (.crdownload, .part) and rename at the end, so only finished files match.
    /// </summary>
    public static async Task<string> WaitForNewFileAsync(string folder, DateTime since, CancellationToken ct)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => ArchiveContents.SupportedExtensions.Contains(f.Extension.ToLowerInvariant()))
                .Where(f => f.CreationTime >= since || f.LastWriteTime >= since)
                .OrderBy(f => f.LastWriteTime)
                .ToList();

            foreach (var f in candidates)
            {
                f.Refresh();
                if (sizes.TryGetValue(f.FullName, out var last) && last == f.Length && f.Length > 0 && CanOpen(f.FullName))
                    return f.FullName;
                sizes[f.FullName] = f.Length;
            }
            await Task.Delay(1000, ct);
        }
    }

    private static bool CanOpen(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
