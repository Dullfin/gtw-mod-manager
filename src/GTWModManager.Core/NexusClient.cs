using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GTWModManager.Core;

public sealed record NexusUser(string Name, bool IsPremium);

public sealed class NexusModInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("author")] public string? Author { get; set; }
}

public sealed class NexusFile
{
    [JsonPropertyName("file_id")] public long FileId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("category_name")] public string? Category { get; set; }
    [JsonPropertyName("file_name")] public string FileName { get; set; } = "";
    [JsonPropertyName("uploaded_timestamp")] public long Uploaded { get; set; }
}

public sealed class NexusException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Nexus Mods API v1 with the user's personal API key.
/// Every API call needs a key. Direct downloads additionally need a Premium account: the API only
/// gives free accounts a download link when they click "Mod manager download" on the site, and
/// Get To Work mods only offer "Manual download". Free users download in the browser instead,
/// and the app picks the file up from their Downloads folder.
/// </summary>
public sealed class NexusClient : IDisposable
{
    private const string Api = "https://api.nexusmods.com/v1";
    private readonly HttpClient _http;

    public NexusClient(string apiKey)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("apikey", apiKey);
        _http.DefaultRequestHeaders.Add("Application-Name", "GTWModManager");
        _http.DefaultRequestHeaders.Add("Application-Version", "0.2.0");
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GTWModManager", "0.2.0"));
    }

    public static string ModPageUrl(string domain, int modId) => $"https://www.nexusmods.com/{domain}/mods/{modId}";

    public static string FilesPageUrl(string domain, int modId, long? fileId = null) =>
        $"https://www.nexusmods.com/{domain}/mods/{modId}?tab=files" + (fileId is { } f ? $"&file_id={f}" : "");

    public async Task<NexusUser> ValidateAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("/users/validate.json", ct);
        var root = doc.RootElement;
        return new NexusUser(
            root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            root.TryGetProperty("is_premium", out var p) && p.ValueKind == JsonValueKind.True);
    }

    public async Task<NexusModInfo> GetModAsync(NexusRef mod, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"/games/{mod.Domain}/mods/{mod.ModId}.json", ct);
        return doc.RootElement.Deserialize<NexusModInfo>() ?? new NexusModInfo();
    }

    public async Task<List<NexusFile>> GetFilesAsync(NexusRef mod, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"/games/{mod.Domain}/mods/{mod.ModId}/files.json", ct);
        return doc.RootElement.GetProperty("files").Deserialize<List<NexusFile>>() ?? [];
    }

    /// <summary>Premium only. Returns a CDN link for the file.</summary>
    public async Task<string> GetDownloadUrlAsync(NexusRef mod, long fileId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"/games/{mod.Domain}/mods/{mod.ModId}/files/{fileId}/download_link.json", ct);
        foreach (var mirror in doc.RootElement.EnumerateArray())
            if (mirror.TryGetProperty("URI", out var uri) && uri.GetString() is { Length: > 0 } url)
                return url;
        throw new NexusException("Nexus returned no download link.");
    }

    /// <summary>
    /// Picks the file to install: the pattern match if given, else the newest MAIN file.
    /// Bundled copies of the loaders themselves are skipped; the manager installs those.
    /// </summary>
    public static NexusFile? PickFile(IEnumerable<NexusFile> files, string? pattern)
    {
        var list = files.Where(f => f.Category is not ("OLD_VERSION" or "ARCHIVED" or "REMOVED")).ToList();
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            var regex = ModCatalogClient.Glob(pattern);
            return list.Where(f => regex.IsMatch(f.FileName) || regex.IsMatch(f.Name)).MaxBy(f => f.Uploaded);
        }
        bool IsLoader(NexusFile f) =>
            f.FileName.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase) ||
            f.FileName.StartsWith("MelonLoader", StringComparison.OrdinalIgnoreCase);
        var candidates = list.Where(f => !IsLoader(f)).ToList();
        return candidates.Where(f => f.Category == "MAIN").MaxBy(f => f.Uploaded) ?? candidates.MaxBy(f => f.Uploaded);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(Api + path, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            string? message = null;
            try { message = JsonDocument.Parse(body).RootElement.GetProperty("message").GetString(); } catch (Exception) { }
            throw resp.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new NexusException("The Nexus API key was rejected. Check it in Settings.", resp.StatusCode),
                HttpStatusCode.Forbidden => new NexusException(message ?? "Nexus refused the request. Direct downloads need a Premium account.", resp.StatusCode),
                HttpStatusCode.TooManyRequests => new NexusException("Nexus rate limit reached. Try again in an hour.", resp.StatusCode),
                _ => new NexusException($"Nexus API error {(int)resp.StatusCode}: {message ?? resp.ReasonPhrase}", resp.StatusCode),
            };
        }
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    public void Dispose() => _http.Dispose();
}
