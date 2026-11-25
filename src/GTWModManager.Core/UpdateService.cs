using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GTWModManager.Core;

/// <summary>A published release of the mod manager itself.</summary>
public sealed record AppRelease(Version Version, string Tag, string Name, string Notes, string PageUrl,
    AppReleaseAsset? Exe, AppReleaseAsset? Checksums);

public sealed record AppReleaseAsset(string Name, string Url, long Size);

/// <summary>
/// Checks GitHub for a newer release of the mod manager and replaces the running exe with it.
///
/// Releases carry single-file builds, named by tools/build-release.ps1:
///   GTWModManager-{version}-win-x64.exe                 Windows, standalone (bundles .NET)
///   GTWModManager-{version}-win-x64-needs-dotnet9.exe   Windows, needs the .NET 9 Desktop Runtime
///   GTWModManager-{version}-linux-x64                   Linux, standalone
/// plus SHA256SUMS.txt. Each build updates to the same kind it already is.
///
/// Both Windows and Linux let a running program's file be renamed, so the update renames the
/// current one to "*.old", moves the new one into place and restarts. The next start deletes the ".old" copy.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string Repo = "Dullfin/gtw-mod-manager";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases";
    public const string UpdatedFromArg = "--updated-from";
    private const string NeedsDotnetMarker = "needs-dotnet";

    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GTWModManager", CurrentVersion.ToString(3)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// The GitHub API URL of the latest release. GTWMM_UPDATE_URL overrides it so the update can be
    /// tested against a local server.
    /// </summary>
    public static string LatestReleaseUrl =>
        Environment.GetEnvironmentVariable("GTWMM_UPDATE_URL") is { Length: > 0 } url ? url
            : $"https://api.github.com/repos/{Repo}/releases/latest";

    public static Version CurrentVersion
    {
        get
        {
            var v = (Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly).GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>Only a published single-file exe can replace itself; a dev build has DLLs beside it.</summary>
    public static bool CanSelfUpdate => string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location) && Environment.ProcessPath is not null;

    /// <summary>True for the standalone build, which carries its own copy of .NET.</summary>
    public static bool IsSelfContained =>
        !RuntimeEnvironment.GetRuntimeDirectory().Replace('\\', '/').Contains("/dotnet/shared/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the latest release, or null if there is none (or the repo isn't public).</summary>
    public async Task<AppRelease?> GetLatestAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(LatestReleaseUrl, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if ((int)resp.StatusCode is 403 or 429)
            throw new HttpRequestException("GitHub rate limit reached. Try again later.");
        resp.EnsureSuccessStatusCode();

        var dto = await JsonSerializer.DeserializeAsync<ReleaseDto>(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (dto is null || dto.Draft || dto.Prerelease || !TryParseVersion(dto.TagName, out var version)) return null;

        var assets = dto.Assets.Select(a => new AppReleaseAsset(a.Name, a.Url, a.Size)).ToList();
        return new AppRelease(version, dto.TagName, dto.Name ?? dto.TagName, dto.Body ?? "", dto.HtmlUrl ?? ReleasesPage,
            PickExe(assets, IsSelfContained, linux: Platform.IsLinux),
            assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsNewer(AppRelease release, Version current) => release.Version > current;

    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var text = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(text.Contains('.') ? text : text + ".0", out var v)) return false;
        version = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        return true;
    }

    /// <summary>Picks the build of the same kind as the running one.</summary>
    public static AppReleaseAsset? PickExe(IEnumerable<AppReleaseAsset> assets, bool selfContained, bool linux = false)
    {
        if (linux)
            return assets.FirstOrDefault(a => a.Name.Contains("GTWModManager", StringComparison.OrdinalIgnoreCase)
                                              && a.Name.EndsWith("-linux-x64", StringComparison.OrdinalIgnoreCase));
        var exes = assets.Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                     && a.Name.Contains("GTWModManager", StringComparison.OrdinalIgnoreCase)).ToList();
        return selfContained
            ? exes.FirstOrDefault(a => !a.Name.Contains(NeedsDotnetMarker, StringComparison.OrdinalIgnoreCase))
            : exes.FirstOrDefault(a => a.Name.Contains(NeedsDotnetMarker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Downloads the release's exe next to the running one (same drive, so it can be moved into
    /// place) and checks it against SHA256SUMS.txt when the release has one.
    /// </summary>
    public async Task<string> DownloadAsync(AppRelease release, string targetExe, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var asset = release.Exe ?? throw new InvalidOperationException(
            $"Release {release.Tag} has no {(Platform.IsLinux ? "Linux" : IsSelfContained ? "standalone" : ".NET 9")} build to update to.");
        var dest = targetExe + ".update";
        if (File.Exists(dest)) File.Delete(dest);

        using (var resp = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? asset.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(dest);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        try
        {
            if (release.Checksums is { } sums)
                VerifyChecksum(dest, await _http.GetStringAsync(sums.Url, ct), asset.Name);
            if (new FileInfo(dest).Length < 100_000)
                throw new InvalidDataException("The downloaded update is too small to be the app.");
        }
        catch
        {
            File.Delete(dest);
            throw;
        }
        return dest;
    }

    /// <summary>Checks a file against a "hash  filename" line in a SHA256SUMS file.</summary>
    public static void VerifyChecksum(string file, string sumsText, string assetName)
    {
        var line = sumsText.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.EndsWith(assetName, StringComparison.OrdinalIgnoreCase));
        var expected = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (expected is null)
            throw new InvalidDataException($"SHA256SUMS.txt has no entry for {assetName}.");

        using var f = File.OpenRead(file);
        var actual = Convert.ToHexString(SHA256.HashData(f));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The downloaded update failed its checksum (expected {expected}, got {actual.ToLowerInvariant()}).");
    }

    /// <summary>Moves the running exe aside and the new one into its place. Restores the old one on failure.</summary>
    public static void SwapExecutable(string currentExe, string newExe)
    {
        var old = currentExe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(currentExe, old);
        try
        {
            File.Move(newExe, currentExe);
            Platform.MakeExecutable(currentExe); // a download isn't executable on Linux until marked so
        }
        catch
        {
            File.Move(old, currentExe);
            throw;
        }
    }

    /// <summary>Deletes the previous version left by an update. The old process may take a moment to exit.</summary>
    public static async Task CleanupAfterUpdateAsync(string currentExe)
    {
        foreach (var leftover in new[] { currentExe + ".old", currentExe + ".update" })
        {
            for (int attempt = 0; attempt < 20 && File.Exists(leftover); attempt++)
            {
                try { File.Delete(leftover); }
                catch (IOException) { await Task.Delay(500); }
                catch (UnauthorizedAccessException) { await Task.Delay(500); }
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<AssetDto> Assets { get; set; } = [];
    }

    private sealed class AssetDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string Url { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
