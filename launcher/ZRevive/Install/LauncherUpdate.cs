using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace ZRevive.Launcher.Install;

/// <summary>
/// Keeps the launcher itself current.
/// <para>Before this existed, only the GAME files auto-updated: a player who downloaded the exe
/// once kept running it forever, so every launcher bug stayed with them and a release that needed
/// a newer launcher just failed in a confusing way. The manifest's <c>requiredLauncher</c> field
/// was being published and ignored.</para>
/// <para>The update is a straight file swap: a single-file exe cannot overwrite itself while it is
/// running, so the new build is downloaded next to the old one and a detached shell waits for this
/// process to exit, replaces the file, and starts it again.</para>
/// </summary>
public static class LauncherUpdate
{
    /// <summary>Where the published launcher always lives, whatever the newest release is.</summary>
    public const string DownloadUrl = "https://github.com/ZRevive/ZRevive/releases/latest/download/ZRevive.exe";

    /// <summary>The release page, for when the swap can't be done automatically.</summary>
    public const string ReleasePage = "https://github.com/ZRevive/ZRevive/releases/latest";

    /// <summary>This launcher's version, as major.minor.build.</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = typeof(LauncherUpdate).Assembly.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>
    /// Is <paramref name="required"/> newer than what is running? Unparseable or missing values
    /// mean "no update needed": a typo in a manifest must not lock every player out of the game.
    /// </summary>
    public static bool IsOlderThan(string? required, string? current = null)
    {
        if (string.IsNullOrWhiteSpace(required)) return false;
        if (!TryParse(required, out var want)) return false;
        if (!TryParse(current ?? CurrentVersion, out var have)) return false;
        return have < want;
    }

    static bool TryParse(string s, out Version v)
    {
        v = new Version(0, 0, 0);
        s = s.Trim().TrimStart('v', 'V');
        // Version.Parse wants at least major.minor; accept "1" too.
        if (!s.Contains('.')) s += ".0";
        return Version.TryParse(s, out v!);
    }

    /// <summary>
    /// Downloads the newest launcher next to the running one and hands over to a detached shell
    /// that swaps the files and restarts. Returns false (with a reason) if anything stops it, so
    /// the caller can fall back to "download it yourself" rather than leaving the player stuck.
    /// </summary>
    // Its own client: this runs once, follows GitHub's redirect to the asset host, and must not
    // inherit any base address or header the install downloader sets for the payload CDN.
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public static async Task<(bool Started, string Reason)> DownloadAndRestartAsync(
        IProgress<double>? progress, CancellationToken ct)
    {
        var http = Http;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            return (false, "the running launcher's path could not be determined");

        var dir = Path.GetDirectoryName(exe)!;
        var staged = Path.Combine(dir, "ZRevive.update.exe");

        try
        {
            using var res = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? 0;

            await using (var src = await res.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[128 * 1024];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            // A truncated download must never replace a working launcher.
            if (new FileInfo(staged).Length < 1_000_000)
            {
                TryDelete(staged);
                return (false, "the download was incomplete");
            }
        }
        catch (OperationCanceledException) { TryDelete(staged); throw; }
        catch (Exception ex) { TryDelete(staged); return (false, ex.Message); }

        try
        {
            // ping, not timeout: timeout needs a console that a detached process does not have.
            // The move is retried because the old exe's handle is released a moment after exit.
            var script =
                $"ping 127.0.0.1 -n 3 > nul & " +
                $"move /y \"{staged}\" \"{exe}\" > nul || (ping 127.0.0.1 -n 4 > nul & move /y \"{staged}\" \"{exe}\" > nul) & " +
                $"start \"\" \"{exe}\"";
            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                Arguments = "/c " + script,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            return (true, "");
        }
        catch (Exception ex)
        {
            TryDelete(staged);
            return (false, ex.Message);
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Opens the release page in the browser, for a manual download.</summary>
    public static void OpenReleasePage()
    {
        try { Process.Start(new ProcessStartInfo(ReleasePage) { UseShellExecute = true }); }
        catch { }
    }
}
