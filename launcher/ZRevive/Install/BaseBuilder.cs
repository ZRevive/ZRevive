using System.IO;
using System.Runtime.InteropServices;

namespace ZRevive.Launcher.Install;

public sealed record BuildProgress(string Stage, string CurrentFile, long DoneBytes, long TotalBytes, int DoneFiles, int TotalFiles, double BytesPerSecond);

/// <summary>
/// Step 2 of the install: make the ZRevive folder from the player's own Z1BR install.
///
/// The player's install is sacred. Every source handle is opened read-only with
/// FileShare.Read, nothing is ever created or deleted under the source, and
/// <see cref="SourceSnapshot"/> is taken before and re-checked after so a bug would be
/// caught rather than silently corrupting a 15 GB Steam install.
///
/// Hardlinks are opt-in only: the repo already got burned by C:\Games\ZRevive being
/// hardlinked to the ROTK copy (see backup\ and the project notes), because an in-place
/// write to one install changes the other. COPY is the default.
/// </summary>
public static class BaseBuilder
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr reserved);

    /// <summary>Hardlinks only work within one NTFS volume.</summary>
    public static bool SameVolume(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Rejects source/target combinations that would damage either install.</summary>
    public static string? ValidatePair(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "Pick where ZRevive should be installed.";
        if (SteamLocator.ValidateSource(source) is string bad) return bad;
        try
        {
            var s = Path.GetFullPath(source);
            var t = Path.GetFullPath(target);
            if (PathSafety.IsSameOrInside(s, t)) return "The ZRevive folder can't be inside your Steam install.";
            if (PathSafety.IsSameOrInside(t, s)) return "That folder contains your Steam install. Pick a separate folder.";
            if (File.Exists(Path.Combine(t, ".rotk-installation.json")))
                return "That's the ROTK install folder. Pick a different folder.";
            if (Directory.Exists(t) && Directory.EnumerateFileSystemEntries(t).Any()
                                    && !File.Exists(Path.Combine(t, InstallState.MarkerFile))
                                    && !File.Exists(Path.Combine(t, "H1Z1.exe")))
                return "That folder already has other files in it. Pick an empty folder.";
            return null;
        }
        catch (Exception ex) { return "Can't use that folder: " + ex.Message; }
    }

    public sealed record SourceSnapshot(int FileCount, long TotalBytes, long NewestWriteTicks);

    public static SourceSnapshot Snapshot(string source)
    {
        var count = 0;
        long bytes = 0, newest = 0;
        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            try
            {
                var fi = new FileInfo(f);
                count++;
                bytes += fi.Length;
                newest = Math.Max(newest, fi.LastWriteTimeUtc.Ticks);
            }
            catch { }
        }
        return new SourceSnapshot(count, bytes, newest);
    }

    /// <summary>
    /// Post-build assertion: the source must still have the same file count, total size and
    /// newest write time. Returns null when untouched, else a description of the difference.
    /// </summary>
    public static string? VerifySourceUntouched(string source, SourceSnapshot before)
    {
        var after = Snapshot(source);
        if (after.FileCount != before.FileCount) return $"file count changed ({before.FileCount} -> {after.FileCount})";
        if (after.TotalBytes != before.TotalBytes) return $"total size changed ({before.TotalBytes} -> {after.TotalBytes})";
        if (after.NewestWriteTicks != before.NewestWriteTicks) return "a file was modified";
        return null;
    }

    /// <summary>
    /// Copies (or hardlinks) the base game into <paramref name="target"/>. Resumable: a file
    /// that already exists with the same length and write time is skipped, so re-running after
    /// a cancel is cheap.
    /// </summary>
    /// <summary>
    /// Called periodically during the copy with a one-line progress note for the disk log. Set by
    /// the window; null in tests. Deliberately not an IProgress: this must not be marshalled to the
    /// UI thread, because its whole purpose is to survive the UI going away.
    /// </summary>
    public static Action<string>? CopyHeartbeat;

    /// <summary>
    /// Files a player's Steam copy may carry that are NOT Daybreak's and must never reach a ZRevive
    /// folder. Found the hard way: the owner's Steam install had been used with a third-party H1Z1
    /// emulator, whose <c>dinput8.dll</c> is a 611,840-byte DLL-hijack injector (Daybreak's is
    /// 99,840). Copied into a ZRevive build it loads itself into the game and the client dies with
    /// an access violation the moment the title screen is built - which cost a whole evening on
    /// 2026-10-07 and was blamed on half a dozen innocent things first. A working ZRevive install
    /// has no dinput8.dll at all.
    /// </summary>
    static readonly string[] NeverCopy =
    {
        "dinput8.dll",
        ".zemu-integrity-cache.json", "zemu-patch.log", "zbridge.log", "ZEmuVivox.ini",
    };

    /// <summary>
    /// Marker files an emulator leaves behind. Their presence means the Steam copy has had files
    /// REPLACED, not just added, so what we would copy is not the game Daybreak ships - and the
    /// missing originals cannot be recovered by skipping anything.
    /// </summary>
    static readonly string[] EmulatorMarkers =
    {
        ".zemu-integrity-cache.json", "zemu-patch.log", "zbridge.log", "ZEmuVivox.ini",
    };

    /// <summary>
    /// The emulator's own integrity cache makes Steam's "Verify integrity of game files" pass
    /// without repairing anything, so the player cannot discover this on their own; the only
    /// reliable repair is to delete the markers first and verify again. Returns null when the
    /// source looks clean.
    /// </summary>
    public static string? ThirdPartyWarning(string source)
    {
        try
        {
            var found = EmulatorMarkers.Where(m => File.Exists(Path.Combine(source, m))).ToList();
            if (found.Count == 0) return null;
            return "Your Z1 Battle Royale folder has been modified by another H1Z1 emulator "
                 + $"({string.Join(", ", found)}). Some of the game's own files have been replaced, so a "
                 + "ZRevive install built from it will not start.\n\n"
                 + "Delete those files from the Steam folder, then use Steam → Properties → Installed "
                 + "Files → Verify integrity of game files. Steam's check passes without repairing while "
                 + "they are present, so they have to go first.";
        }
        catch { return null; }
    }

    public static async Task<SourceSnapshot> BuildAsync(string source, string target, bool allowHardlinks,
        IProgress<BuildProgress>? progress, CancellationToken ct)
    {
        if (ValidatePair(source, target) is string problem) throw new InvalidOperationException(problem);

        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        var hardlink = allowHardlinks && SameVolume(source, target);

        var before = Snapshot(source);
        Directory.CreateDirectory(target);

        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        long done = 0;
        var doneFiles = 0;
        var started = DateTime.UtcNow;

        foreach (var src in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(source, src);

            // Never carry a third-party injector or an emulator's leftovers into ZRevive.
            if (NeverCopy.Contains(Path.GetFileName(rel), StringComparer.OrdinalIgnoreCase)) continue;

            // The source is enumerated, not manifest-supplied, but run it through the same
            // gate anyway: a junction inside the Steam folder shouldn't redirect our writes.
            if (!PathSafety.TryResolve(target, rel, out var dst, out _)) continue;

            var si = new FileInfo(src);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            var di = new FileInfo(dst);
            var alreadyThere = di.Exists && di.Length == si.Length && di.LastWriteTimeUtc == si.LastWriteTimeUtc;

            if (!alreadyThere)
            {
                if (di.Exists) { di.Attributes = FileAttributes.Normal; di.Delete(); }

                if (hardlink && CreateHardLinkW(dst, src, IntPtr.Zero))
                {
                    // linked; nothing copied
                }
                else
                {
                    await CopyReadOnlyAsync(src, dst, ct);
                    try { File.SetLastWriteTimeUtc(dst, si.LastWriteTimeUtc); } catch { }
                }
            }

            done += si.Length;
            doneFiles++;
            // A heartbeat on disk, so a launcher that is killed mid-copy still says how far it got
            // and on which file. Every 200 files is often enough to locate the stop without
            // turning the copy into a write-amplified crawl.
            if (doneFiles % 200 == 0 || doneFiles == files.Count)
                CopyHeartbeat?.Invoke($"copied {doneFiles}/{files.Count} files, {done} bytes, last: {rel}");
            var secs = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
            progress?.Report(new BuildProgress(
                hardlink ? "Linking base game files" : "Copying base game files",
                rel, done, before.TotalBytes, doneFiles, files.Count, done / secs));
        }

        if (VerifySourceUntouched(source, before) is string changed)
            throw new IOException("Aborting: your Steam install changed during the build (" + changed + "). Nothing was launched.");

        return before;
    }

    /// <summary>Copy with the source strictly read-only; the destination is written via a .part temp.</summary>
    static async Task CopyReadOnlyAsync(string src, string dst, CancellationToken ct)
    {
        var tmp = dst + ".zrpart";
        try
        {
            await using (var inStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var outStream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20,
                             FileOptions.Asynchronous))
                await inStream.CopyToAsync(outStream, 1 << 20, ct);

            // Move-over-the-top instead of writing in place: if the destination is hardlinked
            // to anything else, only our copy changes.
            File.Move(tmp, dst, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}
