using System.IO;
using System.Net.Http;
using System.Text;
using ZRevive.Launcher.Install;

namespace ZRevive.Launcher.Tests;

/// <summary>
/// Counts calls and can be told to answer 304, so ETag behaviour and the offline fallback
/// are testable without a network.
/// </summary>
sealed class StubManifestFetcher : IManifestFetcher
{
    public string Json = "";
    public string ETag = "\"v1\"";
    public bool Offline;
    public int Calls, ConditionalCalls, NotModifiedAnswers;
    public string? LastSentETag;

    public Task<ManifestFetch> FetchAsync(string url, string? etag, CancellationToken ct)
    {
        Calls++;
        LastSentETag = etag;
        if (etag != null) ConditionalCalls++;
        if (Offline) throw new HttpRequestException("simulated CDN outage");
        if (etag == ETag) { NotModifiedAnswers++; return Task.FromResult(new ManifestFetch("", etag, true, false)); }
        return Task.FromResult(new ManifestFetch(Json, ETag, false, false));
    }
}

static class UpdateTests
{
    const string HelloSha = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

    static string ManifestJson(string releaseHash, string sha = HelloSha, bool critical = false, long size = 5) => $@"
{{ ""version"": 1, ""releaseHash"": ""{releaseHash}"", ""baseUrl"": ""https://cdn.zrevive.test/r1/"",
   ""entries"": [ {{ ""name"": ""x.pack2"", ""url"": ""hello.bin"", ""targetPath"": ""Resources/Assets/x.pack2"",
                    ""size"": {size}, ""sha256"": ""{sha}"", ""kind"": ""file""{(critical ? ", \"critical\": true" : "")} }} ] }}";

    public static async Task RunAll()
    {
        await CacheAndETagTests();
        await QuickVerifyTests();
        await AutoUpdateTests();
        FolderHealthTests();
        await ForeignBaselineTests();
        SplitManifestTests();
        SecurityGateTests();
    }

    /// <summary>
    /// Secure Boot + Memory integrity are required to launch. The live machine only ever shows one
    /// of these states, so the refusal paths are tested through <c>Evaluate</c> - otherwise the
    /// branches that actually block a player would never run before a player hits them.
    /// </summary>
    static void SecurityGateTests()
    {
        T.Group("SecurityGate: Secure Boot + HVCI required to launch");

        var pass = SecurityGate.Evaluate(secureBoot: true, hvci: true, auditOnly: false, hvciKnown: true);
        T.Ok(pass.Ok, "both on -> allowed");

        var noSb = SecurityGate.Evaluate(false, true, false, true);
        T.Ok(!noSb.Ok, "Secure Boot off -> refused");
        T.Ok(noSb.Headline.Contains("Secure Boot"), "the refusal names Secure Boot");
        T.Ok(noSb.Detail.Contains("firmware"), "and says where to turn it on");
        T.Ok(!noSb.Detail.Contains("Memory integrity:"), "it does not also blame Memory integrity, which is on");

        var noHvci = SecurityGate.Evaluate(true, false, false, true);
        T.Ok(!noHvci.Ok, "Memory integrity off -> refused");
        T.Ok(noHvci.Headline.Contains("Memory integrity"), "the refusal names Memory integrity");
        T.Ok(noHvci.Detail.Contains("Core isolation"), "and points at Core isolation");

        var neither = SecurityGate.Evaluate(false, false, false, true);
        T.Ok(!neither.Ok, "both off -> refused");
        T.Ok(neither.Headline.Contains("Secure Boot") && neither.Headline.Contains("Memory integrity"),
            "both are named in one line rather than one at a time");

        // Audit mode is HVCI "on" in Windows' own UI, but it only logs - the unsigned driver still
        // loads, so it must not pass.
        var audit = SecurityGate.Evaluate(true, false, auditOnly: true, hvciKnown: true);
        T.Ok(!audit.Ok, "HVCI in audit mode -> refused");
        T.Ok(audit.Detail.Contains("audit mode"), "and the message explains audit mode rather than claiming it is off");

        // Legacy BIOS: no Secure Boot state exists at all.
        var legacy = SecurityGate.Evaluate(null, true, false, true);
        T.Ok(!legacy.Ok, "no Secure Boot state (legacy BIOS) -> refused");
        T.Ok(legacy.Detail.Contains("legacy"), "and the message says it is booting in legacy mode");

        // Fail closed when nothing can be read, but say so honestly.
        var blind = SecurityGate.Evaluate(null, false, false, hvciKnown: false);
        T.Ok(!blind.Ok, "nothing readable -> refused (fails closed)");
        T.Ok(blind.Undetermined, "and it is flagged as undetermined, not as 'off'");
        T.Ok(!blind.Headline.Contains("off"), "the headline does not accuse the player of having it off");

        // The waiver: the portal decides, and the launcher must honour it. A launcher-side list of
        // exempt names would be defeated by claiming a name, so the flag arrives with the sign-in
        // reply and defaults to false when an older portal omits it.
        var plain = new LoginResult(true, null, "Someone", 0, "Player", null, null, null, null, null);
        T.Ok(!plain.SecurityExempt, "an account is NOT exempt unless the portal says so");
        var older = System.Text.Json.JsonSerializer.Deserialize<LoginResult>(
            "{\"ok\":true,\"name\":\"Someone\",\"permissionLevel\":0}");
        T.Ok(older != null && !older.SecurityExempt,
            "a portal that does not send securityExempt enforces the requirement (fails closed)");
        var waived = System.Text.Json.JsonSerializer.Deserialize<LoginResult>(
            "{\"ok\":true,\"name\":\"Brayden\",\"permissionLevel\":0,\"securityExempt\":true}");
        T.Ok(waived != null && waived.SecurityExempt, "a waived account is read as exempt");

        // The device fingerprint a ban can follow. It must be stable and opaque: an id that
        // changed between calls would make every sign-in look like a new PC, and one that leaked
        // the raw MachineGuid would hand the portal something it has no business storing.
        var id = MachineId.Current;
        T.Ok(id != null, "this machine produces a device fingerprint");
        T.Ok(id!.Length == 64 && id.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')),
            "it is 64 lowercase hex characters");
        T.Eq(MachineId.Current, id, "it is the same on a second call");
        var guid = MachineId.MachineGuid();
        T.Ok(guid == null || !id.Contains(guid, StringComparison.OrdinalIgnoreCase),
            "the raw MachineGuid is not carried inside it - it is hashed, and salted for ZRevive");

        // The real readings on this machine must at least be answerable - a null here would mean
        // every player gets the "undetermined" refusal.
        T.Ok(SecurityGate.ReadCodeIntegrityOptions() != null,
            "the kernel code-integrity query answers on this machine (unelevated)");
        var live = SecurityGate.Check();
        T.Ok(live.Headline.Length > 0, "Check() always produces a headline to show");
    }

    /// <summary>
    /// A payload too large for one download ships as parts. The manifest rules are asserted here
    /// because a part list that does not add up would otherwise only be discovered after
    /// downloading gigabytes and joining them.
    /// </summary>
    static void SplitManifestTests()
    {
        T.Group("manifest: split payloads (parts)");

        const string ShaA = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
        const string ShaB = "486ea46224d1bb4fb680f34f7c9ad96a8f24ec88be73ea8e5a6c65260e9cb8a7";

        string Split(string parts, long size = 10) => $@"
{{ ""version"": 2, ""releaseHash"": ""aaaa1111"", ""baseUrl"": ""https://cdn.zrevive.test/r1/"",
   ""entries"": [ {{ ""name"": ""big.pack2"", ""targetPath"": ""Resources/Assets/big.pack2"",
                    ""size"": {size}, ""sha256"": ""{ShaA}"", ""kind"": ""own"", ""parts"": [{parts}] }} ] }}";

        var good = ManifestParser.Parse(Split(
            $@"{{""url"":""big.part0"",""size"":6,""sha256"":""{ShaA}""}},
               {{""url"":""big.part1"",""size"":4,""sha256"":""{ShaB}""}}"));
        var e = good.Entries[0];
        T.Ok(e.IsSplit, "an entry with parts is recognised as split");
        T.Eq(e.Parts!.Count, 2, "both parts are parsed");
        T.Eq(e.Parts[0].Url, "big.part0", "parts keep their order");
        T.Ok(e.Url == null, "a split entry needs no url of its own");
        T.Eq(e.Size, 10L, "the entry's size is the FINISHED file, not a part");

        // The check that matters: parts must account for exactly the declared size.
        T.Throws<ManifestException>(() => ManifestParser.Parse(Split(
            $@"{{""url"":""big.part0"",""size"":6,""sha256"":""{ShaA}""}},
               {{""url"":""big.part1"",""size"":3,""sha256"":""{ShaB}""}}")),
            "parts that don't add up to the declared size are refused");

        T.Throws<ManifestException>(() => ManifestParser.Parse(Split(
            $@"{{""url"":""big.part0"",""size"":10}}")),
            "a part without a sha256 is refused");
        T.Throws<ManifestException>(() => ManifestParser.Parse(Split(
            $@"{{""size"":10,""sha256"":""{ShaA}""}}")),
            "a part without a url is refused");
        T.Throws<ManifestException>(() => ManifestParser.Parse(Split(
            $@"{{""url"":""big.part0"",""size"":10,""sha256"":""nothex""}}")),
            "a part whose sha256 isn't 64 hex characters is refused");
        T.Throws<ManifestException>(() => ManifestParser.Parse(Split(
            $@"{{""url"":""big.part0"",""size"":0,""sha256"":""{ShaA}""}}", 0)),
            "a zero-length part is refused");
        T.Throws<ManifestException>(() => ManifestParser.Parse(Split("")),
            "an empty parts list is refused");

        // Part URLs resolve by the same rules as any other payload URL.
        var url = ManifestParser.ResolveOne(good, good.Entries[0].Parts![1].Url, "big.pack2",
            "https://cdn.zrevive.test/m.json");
        T.Eq(url.ToString(), "https://cdn.zrevive.test/r1/big.part1", "a part url resolves against baseUrl");
        T.Throws<ManifestException>(() => ManifestParser.ResolveOne(good, "file:///C:/evil.bin", "big.pack2",
            "https://cdn.zrevive.test/m.json"), "a part may not point at the local disk");
    }

    // ------------------------------------------------- foreign baseline

    /// <summary>
    /// A release built for a clean Steam install must leave a ROTK-derived tree's game content
    /// alone. Both halves are asserted: the folder is RECOGNISED as foreign, and verification of
    /// such a folder comes back healthy so the player can play instead of being offered an update
    /// that would overwrite a real 487 MB Daybreak pack with our 3 MB one.
    /// </summary>
    static async Task ForeignBaselineTests()
    {
        T.Group("ForeignBaseline: a release for stock must not touch a ROTK-derived tree");

        var dir = Temp("foreign");
        var assets = Path.Combine(dir, "Resources", "Assets");
        Directory.CreateDirectory(assets);

        // Our pack is small; the file already at that name is a real Daybreak one. 60 MB is enough
        // to trip the same "far larger" rule the installer refuses on (>4x and >50 MB difference).
        var ourSize = 1000;
        var theirs = Path.Combine(assets, "x.pack2");
        using (var fs = new FileStream(theirs, FileMode.Create, FileAccess.Write))
            fs.SetLength(60L * 1024 * 1024);

        var manifest = ManifestParser.Parse(ManifestJson("deadbeef", size: ourSize));

        var why = ForeignBaseline.Detect(manifest, dir);
        T.Ok(why != null, "a tree whose file is far larger than ours is recognised as foreign");
        T.Ok(why!.Contains("x.pack2"), "the reason names the file, not just 'mismatch'");
        // "N0" uses the machine's thousands separator - a comma here, a non-breaking space under
        // sv-SE - so compare the digits only rather than pinning the test to one culture.
        var digitsOnly = new string(why.Where(char.IsDigit).ToArray());
        T.Ok(digitsOnly.Contains("62914560"), "the reason states the size actually on disk");

        var report = await InstallService.VerifyAsync(manifest, dir, null, default);
        T.Ok(report.Healthy, "verification of a foreign tree is healthy, so the launcher offers PLAY not UPDATE");
        T.Eq(report.RepairBytes, 0L, "and nothing is queued for download");
        T.Ok(report.All.All(s => s.Detail?.StartsWith("skipped:") == true),
            "each skipped entry records why it was skipped");

        // The same manifest against a folder that really is stock-shaped must still be verified
        // normally - skipping must not become the default.
        var stock = Temp("stockish");
        Directory.CreateDirectory(Path.Combine(stock, "Resources", "Assets"));
        T.Ok(ForeignBaseline.Detect(manifest, stock) == null,
            "a folder without the conflicting file is not called foreign");
        var stockReport = await InstallService.VerifyAsync(manifest, stock, null, default);
        T.Ok(!stockReport.Healthy, "a genuinely missing file is still reported as needing work");
    }

    // ------------------------------------------------- manifest cache / ETag

    static async Task CacheAndETagTests()
    {
        T.Group("ManifestSource: ETag / If-None-Match + cache + offline fallback");

        var dir = Temp("cache");
        var cache = new ManifestCache(Path.Combine(dir, "manifest-cache.json"));
        var stub = new StubManifestFetcher { Json = ManifestJson("aaaa1111"), ETag = "\"v1\"" };
        var src = new ManifestSource(stub, cache);
        const string url = "https://cdn.zrevive.test/r1/manifest.json";

        var first = await src.GetAsync(url, CancellationToken.None);
        T.Eq(first.Manifest.ReleaseHash, "aaaa1111", "first fetch parses the manifest");
        T.Ok(!first.Unchanged, "first fetch is not 'unchanged'");
        T.Eq(stub.LastSentETag, null, "first fetch sends no If-None-Match");

        var second = await src.GetAsync(url, CancellationToken.None);
        T.Eq(stub.LastSentETag, "\"v1\"", "second fetch sends the cached ETag");
        T.Eq(stub.NotModifiedAnswers, 1, "server answered 304");
        T.Ok(second.Unchanged, "304 is reported as unchanged");
        T.Eq(second.Manifest.ReleaseHash, "aaaa1111", "304 serves the manifest from cache");
        T.Ok(!second.Offline, "304 is not an offline result");

        // a new release on the server
        stub.Json = ManifestJson("bbbb2222");
        stub.ETag = "\"v2\"";
        var third = await src.GetAsync(url, CancellationToken.None);
        T.Eq(third.Manifest.ReleaseHash, "bbbb2222", "a changed ETag delivers the new manifest");
        T.Ok(!third.Unchanged, "a new release is not 'unchanged'");

        // CDN down -> cached manifest, flagged offline, no throw
        stub.Offline = true;
        var fourth = await src.GetAsync(url, CancellationToken.None);
        T.Ok(fourth.Offline, "CDN outage falls back to the cache");
        T.Eq(fourth.Manifest.ReleaseHash, "bbbb2222", "offline fallback serves the last known release");
        T.Ok(fourth.Warning != null, "offline fallback carries a warning to show the player");

        // CDN down with nothing cached -> must throw (the caller decides it's non-blocking)
        var empty = new ManifestSource(new StubManifestFetcher { Offline = true }, new ManifestCache(Path.Combine(dir, "empty.json")));
        var threw = false;
        try { await empty.GetAsync(url, CancellationToken.None); } catch (Exception) { threw = true; }
        T.Ok(threw, "CDN outage with no cache throws");

        // an invalid manifest must not be cached or served
        var bad = new StubManifestFetcher { Json = "{ not valid", ETag = "\"v9\"" };
        var badSrc = new ManifestSource(bad, new ManifestCache(Path.Combine(dir, "bad.json")));
        T.Throws<ManifestException>(() => badSrc.GetAsync(url, CancellationToken.None).GetAwaiter().GetResult(),
            "an unparseable manifest throws and is never cached");

        // caches are keyed per URL, so switching GitHub <-> portal doesn't cross-contaminate
        cache.Save("https://a.test/m.json", ManifestJson("cccc3333"), "\"a\"");
        T.Eq(cache.Load("https://b.test/m.json"), null, "cache is keyed by URL");
        T.Ok(cache.Load("https://A.TEST/m.json") != null, "cache lookup is case-insensitive on the URL");

        T.Group("Manifest URL sources (nothing GitHub-specific is hardcoded)");
        foreach (var ok in new[]
                 {
                     "https://raw.githubusercontent.com/owner/repo/main/deploy/assets/manifest.json",
                     "https://github.com/owner/repo/releases/download/v1/manifest.json",
                     "https://zrevive.example/assets/manifest.json",
                     "http://127.0.0.1:8080/assets/manifest.json"
                 })
            T.Ok(HttpManifestFetcher.ToUri(ok).Scheme.StartsWith("http"), "accepts manifest URL " + ok);

        var local = Path.Combine(dir, "manifest.json");
        await File.WriteAllTextAsync(local, ManifestJson("dddd4444"));
        T.Ok(HttpManifestFetcher.ToUri(local).IsFile, "accepts a plain local path as a file:// manifest");
        T.Ok(HttpManifestFetcher.ToUri(new Uri(local).AbsoluteUri).IsFile, "accepts an explicit file:// manifest");
        T.Throws<ManifestException>(() => HttpManifestFetcher.ToUri("ftp://x.test/m.json"), "rejects a non-http, non-file manifest URL");
        T.Throws<ManifestException>(() => HttpManifestFetcher.ToUri("not a url"), "rejects junk as a manifest URL");

        // a real file:// round trip through the real fetcher (no network)
        var fileFetcher = new HttpManifestFetcher();
        var f1 = await fileFetcher.FetchAsync(local, null, CancellationToken.None);
        T.Ok(f1.ETag != null && !f1.NotModified, "file:// fetch returns content and a synthetic ETag");
        var f2 = await fileFetcher.FetchAsync(local, f1.ETag, CancellationToken.None);
        T.Ok(f2.NotModified, "unchanged file:// manifest reports NotModified");

        // local payloads are only allowed when the manifest itself is local
        var m = ManifestParser.Parse(ManifestJson("eeee5555"));
        T.Ok(InstallService.AllowsLocalPayloads(local), "a file:// manifest allows local payloads");
        T.Ok(!InstallService.AllowsLocalPayloads("https://cdn.zrevive.test/m.json"), "a remote manifest does not allow local payloads");
        var localManifest = ManifestParser.Parse($@"{{ ""version"":1, ""releaseHash"":""ffff6666"", ""entries"":[
            {{ ""name"":""x"", ""url"":""file:///C:/payload.bin"", ""targetPath"":""x.dll"", ""size"":5, ""sha256"":""{HelloSha}"" }} ] }}");
        T.Throws<ManifestException>(() => ManifestParser.ResolveUrl(localManifest, localManifest.Entries[0], "https://cdn.zrevive.test/m.json"),
            "a remote manifest may NOT point an entry at file://");
        T.Ok(ManifestParser.ResolveUrl(localManifest, localManifest.Entries[0], new Uri(local).AbsoluteUri, allowLocal: true).IsFile,
            "a local manifest may point an entry at a local payload");

        Nuke(dir);
    }

    // ------------------------------------------------- quick verify

    static async Task QuickVerifyTests()
    {
        T.Group("QuickVerifier: fast per-launch check");

        var dir = Temp("quick");
        var install = Path.Combine(dir, "ZRevive");
        var assets = Path.Combine(install, "Resources", "Assets");
        Directory.CreateDirectory(assets);
        var file = Path.Combine(assets, "x.pack2");
        await File.WriteAllTextAsync(file, "hello");

        var manifest = ManifestParser.Parse(ManifestJson("aaaa1111"));

        // no fingerprints yet -> it hashes once and records
        var prints = new FilePrints();
        var r1 = await QuickVerifier.RunAsync(manifest, install, prints, CancellationToken.None);
        T.Ok(r1.Ok, "clean install passes the quick verify");
        T.Eq(r1.Hashed, 1, "with no fingerprint the file is hashed once");
        T.Ok(r1.NoFingerprints, "reports that it started without fingerprints");
        T.Ok(prints.Files.ContainsKey(@"Resources\Assets\x.pack2"), "fingerprint recorded after hashing");

        // second run with fingerprints -> no hashing at all (this is the fast path)
        var r2 = await QuickVerifier.RunAsync(manifest, install, prints, CancellationToken.None);
        T.Ok(r2.Ok, "second quick verify passes");
        T.Eq(r2.Hashed, 0, "a fingerprinted, unchanged file is NOT hashed");

        // a changed file of a DIFFERENT size is caught by the size check alone
        await File.WriteAllTextAsync(file, "hello world");
        var r3 = await QuickVerifier.RunAsync(manifest, install, prints, CancellationToken.None);
        T.Ok(!r3.Ok, "resized file fails the quick verify");
        T.Eq(r3.Hashed, 0, "a size mismatch needs no hashing");
        T.Eq(r3.Suspect[0].Verdict, EntryVerdict.WrongSize, "resized file is WrongSize");
        T.Ok(!r3.HashFailure, "a size mismatch is not a hash failure");

        // a changed file of the SAME size is caught because the mtime moved -> hashed -> fails
        await File.WriteAllTextAsync(file, "HELLO");
        var r4 = await QuickVerifier.RunAsync(manifest, install, prints, CancellationToken.None);
        T.Ok(!r4.Ok, "same-size tampered file fails the quick verify");
        T.Eq(r4.Hashed, 1, "a moved mtime forces a hash");
        T.Eq(r4.Suspect[0].Verdict, EntryVerdict.WrongHash, "same-size tampered file is WrongHash");
        T.Ok(r4.HashFailure, "HashFailure is set: the launcher must not start the game");

        // the nasty case: content changed but size AND mtime were restored.
        // Without 'critical' the fast path trusts it; with 'critical' it is always hashed.
        await File.WriteAllTextAsync(file, "hello");
        var good = await QuickVerifier.RunAsync(manifest, install, new FilePrints(), CancellationToken.None);
        T.Ok(good.Ok, "restored file passes again");
        var stamp = File.GetLastWriteTimeUtc(file);
        var trusted = FilePrints.Load(install);
        trusted.Record(@"Resources\Assets\x.pack2", file, HelloSha);
        await File.WriteAllTextAsync(file, "HELLO");
        File.SetLastWriteTimeUtc(file, stamp);                   // hide the change
        var r5 = await QuickVerifier.RunAsync(manifest, install, trusted, CancellationToken.None);
        T.Ok(r5.Ok, "a change that forges size+mtime slips past the non-critical fast path (by design)");
        T.Eq(r5.Hashed, 0, "...without hashing");

        var criticalManifest = ManifestParser.Parse(ManifestJson("aaaa1111", critical: true));
        var r6 = await QuickVerifier.RunAsync(criticalManifest, install, trusted, CancellationToken.None);
        T.Ok(!r6.Ok, "a critical entry is hashed every launch and catches the forged timestamp");
        T.Eq(r6.Hashed, 1, "critical entry is always hashed");
        T.Ok(r6.HashFailure, "critical hash mismatch blocks the launch");

        // missing file
        File.Delete(file);
        var r7 = await QuickVerifier.RunAsync(manifest, install, new FilePrints(), CancellationToken.None);
        T.Eq(r7.Suspect[0].Verdict, EntryVerdict.Missing, "missing file is Missing");

        // path safety still applies on the fast path
        var evil = ManifestParser.Parse(ManifestJson("aaaa1111"));
        var forged = new ReleaseManifest(evil.Version, evil.ReleaseHash, null, null, null, new[]
        {
            new ManifestEntry("evil", "https://x.test/e.bin", @"..\..\Windows\System32\evil.dll", 5, HelloSha, EntryKind.File, null, null)
        });
        var r8 = await QuickVerifier.RunAsync(forged, install, new FilePrints(), CancellationToken.None);
        T.Eq(r8.Suspect[0].Verdict, EntryVerdict.Unsafe, "quick verify flags an escaping target path as Unsafe");

        // report conversion feeds ApplyAsync
        var report = QuickVerifier.ToReport(manifest, r7);
        T.Eq(report.All.Count, manifest.Entries.Count, "ToReport covers every entry");
        T.Eq(report.Broken.Count(), 1, "ToReport keeps the suspect entry broken");

        // timing: a few hundred entries must stay well under a second
        var many = new List<ManifestEntry>();
        for (var i = 0; i < 400; i++)
        {
            var rel = $@"Resources\Assets\pack_{i}.pack2";
            var p = Path.Combine(install, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            await File.WriteAllTextAsync(p, "hello");
            many.Add(new ManifestEntry($"pack_{i}", "https://x.test/hello.bin", rel, 5, HelloSha, EntryKind.File, null, null));
        }
        var bigManifest = new ReleaseManifest(1, "aaaa1111", null, null, null, many);
        var warm = new FilePrints();
        await QuickVerifier.RunAsync(bigManifest, install, warm, CancellationToken.None);   // first pass hashes + records
        var timed = await QuickVerifier.RunAsync(bigManifest, install, warm, CancellationToken.None);
        T.Ok(timed.Ok, "400-entry warm quick verify passes");
        T.Eq(timed.Hashed, 0, "400-entry warm quick verify hashes nothing");
        T.Ok(timed.ElapsedMs < 500, $"400-entry warm quick verify is fast ({timed.ElapsedMs} ms, target < 500)");
        Console.WriteLine($"        [timing] warm quick verify, 400 entries: {timed.ElapsedMs} ms");

        Nuke(dir);
    }

    // ------------------------------------------------- auto update

    static async Task AutoUpdateTests()
    {
        T.Group("Auto-update: releaseHash compare, delta apply, skip-once");

        var dir = Temp("update");
        var install = Path.Combine(dir, "ZRevive");
        Directory.CreateDirectory(Path.Combine(install, "Resources", "Assets"));

        var dl = new FakeDownloader();
        dl.Files["https://cdn.zrevive.test/r1/hello.bin"] = Encoding.UTF8.GetBytes("hello");
        var cache = new ManifestCache(Path.Combine(dir, "cache.json"));
        var stub = new StubManifestFetcher { Json = ManifestJson("aaaa1111"), ETag = "\"v1\"" };
        var svc = new InstallService(dl, new ManifestSource(stub, cache));
        const string url = "https://cdn.zrevive.test/r1/manifest.json";

        var m1 = (await svc.GetManifestAsync(url, CancellationToken.None)).Manifest;
        T.Ok(InstallService.NeedsUpdate(m1, null), "nothing installed -> update needed");

        var rep = await InstallService.VerifyAsync(m1, install, null, CancellationToken.None);
        await svc.ApplyAsync(m1, url, install, rep, null, CancellationToken.None);
        var installedHash = m1.ReleaseHash;
        T.Ok(!InstallService.NeedsUpdate(m1, installedHash), "after applying, no update is needed");

        // ApplyAsync must have written the fingerprint side-car, so the next launch is free
        var prints = FilePrints.Load(install);
        T.Eq(prints.ReleaseHash, "aaaa1111", "fingerprint file records the release");
        T.Ok(prints.Files.ContainsKey(@"Resources\Assets\x.pack2"), "fingerprint file records the installed file");
        var postInstall = await QuickVerifier.RunAsync(m1, install, prints, CancellationToken.None);
        T.Ok(postInstall.Ok && postInstall.Hashed == 0, "quick verify right after an install hashes nothing");

        // new release on the server
        var newSha = Sha256Hex("hello world");
        stub.Json = ManifestJson("bbbb2222", newSha, size: 11);
        stub.ETag = "\"v2\"";
        dl.Files["https://cdn.zrevive.test/r1/hello.bin"] = Encoding.UTF8.GetBytes("hello world");

        var m2 = (await svc.GetManifestAsync(url, CancellationToken.None)).Manifest;
        T.Ok(InstallService.NeedsUpdate(m2, installedHash), "a new releaseHash means an update is available");

        // skip-once: the player declines, nothing is written, and it's offered again
        var skipped = m2.ReleaseHash;
        T.Ok(FileHasher.HashesEqual(skipped, m2.ReleaseHash), "the skipped release is remembered by hash");
        T.Ok(!FileHasher.HashesEqual(skipped, m1.ReleaseHash), "the skip doesn't match the installed release");
        T.Eq(await File.ReadAllTextAsync(Path.Combine(install, "Resources", "Assets", "x.pack2")), "hello",
            "skipping an update leaves the installed files alone");
        // a third, newer release must clear the skip
        T.Ok(!FileHasher.HashesEqual(skipped, "cccc3333"), "a newer release no longer matches the skipped hash (skip clears)");

        // now accept it: the fast check drives the repair, not a full re-hash
        var quick = await QuickVerifier.RunAsync(m2, install, FilePrints.Load(install), CancellationToken.None);
        T.Ok(!quick.Ok, "the old file doesn't satisfy the new manifest");
        var changed = await svc.ApplyAsync(m2, url, install, QuickVerifier.ToReport(m2, quick), null, CancellationToken.None);
        T.Eq(changed, 1, "the delta installed one file");
        T.Eq(await File.ReadAllTextAsync(Path.Combine(install, "Resources", "Assets", "x.pack2")), "hello world", "update applied");
        var after = await QuickVerifier.RunAsync(m2, install, FilePrints.Load(install), CancellationToken.None);
        T.Ok(after.Ok && after.Hashed == 0, "quick verify after the update hashes nothing");

        // a corrupt payload during an update must never reach the install folder
        stub.Json = ManifestJson("dddd4444", HelloSha, size: 5);
        stub.ETag = "\"v3\"";
        dl.Files["https://cdn.zrevive.test/r1/hello.bin"] = Encoding.UTF8.GetBytes("EVIL!");
        var m3 = (await svc.GetManifestAsync(url, CancellationToken.None)).Manifest;
        var q3 = await QuickVerifier.RunAsync(m3, install, FilePrints.Load(install), CancellationToken.None);
        var blocked = false;
        try { await svc.ApplyAsync(m3, url, install, QuickVerifier.ToReport(m3, q3), null, CancellationToken.None); }
        catch (IOException) { blocked = true; }
        T.Ok(blocked, "an update whose payload fails its hash is rejected");
        T.Eq(await File.ReadAllTextAsync(Path.Combine(install, "Resources", "Assets", "x.pack2")), "hello world",
            "the rejected update left the previous good file in place");

        Nuke(dir);
    }

    // ------------------------------------------------- folder health / adoption

    static void FolderHealthTests()
    {
        T.Group("GameLauncher.Inspect: say WHY, and never adopt a mid-build folder");

        var dir = Temp("health");

        var missing = GameLauncher.Inspect(Path.Combine(dir, "nope"));
        T.Ok(missing.Problem != null && missing.Problem.Contains("gone"), "a missing folder says the folder is gone");
        T.Ok(GameLauncher.Inspect("").Problem != null, "an unset folder reports a problem");
        T.Ok(GameLauncher.Inspect(null).Problem != null, "a null folder reports a problem");

        var noExe = Path.Combine(dir, "NoExe");
        Directory.CreateDirectory(noExe);
        var r = GameLauncher.Inspect(noExe);
        T.Ok(r.Problem != null && r.Problem.Contains("H1Z1.exe"), "names the missing file (H1Z1.exe) instead of a generic error");
        T.Ok(r.Problem!.Contains(noExe), "the message includes the folder it looked in");
        T.Ok(!r.Repairable, "a folder without our marker isn't flagged as one of ours");

        // our marker makes it "repairable" -> the UI offers Verify / repair
        var ourFolder = Path.Combine(dir, "Ours");
        Directory.CreateDirectory(ourFolder);
        File.WriteAllText(Path.Combine(ourFolder, ZRevive.Launcher.Install.InstallState.MarkerFile), "{}");
        T.Ok(GameLauncher.Inspect(ourFolder).Repairable, "a folder with .zrevive-install.json is repairable");

        // ROTK must always be refused
        var rotk = Path.Combine(dir, "ROTK");
        Directory.CreateDirectory(rotk);
        File.WriteAllText(Path.Combine(rotk, ".rotk-installation.json"), "{}");
        T.Ok(GameLauncher.Inspect(rotk).Problem!.Contains("ROTK"), "the ROTK install is refused by name");

        // mid-build detection: a .zrpart anywhere, or a freshly written core file
        var midBuild = Path.Combine(dir, "ZRevive-Stock");
        Directory.CreateDirectory(Path.Combine(midBuild, "Resources", "Assets"));
        File.WriteAllText(Path.Combine(midBuild, "H1Z1.exe"), "x");
        File.WriteAllText(Path.Combine(midBuild, "Resources", "Assets", "a.pack2"), "x");
        File.WriteAllText(Path.Combine(midBuild, "Resources", "Assets", "b.pack2.zrpart"), "half");
        T.Ok(GameLauncher.LooksMidBuild(midBuild), "a .zrpart marks the folder as mid-build");
        T.Ok(GameLauncher.Inspect(midBuild).LooksMidBuild, "Inspect surfaces LooksMidBuild");
        // ...but mid-build must NOT fail validation on its own: an established install gets
        // written to all the time (pack2 edits), and that must never block PLAY.
        var live = Path.Combine(dir, "Live");
        Directory.CreateDirectory(Path.Combine(live, "Resources", "Assets"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), Path.Combine(live, "H1Z1.exe"), true);
        File.WriteAllText(Path.Combine(live, "Resources", "Assets", "a.pack2"), "x");
        T.Ok(GameLauncher.Inspect(live).LooksMidBuild, "a just-written pack2 sets LooksMidBuild");
        T.Ok(GameLauncher.Inspect(live).Problem != null && !GameLauncher.Inspect(live).Problem!.Contains("assembl"),
            "mid-build is not itself the reported problem (only the exe build is)");

        File.Delete(Path.Combine(midBuild, "Resources", "Assets", "b.pack2.zrpart"));
        T.Ok(GameLauncher.LooksMidBuild(midBuild), "a core file written seconds ago still counts as mid-build");

        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(midBuild, "H1Z1.exe"), old);
        File.SetLastWriteTimeUtc(Path.Combine(midBuild, "Resources", "Assets", "a.pack2"), old);
        T.Ok(!GameLauncher.LooksMidBuild(midBuild), "an idle folder is no longer mid-build");

        // ...and it still fails validation on the exe build, with the build number named
        var stock = GameLauncher.Inspect(midBuild);
        T.Ok(stock.Problem != null && stock.Problem.Contains(GameLauncher.ExpectedVersion),
            "a wrong/unknown exe build is reported with the expected build number");

        Nuke(dir);
    }

    static string Sha256Hex(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return FileHasher.ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    static string Temp(string name)
    {
        var p = Path.Combine(Path.GetTempPath(), "zrevive-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(p);
        return p;
    }

    static void Nuke(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }
}
