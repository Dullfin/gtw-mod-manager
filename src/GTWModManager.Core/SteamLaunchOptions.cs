using System.Text;
using System.Text.RegularExpressions;

namespace GTWModManager.Core;

public enum LaunchOptionState
{
    /// <summary>Not Linux, so nothing is needed.</summary>
    NotNeeded,
    /// <summary>Steam's per-user config wasn't found.</summary>
    Unknown,
    /// <summary>The override isn't there, so no mod loader starts under Proton.</summary>
    Missing,
    /// <summary>It's there, but only lets one of the two loaders start.</summary>
    Partial,
    Ok,
}

public sealed record LaunchOptionStatus(LaunchOptionState State, string? CurrentOptions, bool AllowsBepInEx, bool AllowsMelonLoader);

/// <summary>
/// On Linux, Get To Work runs through Proton (Wine). Wine loads its own winhttp.dll and version.dll
/// unless told to prefer the game folder's copies, which are how BepInEx and MelonLoader start.
/// That's done with a launch option on the game in Steam. The app can't safely set it while Steam
/// runs, so it reads Steam's config to check, and shows the text to paste when it's missing.
/// </summary>
public static partial class SteamLaunchOptions
{
    /// <summary>Lets both loaders start; the app's loader switch decides which one actually does.</summary>
    public const string Recommended = "WINEDLLOVERRIDES=\"winhttp,version=n,b\" %command%";

    public static LaunchOptionStatus Check()
    {
        if (Platform.IsWindows) return new(LaunchOptionState.NotNeeded, null, true, true);

        // Several Steam accounts may have used this PC: the most recently written config is the current one.
        var config = GameLocator.SteamRoots()
            .SelectMany(root => SafeEnumerate(Path.Combine(root, "userdata")))
            .Select(user => Path.Combine(user, "config", "localconfig.vdf"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (config is null) return new(LaunchOptionState.Unknown, null, false, false);

        string text;
        try { text = File.ReadAllText(config); }
        catch (IOException) { return new(LaunchOptionState.Unknown, null, false, false); }
        return Evaluate(ReadLaunchOptions(text, GamePaths.SteamAppId));
    }

    /// <summary>Works out which loader DLLs a launch option string lets Wine take from the game folder.</summary>
    public static LaunchOptionStatus Evaluate(string? options)
    {
        var dlls = NativeOverrides(options);
        bool bep = dlls.Contains("winhttp"), melon = dlls.Contains("version");
        var state = bep && melon ? LaunchOptionState.Ok : bep || melon ? LaunchOptionState.Partial : LaunchOptionState.Missing;
        return new(state, string.IsNullOrWhiteSpace(options) ? null : options, bep, melon);
    }

    /// <summary>
    /// DLL names set to load the game's own copy first. WINEDLLOVERRIDES looks like
    /// "winhttp,version=n,b;d3d11=b": groups split by ';', each "dll1,dll2=modes".
    /// </summary>
    public static HashSet<string> NativeOverrides(string? options)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(options)) return result;
        var m = OverrideRegex().Match(options);
        if (!m.Success) return result;
        var value = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["u"].Value;
        foreach (var group in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = group.Split('=', 2);
            if (parts.Length != 2) continue;
            var modes = parts[1].Trim();
            if (!modes.StartsWith('n')) continue; // "n" or "n,b": native (the game folder's DLL) first
            foreach (var dll in parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries))
                result.Add(Path.GetFileNameWithoutExtension(dll.Trim()));
        }
        return result;
    }

    /// <summary>Finds the "LaunchOptions" value for an app in a localconfig.vdf file.</summary>
    public static string? ReadLaunchOptions(string vdf, string appId)
    {
        // Steam's apps section has one block per game: "2706170" { ... "LaunchOptions" "..." ... }
        foreach (Match start in Regex.Matches(vdf, "\"" + Regex.Escape(appId) + "\"\\s*\\{"))
        {
            int depth = 1, i = start.Index + start.Length;
            int blockStart = i;
            bool inString = false;
            for (; i < vdf.Length && depth > 0; i++)
            {
                char c = vdf[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            var block = vdf[blockStart..Math.Min(i, vdf.Length)];
            var opt = LaunchOptionsRegex().Match(block);
            if (opt.Success) return Unescape(opt.Groups[1].Value);
        }
        return null;
    }

    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    [GeneratedRegex("WINEDLLOVERRIDES=(?:\"(?<q>[^\"]*)\"|'(?<q>[^']*)'|(?<u>\\S+))")]
    private static partial Regex OverrideRegex();

    [GeneratedRegex("\"LaunchOptions\"\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex LaunchOptionsRegex();
}
