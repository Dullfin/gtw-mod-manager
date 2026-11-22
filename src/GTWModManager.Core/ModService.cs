using System.Diagnostics;

namespace GTWModManager.Core;

public enum ModKind
{
    /// <summary>Installed by this manager; we know its files.</summary>
    Managed,
    /// <summary>Found in a mod folder but not installed by this manager.</summary>
    Unmanaged,
    /// <summary>A DLL sitting directly in BepInEx\, which BepInEx never loads.</summary>
    Stray,
}

public sealed class ModInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? Guids { get; init; }
    public ModKind Kind { get; init; }
    public ModLoader Loader { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsMissing { get; init; }
    public string Source { get; init; } = "";
    /// <summary>Toggleable items ("plugins/Foo", "Mods/Bar.dll"), or for Stray the DLL file name.</summary>
    public IReadOnlyList<string> Items { get; init; } = [];
    public string Location { get; init; } = "";
}

/// <summary>Details for an install that came from the community list.</summary>
public sealed record InstallRequest(
    string? Id = null,
    string? Name = null,
    string? Version = null,
    string? Author = null,
    string Source = "file",
    ModLoader? Loader = null,
    bool ReplaceUnmanaged = false);

public sealed class ModConflictException(string message, IReadOnlyList<string> paths) : Exception(message)
{
    public IReadOnlyList<string> Paths { get; } = paths;
}

public sealed class GameRunningException() : Exception("Get To Work is running. Close the game first.");

/// <summary>Scans, toggles, installs and removes mods for both loaders in one game folder.</summary>
public sealed class ModService
{
    public ModService(GamePaths paths)
    {
        Paths = paths;
        Registry = new InstalledRegistry(paths);
    }

    public GamePaths Paths { get; }
    public InstalledRegistry Registry { get; }

    /// <summary>Set to false in tests so the running-game check is skipped.</summary>
    public bool CheckGameRunning { get; set; } = true;

    public static bool IsGameRunning() => Process.GetProcessesByName(GamePaths.ProcessName).Length > 0;

    public void EnsureGameNotRunning()
    {
        if (CheckGameRunning && IsGameRunning()) throw new GameRunningException();
    }

    // ---------------------------------------------------------------- loaders

    public LoaderStatus LoaderStatus(ModLoader loader) => LoaderService.GetStatus(Paths, loader);

    public ModLoader? ActiveLoader => LoaderService.ActiveLoader(Paths);

    public void SetActiveLoader(ModLoader? loader)
    {
        EnsureGameNotRunning();
        LoaderService.SetActive(Paths, loader);
    }

    // ---------------------------------------------------------------- scanning

    public List<ModInfo> Scan()
    {
        Registry.Load();
        var mods = new List<ModInfo>();

        foreach (var layout in Paths.Loaders)
        {
            var found = new List<(string Item, bool Disabled)>();
            foreach (var root in layout.ToggleRoots)
            {
                foreach (var disabled in new[] { false, true })
                {
                    var dir = Paths.ResolveItem(layout.Loader, root, disabled);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                    {
                        if (!Directory.Exists(entry) && !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                        found.Add(($"{root}/{Path.GetFileName(entry)}", disabled));
                    }
                }
            }

            foreach (var reg in Registry.Mods.Where(m => m.Loader == layout.Loader))
            {
                var mine = found.Where(f => reg.Items.Contains(f.Item, StringComparer.OrdinalIgnoreCase)).ToList();
                var meta = mine.SelectMany(f => ReadMetadata(Paths.ResolveItem(layout.Loader, f.Item, f.Disabled))).ToList();
                mods.Add(new ModInfo
                {
                    Id = reg.Id,
                    Name = reg.Name,
                    Version = reg.Version ?? meta.FirstOrDefault()?.Version,
                    Author = reg.Author ?? meta.FirstOrDefault()?.Author,
                    Guids = string.Join(", ", meta.Select(m => m.Guid).Distinct()),
                    Kind = ModKind.Managed,
                    Loader = layout.Loader,
                    IsEnabled = mine.Any(f => !f.Disabled),
                    IsMissing = mine.Count == 0,
                    Source = reg.Source,
                    Items = reg.Items,
                    Location = string.Join(", ", reg.Items),
                });
            }

            foreach (var f in found.Where(f => Registry.FindOwner(layout.Loader, f.Item) is null))
            {
                var meta = ReadMetadata(Paths.ResolveItem(layout.Loader, f.Item, f.Disabled)).ToList();
                var first = meta.FirstOrDefault();
                mods.Add(new ModInfo
                {
                    Id = $"unmanaged:{layout.Loader}:{f.Item}",
                    Name = first?.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(f.Item),
                    Version = first?.Version,
                    Author = first?.Author,
                    Guids = string.Join(", ", meta.Select(m => m.Guid).Distinct()),
                    Kind = ModKind.Unmanaged,
                    Loader = layout.Loader,
                    IsEnabled = !f.Disabled,
                    Source = "manual",
                    Items = [f.Item],
                    Location = f.Item,
                });
            }
        }

        if (Directory.Exists(Paths.BepInExDir))
        {
            foreach (var dll in Directory.EnumerateFiles(Paths.BepInExDir, "*.dll"))
            {
                var meta = PluginMetadataReader.Read(dll);
                var first = meta.FirstOrDefault();
                mods.Add(new ModInfo
                {
                    Id = "stray:" + Path.GetFileName(dll),
                    Name = first?.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(dll),
                    Version = first?.Version,
                    Guids = string.Join(", ", meta.Select(m => m.Guid).Distinct()),
                    Kind = ModKind.Stray,
                    Loader = ModLoader.BepInEx,
                    Source = "manual",
                    Items = [Path.GetFileName(dll)],
                    Location = "BepInEx\\" + Path.GetFileName(dll),
                });
            }
        }

        return mods.OrderBy(m => m.Kind == ModKind.Stray).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<PluginMetadata> ReadMetadata(string path)
    {
        if (File.Exists(path)) return PluginMetadataReader.Read(path);
        if (Directory.Exists(path))
            return Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories).SelectMany(PluginMetadataReader.Read);
        return [];
    }

    // ---------------------------------------------------------------- toggling

    public void SetEnabled(ModInfo mod, bool enabled)
    {
        if (mod.Kind == ModKind.Stray)
            throw new InvalidOperationException("This DLL is outside the plugins folder. Use \"Move into plugins\" first.");
        EnsureGameNotRunning();

        // Check every move first so a conflict never leaves a mod half-toggled.
        var moves = new List<(string From, string To)>();
        foreach (var item in mod.Items)
        {
            var from = Paths.ResolveItem(mod.Loader, item, disabled: enabled);
            var to = Paths.ResolveItem(mod.Loader, item, disabled: !enabled);
            if (!PathExists(from)) continue;
            if (PathExists(to))
                throw new ModConflictException($"Can't move {item}: a copy already exists in {Path.GetDirectoryName(to)}.", [to]);
            moves.Add((from, to));
        }
        foreach (var (from, to) in moves) MovePath(from, to);
    }

    /// <summary>Moves a DLL from BepInEx\ (where it is ignored) into BepInEx\plugins\.</summary>
    public void AdoptStray(ModInfo mod)
    {
        if (mod.Kind != ModKind.Stray) return;
        EnsureGameNotRunning();
        var from = Path.Combine(Paths.BepInExDir, mod.Items[0]);
        var to = Path.Combine(Paths.PluginsDir, mod.Items[0]);
        if (PathExists(to) || PathExists(Path.Combine(Paths.DisabledPluginsDir, mod.Items[0])))
            throw new ModConflictException($"plugins already has {mod.Items[0]}.", [to]);
        MovePath(from, to);
    }

    // ---------------------------------------------------------------- removing

    /// <summary>
    /// Removes a mod. Files are moved to GTWModManager\trash\ rather than deleted, and
    /// settings (BepInEx\config, UserData) are kept so they survive a reinstall.
    /// </summary>
    public string Uninstall(ModInfo mod)
    {
        EnsureGameNotRunning();
        var trash = NewTrashDir(mod.Name);

        if (mod.Kind == ModKind.Stray)
        {
            MoveToTrash(Path.Combine(Paths.BepInExDir, mod.Items[0]), trash, mod.Items[0]);
            return trash;
        }

        foreach (var item in mod.Items)
            foreach (var disabled in new[] { false, true })
            {
                var path = Paths.ResolveItem(mod.Loader, item, disabled);
                if (PathExists(path))
                    MoveToTrash(path, trash, (disabled ? "disabled/" : "") + item);
            }

        if (mod.Kind == ModKind.Managed && Registry.Find(mod.Id) is { } reg)
        {
            var layout = Paths.Layout(reg.Loader);
            var sharedByOthers = Registry.Mods.Where(m => m != reg && m.Loader == reg.Loader)
                .SelectMany(m => m.ExtraFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var extra in reg.ExtraFiles.Where(f => !layout.IsKeepPath(f) && !sharedByOthers.Contains(f)))
            {
                var path = Path.Combine(layout.ContentRoot, extra);
                if (File.Exists(path)) MoveToTrash(path, trash, extra);
            }
            Registry.Mods.Remove(reg);
            Registry.Save();
        }
        return trash;
    }

    private static void MoveToTrash(string path, string trashDir, string relative)
    {
        var dest = Path.Combine(trashDir, relative.Replace('/', Path.DirectorySeparatorChar));
        MovePath(path, dest);
    }

    // ---------------------------------------------------------------- installing

    /// <summary>Works out which loader a file is for without installing it.</summary>
    public ModLoader DetectLoader(string file, ModLoader? hint = null)
    {
        using var contents = ArchiveContents.Open(file);
        return ArchiveInstaller.Plan(Paths, contents.Files, "probe", hint, ActiveLoader ?? ModLoader.BepInEx).Loader;
    }

    /// <summary>Installs a .zip, .7z, .rar or .dll from disk, for whichever loader it targets.</summary>
    public InstalledMod InstallFromFile(string file, InstallRequest? request = null)
    {
        request ??= new InstallRequest();
        EnsureGameNotRunning();
        var fileName = Path.GetFileNameWithoutExtension(file);
        using var contents = ArchiveContents.Open(file);
        var plan = ArchiveInstaller.Plan(Paths, contents.Files, request.Id ?? request.Name ?? fileName,
            request.Loader, ActiveLoader ?? ModLoader.BepInEx);
        return Apply(plan, request, fileName);
    }

    private InstalledMod Apply(InstallPlan plan, InstallRequest request, string fileName)
    {
        var layout = Paths.Layout(plan.Loader);
        var name = request.Name ?? (plan.Metadata?.Name is { Length: > 0 } n ? n : fileName);
        var id = request.Id ?? (plan.Metadata?.Guid is { Length: > 0 } guid ? guid : ArchiveInstaller.SafeName(name));
        var version = request.Version ?? plan.Metadata?.Version;
        var author = request.Author ?? plan.Metadata?.Author;

        // Reinstalling the same mod replaces it, keeping its enabled/disabled state.
        bool wasDisabled = false;
        if (Registry.Find(id) is { } existing)
        {
            var current = Scan().First(m => m.Id == existing.Id);
            wasDisabled = !current.IsEnabled && !current.IsMissing;
            Uninstall(current);
        }

        var clashes = new List<string>();
        foreach (var item in plan.Items)
            foreach (var disabled in new[] { false, true })
            {
                var path = Paths.ResolveItem(plan.Loader, item, disabled);
                if (PathExists(path) && (Registry.FindOwner(plan.Loader, item) is not null || !request.ReplaceUnmanaged))
                    clashes.Add(path);
            }
        if (clashes.Count > 0)
            throw new ModConflictException(
                "These files are already installed:\n" + string.Join("\n", clashes) +
                "\nRemove that mod first, or replace them.", clashes);

        // Unmanaged copies the user agreed to replace go to the trash.
        string? replacedTrash = null;
        foreach (var item in plan.Items)
            foreach (var disabled in new[] { false, true })
            {
                var path = Paths.ResolveItem(plan.Loader, item, disabled);
                if (PathExists(path))
                    MoveToTrash(path, replacedTrash ??= NewTrashDir("replaced"), (disabled ? "disabled/" : "") + item);
            }

        var root = layout.ContentRoot + Path.DirectorySeparatorChar;
        var written = new List<string>();
        try
        {
            foreach (var file in plan.Files)
            {
                var dest = Path.GetFullPath(Path.Combine(layout.ContentRoot, file.RelativePath));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Refusing to write outside the mod folders: {file.RelativePath}");
                if (layout.IsKeepPath(file.RelativePath) && File.Exists(dest)) continue; // keep the user's settings

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using (var src = file.Open())
                using (var dst = File.Create(dest))
                    src.CopyTo(dst);
                written.Add(dest);
            }
        }
        catch
        {
            foreach (var w in written) { try { File.Delete(w); } catch (IOException) { } }
            throw;
        }

        var mod = new InstalledMod
        {
            Id = id,
            Name = name,
            Version = version,
            Author = author,
            Loader = plan.Loader,
            Source = request.Source,
            Items = plan.Items,
            ExtraFiles = plan.ExtraFiles,
        };
        Registry.Mods.Add(mod);
        Registry.Save();

        if (wasDisabled)
            SetEnabled(Scan().First(m => m.Id == id), false);
        return mod;
    }

    // ---------------------------------------------------------------- layout migration

    public sealed record FlattenResult(List<string> Moved, List<string> Skipped);

    /// <summary>
    /// Earlier versions put single-DLL BepInEx mods in their own folder (plugins/&lt;Mod&gt;/Mod.dll).
    /// Moves those DLLs directly into plugins/ (or plugins_disabled/), like a fresh install now does.
    /// Only touches mods this manager installed whose folder holds nothing but DLLs and docs,
    /// and never overwrites a file that's already there.
    /// </summary>
    public FlattenResult FlattenFolderInstalls()
    {
        var result = new FlattenResult([], []);
        Registry.Load();
        bool changed = false;

        foreach (var mod in Registry.Mods.Where(m => m.Loader == ModLoader.BepInEx))
        {
            var newItems = new List<string>();
            bool modChanged = false;
            foreach (var item in mod.Items)
            {
                var parts = item.Split('/');
                var disabled = Directory.Exists(Paths.ResolveItem(mod.Loader, item, true)) && !Directory.Exists(Paths.ResolveItem(mod.Loader, item, false));
                var folder = Paths.ResolveItem(mod.Loader, item, disabled);
                if (parts.Length != 2 || !Directory.Exists(folder)
                    || Directory.EnumerateDirectories(folder).Any()
                    || Directory.EnumerateFiles(folder).Any(f => !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !ArchiveInstaller.IsDocFile(f)))
                {
                    newItems.Add(item);
                    continue;
                }

                var dlls = Directory.EnumerateFiles(folder, "*.dll").ToList();
                var targets = dlls.Select(d => (From: d, Item: $"{parts[0]}/{Path.GetFileName(d)}")).ToList();
                var clash = targets.FirstOrDefault(t => PathExists(Paths.ResolveItem(mod.Loader, t.Item, false)) || PathExists(Paths.ResolveItem(mod.Loader, t.Item, true)));
                if (dlls.Count == 0 || clash.From is not null)
                {
                    if (clash.From is not null)
                        result.Skipped.Add($"{mod.Name}: {Path.GetFileName(clash.From)} is already in {parts[0]}{(disabled ? GamePaths.DisabledSuffix : "")}");
                    newItems.Add(item);
                    continue;
                }

                EnsureGameNotRunning();
                foreach (var (from, target) in targets)
                    MovePath(from, Paths.ResolveItem(mod.Loader, target, disabled));
                var leftovers = Directory.EnumerateFiles(folder).ToList();
                if (leftovers.Count > 0)
                {
                    var trash = NewTrashDir(mod.Name + " docs");
                    foreach (var f in leftovers) MoveToTrash(f, trash, Path.GetFileName(f));
                }
                Directory.Delete(folder);
                newItems.AddRange(targets.Select(t => t.Item));
                modChanged = true;
            }
            if (modChanged)
            {
                mod.Items = newItems;
                result.Moved.Add(mod.Name);
                changed = true;
            }
        }

        if (changed) Registry.Save();
        return result;
    }

    // ---------------------------------------------------------------- helpers

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private string NewTrashDir(string label)
    {
        var baseName = Path.Combine(Paths.TrashDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{ArchiveInstaller.SafeName(label)}");
        var dir = baseName;
        for (int i = 2; Directory.Exists(dir); i++) dir = $"{baseName}-{i}";
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void MovePath(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }
}
