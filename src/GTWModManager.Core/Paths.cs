namespace GTWModManager.Core;

public enum ModLoader
{
    BepInEx,
    MelonLoader,
}

/// <summary>Where one mod loader keeps its mods, relative to its content root.</summary>
public sealed record LoaderLayout(
    ModLoader Loader,
    string DisplayName,
    string ContentRoot,
    /// <summary>Folders whose top-level entries are mods; each has a "_disabled" twin.</summary>
    string[] ToggleRoots,
    /// <summary>Other folders a mod may write to (libraries, configs). Never toggled.</summary>
    string[] ExtraRoots,
    /// <summary>Folders whose files are the user's settings: never overwritten or removed.</summary>
    string[] KeepRoots,
    /// <summary>The DLL in the game folder that makes the game load this loader.</summary>
    string ProxyDll)
{
    public IEnumerable<string> AllowedRoots => ToggleRoots.Concat(ExtraRoots);

    /// <summary>Returns the canonical spelling if <paramref name="segment"/> is one of this loader's roots.</summary>
    public string? MatchRoot(string segment) =>
        AllowedRoots.FirstOrDefault(r => r.Equals(segment, StringComparison.OrdinalIgnoreCase));

    public bool IsToggleRoot(string segment) => ToggleRoots.Contains(segment, StringComparer.OrdinalIgnoreCase);
    public bool IsKeepPath(string relative) =>
        KeepRoots.Any(k => relative.StartsWith(k + "/", StringComparison.OrdinalIgnoreCase));
}

/// <summary>All the folders the manager touches, derived from the game folder.</summary>
public sealed class GamePaths
{
    public const string ExeName = "Get To Work.exe";
    public const string ProcessName = "Get To Work";
    public const string SteamAppId = "2706170";
    public const string NexusDomain = "gettowork";
    public const string DisabledSuffix = "_disabled";

    public GamePaths(string gameDir)
    {
        GameDir = Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar);
        BepInEx = new LoaderLayout(ModLoader.BepInEx, "BepInEx", BepInExDir,
            ["plugins", "patchers"], ["config"], ["config"], "winhttp.dll");
        MelonLoader = new LoaderLayout(ModLoader.MelonLoader, "MelonLoader", GameDir,
            ["Mods", "Plugins"], ["UserLibs", "UserData"], ["UserData"], "version.dll");
    }

    public string GameDir { get; }
    public string Exe => Path.Combine(GameDir, ExeName);
    public string BepInExDir => Path.Combine(GameDir, "BepInEx");
    public string PluginsDir => Path.Combine(BepInExDir, "plugins");
    public string DisabledPluginsDir => Path.Combine(BepInExDir, "plugins" + DisabledSuffix);
    public string MelonModsDir => Path.Combine(GameDir, "Mods");

    /// <summary>The manager's own data lives outside both loaders so it survives either being removed.</summary>
    public string ManagerDataDir => Path.Combine(GameDir, "GTWModManager");
    public string RegistryFile => Path.Combine(ManagerDataDir, "installed.json");
    public string TrashDir => Path.Combine(ManagerDataDir, "trash");
    /// <summary>Where version 0.1 kept its data.</summary>
    public string LegacyManagerDataDir => Path.Combine(BepInExDir, "modmanager");

    public LoaderLayout BepInEx { get; }
    public LoaderLayout MelonLoader { get; }
    public IEnumerable<LoaderLayout> Loaders => [BepInEx, MelonLoader];
    public LoaderLayout Layout(ModLoader loader) => loader == ModLoader.BepInEx ? BepInEx : MelonLoader;

    public bool IsValidGameDir => File.Exists(Exe);

    /// <summary>Converts "plugins/Foo" into an absolute path, optionally in its disabled twin folder.</summary>
    public string ResolveItem(ModLoader loader, string item, bool disabled)
    {
        var layout = Layout(loader);
        var parts = Normalize(item).Split('/', 2);
        var root = disabled ? parts[0] + DisabledSuffix : parts[0];
        return parts.Length == 1
            ? Path.Combine(layout.ContentRoot, root)
            : Path.Combine(layout.ContentRoot, root, parts[1].Replace('/', Path.DirectorySeparatorChar));
    }

    public static string Normalize(string relative) => relative.Replace('\\', '/').Trim('/');
}
