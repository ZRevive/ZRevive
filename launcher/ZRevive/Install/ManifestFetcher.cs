using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZRevive.Launcher.Install;

/// <summary>
/// Result of a conditional manifest fetch. <see cref="NotModified"/> means the server
/// answered 304 and <see cref="Json"/> came out of the local cache.
/// </summary>
public sealed record ManifestFetch(string Json, string? ETag, bool NotModified, bool FromCache);

public interface IManifestFetcher
{
    /// <summary>
    /// Fetches the manifest, sending If-None-Match when <paramref name="etag"/> is known.
    /// Implementations must not throw for 304.
    /// </summary>
    Task<ManifestFetch> FetchAsync(string url, string? etag, CancellationToken ct);
}

/// <summary>
/// Caches the manifest body + ETag in %APPDATA%\ZRevive so the per-launch check is a
/// conditional request (a few hundred bytes of headers) instead of a full download.
/// Keyed by URL, so switching between a GitHub raw URL, a Releases asset and the portal
/// doesn't mix caches up.
/// </summary>
public sealed class ManifestCache
{
    public sealed class Record
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("etag")] public string? ETag { get; set; }
        [JsonPropertyName("json")] public string? Json { get; set; }
        [JsonPropertyName("fetchedUtc")] public DateTime? FetchedUtc { get; set; }
    }

    readonly string _path;

    public ManifestCache(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZRevive", "manifest-cache.json");

    public Record? Load(string url)
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var r = JsonSerializer.Deserialize<Record>(File.ReadAllText(_path));
            return r != null && string.Equals(r.Url, url, StringComparison.OrdinalIgnoreCase) && r.Json != null ? r : null;
        }
        catch { return null; }
    }

    public void Save(string url, string json, string? etag)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                new Record { Url = url, ETag = etag, Json = json, FetchedUtc = DateTime.UtcNow },
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* the cache is an optimisation, never fatal */ }
    }
}

/// <summary>
/// Real fetcher. Deliberately source-agnostic: any http(s) URL works, so the manifest can
/// live on raw.githubusercontent.com, a GitHub Releases asset, R2 or the portal. Nothing is
/// hardcoded — the URL comes from Settings (empty = &lt;portal&gt;/assets/manifest.json).
/// A file:// URL (or a plain local path) is accepted too, for local testing and mirrors.
/// </summary>
public sealed class HttpManifestFetcher : IManifestFetcher
{
    readonly HttpClient _http;

    public HttpManifestFetcher(HttpClient? http = null, TimeSpan? timeout = null)
    {
        _http = http ?? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });
        if (http == null) _http.Timeout = timeout ?? TimeSpan.FromSeconds(6);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ZReviveLauncher/" +
            (typeof(HttpManifestFetcher).Assembly.GetName().Version?.ToString(3) ?? "0"));
    }

    /// <summary>Normalises a configured value into a URI; a bare path becomes file://.</summary>
    public static Uri ToUri(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.IsFile))
            return u;
        if (Path.IsPathFullyQualified(url)) return new Uri(Path.GetFullPath(url));
        throw new ManifestException("The manifest URL must be an http(s) address, a file:// URL or a full local path.");
    }

    public async Task<ManifestFetch> FetchAsync(string url, string? etag, CancellationToken ct)
    {
        var uri = ToUri(url);

        if (uri.IsFile)
        {
            var path = uri.LocalPath;
            if (!File.Exists(path)) throw new ManifestException("No manifest at " + path);
            // mtime+size stands in for an ETag on a local file.
            var fi = new FileInfo(path);
            var tag = "\"" + fi.LastWriteTimeUtc.Ticks + "-" + fi.Length + "\"";
            if (etag == tag) return new ManifestFetch("", tag, NotModified: true, FromCache: false);
            return new ManifestFetch(await File.ReadAllTextAsync(path, ct), tag, false, false);
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (res.StatusCode == HttpStatusCode.NotModified)
            return new ManifestFetch("", etag, NotModified: true, FromCache: false);

        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);
        return new ManifestFetch(json, res.Headers.ETag?.ToString(), false, false);
    }
}

/// <summary>
/// Fetch + cache + parse in one call. Returns the parsed manifest and whether the server
/// said "unchanged", which is what makes the per-launch check cheap.
/// </summary>
public sealed class ManifestSource
{
    readonly IManifestFetcher _fetcher;
    readonly ManifestCache _cache;

    public ManifestSource(IManifestFetcher fetcher, ManifestCache? cache = null)
    {
        _fetcher = fetcher;
        _cache = cache ?? new ManifestCache();
    }

    public sealed record Result(ReleaseManifest Manifest, bool Unchanged, bool Offline, string? Warning);

    /// <summary>
    /// Never throws for a network problem: when the manifest can't be reached but a cached
    /// copy exists, the cached manifest is returned with <see cref="Result.Offline"/> set, so
    /// the player can still launch. A manifest that is reachable but *invalid* does throw.
    /// </summary>
    public async Task<Result> GetAsync(string url, CancellationToken ct)
    {
        var cached = _cache.Load(url);
        try
        {
            var fetched = await _fetcher.FetchAsync(url, cached?.ETag, ct);

            if (fetched.NotModified)
            {
                if (cached?.Json == null) throw new ManifestException("The server says the manifest is unchanged but nothing is cached.");
                return new Result(ManifestParser.Parse(cached.Json), Unchanged: true, false, null);
            }

            var manifest = ManifestParser.Parse(fetched.Json);   // only cache something that parses
            var same = cached?.Json == fetched.Json;
            _cache.Save(url, fetched.Json, fetched.ETag);
            return new Result(manifest, same, false, null);
        }
        catch (ManifestException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (cached?.Json != null)
                return new Result(ManifestParser.Parse(cached.Json), Unchanged: true, Offline: true,
                    $"Couldn't reach the update server ({ex.Message}); using the last known release.");
            throw;
        }
    }
}
