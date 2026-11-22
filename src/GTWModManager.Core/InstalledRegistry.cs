using System.Text.Json;
using System.Text.Json.Serialization;

namespace GTWModManager.Core;

/// <summary>A mod the manager installed, and exactly which files it put where.</summary>
public sealed class InstalledMod
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public ModLoader Loader { get; set; } = ModLoader.BepInEx;
    /// <summary>"index", "nexus", "file" ...</summary>
    public string Source { get; set; } = "file";
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Top-level items under a toggle root of the mod's loader, e.g. "plugins/MyMod" or "Mods/MyMod.dll".
    /// These move to the "_disabled" twin folder when the mod is disabled.
    /// </summary>
    public List<string> Items { get; set; } = [];

    /// <summary>Other files relative to the loader's content root (configs, libraries). Never toggled.</summary>
    public List<string> ExtraFiles { get; set; } = [];
}

public sealed class InstalledRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly GamePaths _paths;

    public InstalledRegistry(GamePaths paths)
    {
        _paths = paths;
        MigrateLegacy();
        Load();
    }

    public List<InstalledMod> Mods { get; private set; } = [];

    public InstalledMod? Find(string id) =>
        Mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public InstalledMod? FindOwner(ModLoader loader, string item) =>
        Mods.FirstOrDefault(m => m.Loader == loader &&
            m.Items.Any(i => string.Equals(GamePaths.Normalize(i), GamePaths.Normalize(item), StringComparison.OrdinalIgnoreCase)));

    public void Load()
    {
        if (!File.Exists(_paths.RegistryFile)) { Mods = []; return; }
        var json = File.ReadAllText(_paths.RegistryFile);
        Mods = JsonSerializer.Deserialize<List<InstalledMod>>(json, JsonOptions) ?? [];
    }

    public void Save()
    {
        Directory.CreateDirectory(_paths.ManagerDataDir);
        var tmp = _paths.RegistryFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Mods, JsonOptions));
        File.Move(tmp, _paths.RegistryFile, overwrite: true);
    }

    /// <summary>Version 0.1 stored its data in BepInEx\modmanager; move it next to the game.</summary>
    private void MigrateLegacy()
    {
        var legacy = _paths.LegacyManagerDataDir;
        if (!Directory.Exists(legacy) || Directory.Exists(_paths.ManagerDataDir)) return;
        try { Directory.Move(legacy, _paths.ManagerDataDir); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
