using System.Diagnostics;
using System.IO.Compression;

namespace GTWModManager.Core;

/// <summary>One file inside a download, by its path in the archive.</summary>
public sealed record SourceFile(string Path, Func<Stream> Open);

/// <summary>An opened download (.zip, .7z, .rar or .dll). Dispose to close it and remove temp files.</summary>
public sealed class ArchiveContents : IDisposable
{
    private readonly IDisposable? _zip;
    private readonly string? _tempDir;

    private ArchiveContents(List<SourceFile> files, IDisposable? zip, string? tempDir)
    {
        Files = files;
        _zip = zip;
        _tempDir = tempDir;
    }

    public List<SourceFile> Files { get; }

    public static readonly string[] SupportedExtensions = [".zip", ".7z", ".rar", ".dll"];

    public static ArchiveContents Open(string file)
    {
        switch (Path.GetExtension(file).ToLowerInvariant())
        {
            case ".dll":
                return new ArchiveContents([new SourceFile(Path.GetFileName(file), () => File.OpenRead(file))], null, null);

            case ".zip":
                var zip = ZipFile.OpenRead(file);
                var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name))
                    .Select(e => new SourceFile(e.FullName, e.Open)).ToList();
                return new ArchiveContents(entries, zip, null);

            case ".7z" or ".rar":
                var temp = Path.Combine(Path.GetTempPath(), "GTWModManager", "x-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                var psi = ExtractCommand(file, temp);
                if (psi is null)
                {
                    TryDelete(temp);
                    throw new NotSupportedException(
                        "Installing .7z and .rar files needs an extraction tool. Install your distro's 7-Zip " +
                        "(p7zip / 7zip) or libarchive (bsdtar), or extract the file yourself and install the folder's contents.");
                }
                using (var p = Process.Start(psi)!)
                {
                    var stdout = p.StandardOutput.ReadToEndAsync(); // drain both streams so the tool can't block
                    var err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    stdout.Wait();
                    if (p.ExitCode != 0)
                    {
                        TryDelete(temp);
                        throw new InvalidDataException($"Couldn't extract {Path.GetFileName(file)}: {err.Trim()}");
                    }
                }
                var files = Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories)
                    .Select(f => new SourceFile(Path.GetRelativePath(temp, f), () => File.OpenRead(f))).ToList();
                return new ArchiveContents(files, null, temp);

            default:
                throw new NotSupportedException("Only .zip, .7z, .rar and .dll files can be installed.");
        }
    }

    /// <summary>
    /// A command that extracts a 7z/rar file into a folder. Windows 10+ ships bsdtar (libarchive) as
    /// tar.exe; on Linux use bsdtar or 7-Zip if installed. All of them refuse ".." paths.
    /// </summary>
    private static ProcessStartInfo? ExtractCommand(string file, string dest)
    {
        ProcessStartInfo Make(string exe, params string[] args)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            return psi;
        }

        if (Platform.IsWindows)
            return Make(Path.Combine(Environment.SystemDirectory, "tar.exe"), "-xf", file, "-C", dest);
        if (Platform.FindOnPath("bsdtar") is { } bsdtar)
            return Make(bsdtar, "-xf", file, "-C", dest);
        if (Platform.FindOnPath("7zz", "7z", "7za") is { } sevenZip)
            return Make(sevenZip, "x", "-y", "-o" + dest, file);
        if (file.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) && Platform.FindOnPath("unrar") is { } unrar)
            return Make(unrar, "x", "-o+", "-y", file, dest + Path.DirectorySeparatorChar);
        return null;
    }

    public void Dispose()
    {
        _zip?.Dispose();
        if (_tempDir is not null) TryDelete(_tempDir);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>One file to write, relative to the loader's content root (e.g. "plugins/MyMod/MyMod.dll").</summary>
public sealed record PlannedFile(string RelativePath, Func<Stream> Open);

public sealed class InstallPlan
{
    public ModLoader Loader { get; set; }
    public List<PlannedFile> Files { get; } = [];
    public List<string> Items { get; } = [];
    public List<string> ExtraFiles { get; } = [];
    public PluginMetadata? Metadata { get; set; }
}

/// <summary>
/// Works out which loader a download is for and where each file belongs.
///
/// BepInEx layouts:      BepInEx/plugins/...,  plugins/ patchers/ config/,  or bare DLLs (→ plugins/&lt;Mod&gt;/)
/// MelonLoader layouts:  Mods/ Plugins/ UserLibs/ UserData/,  or bare DLLs (→ Mods/, other files → UserData/&lt;Mod&gt;/)
///
/// A single wrapper folder like "MyMod-1.2/" is stripped first. Loader files themselves
/// (BepInEx/core, winhttp.dll, MelonLoader/, version.dll) are never written.
/// </summary>
public static class ArchiveInstaller
{
    private static readonly string[] KnownTopFolders =
        ["BepInEx", "plugins", "patchers", "config", "Mods", "UserLibs", "UserData", "MelonLoader"];

    public static InstallPlan Plan(GamePaths paths, IReadOnlyList<SourceFile> source, string folderName,
        ModLoader? loaderHint, ModLoader fallbackLoader)
    {
        var entries = source
            .Select(f => (File: f, Path: GamePaths.Normalize(f.Path)))
            .Where(e => e.Path.Length > 0 && !e.Path.Split('/').Any(seg => seg is ".." or "."))
            .ToList();
        if (entries.Count == 0) throw new InvalidDataException("The download is empty.");

        // Strip wrapper folders ("MyMod-v1/plugins/..." -> "plugins/...").
        while (true)
        {
            var firsts = entries.Select(e => First(e.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (firsts.Count != 1 || entries.Any(e => !e.Path.Contains('/'))) break;
            if (KnownTopFolders.Contains(firsts[0], StringComparer.OrdinalIgnoreCase)) break;
            var len = firsts[0].Length + 1;
            entries = entries.Select(e => (e.File, e.Path[len..])).ToList();
        }

        // Read plugin metadata from the DLLs to learn which loader the mod is for.
        var metadata = new List<PluginMetadata>();
        foreach (var e in entries.Where(e => e.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Take(40))
        {
            using var s = e.File.Open();
            metadata.AddRange(PluginMetadataReader.Read(s));
        }

        var loader = loaderHint ?? DetectLoader(metadata, entries.Select(e => e.Path).ToList()) ?? fallbackLoader;
        var plan = new InstallPlan
        {
            Loader = loader,
            Metadata = metadata.FirstOrDefault(m => m.Loader == loader) ?? metadata.FirstOrDefault(),
        };

        var layout = paths.Layout(loader);
        var safe = SafeName(folderName);
        var mapped = loader == ModLoader.BepInEx ? MapBepInEx(entries, safe) : MapMelon(entries, safe);

        foreach (var (file, rel) in mapped)
        {
            if (rel is null || !rel.Contains('/')) continue;
            var root = layout.MatchRoot(First(rel));
            if (root is null) continue; // only ever write into this loader's mod folders
            var canonical = root + rel[root.Length..];
            if (plan.Files.Any(f => f.RelativePath.Equals(canonical, StringComparison.OrdinalIgnoreCase))) continue;
            plan.Files.Add(new PlannedFile(canonical, file.Open));
        }

        if (plan.Files.Count == 0)
            throw new InvalidDataException(
                $"Nothing in the download goes into {layout.DisplayName}'s mod folders ({string.Join(", ", layout.AllowedRoots)}).");

        foreach (var f in plan.Files)
        {
            var parts = f.RelativePath.Split('/');
            if (layout.IsToggleRoot(parts[0]))
            {
                var item = parts[0] + "/" + parts[1];
                if (!plan.Items.Contains(item, StringComparer.OrdinalIgnoreCase)) plan.Items.Add(item);
            }
            else
            {
                plan.ExtraFiles.Add(f.RelativePath);
            }
        }
        return plan;
    }

    public static ModLoader? DetectLoader(IReadOnlyList<PluginMetadata> metadata, IReadOnlyList<string> paths)
    {
        bool bep = metadata.Any(m => m.Loader == ModLoader.BepInEx);
        bool melon = metadata.Any(m => m.Loader == ModLoader.MelonLoader);
        if (bep != melon) return bep ? ModLoader.BepInEx : ModLoader.MelonLoader;

        bool Has(string folder) => paths.Any(p => First(p).Equals(folder, StringComparison.OrdinalIgnoreCase));
        if (Has("BepInEx") || Has("patchers") || Has("config")) return ModLoader.BepInEx;
        if (Has("Mods") || Has("UserLibs") || Has("UserData") || Has("MelonLoader")) return ModLoader.MelonLoader;
        return null;
    }

    private static IEnumerable<(SourceFile File, string? Rel)> MapBepInEx(List<(SourceFile File, string Path)> entries, string safe)
    {
        bool hasBepInExFolder = entries.Any(e => First(e.Path).Equals("BepInEx", StringComparison.OrdinalIgnoreCase));
        bool hasRootFolders = entries.Any(e => First(e.Path) is var f &&
            (f.Equals("plugins", StringComparison.OrdinalIgnoreCase) || f.Equals("patchers", StringComparison.OrdinalIgnoreCase) || f.Equals("config", StringComparison.OrdinalIgnoreCase)));

        // A download that is just DLLs (plus a readme or licence) goes straight into plugins/.
        // Anything else keeps a folder, because mods load their asset files from beside their DLL.
        bool onlyDlls = entries.Where(e => !IsDocFile(e.Path)).All(e => IsDll(e.Path));

        foreach (var (file, path) in entries)
        {
            string? rel;
            if (hasBepInExFolder)
                rel = First(path).Equals("BepInEx", StringComparison.OrdinalIgnoreCase) && path.Contains('/') ? path[(path.IndexOf('/') + 1)..] : null;
            else if (hasRootFolders)
                // Thunderstore-style packages can mix root DLLs with a config/ folder.
                rel = path.Contains('/') ? path
                    : IsDll(path) ? $"plugins/{path}" : null;
            else if (onlyDlls)
                rel = IsDll(path) ? $"plugins/{Path.GetFileName(path)}" : null;
            else
                rel = $"plugins/{safe}/{path}";
            yield return (file, rel);
        }
    }

    private static IEnumerable<(SourceFile File, string? Rel)> MapMelon(List<(SourceFile File, string Path)> entries, string safe)
    {
        string[] roots = ["Mods", "Plugins", "UserLibs", "UserData"];
        bool IsRoot(string seg) => roots.Contains(seg, StringComparer.OrdinalIgnoreCase);
        bool hasRootFolders = entries.Any(e => e.Path.Contains('/') && IsRoot(First(e.Path)));

        foreach (var (file, path) in entries)
        {
            var first = First(path);
            bool isDll = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            string? rel;
            if (first.Equals("MelonLoader", StringComparison.OrdinalIgnoreCase) || first.Equals("BepInEx", StringComparison.OrdinalIgnoreCase))
                rel = null; // loader files
            else if (!path.Contains('/') && (path.Equals("version.dll", StringComparison.OrdinalIgnoreCase) || path.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase)))
                rel = null;
            else if (path.Contains('/') && IsRoot(first))
                rel = path;
            else if (hasRootFolders)
                rel = !path.Contains('/') && isDll ? "Mods/" + path : null;
            else
                // MelonLoader only loads DLLs placed directly in Mods/.
                rel = isDll ? "Mods/" + Path.GetFileName(path) : $"UserData/{safe}/{path}";
            yield return (file, rel);
        }
    }

    private static string First(string path) => path.Split('/')[0];

    private static bool IsDll(string path) => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>Readmes, licences and package metadata that a mod doesn't need at runtime.</summary>
    public static bool IsDocFile(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var ext = Path.GetExtension(name);
        return ext is ".md" or ".txt" or ".pdf" or ".url"
               || name.StartsWith("readme") || name.StartsWith("license") || name.StartsWith("licence")
               || name.StartsWith("changelog") || name is "manifest.json" or "icon.png";
    }

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return string.IsNullOrEmpty(clean) ? "Mod" : clean;
    }
}
