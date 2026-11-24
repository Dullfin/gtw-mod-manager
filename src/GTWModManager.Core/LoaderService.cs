using System.Diagnostics;
using System.IO.Compression;

namespace GTWModManager.Core;

public sealed record LoaderStatus(ModLoader Loader, bool Installed, string? Version, bool Active);

/// <summary>
/// Installs BepInEx and MelonLoader and switches between them. Each loader is started by a proxy
/// DLL in the game folder (winhttp.dll / version.dll); the inactive one's proxy is renamed so
/// only one loader ever runs. Each keeps its own mods, so switching is instant.
/// </summary>
public static class LoaderService
{
    public const string DisabledProxySuffix = ".gtwmm-disabled";

    public static LoaderStatus GetStatus(GamePaths paths, ModLoader loader)
    {
        var layout = paths.Layout(loader);
        var proxy = Path.Combine(paths.GameDir, layout.ProxyDll);
        bool proxyPresent = File.Exists(proxy);
        bool proxyParked = File.Exists(proxy + DisabledProxySuffix);

        if (loader == ModLoader.BepInEx)
        {
            var core = Path.Combine(paths.BepInExDir, "core", "BepInEx.dll");
            bool installed = File.Exists(core) && (proxyPresent || proxyParked);
            return new LoaderStatus(loader, installed, installed ? FileVersion(core) : null,
                installed && proxyPresent && DoorstopEnabled(paths));
        }
        else
        {
            var dir = Path.Combine(paths.GameDir, "MelonLoader");
            bool installed = Directory.Exists(dir) && (proxyPresent || proxyParked);
            string? version = null;
            if (installed)
            {
                var dll = Directory.EnumerateFiles(dir, "MelonLoader.dll", SearchOption.AllDirectories).FirstOrDefault();
                version = dll is null ? "?" : FileVersion(dll);
            }
            return new LoaderStatus(loader, installed, version, installed && proxyPresent);
        }
    }

    public static ModLoader? ActiveLoader(GamePaths paths) =>
        paths.Loaders.Select(l => GetStatus(paths, l.Loader)).FirstOrDefault(s => s.Active)?.Loader;

    /// <summary>Makes <paramref name="loader"/> the only active loader, or none for an unmodded game.</summary>
    public static void SetActive(GamePaths paths, ModLoader? loader)
    {
        foreach (var layout in paths.Loaders)
        {
            if (!GetStatus(paths, layout.Loader).Installed) continue; // never touch a DLL we don't own
            var proxy = Path.Combine(paths.GameDir, layout.ProxyDll);
            var parked = proxy + DisabledProxySuffix;
            if (layout.Loader == loader)
            {
                if (!File.Exists(proxy) && File.Exists(parked)) File.Move(parked, proxy);
            }
            else if (File.Exists(proxy))
            {
                if (File.Exists(parked)) File.Delete(parked);
                File.Move(proxy, parked);
            }
        }
    }

    public static async Task<ResolvedDownload> ResolveLatestAsync(ModCatalogClient client, ModLoader loader, CancellationToken ct = default)
    {
        var entry = loader == ModLoader.BepInEx
            ? new IndexEntry { Id = "BepInEx", Name = "BepInEx", Repo = "BepInEx/BepInEx", Asset = "BepInEx_win_x64_5.*.zip" }
            : new IndexEntry { Id = "MelonLoader", Name = "MelonLoader", Repo = "LavaGang/MelonLoader", Asset = "MelonLoader.x64.zip" };
        return await client.ResolveAsync(entry, fresh: true, ct);
    }

    /// <summary>
    /// Extracts a loader zip into the game folder, keeping existing configs. If another loader is
    /// active, the new one is installed switched off so the two never run together.
    /// </summary>
    public static void InstallFromZip(GamePaths paths, ModLoader loader, string zipPath)
    {
        var activeBefore = ActiveLoader(paths);
        var root = paths.GameDir + Path.DirectorySeparatorChar;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var dest = Path.GetFullPath(Path.Combine(paths.GameDir, entry.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = GamePaths.Normalize(entry.FullName);
                bool isConfig = rel.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase)
                                || rel.StartsWith("UserData/", StringComparison.OrdinalIgnoreCase)
                                || rel.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase);
                if (isConfig && File.Exists(dest)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }

        // A parked proxy from an earlier install would now be stale.
        var proxy = Path.Combine(paths.GameDir, paths.Layout(loader).ProxyDll);
        if (File.Exists(proxy + DisabledProxySuffix)) File.Delete(proxy + DisabledProxySuffix);

        foreach (var dir in paths.Layout(loader).ToggleRoots)
            Directory.CreateDirectory(Path.Combine(paths.Layout(loader).ContentRoot, dir));

        SetActive(paths, activeBefore is { } other && other != loader ? other : loader);
    }

    private static bool DoorstopEnabled(GamePaths paths)
    {
        var ini = Path.Combine(paths.GameDir, "doorstop_config.ini");
        if (!File.Exists(ini)) return true;
        var line = File.ReadLines(ini).FirstOrDefault(l => l.TrimStart().StartsWith("enabled", StringComparison.OrdinalIgnoreCase));
        return line is null || line.Split('=', 2) is not [_, var value] || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FileVersion(string path) => FileVersionInfo.GetVersionInfo(path).FileVersion;
}
