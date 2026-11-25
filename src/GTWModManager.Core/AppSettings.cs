using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GTWModManager.Core;

public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GTWModManager", "settings.json");

    public const string DefaultIndexUrl = "https://raw.githubusercontent.com/Dullfin/gtw-mod-index/main/mods.json";

    public string? GameDir { get; set; }

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>An app version the user chose to skip; no banner is shown for it.</summary>
    public string? SkippedAppVersion { get; set; }

    /// <summary>Raw URL of the community mods.json. Null means <see cref="DefaultIndexUrl"/>.</summary>
    public string? IndexUrl { get; set; }

    /// <summary>
    /// The Nexus API key. On Windows it's encrypted with DPAPI for the current user. On Linux it's
    /// stored as "plain:&lt;base64&gt;" in a settings file only the user can read (mode 600).
    /// </summary>
    public string? NexusApiKeyProtected { get; set; }

    private const string PlainPrefix = "plain:";

    public string EffectiveIndexUrl => string.IsNullOrWhiteSpace(IndexUrl) ? DefaultIndexUrl : IndexUrl;

    public string? GetNexusApiKey()
    {
        if (string.IsNullOrEmpty(NexusApiKeyProtected)) return null;
        try
        {
            if (NexusApiKeyProtected.StartsWith(PlainPrefix, StringComparison.Ordinal))
                return Encoding.UTF8.GetString(Convert.FromBase64String(NexusApiKeyProtected[PlainPrefix.Length..]));
            return Platform.IsWindows
                ? Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(NexusApiKeyProtected)))
                : null;
        }
        catch (Exception e) when (e is FormatException or System.ComponentModel.Win32Exception) { return null; }
    }

    public void SetNexusApiKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) { NexusApiKeyProtected = null; return; }
        var bytes = Encoding.UTF8.GetBytes(key.Trim());
        NexusApiKeyProtected = Platform.IsWindows
            ? Convert.ToBase64String(Dpapi.Protect(bytes))
            : PlainPrefix + Convert.ToBase64String(bytes);
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        Platform.MakePrivate(FilePath);
    }
}

/// <summary>Windows DPAPI (CryptProtectData) for the current user, without extra packages.</summary>
internal static class Dpapi
{
    public static byte[] Protect(byte[] data) => Run(data, protect: true);
    public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, input.Data, data.Length);
        try
        {
            const int uiForbidden = 0x1;
            bool ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, uiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, uiForbidden, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
