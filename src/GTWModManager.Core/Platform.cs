using System.Diagnostics;
using System.Runtime.Versioning;

namespace GTWModManager.Core;

/// <summary>Small differences between Windows and Linux, in one place.</summary>
public static class Platform
{
    [SupportedOSPlatformGuard("windows")]
    public static bool IsWindows => OperatingSystem.IsWindows();

    [SupportedOSPlatformGuard("linux")]
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Opens a URL (including steam://) or a folder with the desktop's default handler.</summary>
    public static void Open(string target)
    {
        if (IsWindows)
        {
            var psi = Directory.Exists(target)
                ? new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true }
                : new ProcessStartInfo(target) { UseShellExecute = true };
            Process.Start(psi);
        }
        else
        {
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(target);
            Process.Start(psi);
        }
    }

    /// <summary>Finds the first of these programs on PATH.</summary>
    public static string? FindOnPath(params string[] names)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in names)
            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
                if (IsWindows && File.Exists(candidate + ".exe")) return candidate + ".exe";
            }
        return null;
    }

    /// <summary>Lets the owner run a file (the updater's new Linux binary).</summary>
    public static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    /// <summary>Restricts a file to its owner (settings that hold the Nexus key on Linux).</summary>
    public static void MakePrivate(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
