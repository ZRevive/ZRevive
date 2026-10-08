using System.Text.Json;

namespace ZRevive.Launcher.Install;

public sealed class ManifestException : Exception
{
    public ManifestException(string message) : base(message) { }
}

public enum EntryKind
{
    /// <summary>Whole file: download <see cref="ManifestEntry.Url"/>, verify sha256, place at TargetPath.</summary>
    File,

    /// <summary>
    /// Same payload as <see cref="File"/> but the entry also declares the sha256 of the
    /// *base* file it replaces (<see cref="ManifestEntry.BaseSha256"/>). Lets the builder
    /// mark "this only applies on top of the stock Z1BR file" and lets us skip or warn
    /// instead of silently clobbering a differently-modded file.
    /// </summary>
    Patch,

    /// <summary>Remove TargetPath if present (base file that must not exist on ZRevive).</summary>
    Delete,

    /// <summary>
    /// A file we authored outright (our own pack2). Installed exactly like <see cref="File"/>;
    /// kept as its own kind so the manifest still says where the bytes came from.
    /// </summary>
    Own,

    /// <summary>
    /// A settings template, written only when the target is missing, so a player's own settings
    /// survive an update. <see cref="ManifestEntry.WriteIfAbsent"/> carries that flag.
    /// </summary>
    Config,

    /// <summary>
    /// A ZRPATCH append patch (<see cref="ZrPatch"/>): a few MB that turn the player's own stock
    /// pack2 into the ZRevive one. This replaces the old "recipe" kind, which shipped Python
    /// scripts to run on the player's machine - impossible, since players have no Python.
    /// </summary>
    Append
}

/// <summary>
/// One piece of a payload that is too large to ship as a single file. Each part carries its own
/// size and hash so a bad piece is caught as it lands, rather than only showing up as a wrong
/// hash on the joined file after gigabytes have been written.
/// </summary>
public sealed record ManifestPart(string Url, long Size, string Sha256);

public sealed record ManifestEntry(
    string Name,
    string? Url,
    string TargetPath,
    long Size,
    string? Sha256,
    EntryKind Kind,
    string? BaseSha256,
    string? PatchFormat,
    /// <summary>
    /// Hashed on every launch by the quick verify, not just when size/mtime look off.
    /// For the handful of files that matter (scripts, configs, anything an anti-cheat or a
    /// mod manager might rewrite in place without changing the size).
    /// </summary>
    bool Critical = false,

    /// <summary>Write the file only when the target is missing (settings templates).</summary>
    bool WriteIfAbsent = false,

    /// <summary>
    /// For <see cref="EntryKind.Append"/>: sha256 of the file AFTER the patch is applied. The
    /// payload's own <see cref="Sha256"/> is the patch file, not the result, so verification of an
    /// installed game file has to use this one.
    /// </summary>
    string? ResultSha256 = null,

    /// <inheritdoc cref="ResultSha256"/>
    long ResultSize = 0,

    /// <summary>
    /// For <see cref="EntryKind.Append"/>: the size the target file must have before the patch is
    /// applied. Checked up front, so a release meant for a different install is refused before a
    /// single file is written.
    /// </summary>
    long BaseSize = 0,

    /// <summary>
    /// Set when the payload is too large to be a single download and ships as several pieces.
    /// A GitHub release asset caps at 2 GiB and one of our packs is 2.5 GB, so that file - and
    /// only that file - arrives as parts which the launcher joins back together.
    ///
    /// When this is set, <see cref="Url"/> is unused: the parts ARE the payload, in list order.
    /// <see cref="Size"/> and <see cref="Sha256"/> still describe the FINISHED file, so the join
    /// is verified exactly like a single download would be.
    /// </summary>
    IReadOnlyList<ManifestPart>? Parts = null)
{
    /// <summary>True when this payload arrives in pieces rather than as one download.</summary>
    public bool IsSplit => Parts is { Count: > 0 };

    /// <summary>What an installed file must hash to: the patch result where there is one.</summary>
    public string? InstalledSha256 => Kind == EntryKind.Append ? ResultSha256 : Sha256;

    /// <summary>What an installed file must measure.</summary>
    public long InstalledSize => Kind == EntryKind.Append ? ResultSize : Size;
}

public sealed record ReleaseManifest(
    int Version,
    string ReleaseHash,
    string? GameVersion,
    string? BaseUrl,
    string? Notes,
    IReadOnlyList<ManifestEntry> Entries,
    /// <summary>
    /// The oldest launcher this release may be applied by, e.g. "0.2.3". Older launchers can be
    /// missing a fix the release depends on, or not understand an entry kind, so they must update
    /// first. Null means "any launcher".
    /// </summary>
    string? RequiredLauncher = null);

/// <summary>
/// Parsing is deliberately isolated in this one class: the manifest format is still being
/// defined in deploy/assets/ by another pass, so only this file should need to change.
///
/// Expected shape (unknown fields are ignored):
/// {
///   "version": 1,
///   "releaseHash": "&lt;hex&gt;",
///   "gameVersion": "1.0.326.439939",
///   "baseUrl": "https://cdn.example/zrevive/r1/",
///   "notes": "optional",
///   "entries": [
///     { "name": "assets_x64_0.pack2", "url": "assets_x64_0.pack2",
///       "targetPath": "Resources/Assets/assets_x64_0.pack2",
///       "size": 123, "sha256": "&lt;64 hex&gt;", "kind": "file" },
///     { "name": "...", "kind": "patch", "baseSha256": "&lt;64 hex&gt;", ... },
///     { "name": "steam_api64.dll", "targetPath": "steam_api64.dll", "kind": "delete" }
///   ]
/// }
/// </summary>
public static class ManifestParser
{
    // 2 = the format deploy/assets/build_delta.py produces: own / config / append entries, where
    // append is a ZRPATCH binary patch. Version 1 was file / patch / delete only, so a v2 release
    // was refused outright with "this manifest needs a newer launcher" (players, 2026-10-07).
    public const int MaxSupportedVersion = 2;

    /// <summary>
    /// Reads ONLY <c>requiredLauncher</c>, without validating anything else.
    /// <para>This exists because of a trap we walked into: a launcher that is too old refuses the
    /// manifest on its <c>version</c> field and throws before it ever reads the field that tells it
    /// to update. The one message an out-of-date launcher most needs was locked inside a file only
    /// an up-to-date launcher could open, so players were never offered the update that would have
    /// fixed them (live, 2026-10-07). Returns null when the value is missing or the JSON is
    /// unreadable - never throws, because the caller is on the "something is already wrong" path.</para>
    /// </summary>
    public static string? PeekRequiredLauncher(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return GetString(doc.RootElement, "requiredLauncher");
        }
        catch { return null; }
    }

    public static ReleaseManifest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ManifestException("The manifest is empty.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex) { throw new ManifestException("The manifest isn't valid JSON: " + ex.Message); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ManifestException("The manifest must be a JSON object.");

            var version = GetInt(root, "version") ?? throw new ManifestException("The manifest has no \"version\".");
            if (version < 1) throw new ManifestException($"Bad manifest version {version}.");
            if (version > MaxSupportedVersion)
                throw new ManifestException($"This manifest needs a newer launcher (manifest version {version}, this launcher understands {MaxSupportedVersion}).");

            var releaseHash = GetString(root, "releaseHash") ?? throw new ManifestException("The manifest has no \"releaseHash\".");
            if (!IsHex(releaseHash, 8, 128)) throw new ManifestException("\"releaseHash\" must be hex.");

            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new ManifestException("The manifest has no \"entries\" array.");

            var list = new List<ManifestEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            foreach (var e in entries.EnumerateArray())
            {
                var where = $"entry #{i++}";
                if (e.ValueKind != JsonValueKind.Object) throw new ManifestException($"{where} is not an object.");

                var target = GetString(e, "targetPath") ?? throw new ManifestException($"{where} has no \"targetPath\".");
                target = target.Replace('/', '\\');   // normalise first so "a/x" and "a\x" can't both pass
                var name = GetString(e, "name") ?? target;
                var kind = ParseKind(GetString(e, "kind"), where);

                // Shape-level path check. The real check happens again against the live
                // install root in PathSafety.TryResolve before anything is written.
                if (!PathSafety.TryResolve(@"C:\zrevive-probe", target, out _, out var pathError))
                    throw new ManifestException($"{where} (\"{name}\") has an unsafe targetPath: {pathError}.");
                if (!seen.Add(target))
                    throw new ManifestException($"{where} (\"{name}\") repeats targetPath \"{target}\".");

                string? sha = null, url = null, baseSha = null, patchFormat = null;
                long size = 0;

                if (kind != EntryKind.Delete)
                {
                    sha = GetString(e, "sha256") ?? throw new ManifestException($"{where} (\"{name}\") has no \"sha256\".");
                    if (!IsHex(sha, 64, 64)) throw new ManifestException($"{where} (\"{name}\") has a \"sha256\" that isn't 64 hex characters.");
                    sha = sha.ToLowerInvariant();

                    // A split payload has no single url - its "parts" carry one each - so the url
                    // is required only when there is no part list to supply the bytes.
                    var hasParts = e.TryGetProperty("parts", out var pv) && pv.ValueKind == JsonValueKind.Array;
                    url = GetString(e, "url");
                    if (url == null && !hasParts)
                        throw new ManifestException($"{where} (\"{name}\") has no \"url\".");
                    size = GetLong(e, "size") ?? throw new ManifestException($"{where} (\"{name}\") has no \"size\".");
                    if (size < 0) throw new ManifestException($"{where} (\"{name}\") has a negative \"size\".");
                }

                if (kind is EntryKind.Patch or EntryKind.Append)
                {
                    baseSha = GetString(e, "baseSha256");
                    if (baseSha != null)
                    {
                        if (!IsHex(baseSha, 64, 64)) throw new ManifestException($"{where} (\"{name}\") has a \"baseSha256\" that isn't 64 hex characters.");
                        baseSha = baseSha.ToLowerInvariant();
                    }
                    // An append entry carries a ZRPATCH file; the patch's own header repeats the base
                    // and result hashes, and ZrPatch refuses to apply one that does not match, so the
                    // manifest's copy is a convenience rather than the thing that keeps us safe.
                    patchFormat = GetString(e, "patchFormat") ?? (kind == EntryKind.Append ? "zrappend" : "full");
                    var allowed = kind == EntryKind.Append ? new[] { "zrappend" } : new[] { "full", "copy" };
                    if (!allowed.Contains(patchFormat))
                        throw new ManifestException($"{where} (\"{name}\") uses patchFormat \"{patchFormat}\", which this launcher can't apply. Rebuild the release with full-file entries.");
                }

                var critical = e.TryGetProperty("critical", out var crit) && crit.ValueKind == JsonValueKind.True;
                // Settings templates must not overwrite what the player has changed.
                var writeIfAbsent = kind == EntryKind.Config
                    || (e.TryGetProperty("writeIfAbsent", out var wia) && wia.ValueKind == JsonValueKind.True);
                var resultSha = GetString(e, "resultSha256")?.ToLowerInvariant();

                // A split payload: "parts" replaces "url" as the source of the bytes. Validated
                // strictly, because a part list that does not add up to the declared size would
                // otherwise only fail after gigabytes had been downloaded and joined.
                List<ManifestPart>? parts = null;
                if (e.TryGetProperty("parts", out var partsEl) && partsEl.ValueKind == JsonValueKind.Array)
                {
                    parts = new List<ManifestPart>();
                    var n = 0;
                    foreach (var p in partsEl.EnumerateArray())
                    {
                        n++;
                        var pUrl = GetString(p, "url")
                            ?? throw new ManifestException($"{where} (\"{name}\") part {n} has no \"url\".");
                        var pSize = GetLong(p, "size")
                            ?? throw new ManifestException($"{where} (\"{name}\") part {n} has no \"size\".");
                        if (pSize <= 0)
                            throw new ManifestException($"{where} (\"{name}\") part {n} has a non-positive \"size\".");
                        var pSha = GetString(p, "sha256")
                            ?? throw new ManifestException($"{where} (\"{name}\") part {n} has no \"sha256\".");
                        if (!IsHex(pSha, 64, 64))
                            throw new ManifestException($"{where} (\"{name}\") part {n} has a \"sha256\" that isn't 64 hex characters.");
                        parts.Add(new ManifestPart(pUrl, pSize, pSha.ToLowerInvariant()));
                    }
                    if (parts.Count == 0)
                        throw new ManifestException($"{where} (\"{name}\") has an empty \"parts\" list.");
                    var sum = parts.Sum(p => p.Size);
                    if (sum != size)
                        throw new ManifestException(
                            $"{where} (\"{name}\") has parts totalling {sum:N0} bytes but declares a size of {size:N0}.");
                }

                list.Add(new ManifestEntry(name, url, target, size, sha, kind, baseSha, patchFormat, critical,
                    writeIfAbsent, resultSha, GetLong(e, "resultSize") ?? 0, GetLong(e, "baseSize") ?? 0, parts));
            }

            if (list.Count == 0) throw new ManifestException("The manifest has no entries.");

            return new ReleaseManifest(
                version,
                releaseHash.ToLowerInvariant(),
                GetString(root, "gameVersion"),
                GetString(root, "baseUrl"),
                GetString(root, "notes"),
                list,
                GetString(root, "requiredLauncher"));
        }
    }

    /// <summary>
    /// Entry URLs may be relative to the manifest's baseUrl (or to the manifest URL itself).
    /// Only http/https is accepted, so a manifest can't point us at file:// or a UNC share.
    /// </summary>
    public static Uri ResolveUrl(ReleaseManifest manifest, ManifestEntry entry, string manifestUrl, bool allowLocal = false)
    {
        if (entry.Url == null) throw new ManifestException($"\"{entry.Name}\" has no url.");
        return ResolveOne(manifest, entry.Url, entry.Name, manifestUrl, allowLocal);
    }

    /// <summary>
    /// Resolves one payload URL. Split entries have a url per part rather than one for the entry,
    /// so the rules that keep a manifest from pointing us somewhere dangerous live here, where
    /// both paths go through them.
    /// </summary>
    public static Uri ResolveOne(ReleaseManifest manifest, string url, string name, string manifestUrl,
        bool allowLocal = false)
    {
        var entry = new ManifestEntry(name, url, name, 0, null, EntryKind.File, null, null);

        Uri? baseUri = null;
        if (!string.IsNullOrWhiteSpace(manifest.BaseUrl)) Uri.TryCreate(manifest.BaseUrl, UriKind.Absolute, out baseUri);
        if (baseUri == null) Uri.TryCreate(manifestUrl, UriKind.Absolute, out baseUri);

        Uri? result;
        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out result) &&
            !(baseUri != null && Uri.TryCreate(baseUri, entry.Url, out result)))
            throw new ManifestException($"\"{entry.Name}\" has an unusable url \"{entry.Url}\".");

        // file:// is only ever allowed when the manifest itself was loaded from a local file
        // (local testing / an offline mirror); a remote manifest can never point at the disk.
        var okScheme = result!.Scheme == Uri.UriSchemeHttp || result.Scheme == Uri.UriSchemeHttps
                       || (allowLocal && result.IsFile);
        if (!okScheme)
            throw new ManifestException($"\"{entry.Name}\" uses url scheme \"{result.Scheme}\"; only http and https are allowed.");
        if (result.IsUnc)
            throw new ManifestException($"\"{entry.Name}\" points at a network share, which isn't allowed.");

        return result;
    }

    static EntryKind ParseKind(string? kind, string where) => (kind ?? "file").ToLowerInvariant() switch
    {
        "file" or "" => EntryKind.File,
        "patch" => EntryKind.Patch,
        "delete" or "remove" => EntryKind.Delete,
        "own" => EntryKind.Own,
        "config" => EntryKind.Config,
        "append" => EntryKind.Append,
        _ => throw new ManifestException($"{where} has an unknown kind \"{kind}\".")
    };

    public static bool IsHex(string? s, int min, int max)
    {
        if (s == null || s.Length < min || s.Length > max) return false;
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }

    static string? GetString(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int? GetInt(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    static long? GetLong(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
