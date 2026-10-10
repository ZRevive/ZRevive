using System.IO;

namespace ZRevive.Launcher.Install;

/// <summary>
/// Decides whether a game folder is the kind of install the current release was built for.
///
/// ZRevive releases are built against a CLEAN Z1 Battle Royale install from Steam: our own pack is
/// numbered for that layout (assets_x64_15 on a tree that ships 0..14), and our locale is derived
/// from that tree's string table. A folder that came from somewhere else - the owner's development
/// install, forked from the ROTK client - has a completely different asset layout and a different,
/// LARGER string table.
///
/// Applying a stock-built release to such a tree is not a partial success, it is damage. It happened
/// twice on 2026-10-07 and again on 2026-10-08:
///   * our 3 MB assets_x64_15.pack2 overwrote a real 487 MB Daybreak pack, which took the game's
///     shaders with it ("G21 - Failed to find SkinnedLODs.dx11efb"), and
///   * our 8,268-string locale replaced a 14,001-string one, so the UI asked for IDs that no longer
///     existed ("Error: String ID 18085 Not Found!").
/// Both files had to be restored by hand from the ROTK tree.
///
/// <see cref="InstallService"/> already REFUSES such an install, which stops the damage but leaves
/// the launcher stuck offering an UPDATE that can never succeed. This class is the other half: when
/// the folder is recognised as foreign, the entries that only make sense on a stock tree are treated
/// as already satisfied, so verification passes and the player can simply PLAY. Their game content
/// is then theirs to manage - which is correct, because on a development install it is built
/// locally anyway.
///
/// The tests below are deliberately the SAME ones <see cref="InstallService"/> uses to refuse, so
/// the two can never disagree about whether a folder is foreign.
/// </summary>
public static class ForeignBaseline
{
    /// <summary>
    /// A file is "ours to manage" only if it is game content. Everything else in a release - the
    /// client config template, the scripts we ship - applies to any tree and is still installed.
    /// </summary>
    static bool IsGameContent(string targetPath)
    {
        var p = targetPath.Replace('\\', '/');
        return p.StartsWith("Resources/Assets/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("Locale/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why this folder is not the install the release targets, or null when it is. The reason is
    /// shown to the player and written to the launcher log, so it names the file and both sizes.
    /// </summary>
    public static string? Detect(ReleaseManifest manifest, string installFolder)
    {
        // A locale carrying the player's key prompts measures differently from the release; size it
        // as the release's file it was made from, or a rebind would read as a foreign install.
        var keyed = KeyedLocaleState.Load(installFolder);
        foreach (var e in manifest.Entries)
        {
            if (!IsGameContent(e.TargetPath)) continue;

            var p = Path.Combine(installFolder, e.TargetPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(p)) continue;

            long len;
            try { len = keyed.BaseLength(e.TargetPath, new FileInfo(p).Length); }
            catch { continue; }

            // An append patch states the exact size of the file it applies to. A size that is
            // neither the base nor the already-patched result means a different file entirely.
            if (e.Kind == EntryKind.Append && e.BaseSize > 0 && len != e.BaseSize && len != e.ResultSize)
                return $"{e.TargetPath} is {len:N0} bytes; this release was built for one of {e.BaseSize:N0}";

            // A file we ADD that is already there and vastly bigger is somebody else's file at the
            // same name - our pack number colliding with a real Daybreak pack.
            if (e.Kind is EntryKind.Own or EntryKind.File
                && e.Size > 0 && len > e.Size * 4 && len - e.Size > 50L * 1024 * 1024)
                return $"{e.TargetPath} is {len:N0} bytes, far larger than the {e.Size:N0} this release "
                     + "would write; that file belongs to another install";
        }
        return null;
    }

    /// <summary>
    /// True when this entry must be left alone because the folder is foreign. Content entries are
    /// skipped wholesale rather than individually: our UI and our locale are a matched pair, and
    /// installing one without the other is what produced the missing-string-ID errors.
    /// </summary>
    public static bool ShouldSkip(ManifestEntry entry) => IsGameContent(entry.TargetPath);
}
