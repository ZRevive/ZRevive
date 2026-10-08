using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZRevive.Launcher.Install;

/// <summary>
/// What we recorded about a managed file the last time we installed or fully verified it.
/// Size + write time are the cheap fingerprint; the hash is what we fall back to.
/// </summary>
public sealed class FilePrint
{
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("mtime")] public long MTimeTicks { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
}

/// <summary>
/// Side-car written next to the install marker. Only covers files the manifest manages, so
/// it stays small (hundreds of entries) and loads in microseconds.
/// </summary>
public sealed class FilePrints
{
    public const string FileName = ".zrevive-files.json";

    [JsonPropertyName("releaseHash")] public string? ReleaseHash { get; set; }
    [JsonPropertyName("files")] public Dictionary<string, FilePrint> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string PathFor(string installFolder) => Path.Combine(installFolder, FileName);

    public static FilePrints Load(string installFolder)
    {
        try
        {
            var p = PathFor(installFolder);
            if (File.Exists(p)) return JsonSerializer.Deserialize<FilePrints>(File.ReadAllText(p)) ?? new FilePrints();
        }
        catch { }
        return new FilePrints();
    }

    public void Save(string installFolder)
    {
        try
        {
            Directory.CreateDirectory(installFolder);
            var p = PathFor(installFolder);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, p, overwrite: true);
        }
        catch { /* losing the fingerprints only costs a slower next check */ }
    }

    public void Record(string targetPath, string fullPath, string? sha)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists) { Files.Remove(targetPath); return; }
            Files[targetPath] = new FilePrint { Size = fi.Length, MTimeTicks = fi.LastWriteTimeUtc.Ticks, Sha256 = sha };
        }
        catch { }
    }
}

public sealed record QuickVerifyResult(
    bool Ok,
    IReadOnlyList<EntryStatus> Suspect,
    int Checked,
    int Hashed,
    long ElapsedMs,
    bool NoFingerprints)
{
    /// <summary>True when something was hashed and came back wrong: never launch on this.</summary>
    public bool HashFailure => Suspect.Any(s => s.Verdict is EntryVerdict.WrongHash);
}

/// <summary>
/// The per-launch check. For each manifest entry:
///   - size differs from the manifest        -> suspect (no hashing needed)
///   - size+mtime match the recorded print   -> trusted, not hashed
///   - anything else, or entry is critical   -> hash it
/// On a clean install nothing gets hashed, so this is a few hundred stat() calls.
/// </summary>
public static class QuickVerifier
{
    public static async Task<QuickVerifyResult> RunAsync(ReleaseManifest manifest, string installFolder,
        FilePrints prints, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var suspect = new List<EntryStatus>();
        var hashed = 0;
        var noPrints = prints.Files.Count == 0;

        foreach (var entry in manifest.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (!PathSafety.TryResolve(installFolder, entry.TargetPath, out var full, out var why))
            {
                suspect.Add(new EntryStatus(entry, EntryVerdict.Unsafe, why));
                continue;
            }

            var info = new FileInfo(full);

            if (entry.Kind == EntryKind.Delete)
            {
                if (info.Exists) suspect.Add(new EntryStatus(entry, EntryVerdict.ShouldDelete, "file should not be present"));
                continue;
            }

            if (!info.Exists)
            {
                suspect.Add(new EntryStatus(entry, EntryVerdict.Missing, "not installed"));
                continue;
            }
            if (info.Length != entry.Size)
            {
                suspect.Add(new EntryStatus(entry, EntryVerdict.WrongSize, $"{info.Length} bytes, expected {entry.Size}"));
                continue;
            }

            var trusted = prints.Files.TryGetValue(entry.TargetPath, out var print)
                          && print.Size == info.Length
                          && print.MTimeTicks == info.LastWriteTimeUtc.Ticks
                          && FileHasher.HashesEqual(print.Sha256, entry.Sha256);

            if (trusted && !entry.Critical) continue;

            // Looks changed, has no fingerprint, or is flagged critical: hash it.
            hashed++;
            var sha = await FileHasher.HashFileAsync(full, null, ct);
            if (!FileHasher.HashesEqual(sha, entry.Sha256))
                suspect.Add(new EntryStatus(entry, EntryVerdict.WrongHash, "checksum doesn't match"));
            else
                prints.Record(entry.TargetPath, full, entry.Sha256);   // remember it so next launch is free
        }

        sw.Stop();
        return new QuickVerifyResult(suspect.Count == 0, suspect, manifest.Entries.Count, hashed, sw.ElapsedMilliseconds, noPrints);
    }

    /// <summary>Turns a quick-verify result into the report ApplyAsync consumes.</summary>
    public static VerifyReport ToReport(ReleaseManifest manifest, QuickVerifyResult quick)
    {
        var bad = quick.Suspect.ToDictionary(s => s.Entry.TargetPath, s => s, StringComparer.OrdinalIgnoreCase);
        return new VerifyReport(manifest.Entries
            .Select(e => bad.TryGetValue(e.TargetPath, out var s) ? s : new EntryStatus(e, EntryVerdict.Ok, null))
            .ToList());
    }
}
