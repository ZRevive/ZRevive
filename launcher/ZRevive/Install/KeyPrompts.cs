using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ZRevive.Launcher.Install;

/// <summary>
/// The player's Interact binding, read the way the 1.0.326 client stores it.
/// <para><b>Where it lives.</b> In the GAME FOLDER, not in Documents or AppData: the client writes
/// every rebind to <c>InputProfile_User.xml</c> next to H1Z1.exe, and falls back to the shipped
/// <c>InputProfile_Default.xml</c> for an action the user file does not list. The action is
/// <c>Interact</c> (displayName <c>UI.Interact</c> / <c>UI.ExitVehicle</c>) and it appears once per
/// action set; on foot - pick up, doors, get in a car - it is the <c>Infantry</c> set. The default
/// is <c>E</c> plus a gamepad button.</para>
/// <para><b>Why the server does not care.</b> The key never reaches the wire: pressing it makes
/// the client send InteractRequest 09 07 / InteractionSelect 09 0C / PlayerSelect 09 16 (loot and
/// cars, WorldObjectPackets.TryParseInteraction) or RequestToggleDoorState 0F 52 (doors), each
/// naming only the target's guid. So a rebind already works in game; only the prompt TEXT is
/// wrong, because the client never substitutes <c>[*key*]</c> (re\loot_prompt_strings.py).</para>
/// </summary>
public static class InteractBinding
{
    public const string ActionSet = "Infantry";
    public const string Action = "Interact";

    /// <summary>The first keyboard/mouse trigger bound to Interact on foot, or null when unbound.</summary>
    public static string? ReadTrigger(string gameFolder)
    {
        foreach (var file in new[] { "InputProfile_User.xml", "InputProfile_Default.xml" })
        {
            var found = ReadFrom(Path.Combine(gameFolder, file), out var trigger);
            if (found) return trigger;     // listed in this file: that IS the binding, even if empty
        }
        return null;
    }

    /// <summary>True when the file lists the action (then <paramref name="trigger"/> is its key, or null if unbound).</summary>
    public static bool ReadFrom(string path, out string? trigger)
    {
        trigger = null;
        try
        {
            if (!File.Exists(path)) return false;
            var doc = XDocument.Parse(File.ReadAllText(path));
            var action = doc.Root?.Elements("ActionSet")
                .FirstOrDefault(s => (string?)s.Attribute("name") == ActionSet)?
                .Elements("Action")
                .FirstOrDefault(a => (string?)a.Attribute("name") == Action);
            if (action == null) return false;
            trigger = action.Elements("Trigger")
                .Select(t => t.Value.Trim())
                .FirstOrDefault(t => t.Length > 0 && !t.StartsWith("Gamepad", StringComparison.OrdinalIgnoreCase));
            return true;
        }
        catch { return false; }   // a half-written profile must never stop a launch
    }

    /// <summary>"E" -> "E", "Mouse_3" -> "MOUSE4", "Control+G" -> "CTRL+G", "MouseWheelUp" -> "WHEEL UP".</summary>
    public static string Label(string trigger) =>
        string.Join("+", trigger.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(KeyName));

    static string KeyName(string k)
    {
        k = k.Trim();
        var m = Regex.Match(k, @"^Mouse_(\d+)$", RegexOptions.IgnoreCase);
        if (m.Success) return "MOUSE" + (int.Parse(m.Groups[1].Value) + 1);   // Mouse_0 is the left button
        m = Regex.Match(k, @"^KP_(.+)$", RegexOptions.IgnoreCase);
        if (m.Success) return "NUM " + KeyName(m.Groups[1].Value);
        return k.ToLowerInvariant() switch
        {
            "control" or "control_left" or "control_right" => "CTRL",
            "alt" or "alt_left" or "alt_right" => "ALT",
            "shift" or "shift_left" or "shift_right" => "SHIFT",
            "mousewheelup" or "mousewheel+" => "WHEEL UP",
            "mousewheeldown" or "mousewheel-" => "WHEEL DOWN",
            "return" => "ENTER",
            "escape" => "ESC",
            "add" => "+",
            "subtract" => "-",
            "pageup" => "PG UP",
            "pagedown" => "PG DN",
            "bracket_left" => "[",       // a bracket would close the "[KEY]" early; see SafeLabel
            "bracket_right" => "]",
            "tilde" => "~",
            "minus" => "-",
            "equals" => "=",
            "period" => ".",
            _ => k.Replace('_', ' ').ToUpperInvariant()
        };
    }

    /// <summary>
    /// The label as it goes between the prompt's square brackets: brackets inside it are swapped for
    /// their round form and the length is capped, so the result always matches the prefix pattern
    /// the next launch looks for (otherwise a "[" bind would make the prefix unrecognisable).
    /// </summary>
    public static string SafeLabel(string label)
    {
        var s = label.Replace('[', '(').Replace(']', ')').Replace("\r", "").Replace("\n", "").Replace("\t", " ").Trim();
        return s.Length == 0 ? "?" : s.Length > KeyPromptLocale.MaxLabel ? s[..KeyPromptLocale.MaxLabel] : s;
    }
}

/// <summary>Forgelight's locale hash (port of re\fl_locale.py lookup2 / string_key).</summary>
public static class LocaleHash
{
    public static uint StringKey(long stringId) => Lookup2(Encoding.ASCII.GetBytes("Global.Text." + stringId));

    /// <summary>Bob Jenkins lookup2, signed-char variant (bytes over 127 are sign-extended).</summary>
    public static uint Lookup2(ReadOnlySpan<byte> bytes, uint initval = 0)
    {
        uint a = 0x9E3779B9, b = 0x9E3779B9, c = initval;
        var k = bytes.ToArray();
        int i = 0, n = k.Length;
        uint S(int at) => at < k.Length ? (uint)(int)(sbyte)k[at] : 0u;
        while (n >= 12)
        {
            a += S(i) + (S(i + 1) << 8) + (S(i + 2) << 16) + (S(i + 3) << 24);
            b += S(i + 4) + (S(i + 5) << 8) + (S(i + 6) << 16) + (S(i + 7) << 24);
            c += S(i + 8) + (S(i + 9) << 8) + (S(i + 10) << 16) + (S(i + 11) << 24);
            Mix(ref a, ref b, ref c);
            i += 12; n -= 12;
        }
        c += (uint)k.Length;
        if (n >= 11) c += S(i + 10) << 24;
        if (n >= 10) c += S(i + 9) << 16;
        if (n >= 9) c += S(i + 8) << 8;
        if (n >= 8) b += S(i + 7) << 24;
        if (n >= 7) b += S(i + 6) << 16;
        if (n >= 6) b += S(i + 5) << 8;
        if (n >= 5) b += S(i + 4);
        if (n >= 4) a += S(i + 3) << 24;
        if (n >= 3) a += S(i + 2) << 16;
        if (n >= 2) a += S(i + 1) << 8;
        if (n >= 1) a += S(i);
        Mix(ref a, ref b, ref c);
        return c;
    }

    static void Mix(ref uint a, ref uint b, ref uint c)
    {
        a -= b; a -= c; a ^= c >> 13;
        b -= c; b -= a; b ^= a << 8;
        c -= a; c -= b; c ^= b >> 13;
        a -= b; a -= c; a ^= c >> 12;
        b -= c; b -= a; b ^= a << 16;
        c -= a; c -= b; c ^= b >> 5;
        a -= b; a -= c; a ^= c >> 3;
        b -= c; b -= a; b ^= a << 10;
        c -= a; c -= b; c ^= b >> 15;
    }
}

/// <summary>One locale file we rewrote: what the release installed (base) and what we put there (keyed).</summary>
public sealed class KeyedFile
{
    [JsonPropertyName("baseSha256")] public string BaseSha256 { get; set; } = "";
    [JsonPropertyName("baseSize")] public long BaseSize { get; set; }
    [JsonPropertyName("keyedSha256")] public string KeyedSha256 { get; set; } = "";
    [JsonPropertyName("keyedSize")] public long KeyedSize { get; set; }
    [JsonPropertyName("keyedMtime")] public long KeyedMTimeTicks { get; set; }
    [JsonPropertyName("key")] public string Key { get; set; } = "";
}

/// <summary>
/// The record of which installed locale files carry the player's key instead of the release's
/// bytes, kept in <c>&lt;game&gt;\.zrevive-keyprompts\state.json</c> next to byte-exact backups of
/// the release's files. Verify consults it so a per-user locale is never "corrupt" (see
/// <see cref="Accepts"/>), and an update restores the backups before it writes anything.
/// </summary>
public sealed class KeyedLocaleState
{
    public const string DirName = ".zrevive-keyprompts";

    [JsonPropertyName("files")]
    public Dictionary<string, KeyedFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string DirFor(string folder) => Path.Combine(folder, DirName);
    public static string BackupFor(string folder, string targetPath) => Path.Combine(DirFor(folder), Norm(targetPath));
    static string Norm(string targetPath) => targetPath.Replace('/', '\\');

    public static KeyedLocaleState Load(string folder)
    {
        try
        {
            var p = Path.Combine(DirFor(folder), "state.json");
            if (File.Exists(p))
                return JsonSerializer.Deserialize<KeyedLocaleState>(File.ReadAllText(p)) is { } s
                    ? new KeyedLocaleState { Files = new(s.Files, StringComparer.OrdinalIgnoreCase) }
                    : new KeyedLocaleState();
        }
        catch { }
        return new KeyedLocaleState();
    }

    public void Save(string folder)
    {
        var dir = DirFor(folder);
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "state.json");
        KeyPromptLocale.WriteReplacing(p, JsonSerializer.SerializeToUtf8Bytes(this));
    }

    public KeyedFile? Get(string targetPath) => Files.TryGetValue(Norm(targetPath), out var f) ? f : null;

    /// <summary>
    /// True when the file on disk is the keyed copy we made FROM the bytes this release expects.
    /// A release that changes the locale changes <paramref name="expectedSha"/>, so the old keyed copy
    /// is then (correctly) not accepted, gets replaced by the update, and is re-keyed at the next PLAY.
    /// </summary>
    public bool Accepts(string targetPath, string? expectedSha, string fullPath, bool hashAlways = false)
    {
        var rec = Get(targetPath);
        if (rec == null || expectedSha == null || !FileHasher.HashesEqual(rec.BaseSha256, expectedSha)) return false;
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists || fi.Length != rec.KeyedSize) return false;
            if (!hashAlways && fi.LastWriteTimeUtc.Ticks == rec.KeyedMTimeTicks) return true;
            using var fs = File.OpenRead(fullPath);
            return FileHasher.HashesEqual(Convert.ToHexString(SHA256.HashData(fs)), rec.KeyedSha256);
        }
        catch { return false; }
    }

    /// <summary>The size a size-only check should see: the release's size while our keyed copy is in place.</summary>
    public long BaseLength(string targetPath, long actualLength)
    {
        var rec = Get(targetPath);
        return rec != null && rec.KeyedSize == actualLength ? rec.BaseSize : actualLength;
    }
}

/// <summary>
/// Puts the player's Interact key into the "[E] ..." prompt strings of the installed locale.
/// <para><b>Rename only, never append.</b> Appending records broke game start once ("G33 -
/// Localization initialization failed", 2026-10-08). This rewrites the TEXT of records that are
/// already there and nothing else: same record count, same hashes, same .dir row order, records in
/// the same physical order (so sorted-by-hash and sorted-by-offset both still hold); only offsets,
/// lengths, MD5Checksum and TextLength are recomputed - exactly what patch_locale_rotk.py's
/// rename-only path does to the locale the owner plays on.</para>
/// <para><b>Which records.</b> (1) Our pickup prompts, ids <see cref="PromptBandStart"/>..
/// <see cref="PromptBandEnd"/> (re\loot_prompt_strings.py: 930000 + NAME_ID), whose text starts
/// "[E] ". Absent from a locale that never got them - then nothing happens. (2) The stock prompts the
/// client or our server already shows on foot and that start with the literal, never-substituted
/// "[[*key*]] " in every language: Enter Vehicle and Flip Vehicle (picked by the client itself),
/// Use Door / Close Door (what DoorsFeature answers 09 2B with, data\kotk\doors\z2-doors.json).</para>
/// </summary>
public static class KeyPromptLocale
{
    public const int PromptBandStart = 930000, PromptBandEnd = 950000;
    public const int MaxLabel = 24;

    /// <summary>Stock key-prompt records, by locale hash (the hash IS the key in the .dat/.dir).</summary>
    public static readonly IReadOnlyDictionary<uint, string> StockKeyPrompts = new Dictionary<uint, string>
    {
        [3339803698] = "[[*key*]] Enter Vehicle",
        [398263315] = "[[*key*]] Flip Vehicle",
        [3718124335] = "[[*key*]] Use Door",
        [3115320672] = "[[*key*]] Close Door",
    };

    static HashSet<uint>? _band;
    static HashSet<uint> Band => _band ??= Enumerable.Range(PromptBandStart, PromptBandEnd - PromptBandStart + 1)
        .Select(i => LocaleHash.StringKey(i)).ToHashSet();

    static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };
    static readonly Regex OurPrefix = new(@"^\[[^\[\]\r\n\t]{1," + MaxLabel + @"}\]", RegexOptions.CultureInvariant);
    const string KeyToken = "[[*key*]]";   // zh_cn has no space after it, every other language does

    public sealed record Result(byte[] Dat, byte[] Dir, int Changed);

    /// <summary>
    /// Rewrites the targeted records' leading "[X] " / "[[*key*]] " to "[<paramref name="label"/>] ".
    /// Throws <see cref="InvalidDataException"/> on any layout it does not fully recognise.
    /// </summary>
    public static Result Rewrite(byte[] dat, byte[] dir, string label)
    {
        // Only the bracketed key is replaced; whatever followed it (a space, or none in zh_cn) is kept.
        var prefix = Encoding.UTF8.GetBytes("[" + InteractBinding.SafeLabel(label) + "]");
        int start = dat.AsSpan().StartsWith(Bom) ? Bom.Length : 0;

        // ---- parse the index, keeping every line so it can be written back in the same order
        var lines = SplitCrlf(dir);
        var rows = new List<(int Line, uint Hash, int Off, int Len, string Flag)>();
        for (int li = 0; li < lines.Count; li++)
        {
            var ln = lines[li];
            if (ln.Length == 0 || ln.StartsWith('#')) continue;
            var p = ln.Split('\t');
            if (p.Length != 4 || !uint.TryParse(p[0], out var h) || !int.TryParse(p[1], out var off) || !int.TryParse(p[2], out var len))
                throw new InvalidDataException($"unrecognised .dir row {li}");
            rows.Add((li, h, off, len, p[3]));
        }
        if (rows.Count == 0) throw new InvalidDataException("empty .dir");

        // ---- the .dat must be exactly the records, CRLF-separated, in offset order
        var byOff = rows.OrderBy(r => r.Off).ToList();
        int expect = start;
        foreach (var r in byOff)
        {
            if (r.Off != expect || r.Off + r.Len + 2 > dat.Length || dat[r.Off + r.Len] != '\r' || dat[r.Off + r.Len + 1] != '\n')
                throw new InvalidDataException("records are not contiguous CRLF-separated lines");
            var head = Encoding.ASCII.GetBytes(r.Hash + "\t");
            if (!dat.AsSpan(r.Off).StartsWith(head)) throw new InvalidDataException($"row {r.Hash} does not point at its record");
            expect = r.Off + r.Len + 2;
        }
        if (expect != dat.Length) throw new InvalidDataException("bytes after the last record");

        // ---- rewrite and re-lay in the SAME physical order
        var outDat = new MemoryStream(dat.Length + 4096);
        outDat.Write(dat, 0, start);
        var newPos = new Dictionary<uint, (int Off, int Len)>();
        int changed = 0, longest = 0;
        foreach (var r in byOff)
        {
            var rec = dat.AsSpan(r.Off, r.Len);
            int t1 = rec.IndexOf((byte)'\t');
            int t2 = t1 < 0 ? -1 : rec[(t1 + 1)..].IndexOf((byte)'\t');
            if (t1 < 0 || t2 < 0) throw new InvalidDataException($"record {r.Hash} has no type/text");
            int textAt = t1 + 1 + t2 + 1;
            var text = rec[textAt..];
            byte[]? replaced = null;
            if (Band.Contains(r.Hash) || StockKeyPrompts.ContainsKey(r.Hash))
                replaced = Rekey(text, prefix);
            if (replaced != null && !replaced.AsSpan().SequenceEqual(text)) changed++;
            var newText = replaced ?? text.ToArray();
            int off = (int)outDat.Position;
            outDat.Write(rec[..textAt]);
            outDat.Write(newText);
            outDat.Write("\r\n"u8);
            newPos[r.Hash] = (off, textAt + newText.Length);
            longest = Math.Max(longest, newText.Length);
        }
        var newDat = outDat.ToArray();

        // ---- the index: same lines, same order; only offsets/lengths and two header values change
        var md5 = Convert.ToHexString(MD5.HashData(newDat));
        foreach (var r in rows)
        {
            var (o, l) = newPos[r.Hash];
            lines[r.Line] = $"{r.Hash}\t{o}\t{l}\t{r.Flag}";
        }
        for (int li = 0; li < lines.Count; li++)
        {
            if (!lines[li].StartsWith('#')) continue;
            if (Regex.IsMatch(lines[li], @"^## MD5Checksum:")) lines[li] = Regex.Replace(lines[li], @"(?<=^## MD5Checksum:\s*)[0-9A-Fa-f]+", md5);
            else if (Regex.IsMatch(lines[li], @"^## TextLength:")) lines[li] = Regex.Replace(lines[li], @"(?<=^## TextLength:\s*)\d+", longest.ToString());
        }
        var newDir = Encoding.UTF8.GetBytes(string.Join("\r\n", lines));
        return new Result(newDat, newDir, changed);
    }

    /// <summary>The text with its key prefix replaced, or null when it has no recognisable one.</summary>
    static byte[]? Rekey(ReadOnlySpan<byte> text, byte[] prefix)
    {
        var s = Encoding.UTF8.GetString(text);
        string rest;
        if (s.StartsWith(KeyToken, StringComparison.Ordinal)) rest = s[KeyToken.Length..];
        else if (OurPrefix.Match(s) is { Success: true } m) rest = s[m.Length..];
        else return null;
        var b = new byte[prefix.Length + Encoding.UTF8.GetByteCount(rest)];
        prefix.CopyTo(b, 0);
        Encoding.UTF8.GetBytes(rest, 0, rest.Length, b, prefix.Length);
        return b;
    }

    /// <summary>Splits on CRLF exactly (a final CRLF yields a trailing empty line, rejoined identically).</summary>
    static List<string> SplitCrlf(byte[] bytes) => Encoding.UTF8.GetString(bytes).Split("\r\n").ToList();

    /// <summary>
    /// Re-checks a rewrite: same records, same order, only the targeted texts changed, MD5 right.
    /// Cheap insurance against ever handing the client a table it refuses (G33).
    /// </summary>
    public static void Check(byte[] oldDat, byte[] oldDir, Result r)
    {
        static List<(uint H, int Off)> Rows(byte[] dir) => SplitCrlf(dir)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t')).Select(p => (uint.Parse(p[0]), int.Parse(p[1]))).ToList();
        var a = Rows(oldDir);
        var b = Rows(r.Dir);
        if (a.Count != b.Count || !a.Select(x => x.H).SequenceEqual(b.Select(x => x.H)))
            throw new InvalidDataException("record set or index order changed");
        if (!b.Select(x => x.Off).SequenceEqual(b.Select(x => x.Off).OrderBy(x => x)) && a.Select(x => x.Off).SequenceEqual(a.Select(x => x.Off).OrderBy(x => x)))
            throw new InvalidDataException("offset order no longer matches the index order");
        var md5 = Convert.ToHexString(MD5.HashData(r.Dat));
        if (!Encoding.UTF8.GetString(r.Dir).Contains("## MD5Checksum: " + md5))
            throw new InvalidDataException("MD5Checksum header does not match the new .dat");
        var ot = Texts(oldDat); var nt = Texts(r.Dat);
        foreach (var (h, t) in ot)
        {
            if (!nt.TryGetValue(h, out var n)) throw new InvalidDataException($"record {h} disappeared");
            bool target = Band.Contains(h) || StockKeyPrompts.ContainsKey(h);
            if (!target && n != t) throw new InvalidDataException($"record {h} changed but is not a key prompt");
        }
        if (nt.Count != ot.Count) throw new InvalidDataException("record count changed");
    }

    static Dictionary<uint, string> Texts(byte[] dat)
    {
        var d = new Dictionary<uint, string>();
        foreach (var line in Encoding.UTF8.GetString(dat).TrimStart('﻿').Split("\r\n"))
        {
            var p = line.Split('\t', 3);
            if (p.Length == 3 && uint.TryParse(p[0], out var h)) d[h] = p[1] + "\t" + p[2];
        }
        return d;
    }

    // ------------------------------------------------------------------ the install side

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>
    /// Called right before H1Z1.exe starts. Reads the binding, and makes every Locale\*_data pair
    /// carry it. Never throws: a prompt label is not worth a failed launch. Returns a one-line summary.
    /// </summary>
    public static string PrepareForLaunch(string folder)
    {
        try
        {
            var trigger = InteractBinding.ReadTrigger(folder);
            var label = trigger == null ? null : InteractBinding.Label(trigger);
            return Apply(folder, label);
        }
        catch (Exception ex) { return "key prompts skipped: " + ex.Message; }
    }

    /// <summary>
    /// Makes the installed locale show <paramref name="label"/> (null = put the release's bytes back).
    /// The release's own bytes are always the source, never a previously keyed file, so repeated
    /// rebinds cannot drift.
    /// </summary>
    public static string Apply(string folder, string? label)
    {
        var localeDir = Path.Combine(folder, "Locale");
        if (!Directory.Exists(localeDir)) return "no Locale folder";
        var state = KeyedLocaleState.Load(folder);
        var notes = new List<string>();
        int rekeyed = 0, kept = 0;

        foreach (var datPath in Directory.EnumerateFiles(localeDir, "*_data.dat").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var dirPath = datPath[..^4] + ".dir";
            var name = Path.GetFileName(datPath);
            var relDat = Path.Combine("Locale", name);
            var relDir = Path.Combine("Locale", Path.GetFileName(dirPath));
            try
            {
                if (!File.Exists(dirPath)) continue;
                if (PathSafety.HasReparsePointOnPath(folder, datPath) || PathSafety.HasReparsePointOnPath(folder, dirPath))
                { notes.Add(name + ": symlink/junction on the path, left alone"); continue; }

                var curDat = File.ReadAllBytes(datPath);
                var curDir = File.ReadAllBytes(dirPath);
                var rd = state.Get(relDat);
                var rr = state.Get(relDir);
                bool isKeyed = rd != null && rr != null
                               && FileHasher.HashesEqual(Sha(curDat), rd.KeyedSha256)
                               && FileHasher.HashesEqual(Sha(curDir), rr.KeyedSha256);

                byte[] baseDat, baseDir;
                if (isKeyed)
                {
                    if (label != null && rd!.Key == label) { kept++; continue; }   // already this key
                    baseDat = File.ReadAllBytes(KeyedLocaleState.BackupFor(folder, relDat));
                    baseDir = File.ReadAllBytes(KeyedLocaleState.BackupFor(folder, relDir));
                    if (!FileHasher.HashesEqual(Sha(baseDat), rd!.BaseSha256) || !FileHasher.HashesEqual(Sha(baseDir), rr!.BaseSha256))
                    { notes.Add(name + ": backup does not match its record, left alone"); continue; }
                }
                else
                {
                    // The release (or an update, or a repair) put these bytes here: they are the base.
                    state.Files.Remove(relDat);
                    state.Files.Remove(relDir);
                    baseDat = curDat;
                    baseDir = curDir;
                }

                Result? r = null;
                if (label != null)
                {
                    r = Rewrite(baseDat, baseDir, label);
                    if (r.Changed == 0) r = null;
                    else Check(baseDat, baseDir, r);
                }

                if (r == null)
                {
                    // Nothing to key (no prompt records, or no binding): the release's bytes belong there.
                    if (isKeyed) { ReplacePair(datPath, baseDat, dirPath, baseDir, curDat); state.Files.Remove(relDat); state.Files.Remove(relDir); }
                    continue;
                }

                if (!isKeyed)
                {
                    WriteReplacing(KeyedLocaleState.BackupFor(folder, relDat), baseDat);
                    WriteReplacing(KeyedLocaleState.BackupFor(folder, relDir), baseDir);
                }
                ReplacePair(datPath, r.Dat, dirPath, r.Dir, curDat);
                state.Files[relDat] = Record(datPath, baseDat, r.Dat, label!);
                state.Files[relDir] = Record(dirPath, baseDir, r.Dir, label!);
                rekeyed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
            {
                notes.Add($"{name}: {ex.Message}");
            }
        }
        state.Save(folder);
        return $"key prompts [{label ?? "unbound"}]: {rekeyed} locale(s) rewritten, {kept} already right"
               + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "");
    }

    /// <summary>
    /// Puts the release's bytes back wherever our keyed copy is in place. Run before an update or
    /// repair writes anything, so an append patch sees the exact base it was built for. Throws when a
    /// file cannot be restored (the game has it open), because installing on top would be wrong.
    /// </summary>
    public static int RestoreAll(string folder)
    {
        var state = KeyedLocaleState.Load(folder);
        if (state.Files.Count == 0) return 0;
        int restored = 0;
        foreach (var (rel, rec) in state.Files.ToList())
        {
            var full = Path.Combine(folder, rel);
            if (File.Exists(full) && FileHasher.HashesEqual(Sha(File.ReadAllBytes(full)), rec.KeyedSha256))
            {
                var orig = File.ReadAllBytes(KeyedLocaleState.BackupFor(folder, rel));
                if (!FileHasher.HashesEqual(Sha(orig), rec.BaseSha256))
                    throw new IOException($"the backup of {rel} is damaged; run VERIFY / REPAIR");
                WriteReplacing(full, orig);
                restored++;
            }
            state.Files.Remove(rel);
        }
        state.Save(folder);
        return restored;
    }

    static KeyedFile Record(string path, byte[] baseBytes, byte[] keyed, string key) => new()
    {
        BaseSha256 = Sha(baseBytes), BaseSize = baseBytes.Length,
        KeyedSha256 = Sha(keyed), KeyedSize = keyed.Length,
        KeyedMTimeTicks = new FileInfo(path).LastWriteTimeUtc.Ticks,
        Key = key
    };

    /// <summary>The .dat and .dir are a pair: if the .dir cannot be replaced, the .dat goes back.</summary>
    static void ReplacePair(string datPath, byte[] dat, string dirPath, byte[] dir, byte[] previousDat)
    {
        WriteReplacing(datPath, dat);
        try { WriteReplacing(dirPath, dir); }
        catch
        {
            try { WriteReplacing(datPath, previousDat); } catch { }
            throw;
        }
    }

    /// <summary>Temp file + rename over the target; never writes into the existing file (hardlink-safe).</summary>
    public static void WriteReplacing(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".zrevive.tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }
        try { File.Move(tmp, path, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }
}
