using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace ZRevive.Launcher.Install;

public sealed record DownloadProgress(long Done, long Total, double BytesPerSecond);

/// <summary>
/// Abstracted so the install pipeline can be exercised without a network (and so the CDN
/// can be swapped later). The launcher uses <see cref="HttpDownloader"/>.
/// </summary>
public interface IDownloader
{
    /// <summary>
    /// Fetches <paramref name="url"/> into <paramref name="destination"/>, resuming a partial
    /// <c>.zrpart</c> next to it when the server supports ranges. Must not leave a partial
    /// file at the destination path.
    /// </summary>
    Task DownloadAsync(Uri url, string destination, long expectedSize, IProgress<DownloadProgress>? progress, CancellationToken ct);

    Task<string> GetStringAsync(Uri url, CancellationToken ct);
}

public sealed class HttpDownloader : IDownloader, IDisposable
{
    readonly HttpClient _http;

    public HttpDownloader(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ZReviveLauncher/" +
            (typeof(HttpDownloader).Assembly.GetName().Version?.ToString(3) ?? "0"));
    }

    public async Task<string> GetStringAsync(Uri url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    public async Task DownloadAsync(Uri url, string destination, long expectedSize,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + ".zrpart";

        // Local mirror / test manifest: copy instead of downloading. Only reachable when the
        // manifest itself was loaded from a file:// URL (see ManifestParser.ResolveUrl).
        if (url.IsFile)
        {
            await using (var src = new FileStream(url.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
                await src.CopyToAsync(dst, 1 << 20, ct);
            progress?.Report(new DownloadProgress(new FileInfo(part).Length, expectedSize, 0));
            File.Move(part, destination, overwrite: true);
            return;
        }

        long have = 0;
        if (File.Exists(part))
        {
            have = new FileInfo(part).Length;
            if (expectedSize > 0 && have > expectedSize) { File.Delete(part); have = 0; }
            else if (expectedSize > 0 && have == expectedSize) have = expectedSize; // complete, just verify+move below
        }

        if (expectedSize <= 0 || have < expectedSize)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (have > 0 && res.StatusCode != HttpStatusCode.PartialContent)
            {
                // Server ignored the range: start over rather than concatenating garbage.
                have = 0;
                File.Delete(part);
            }
            res.EnsureSuccessStatusCode();

            var total = expectedSize > 0
                ? expectedSize
                : have + (res.Content.Headers.ContentLength ?? 0);

            await using (var net = await res.Content.ReadAsStreamAsync(ct))
            await using (var fs = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create,
                             FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                var buf = new byte[1 << 20];
                var done = have;
                var started = DateTime.UtcNow;
                var startBytes = have;
                int n;
                while ((n = await net.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    var secs = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
                    progress?.Report(new DownloadProgress(done, total, (done - startBytes) / secs));
                }
            }
        }

        if (expectedSize > 0 && new FileInfo(part).Length != expectedSize)
        {
            File.Delete(part);
            throw new IOException($"Downloaded size doesn't match the manifest for {Path.GetFileName(destination)}.");
        }

        File.Move(part, destination, overwrite: true);
    }

    public void Dispose() => _http.Dispose();
}
