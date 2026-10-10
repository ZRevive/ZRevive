using System.IO;

namespace ZRevive.Launcher.Install;

public sealed record InstallProgress(
    string Stage,
    string CurrentFile,
    double OverallFraction,
    double FileFraction,
    long DoneBytes,
    long TotalBytes,
    double BytesPerSecond,
    int DoneItems,
    int TotalItems);

public enum EntryVerdict { Ok, Missing, WrongHash, WrongSize, ShouldDelete, Unsafe }

public sealed record EntryStatus(ManifestEntry Entry, EntryVerdict Verdict, string? Detail);

public sealed record VerifyReport(IReadOnlyList<EntryStatus> All)
{
    public IEnumerable<EntryStatus> Broken => All.Where(s => s.Verdict is not EntryVerdict.Ok);
    public IEnumerable<EntryStatus> Unsafe => All.Where(s => s.Verdict is EntryVerdict.Unsafe);
    public bool Healthy => !All.Any(s => s.Verdict is not EntryVerdict.Ok);
    public long RepairBytes => All.Where(s => s.Verdict is EntryVerdict.Missing or EntryVerdict.WrongHash or EntryVerdict.WrongSize)
                                  .Sum(s => s.Entry.Size);
}

/// <summary>
/// Step 3+4 of the install: fetch the manifest, verify the ZRevive folder against it,
/// and download/apply whatever is missing or wrong. No knowledge of WPF.
///
/// Staging: every download lands in &lt;install&gt;\.zrevive-staging and is only moved into
/// place after its SHA-256 matches, so a cancelled or corrupted download can never leave a
/// half file where the game expects a good one. The staging folder also gives resume: the
/// downloader keeps a .zrpart next to the staged file.
/// </summary>
public sealed class InstallService
{
    public const string StagingDir = ".zrevive-staging";

    readonly IDownloader _downloader;
    readonly ManifestSource? _manifests;

    public InstallService(IDownloader downloader, ManifestSource? manifests = null)
    {
        _downloader = downloader;
        _manifests = manifests;
    }

    // ---------- manifest ----------

    /// <summary>
    /// Conditional, cached fetch (ETag / If-None-Match). Falls back to the cached manifest
    /// when the server is unreachable so a CDN outage never blocks playing.
    /// </summary>
    public Task<ManifestSource.Result> GetManifestAsync(string manifestUrl, CancellationToken ct) =>
        (_manifests ?? throw new InvalidOperationException("No manifest source configured.")).GetAsync(manifestUrl, ct);

    public async Task<ReleaseManifest> FetchManifestAsync(string manifestUrl, CancellationToken ct)
    {
        if (_manifests != null) return (await _manifests.GetAsync(manifestUrl, ct)).Manifest;
        return ManifestParser.Parse(await _downloader.GetStringAsync(HttpManifestFetcher.ToUri(manifestUrl), ct));
    }

    /// <summary>True when entry URLs in this manifest may be local (manifest came from disk).</summary>
    public static bool AllowsLocalPayloads(string manifestUrl)
    {
        try { return HttpManifestFetcher.ToUri(manifestUrl).IsFile; }
        catch { return false; }
    }

    public static bool NeedsUpdate(ReleaseManifest manifest, string? installedReleaseHash) =>
        !FileHasher.HashesEqual(manifest.ReleaseHash, installedReleaseHash);

    // ---------- verify / repair ----------

    public static async Task<VerifyReport> VerifyAsync(ReleaseManifest manifest, string installFolder,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var results = new List<EntryStatus>(manifest.Entries.Count);
        long totalBytes = manifest.Entries.Sum(e => e.Size), doneBytes = 0;
        var i = 0;
        // A full verify hashes everything, so it also refreshes the quick-verify fingerprints.
        var prints = FilePrints.Load(installFolder);

        // Decided once, before the loop: is this folder the kind of install the release was built
        // for? If not, its game content is left alone instead of being overwritten with files meant
        // for a different tree - see ForeignBaseline for what that cost us.
        var foreign = ForeignBaseline.Detect(manifest, installFolder);
        // Per-player key prompts in the locale are not damage (KeyPromptLocale): accepted while they
        // were made from exactly the bytes this release expects.
        var keyed = KeyedLocaleState.Load(installFolder);

        foreach (var entry in manifest.Entries)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            if (foreign is not null && ForeignBaseline.ShouldSkip(entry))
            {
                results.Add(new EntryStatus(entry, EntryVerdict.Ok, "skipped: " + foreign));
                doneBytes += entry.Size;
                continue;
            }
            progress?.Report(new InstallProgress("Verifying files", entry.Name,
                manifest.Entries.Count == 0 ? 1 : (double)i / manifest.Entries.Count, 0,
                doneBytes, totalBytes, 0, i, manifest.Entries.Count));

            if (!PathSafety.TryResolve(installFolder, entry.TargetPath, out var full, out var why))
            {
                results.Add(new EntryStatus(entry, EntryVerdict.Unsafe, why));
                continue;
            }
            if (PathSafety.HasReparsePointOnPath(installFolder, full))
            {
                results.Add(new EntryStatus(entry, EntryVerdict.Unsafe, "a symlink or junction is on that path"));
                continue;
            }

            if (entry.Kind == EntryKind.Delete)
            {
                results.Add(File.Exists(full)
                    ? new EntryStatus(entry, EntryVerdict.ShouldDelete, "file should not be present")
                    : new EntryStatus(entry, EntryVerdict.Ok, null));
                continue;
            }

            var info = new FileInfo(full);
            // A settings template counts as installed as soon as the file exists: the player is
            // meant to edit it, so its contents are theirs, not ours to check.
            if (entry.WriteIfAbsent && info.Exists)
            {
                results.Add(new EntryStatus(entry, EntryVerdict.Ok, null));
                doneBytes += entry.Size;
                continue;
            }

            // For an append patch the payload is the PATCH; what is on disk is the RESULT, so the
            // size and hash to compare against are the result's, not the patch's.
            var wantSize = entry.InstalledSize;
            var wantSha = entry.InstalledSha256;

            if (info.Exists && keyed.Accepts(entry.TargetPath, wantSha, full, hashAlways: true))
            {
                results.Add(new EntryStatus(entry, EntryVerdict.Ok, "player's key prompts"));
            }
            else if (!info.Exists)
            {
                results.Add(new EntryStatus(entry, EntryVerdict.Missing, "not installed"));
            }
            else if (wantSize > 0 && info.Length != wantSize)
            {
                results.Add(new EntryStatus(entry, EntryVerdict.WrongSize, $"{info.Length} bytes, expected {wantSize}"));
            }
            else
            {
                var hash = await FileHasher.HashFileAsync(full, null, ct);
                if (FileHasher.HashesEqual(hash, wantSha))
                {
                    results.Add(new EntryStatus(entry, EntryVerdict.Ok, null));
                    prints.Record(entry.TargetPath, full, wantSha);
                }
                else
                {
                    results.Add(new EntryStatus(entry, EntryVerdict.WrongHash, "checksum doesn't match"));
                    prints.Files.Remove(entry.TargetPath);
                }
            }

            doneBytes += entry.Size;
        }

        prints.ReleaseHash = manifest.ReleaseHash;
        prints.Save(installFolder);
        return new VerifyReport(results);
    }

    /// <summary>
    /// Downloads and installs everything the report flagged. Safe to call again after a
    /// cancel or a failure. Returns the number of files changed.
    /// </summary>
    public async Task<int> ApplyAsync(ReleaseManifest manifest, string manifestUrl, string installFolder,
        VerifyReport report, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (report.Unsafe.Any())
        {
            var first = report.Unsafe.First();
            throw new ManifestException($"Refusing to install: \"{first.Entry.Name}\" targets an unsafe path ({first.Detail}).");
        }

        // PRE-FLIGHT: refuse the whole release before writing anything if this install is not the
        // one it was built for.
        //
        // Why this is not optional: entries used to be applied one at a time, so a release aimed at
        // a stock Steam build would install several files into a DIFFERENT tree and only then hit a
        // patch whose base did not match. The owner's working ROTK-derived install was damaged
        // exactly that way on 2026-10-07 - our own pack is numbered 15 on a stock layout, that tree
        // already had a real 487 MB assets_x64_15.pack2, and it was overwritten with our 3 MB one.
        // The game then failed with "G21 - Failed to find SkinnedLODs.dx11efb". Nothing had warned
        // first, because the mismatch was only noticed later in the loop.
        //
        // An append patch states the exact size and hash of the file it applies to, so checking all
        // of them up front is a cheap and reliable "is this the right install" test.
        // Put the release's own locale bytes back first: an append patch must see the exact base it
        // was built for, and a keyed file is neither base nor result. Re-keyed at the next PLAY.
        try { KeyPromptLocale.RestoreAll(installFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Couldn't restore the locale files (is the game running? close it and try again): " + ex.Message, ex);
        }

        var mismatched = new List<string>();
        foreach (var e in report.Broken.Select(b => b.Entry).Where(e => e.Kind == EntryKind.Append))
        {
            var p = Path.Combine(installFolder, e.TargetPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(p)) { mismatched.Add($"{e.TargetPath} is missing"); continue; }
            var len = new FileInfo(p).Length;
            // The base size lives in the patch header; the manifest repeats it for exactly this check.
            if (e.BaseSize > 0 && len != e.BaseSize && len != e.ResultSize)
                mismatched.Add($"{e.TargetPath} is {len:N0} bytes, this release expects {e.BaseSize:N0}");
        }
        // Same guard for a file we ADD: our own pack is numbered for a stock layout (assets_x64_15
        // on a tree that ships 0..14). A ROTK-derived tree already has a real 487 MB pack at that
        // number, and installing ours over it deleted the game's shaders - the owner's install broke
        // that way twice on 2026-10-07 ("G21 - Failed to find SkinnedLODs.dx11efb"). If the target
        // exists and is nothing like what we are about to write, that number belongs to someone
        // else and this release is not for this folder.
        foreach (var e in report.Broken.Select(b => b.Entry).Where(e => e.Kind is EntryKind.Own or EntryKind.File))
        {
            var p = Path.Combine(installFolder, e.TargetPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(p)) continue;
            var len = new FileInfo(p).Length;
            if (e.Size > 0 && len > e.Size * 4 && len - e.Size > 50L * 1024 * 1024)
                mismatched.Add($"{e.TargetPath} already exists and is {len:N0} bytes, far larger than the "
                             + $"{e.Size:N0} this release would write - that file belongs to another install");
        }
        if (mismatched.Count > 0)
            throw new ManifestException(
                "This ZRevive release was built for a clean Z1 Battle Royale install, and this folder is not one:\n  "
                + string.Join("\n  ", mismatched)
                + "\n\nNothing was changed. Re-run setup to build ZRevive from your own Steam copy.");

        var work = report.Broken.ToList();
        var staging = Path.Combine(installFolder, StagingDir);
        Directory.CreateDirectory(staging);
        var allowLocal = AllowsLocalPayloads(manifestUrl);
        var prints = FilePrints.Load(installFolder);

        long totalBytes = work.Where(w => w.Entry.Kind != EntryKind.Delete).Sum(w => w.Entry.Size);
        long doneBytes = 0;
        var changed = 0;
        var index = 0;

        foreach (var item in work)
        {
            ct.ThrowIfCancellationRequested();
            var entry = item.Entry;
            index++;

            // Re-check the path right before writing; never trust the earlier pass.
            if (!PathSafety.TryResolve(installFolder, entry.TargetPath, out var full, out var why))
                throw new ManifestException($"Refusing to install \"{entry.Name}\": {why}.");
            if (PathSafety.HasReparsePointOnPath(installFolder, full))
                throw new ManifestException($"Refusing to install \"{entry.Name}\": a symlink or junction is on that path.");

            if (entry.Kind == EntryKind.Delete)
            {
                try { if (File.Exists(full)) { File.SetAttributes(full, FileAttributes.Normal); File.Delete(full); changed++; } }
                catch (Exception ex) { throw new IOException($"Couldn't remove {entry.Name}: {ex.Message}", ex); }
                prints.Files.Remove(entry.TargetPath);
                continue;
            }

            // A "patch" entry declares the base file it is built against. If the local file
            // is neither the stock base nor the finished file, say so instead of clobbering.
            if (entry.Kind == EntryKind.Patch && entry.BaseSha256 != null && File.Exists(full))
            {
                var local = await FileHasher.HashFileAsync(full, null, ct);
                if (!FileHasher.HashesEqual(local, entry.BaseSha256) && !FileHasher.HashesEqual(local, entry.Sha256))
                    throw new IOException(
                        $"{entry.Name} isn't the stock game file this release patches. Re-run setup to rebuild the ZRevive folder from your Steam copy.");
            }

            var staged = Path.Combine(staging, Sanitise(entry.TargetPath));

            void Report(string stage, double fileFraction, double speed, long extra) =>
                progress?.Report(new InstallProgress(stage, entry.Name,
                    totalBytes == 0 ? 1 : Math.Clamp((doneBytes + extra) / (double)totalBytes, 0, 1),
                    fileFraction, doneBytes + extra, totalBytes, speed, index, work.Count));

            Report("Downloading update", 0, 0, 0);
            if (entry.IsSplit)
            {
                // Too large for one download (a GitHub release asset caps at 2 GiB, and one of our
                // packs is 2.5 GB). Each part is verified as it lands, so a bad piece is caught
                // before gigabytes are joined, and the joined file is then verified as a whole by
                // the same check every other entry gets.
                var partFiles = new List<string>();
                long carried = 0;
                try
                {
                    var n = 0;
                    foreach (var part in entry.Parts!)
                    {
                        n++;
                        var partPath = staged + $".part{n - 1}";
                        partFiles.Add(partPath);
                        var partUrl = ManifestParser.ResolveOne(manifest, part.Url, entry.Name, manifestUrl, allowLocal);
                        var before = carried;
                        await _downloader.DownloadAsync(partUrl, partPath, part.Size,
                            new Progress<DownloadProgress>(p => Report(
                                $"Downloading update (part {n} of {entry.Parts!.Count})",
                                entry.Size <= 0 ? 0 : Math.Clamp((before + p.Done) / (double)entry.Size, 0, 1),
                                p.BytesPerSecond, before + p.Done)),
                            ct);

                        if (!await FileHasher.VerifyAsync(partPath, part.Sha256, part.Size, null, ct))
                            throw new IOException(
                                $"{entry.Name}: part {n} of {entry.Parts!.Count} failed its checksum. " +
                                "The download was discarded; try Verify / repair again.");
                        carried += part.Size;
                    }

                    Report("Joining download", 1, 0, entry.Size);
                    await using (var dst = new FileStream(staged, FileMode.Create, FileAccess.Write,
                                     FileShare.None, 1 << 20, FileOptions.Asynchronous))
                        foreach (var p in partFiles)
                        {
                            await using var src = new FileStream(p, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await src.CopyToAsync(dst, 1 << 20, ct);
                        }
                }
                finally
                {
                    // The parts have served their purpose either way; a failed install should not
                    // leave gigabytes of them behind in staging.
                    foreach (var p in partFiles)
                        try { if (File.Exists(p)) File.Delete(p); } catch { }
                }
            }
            else
            {
                var url = ManifestParser.ResolveUrl(manifest, entry, manifestUrl, allowLocal);
                await _downloader.DownloadAsync(url, staged, entry.Size,
                    new Progress<DownloadProgress>(p => Report("Downloading update",
                        p.Total <= 0 ? 0 : Math.Clamp(p.Done / (double)p.Total, 0, 1), p.BytesPerSecond, p.Done)),
                    ct);
            }

            Report("Checking download", 1, 0, entry.Size);
            if (!await FileHasher.VerifyAsync(staged, entry.Sha256!, entry.Size, null, ct))
            {
                try { File.Delete(staged); } catch { }
                throw new IOException($"{entry.Name} failed its checksum. The download was discarded; try Verify / repair again.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            try { if (File.Exists(full)) File.SetAttributes(full, FileAttributes.Normal); } catch { }

            if (entry.Kind == EntryKind.Append)
            {
                // The payload is a patch, not the file. Turn the player's own stock file into the
                // ZRevive one: ZrPatch verifies the base before it writes anything and verifies the
                // result before it replaces the original, so a wrong or already-patched base is
                // reported rather than corrupted.
                if (!File.Exists(full))
                    throw new IOException(
                        $"{entry.Name}: {entry.TargetPath} is missing, so there is nothing to patch. " +
                        "Re-run setup to rebuild the ZRevive folder from your Steam copy.");
                Report("Patching game file", 1, 0, entry.Size);
                try { ZrPatch.Apply(full, staged, full, ct); }
                catch (ZrPatchException ex) { throw new IOException($"{entry.Name}: {ex.Message}", ex); }
                finally { try { File.Delete(staged); } catch { } }
            }
            else if (entry.WriteIfAbsent && File.Exists(full))
            {
                // A settings template the player already has: keep theirs.
                try { File.Delete(staged); } catch { }
            }
            else
            {
                // Move over the top rather than writing in place: if this file is hardlinked to
                // another install, only our copy changes.
                File.Move(staged, full, overwrite: true);
            }
            // Fingerprint it now so the next launch's quick verify costs one stat() call.
            prints.Record(entry.TargetPath, full, entry.InstalledSha256);

            doneBytes += entry.Size;
            changed++;
        }

        prints.ReleaseHash = manifest.ReleaseHash;
        prints.Save(installFolder);

        try { if (Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging); }
        catch { }

        return changed;
    }

    /// <summary>Flattens a relative target path into a single staging file name.</summary>
    public static string Sanitise(string targetPath)
    {
        var s = targetPath.Replace('\\', '_').Replace('/', '_');
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 120 ? s[^120..] : s;
    }
}
