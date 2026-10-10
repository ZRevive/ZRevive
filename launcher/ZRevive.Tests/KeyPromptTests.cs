using System.IO;
using System.Security.Cryptography;
using System.Text;
using ZRevive.Launcher.Install;

namespace ZRevive.Launcher.Tests;

/// <summary>The player's Interact key in the prompt strings (Install\KeyPrompts.cs).</summary>
static class KeyPromptTests
{
    const uint EnterVehicle = 3339803698, PickUpToken = 450801649;

    public static async Task RunAll()
    {
        T.Group("key prompts: locale hash matches re\\fl_locale.py");
        T.Eq(LocaleHash.StringKey(13338), 450801649u, "string_key(13338)");
        T.Eq(LocaleHash.StringKey(930000), 3718042956u, "string_key(930000)");
        T.Eq(LocaleHash.StringKey(930032), 711558735u, "string_key(930032)");
        T.Eq(LocaleHash.Lookup2(Encoding.Latin1.GetBytes("Global.Text.\u00e9\u00ff")), 3980131958u, "signed-char bytes over 127");

        T.Group("key prompts: binding labels");
        T.Eq(InteractBinding.Label("E"), "E", "E");
        T.Eq(InteractBinding.Label("Mouse_3"), "MOUSE4", "Mouse_3 is the fourth button");
        T.Eq(InteractBinding.Label("Control+G"), "CTRL+G", "modifier");
        T.Eq(InteractBinding.Label("MouseWheelUp"), "WHEEL UP", "wheel");
        T.Eq(InteractBinding.SafeLabel(InteractBinding.Label("Bracket_Left")), "(", "a bracket key cannot break the prefix");

        var tmp = Path.Combine(Path.GetTempPath(), "zr-keyprompt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "Locale"));
        try
        {
            T.Group("key prompts: reading InputProfile_User.xml");
            File.WriteAllText(Path.Combine(tmp, "InputProfile_Default.xml"),
                "<Profile name=\"Default\"><ActionSet name=\"Infantry\"><Action name=\"Interact\" displayName=\"UI.Interact\"><Trigger>E</Trigger></Action></ActionSet></Profile>");
            T.Eq(InteractBinding.ReadTrigger(tmp), "E", "falls back to the default profile");
            File.WriteAllText(Path.Combine(tmp, "InputProfile_User.xml"),
                "<Profile name=\"User\"><ActionSet name=\"GroundVehicle\"><Action name=\"Interact\"><Trigger>G</Trigger></Action></ActionSet>"
                + "<ActionSet name=\"Infantry\"><Action name=\"Interact\" version=\"2\"><Trigger>Gamepad_1_Button_0</Trigger><Trigger>Mouse_3</Trigger></Action></ActionSet></Profile>");
            T.Eq(InteractBinding.ReadTrigger(tmp), "Mouse_3", "on-foot set, gamepad trigger skipped");

            T.Group("key prompts: rename-only rewrite");
            var band = LocaleHash.StringKey(930032);
            var (dat, dir) = Locale(new()
            {
                [7] = "Plain text, untouched",
                [band] = "[E] AR-15",
                [EnterVehicle] = "[[*key*]] Enter Vehicle",
                [PickUpToken] = "[[*key*]] Pick Up [*target*]",
            });
            var r = KeyPromptLocale.Rewrite(dat, dir, "MOUSE4");
            T.Eq(r.Changed, 2, "our band record and Enter Vehicle, nothing else");
            KeyPromptLocale.Check(dat, dir, r);
            var text = Encoding.UTF8.GetString(r.Dat);
            T.Ok(text.Contains($"{band}\tucdt\t[MOUSE4] AR-15\r\n"), "band prompt re-keyed");
            T.Ok(text.Contains($"{EnterVehicle}\tucdt\t[MOUSE4] Enter Vehicle\r\n"), "stock token prompt re-keyed");
            T.Ok(text.Contains("[[*key*]] Pick Up [*target*]"), "a prompt we don't own is left alone");
            T.Eq(Rows(r.Dir).Count, 4, "record count unchanged");
            T.Ok(Rows(r.Dir).Select(x => x.Hash).SequenceEqual(Rows(dir).Select(x => x.Hash)), "index order unchanged (sorted by hash)");
            T.Ok(Rows(r.Dir).Select(x => x.Off).SequenceEqual(Rows(r.Dir).Select(x => x.Off).OrderBy(o => o)), "offsets still ascending with the index");
            T.Ok(Encoding.UTF8.GetString(r.Dir).Contains("## MD5Checksum: " + Convert.ToHexString(MD5.HashData(r.Dat))), "MD5Checksum recomputed");
            T.Ok(Encoding.UTF8.GetString(r.Dir).StartsWith("## Count:\t4\r\n"), "Count header untouched");
            var back = KeyPromptLocale.Rewrite(r.Dat, r.Dir, "E");
            T.Ok(back.Dat.AsSpan().SequenceEqual(KeyPromptLocale.Rewrite(dat, dir, "E").Dat), "re-keying a keyed file gives the same bytes as keying the base");

            T.Group("key prompts: install flow (apply, verify, restore)");
            File.WriteAllBytes(Path.Combine(tmp, "Locale", "en_us_data.dat"), dat);
            File.WriteAllBytes(Path.Combine(tmp, "Locale", "en_us_data.dir"), dir);
            var note = KeyPromptLocale.PrepareForLaunch(tmp);
            T.Ok(note.Contains("1 locale(s) rewritten"), "launch keys the locale: " + note);
            T.Ok(File.ReadAllText(Path.Combine(tmp, "Locale", "en_us_data.dat")).Contains("[MOUSE4] AR-15"), "file on disk carries the key");
            T.Ok(File.ReadAllBytes(KeyedLocaleState.BackupFor(tmp, @"Locale\en_us_data.dat")).AsSpan().SequenceEqual(dat), "release bytes backed up");
            T.Ok(!Directory.EnumerateFiles(tmp, "*.zrevive.tmp", SearchOption.AllDirectories).Any(), "no temp files left behind");
            T.Ok(KeyPromptLocale.PrepareForLaunch(tmp).Contains("1 already right"), "same key again: nothing rewritten");

            var m = new ReleaseManifest(2, "ab12cd34", null, null, null, new List<ManifestEntry>
            {
                new("dat", "dat", @"Locale\en_us_data.dat", dat.Length, Sha(dat), EntryKind.Own, null, null),
                new("dir", "dir", @"Locale\en_us_data.dir", dir.Length, Sha(dir), EntryKind.Own, null, null),
            });
            var q = await QuickVerifier.RunAsync(m, tmp, new FilePrints(), CancellationToken.None);
            T.Ok(q.Ok, "quick verify accepts the keyed locale (no repair loop)");
            var full = await InstallService.VerifyAsync(m, tmp, null, CancellationToken.None);
            T.Ok(full.Broken.Count() == 0, "full verify accepts it too");

            var other = m with { Entries = new List<ManifestEntry> { m.Entries[0] with { Sha256 = new string('0', 64) } } };
            var q2 = await QuickVerifier.RunAsync(other, tmp, new FilePrints(), CancellationToken.None);
            T.Ok(!q2.Ok, "a release with a DIFFERENT locale still flags it (the update replaces it)");

            var append = new ReleaseManifest(2, "ab12cd34", null, null, null, new List<ManifestEntry>
            {
                new("dat", "p", @"Locale\en_us_data.dat", 10, Sha(dat), EntryKind.Append, null, "zrappend", ResultSha256: Sha(dat), ResultSize: dat.Length, BaseSize: 5),
            });
            T.Eq(ForeignBaseline.Detect(append, tmp), null, "a keyed locale is not mistaken for a foreign install");

            File.WriteAllText(Path.Combine(tmp, "InputProfile_User.xml"),
                "<Profile name=\"User\"><ActionSet name=\"Infantry\"><Action name=\"Interact\"><Trigger>F</Trigger></Action></ActionSet></Profile>");
            KeyPromptLocale.PrepareForLaunch(tmp);
            var nowText = File.ReadAllText(Path.Combine(tmp, "Locale", "en_us_data.dat"));
            T.Ok(nowText.Contains("[F] AR-15") && nowText.Contains("[F] Enter Vehicle"), "rebind to F re-keys from the backup");

            T.Eq(KeyPromptLocale.RestoreAll(tmp), 2, "update path restores both files of the pair");
            T.Ok(File.ReadAllBytes(Path.Combine(tmp, "Locale", "en_us_data.dat")).AsSpan().SequenceEqual(dat)
                 && File.ReadAllBytes(Path.Combine(tmp, "Locale", "en_us_data.dir")).AsSpan().SequenceEqual(dir), "release bytes are back exactly");

            T.Group("key prompts: the real installed locale (read-only)");
            foreach (var p in Directory.Exists(@"C:\Games\ZRevive\Locale")
                         ? Directory.GetFiles(@"C:\Games\ZRevive\Locale", "*_data.dat") : Array.Empty<string>())
            {
                var rd = File.ReadAllBytes(p);
                var rr = File.ReadAllBytes(p[..^4] + ".dir");
                var rk = KeyPromptLocale.Rewrite(rd, rr, "MOUSE4");
                KeyPromptLocale.Check(rd, rr, rk);
                T.Ok(rk.Changed >= 4 && Rows(rk.Dir).Count == Rows(rr).Count,
                    $"{Path.GetFileName(p)}: {rk.Changed} prompt(s) re-keyed, {Rows(rr).Count} records, layout checks pass");
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static List<(uint Hash, int Off)> Rows(byte[] dir) => Encoding.UTF8.GetString(dir).Split("\r\n")
        .Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t'))
        .Select(p => (uint.Parse(p[0]), int.Parse(p[1]))).ToList();

    /// <summary>A locale pair laid out like the shipped ones: BOM, records in hash order, CRLF everywhere.</summary>
    static (byte[] Dat, byte[] Dir) Locale(Dictionary<uint, string> records)
    {
        var dat = new MemoryStream();
        dat.Write(new byte[] { 0xEF, 0xBB, 0xBF });
        var rows = new List<string>();
        int longest = 0;
        foreach (var (h, t) in records.OrderBy(kv => kv.Key))
        {
            var rec = Encoding.UTF8.GetBytes($"{h}\tucdt\t{t}");
            rows.Add($"{h}\t{dat.Position}\t{rec.Length}\td");
            dat.Write(rec);
            dat.Write("\r\n"u8);
            longest = Math.Max(longest, Encoding.UTF8.GetByteCount(t));
        }
        var d = dat.ToArray();
        var header = new[] { $"## Count:\t{records.Count}", "## MD5Checksum: " + Convert.ToHexString(MD5.HashData(d)), $"## TextLength:\t{longest}" };
        return (d, Encoding.UTF8.GetBytes(string.Join("\r\n", header.Concat(rows)) + "\r\n"));
    }
}
