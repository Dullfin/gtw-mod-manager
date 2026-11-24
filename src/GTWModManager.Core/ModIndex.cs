using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GTWModManager.Core;

/// <summary>The community mod list (index/mods.json in the index repo).</summary>
public sealed class ModIndex
{
    public int SchemaVersion { get; set; } = 1;
    public List<IndexEntry> Mods { get; set; } = [];
}

public sealed class IndexEntry
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Homepage { get; set; }
    /// <summary>GitHub "owner/repo". The latest non-prerelease release is used.</summary>
    public string? Repo { get; set; }
    /// <summary>Release asset file name; "*" wildcards allowed. Defaults to the first .zip, then .dll.</summary>
    public string? Asset { get; set; }
    /// <summary>Direct download instead of GitHub releases. Use with Version and Sha256.</summary>
    public string? DownloadUrl { get; set; }
    public string? Version { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>A mod hosted on Nexus Mods instead of GitHub.</summary>
    public NexusRef? Nexus { get; set; }
    /// <summary>"bepinex" or "melonloader". Optional: detected from the mod's DLLs when missing.</summary>
    public string? Loader { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<string> Dependencies { get; set; } = [];

    public ModLoader? LoaderHint => Loader?.ToLowerInvariant() switch
    {
        "bepinex" => ModLoader.BepInEx,
        "melonloader" or "melon" => ModLoader.MelonLoader,
        _ => null,
    };

    public string? WebsiteUrl =>
        Homepage ?? (Nexus is { } n ? NexusClient.ModPageUrl(n.Domain, n.ModId)
            : Repo is { } r ? "https://github.com/" + r : null);
}

public sealed class NexusRef
{
    public int ModId { get; set; }
    public string Domain { get; set; } = GamePaths.NexusDomain;
    /// <summary>File name to pick on the Files tab; "*" wildcards allowed. Default: newest main file.</summary>
    public string? File { get; set; }
}

/// <summary>Where to download a specific version of a mod from.</summary>
public sealed record ResolvedDownload(string Version, string Url, string FileName, long? Size, string? Sha256);

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string Url { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class ModCatalogClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private readonly HttpClient _http;
    private readonly string _apiBase;

    /// <summary>
    /// Release lists are remembered briefly so showing the list doesn't spend GitHub's 60-requests-an-hour
    /// limit, but never for long: a mod author may publish a new version while the app is open.
    /// Installs and "Check for updates" always ask GitHub again.
    /// </summary>
    public static readonly TimeSpan ReleaseCacheLifetime = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, (DateTime Fetched, List<GitHubRelease> Releases)> _releaseCache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="apiBase">GitHub API root; tests point it at a local server.</param>
    public ModCatalogClient(string apiBase = "https://api.github.com")
    {
        _apiBase = apiBase.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GTWModManager", "1.0"));
    }

    /// <summary>Forgets every remembered release list, e.g. when the user clicks Refresh.</summary>
    public void ClearReleaseCache() => _releaseCache.Clear();

    /// <summary>Loads the index from a URL, or the copy built into the app when url is empty.</summary>
    public async Task<ModIndex> LoadIndexAsync(string? url, CancellationToken ct = default)
    {
        string json;
        if (string.IsNullOrWhiteSpace(url))
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GTWModManager.Core.DefaultIndex.json")!;
            using var r = new StreamReader(s);
            json = await r.ReadToEndAsync(ct);
        }
        else if (File.Exists(url))
        {
            json = await File.ReadAllTextAsync(url, ct);
        }
        else
        {
            json = await _http.GetStringAsync(url, ct);
        }
        var index = JsonSerializer.Deserialize<ModIndex>(json, JsonOptions) ?? new ModIndex();
        index.Mods.RemoveAll(m => string.IsNullOrWhiteSpace(m.Id) || (m.Repo is null && m.DownloadUrl is null && m.Nexus is null));
        return index;
    }

    /// <param name="fresh">Ask GitHub even if the release list was fetched recently. Use it for installs.</param>
    public async Task<ResolvedDownload> ResolveAsync(IndexEntry entry, bool fresh = false, CancellationToken ct = default)
    {
        if (entry.Repo is null && entry.DownloadUrl is null)
            throw new InvalidOperationException($"{entry.Name} is hosted on Nexus Mods; use the Nexus client.");
        if (!string.IsNullOrWhiteSpace(entry.DownloadUrl))
        {
            var name = Path.GetFileName(new Uri(entry.DownloadUrl).LocalPath);
            return new ResolvedDownload(entry.Version ?? "?", entry.DownloadUrl, name, null, entry.Sha256);
        }

        var releases = await GetReleasesAsync(entry.Repo!, fresh, ct);
        foreach (var release in releases.Where(r => !r.Draft && !r.Prerelease))
        {
            var asset = PickAsset(release.Assets, entry.Asset);
            if (asset is not null)
                return new ResolvedDownload(release.TagName.TrimStart('v', 'V'), asset.Url, asset.Name, asset.Size, entry.Sha256);
        }
        throw new InvalidOperationException($"No release of {entry.Repo} has a file matching \"{entry.Asset ?? "*.zip / *.dll"}\".");
    }

    public async Task<List<GitHubRelease>> GetReleasesAsync(string repo, bool fresh = false, CancellationToken ct = default)
    {
        if (!fresh && _releaseCache.TryGetValue(repo, out var cached) && DateTime.UtcNow - cached.Fetched < ReleaseCacheLifetime)
            return cached.Releases;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_apiBase}/repos/{repo}/releases?per_page=30");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var resp = await _http.SendAsync(request, ct);
        if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429)
            throw new HttpRequestException("GitHub rate limit reached (60 requests per hour without sign-in). Try again later.");
        resp.EnsureSuccessStatusCode();
        var list = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(await resp.Content.ReadAsStreamAsync(ct), JsonOptions, ct) ?? [];
        _releaseCache[repo] = (DateTime.UtcNow, list);
        return list;
    }

    public static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);

    public static GitHubAsset? PickAsset(IEnumerable<GitHubAsset> assets, string? pattern)
    {
        var list = assets.ToList();
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            var regex = Glob(pattern);
            return list.FirstOrDefault(a => regex.IsMatch(a.Name));
        }
        return list.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(a => a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Downloads to a temp folder and checks the SHA-256 when one is given.</summary>
    public async Task<string> DownloadAsync(ResolvedDownload download, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "GTWModManager", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ArchiveInstaller.SafeName(download.FileName));

        using var resp = await _http.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? download.Size;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(download.Sha256))
        {
            await using var f = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
            if (!hash.Equals(download.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidDataException($"Download of {download.FileName} failed its checksum. Expected {download.Sha256}, got {hash}.");
            }
        }
        return path;
    }

    public void Dispose() => _http.Dispose();
}
