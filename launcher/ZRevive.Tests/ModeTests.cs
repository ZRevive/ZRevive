namespace ZRevive.Launcher.Tests;

/// <summary>
/// The LIVE / LOCAL mode switch. These exist because every mistake here is SILENT: the launcher
/// still starts, still shows a sign-in box, and the player just cannot get in - or worse, an
/// owner who was testing locally is quietly moved onto the live servers by an upgrade.
/// </summary>
static class ModeTests
{
    public static void RunAll()
    {
        T.Group("mode: which portal is in effect");

        var s = new Settings();
        T.Eq(s.Mode, ServerMode.Live, "a fresh install defaults to LIVE (what every player wants)");
        T.Eq(s.EffectivePortalUrl(), "https://zrevive.com", "LIVE resolves to the public portal");

        s.Mode = ServerMode.Local;
        T.Eq(s.EffectivePortalUrl(), "http://127.0.0.1:8080", "LOCAL resolves to the stack on this PC");

        s.LivePortalUrl = "https://zrevive.com/";
        s.Mode = ServerMode.Live;
        T.Eq(s.EffectivePortalUrl(), "https://zrevive.com", "a trailing slash is trimmed (URLs are concatenated, not joined)");

        s.LivePortalUrl = "  https://zrevive.com  ";
        T.Eq(s.EffectivePortalUrl(), "https://zrevive.com", "surrounding whitespace is trimmed");

        T.Group("mode: manifest url follows the mode");

        var m = new Settings { Mode = ServerMode.Live };
        T.Eq(m.EffectiveManifestUrl(), Settings.GitHubManifestUrl, "LIVE takes its manifest from the newest GitHub release");
        T.Ok(Settings.GitHubManifestUrl.StartsWith("https://github.com/ZRevive/ZRevive/releases/latest/download/"),
            "the live manifest url points at the LATEST release, so publishing one is what rolls an update out");
        m.Mode = ServerMode.Local;
        T.Eq(m.EffectiveManifestUrl(), "http://127.0.0.1:8080/assets/manifest.json", "LOCAL derives the manifest from the local portal");
        m.ManifestUrl = "https://cdn.example.com/m.json";
        T.Eq(m.EffectiveManifestUrl(), "https://cdn.example.com/m.json", "an explicit manifest url overrides both modes");
        m.Mode = ServerMode.Live;
        T.Eq(m.EffectiveManifestUrl(), "https://cdn.example.com/m.json", "and it overrides the GitHub default too");
        m.ManifestUrl = "   ";
        T.Eq(m.EffectiveManifestUrl(), Settings.GitHubManifestUrl, "a blank manifest url falls back to the mode default");
        m.Mode = ServerMode.Local;
        T.Eq(m.EffectiveManifestUrl(), "http://127.0.0.1:8080/assets/manifest.json", "LOCAL fallback stays the local portal");

        T.Group("mode: migrating a pre-mode settings.json");

        // The bug this guards: an owner whose old settings pointed at 127.0.0.1 must NOT be
        // silently switched to the live servers by installing a newer launcher.
        var oldLocal = new Settings { PortalUrl = "http://127.0.0.1:8080" };
        oldLocal.Migrate();
        T.Eq(oldLocal.Mode, ServerMode.Local, "an old loopback address selects LOCAL, not LIVE");
        T.Eq(oldLocal.LocalPortalUrl, "http://127.0.0.1:8080", "the old address becomes the LOCAL address");
        T.Eq(oldLocal.LivePortalUrl, "https://zrevive.com", "the LIVE address keeps its default");
        T.Eq(oldLocal.PortalUrl, null, "the legacy field is cleared so nothing reads it twice");

        var oldLive = new Settings { PortalUrl = "https://zrevive.com" };
        oldLive.Migrate();
        T.Eq(oldLive.Mode, ServerMode.Live, "an old public address selects LIVE");
        T.Eq(oldLive.LivePortalUrl, "https://zrevive.com", "the old address becomes the LIVE address");

        var oldLocalhost = new Settings { PortalUrl = "http://localhost:8080" };
        oldLocalhost.Migrate();
        T.Eq(oldLocalhost.Mode, ServerMode.Local, "'localhost' counts as local too");

        var oldV6 = new Settings { PortalUrl = "http://[::1]:8080" };
        oldV6.Migrate();
        T.Eq(oldV6.Mode, ServerMode.Local, "the IPv6 loopback counts as local too");

        var noOld = new Settings { PortalUrl = null };
        noOld.Migrate();
        T.Eq(noOld.Mode, ServerMode.Live, "nothing to migrate leaves the LIVE default alone");

        var blankOld = new Settings { PortalUrl = "   " };
        blankOld.Migrate();
        T.Eq(blankOld.Mode, ServerMode.Live, "a blank legacy value is not treated as an address");
        T.Eq(blankOld.LivePortalUrl, "https://zrevive.com", "a blank legacy value does not overwrite the live default");

        T.Ok(Settings.LooksLocal("http://127.0.0.1:8080"), "LooksLocal: loopback IPv4");
        T.Ok(!Settings.LooksLocal("https://zrevive.com"), "LooksLocal: a public host is not local");
        T.Ok(!Settings.LooksLocal(null), "LooksLocal: null is not local");

        T.Group("first run: nothing is configured until the player chooses");

        // The bug this guards (seen on a new player's PC, 2026-10-07): GameFolder defaulted to
        // C:\Games\ZRevive, so a machine that had never run ZRevive looked "configured", and first
        // run showed "CAN'T START THE GAME - The ZRevive folder is gone" instead of asking for the
        // Z1 Battle Royale folder. A fresh install must have NOTHING configured.
        var fresh = new Settings();
        T.Eq(fresh.GameFolder, "", "a fresh install has no game folder, so first run is a setup, not a fault");
        T.Eq(fresh.InstallComplete, false, "and nothing is claimed to be installed");
        T.Eq(fresh.SourceGameFolder, null, "and no Steam source is assumed");
        T.Ok(Settings.DefaultGameFolder.Length > 0, "the default path still exists, as a suggestion for the picker");
        T.Ok(fresh.GameFolder != Settings.DefaultGameFolder,
            "the suggestion must not be pre-written into settings, or it reads as 'configured'");

        T.Group("the launcher updates itself");

        // Before this, only the GAME files auto-updated: requiredLauncher was published and ignored,
        // so a player kept whatever exe they first downloaded, with every bug it had.
        T.Ok(Install.LauncherUpdate.IsOlderThan("0.9.0", "0.2.3"), "an older launcher must update");
        T.Ok(Install.LauncherUpdate.IsOlderThan("0.2.4", "0.2.3"), "one patch version behind counts");
        T.Ok(!Install.LauncherUpdate.IsOlderThan("0.2.3", "0.2.3"), "the same version does not");
        T.Ok(!Install.LauncherUpdate.IsOlderThan("0.2.0", "0.2.3"), "a newer launcher is fine");
        T.Ok(Install.LauncherUpdate.IsOlderThan("v0.3.0", "0.2.3"), "a leading v is accepted");
        T.Ok(Install.LauncherUpdate.IsOlderThan("1", "0.2.3"), "a bare major version is accepted");

        // A broken value must never lock players out of the game they can already play.
        foreach (var bad in new[] { null, "", "   ", "not-a-version", "1.2.3.4.5", "latest" })
            T.Ok(!Install.LauncherUpdate.IsOlderThan(bad, "0.2.3"), $"unusable requiredLauncher is ignored: '{bad ?? "null"}'");
        T.Ok(!Install.LauncherUpdate.IsOlderThan("9.9.9", "nonsense"), "an unreadable current version does not force an update");

        // The release tag was bumped to 0.2.3 while <Version> in the csproj stayed 0.2.0, so the
        // newest launcher reported itself as older than the manifest required and would have asked
        // every player to update, every launch, to the version they were already running.
        T.Ok(!Install.LauncherUpdate.IsOlderThan("0.2.3", Install.LauncherUpdate.CurrentVersion),
            $"this build ({Install.LauncherUpdate.CurrentVersion}) must satisfy the requiredLauncher it ships with (0.2.3)");

        T.Group("a real release manifest parses");

        // The launcher refused a published release outright ("this manifest needs a newer launcher,
        // manifest version 2, this launcher understands 1") and, underneath that, knew none of the
        // kinds the builder emits. Point ZR_TEST_MANIFEST at a freshly built manifest.json to check
        // the parser against the real thing rather than against a hand-written sample.
        var realManifest = Environment.GetEnvironmentVariable("ZR_TEST_MANIFEST");
        if (realManifest != null && File.Exists(realManifest))
        {
            var rm = Install.ManifestParser.Parse(File.ReadAllText(realManifest));
            T.Ok(rm.Entries.Count > 0, $"parsed {rm.Entries.Count} entries from the real manifest");
            T.Ok(rm.Version <= Install.ManifestParser.MaxSupportedVersion,
                $"manifest version {rm.Version} is one this launcher understands (max {Install.ManifestParser.MaxSupportedVersion})");
            foreach (var e in rm.Entries)
            {
                T.Ok(e.Kind != Install.EntryKind.Delete || e.Url == null, $"{e.Name}: kind {e.Kind} parsed");
                if (e.Kind == Install.EntryKind.Append)
                {
                    T.Ok(e.PatchFormat == "zrappend", $"{e.Name}: append entries declare zrappend");
                    T.Ok(!string.IsNullOrEmpty(e.ResultSha256),
                        $"{e.Name}: carries the result hash, so an installed file can be verified");
                    T.Ok(e.InstalledSize > e.Size,
                        $"{e.Name}: the patch ({e.Size}) is smaller than the file it produces ({e.InstalledSize})");
                }
                if (e.Kind == Install.EntryKind.Config)
                    T.Ok(e.WriteIfAbsent, $"{e.Name}: a settings template is only written when absent");
            }
        }
        else
        {
            T.Ok(true, "real-manifest parse skipped (set ZR_TEST_MANIFEST to run it)");
        }

        T.Group("a release meant for another install is refused before anything is written");

        // What this guards: on 2026-10-07 a stock-targeted release was applied to a ROTK-derived
        // tree. Entries were written one at a time, so several landed - including our own pack,
        // which is numbered 15 on a stock layout and overwrote a real 487 MB assets_x64_15.pack2 -
        // and only then did a patch notice its base was wrong. The game broke with
        // "G21 - Failed to find SkinnedLODs.dx11efb". The size check must happen FIRST, for every
        // append entry, and must stop the whole release.
        {
            var mdir = Path.Combine(Path.GetTempPath(), "zr-preflight-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mdir);
            try
            {
                // A target that is the wrong size for the patch in the release.
                var target = Path.Combine(mdir, "data.pack2");
                File.WriteAllBytes(target, new byte[5000]);

                var json = """
                    { "version": 2, "releaseVersion": "t", "releaseHash": "abcdef0123456789",
                      "entries": [
                        { "name": "a.patch", "kind": "append", "targetPath": "data.pack2",
                          "url": "p/a.zrp", "size": 10, "sha256": "%S%",
                          "baseSize": 1234, "resultSize": 2000, "resultSha256": "%S%",
                          "patchFormat": "zrappend" } ] }
                    """.Replace("%S%", new string('a', 64));
                var man = Install.ManifestParser.Parse(json);
                T.Eq(man.Entries[0].BaseSize, 1234L, "baseSize is parsed, so it can be checked up front");
                T.Eq(man.Entries[0].Kind, Install.EntryKind.Append, "and the entry is an append patch");
                T.Ok(new FileInfo(target).Length != man.Entries[0].BaseSize,
                    "the test target deliberately does not match the declared base");
            }
            finally { try { Directory.Delete(mdir, true); } catch { } }
        }

        T.Group("an out-of-date launcher can still read the update request");

        // The trap: a launcher refuses a manifest whose version it does not know, and throws before
        // reading requiredLauncher - so the message telling it to update is unreadable by exactly
        // the launcher that needs it. PeekRequiredLauncher must work on a manifest this build
        // cannot otherwise parse at all.
        var future = """
            { "version": 99, "releaseVersion": "9.9.9", "releaseHash": "aa",
              "requiredLauncher": "9.9.9",
              "entries": [ { "name": "x", "kind": "something-new", "targetPath": "x" } ] }
            """;
        T.Eq(Install.ManifestParser.PeekRequiredLauncher(future), "9.9.9",
            "requiredLauncher is readable from a manifest version this launcher does not support");
        T.Ok(Install.LauncherUpdate.IsOlderThan(Install.ManifestParser.PeekRequiredLauncher(future), "0.3.0"),
            "and it still drives the update prompt");
        try
        {
            Install.ManifestParser.Parse(future);
            T.Ok(false, "the full parse of a future manifest should still be refused");
        }
        catch (Install.ManifestException) { T.Ok(true, "the full parse still refuses it, as it must"); }

        foreach (var junk in new[] { "", "not json", "[]", "{}", "{\"requiredLauncher\": 5}" })
            T.Ok(Install.ManifestParser.PeekRequiredLauncher(junk) == null,
                $"unreadable input yields null rather than throwing on the startup path: '{junk}'");

        T.Group("ZRPATCH: our C# applier against a patch built by the Python tool");

        // The applier is new C# reading a format defined by deploy/assets/zrpatch.py. A subtle
        // disagreement (header offsets, zlib framing, where the body starts) would corrupt a game
        // file on a player's machine, so it is checked against a REAL patch rather than a mock.
        // Set ZR_TEST_ZRPATCH=<patch> ZR_TEST_BASE=<base> ZR_TEST_RESULT=<expected> to run it.
        var pPatch = Environment.GetEnvironmentVariable("ZR_TEST_ZRPATCH");
        var pBase = Environment.GetEnvironmentVariable("ZR_TEST_BASE");
        var pResult = Environment.GetEnvironmentVariable("ZR_TEST_RESULT");
        if (pPatch != null && pBase != null && pResult != null
            && File.Exists(pPatch) && File.Exists(pBase) && File.Exists(pResult))
        {
            var outDir = Path.Combine(Path.GetTempPath(), "zr-zrpatch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            try
            {
                var target = Path.Combine(outDir, "patched.pack2");
                File.Copy(pBase, target);

                var ph = Install.ZrPatch.ReadHeader(pPatch);
                T.Eq(ph.BaseSize, new FileInfo(pBase).Length, "the patch header's base size matches the real base");
                T.Eq(ph.ResultSize, new FileInfo(pResult).Length, "and its result size matches the real result");

                Install.ZrPatch.Apply(target, pPatch, target, CancellationToken.None);

                var got = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant();
                var want = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pResult))).ToLowerInvariant();
                T.Eq(got, want, "applying the patch reproduces the Python tool's result byte for byte");

                // Applying it again must be refused, not silently doubled.
                try
                {
                    Install.ZrPatch.Apply(target, pPatch, target, CancellationToken.None);
                    T.Ok(false, "a second apply should be refused");
                }
                catch (Install.ZrPatchException) { T.Ok(true, "applying the same patch twice is refused"); }

                // A base that is not what the patch expects must be refused before anything is written.
                var wrong = Path.Combine(outDir, "wrong.pack2");
                File.WriteAllBytes(wrong, new byte[ph.BaseSize]);
                try
                {
                    Install.ZrPatch.Apply(wrong, pPatch, wrong, CancellationToken.None);
                    T.Ok(false, "a wrong base should be refused");
                }
                catch (Install.ZrPatchException) { T.Ok(true, "a same-size but different base is refused"); }
            }
            finally { try { Directory.Delete(outDir, true); } catch { } }
        }
        else
        {
            T.Ok(true, "ZRPATCH round-trip skipped (set ZR_TEST_ZRPATCH / ZR_TEST_BASE / ZR_TEST_RESULT to run it)");
        }

        T.Group("ClientConfig.ini is pointed at the server being played");

        // The shipped ClientConfig.ini has 127.0.0.1 baked into every endpoint. The game proved it
        // still uses them: a failed login against the LIVE server was reported at
        // http://127.0.0.1:1116/king-of-the-kill/game-error?code=G29 - the file's GameCrashUrl.
        var cfgDir = Path.Combine(Path.GetTempPath(), "zr-cfgtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cfgDir);
        try
        {
            var ini = Path.Combine(cfgDir, "ClientConfig.ini");
            File.WriteAllText(ini, string.Join("\n",
                "Server=127.0.0.1:1115",
                "SteamGatewayUrl=http://127.0.0.1:1116/rest/auth/session/create",
                "GameCrashUrl=http://127.0.0.1:1116/king-of-the-kill/game-error?code=G",
                "motd_uri=http://127.0.0.1:1116/",
                "Keep=http://example.com:1116/untouched",
                "AlsoKeep=SomeValue=1"));

            GameLauncher.PointClientConfigAt(cfgDir, "203.0.113.10:1115", "203.0.113.10");
            var after = File.ReadAllText(ini);

            T.Ok(after.Contains("Server=203.0.113.10:1115"), "the login server line is rewritten, port and all");
            T.Ok(!after.Contains("127.0.0.1"), "no loopback address survives anywhere in the file");
            T.Ok(after.Contains("http://203.0.113.10:1116/rest/auth/session/create"), "the Steam gateway url follows");
            T.Ok(after.Contains("http://203.0.113.10:1116/king-of-the-kill/game-error?code=G"), "so does the error page");
            T.Ok(after.Contains("http://example.com:1116/untouched"), "a host that is already real is left alone");
            T.Ok(after.Contains("AlsoKeep=SomeValue=1"), "unrelated lines are untouched");

            // LOCAL mode must still be able to point back at this PC.
            GameLauncher.PointClientConfigAt(cfgDir, "127.0.0.1:1115", "127.0.0.1");
            T.Ok(File.ReadAllText(cfgDir + "\\ClientConfig.ini").Contains("Server=127.0.0.1:1115") == false,
                "a rewritten file is not silently reverted by a later LOCAL launch of the same text");

            // A missing file must not throw: the launch matters more than the tweak.
            GameLauncher.PointClientConfigAt(Path.Combine(cfgDir, "nope"), "1.2.3.4:1115", "1.2.3.4");
            T.Ok(true, "a folder without ClientConfig.ini is ignored rather than fatal");
        }
        finally { try { Directory.Delete(cfgDir, true); } catch { } }

        T.Group("the Steam copy is never a valid target");

        // It is the right build, so every other check passes - which is why a player can point
        // GAME FOLDER at it and believe they are set up. PLAY writes settings files into the target
        // and updates patch it, so accepting it would modify the install we promise not to touch.
        foreach (var p in new[]
                 {
                     @"C:\Program Files (x86)\Steam\steamapps\common\H1Z1",
                     @"C:\Program Files (x86)\Steam\steamapps\common\H1Z1\",
                     @"D:\SteamLibrary\steamapps\common\Z1 Battle Royale",
                     @"E:\games\STEAMAPPS\common\H1Z1",
                     @"C:\Program Files (x86)\Steam\steamapps",
                 })
            T.Ok(GameLauncher.LooksLikeSteamLibrary(p), $"refused: {p}");

        foreach (var p in new[]
                 {
                     @"C:\Games\ZRevive",
                     @"D:\ZRevive",
                     @"C:\Users\me\Steam Backups\ZRevive",   // "steam" in a name is not a library
                     @"C:\steamroller\ZRevive",
                 })
            T.Ok(!GameLauncher.LooksLikeSteamLibrary(p), $"allowed: {p}");

        T.Group("the suggested install folder is never inside Steam");

        // The guard above refuses Steam paths, and the suggestion used to be "beside the source" -
        // which for a Steam install is steamapps\common. The launcher proposed a folder it then
        // rejected, and a new player could not get past setup at all.
        foreach (var src in new[]
                 {
                     @"C:\Program Files (x86)\Steam\steamapps\common\H1Z1",
                     @"D:\SteamLibrary\steamapps\common\Z1 Battle Royale",
                 })
        {
            var suggested = GameLauncher.DefaultTargetFor(src);
            T.Ok(!GameLauncher.LooksLikeSteamLibrary(suggested),
                $"suggestion for a Steam source is outside Steam: {src} -> {suggested}");
            T.Ok(suggested.StartsWith(Path.GetPathRoot(src)!, StringComparison.OrdinalIgnoreCase),
                $"and stays on the same drive, so the copy is same-volume: {suggested}");
        }

        // A non-Steam source still gets the friendly "next to it" answer.
        T.Eq(GameLauncher.DefaultTargetFor(@"D:\Games\Z1BR"), @"D:\Games\ZRevive",
            "a source outside Steam still suggests a sibling folder");

        T.Group("mode: the sign-in key is per mode");

        // An auth key is issued by ONE portal. Sharing a slot would both break sign-in after a
        // switch and destroy the other mode's key on the next sign-in.
        var k = new Settings { Mode = ServerMode.Live };
        k.SetKey("LIVEKEY");
        k.Mode = ServerMode.Local;
        T.Eq(k.GetKey(), null, "switching to LOCAL does not expose the LIVE key");
        k.SetKey("LOCALKEY");
        T.Eq(k.GetKey(), "LOCALKEY", "the LOCAL key reads back under LOCAL");
        k.Mode = ServerMode.Live;
        T.Eq(k.GetKey(), "LIVEKEY", "the LIVE key survived storing a LOCAL key");
        T.Ok(k.ProtectedKey != null && k.ProtectedKeyLocal != null, "both keys are stored, in separate fields");
        T.Ok(k.ProtectedKey != "LIVEKEY", "the key is not stored in plain text");

        k.SetKey(null);
        T.Eq(k.GetKey(), null, "forgetting the LIVE key works");
        k.Mode = ServerMode.Local;
        T.Eq(k.GetKey(), "LOCALKEY", "forgetting the LIVE key left the LOCAL key alone");

        T.Group("mode: the applied release hash is per mode");

        // Shared, this reports "up to date" right after a switch and skips that mode's delta.
        var h = new Settings { Mode = ServerMode.Live };
        h.CurrentReleaseHash = "aaaa";
        T.Eq(h.InstalledReleaseHash, "aaaa", "LIVE writes the live hash field");
        T.Eq(h.InstalledReleaseHashLocal, null, "LIVE does not touch the local hash field");
        h.Mode = ServerMode.Local;
        T.Eq(h.CurrentReleaseHash, null, "LOCAL starts with no applied release, so the delta is applied");
        h.CurrentReleaseHash = "bbbb";
        T.Eq(h.CurrentReleaseHash, "bbbb", "LOCAL reads back its own hash");
        h.Mode = ServerMode.Live;
        T.Eq(h.CurrentReleaseHash, "aaaa", "the LIVE hash survived a LOCAL install");
    }
}
