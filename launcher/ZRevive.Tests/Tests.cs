using System.IO;
using System.Text;
using ZRevive.Launcher.Install;

namespace ZRevive.Launcher.Tests;

static class T
{
    static int _pass, _fail;
    static string _group = "";

    public static void Group(string name) { _group = name; Console.WriteLine("\n== " + name); }

    public static void Ok(bool condition, string what)
    {
        if (condition) { _pass++; Console.WriteLine("  PASS  " + what); }
        else { _fail++; Console.WriteLine("  FAIL  " + what + "   [" + _group + "]"); }
    }

    public static void Eq<TV>(TV actual, TV expected, string what) =>
        Ok(EqualityComparer<TV>.Default.Equals(actual, expected), $"{what} (got {actual}, expected {expected})");

    public static void Throws<TEx>(Action a, string what) where TEx : Exception
    {
        try { a(); Ok(false, what + " (nothing thrown)"); }
        catch (TEx) { Ok(true, what); }
        catch (Exception ex) { Ok(false, what + $" (threw {ex.GetType().Name}: {ex.Message})"); }
    }

    public static int Finish()
    {
        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}

sealed class SyncProgress<TP> : IProgress<TP>
{
    readonly Action<TP> _on;
    public SyncProgress(Action<TP> on) => _on = on;
    public void Report(TP value) => _on(value);
}

/// <summary>Stands in for the CDN: no network is touched anywhere in these tests.</summary>
sealed class FakeDownloader : IDownloader
{
    public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
    public string Manifest = "";
    public readonly List<string> Requested = new();

    public Task<string> GetStringAsync(Uri url, CancellationToken ct) => Task.FromResult(Manifest);

    public async Task DownloadAsync(Uri url, string destination, long expectedSize, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Requested.Add(url.AbsoluteUri);
        if (!Files.TryGetValue(url.AbsoluteUri, out var bytes)) throw new IOException("404 " + url);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, bytes, ct);
        progress?.Report(new DownloadProgress(bytes.Length, bytes.Length, 1000));
    }
}

static class Program
{
    const string Root = @"C:\Games\ZRevive";
    const string HelloSha = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"; // sha256("hello")

    static async Task<int> Main(string[] args)
    {
        // "validate <manifest.json>": read a real manifest through the launcher's own parser before
        // it is published. Python can write a manifest that looks fine and still trips a rule the
        // launcher enforces - and a release carries 6.8 GB behind it, so this check is cheap.
        if (args.Length == 2 && args[0] == "validate")
            return ValidateManifest(args[1]);

        PathSafetyTests();
        ManifestTests();
        await HashTests();
        MiscTests();
        await BuilderTests();
        await ApplyTests();
        await UpdateTests.RunAll();
        ModeTests.RunAll();
        await KeyPromptTests.RunAll();
        return T.Finish();
    }

    /// <summary>
    /// Parses a manifest exactly as the launcher would and prints what it found, so a release is
    /// checked against the code that will consume it rather than against the code that wrote it.
    /// Every payload URL is resolved too, since a url that fails the scheme rules would otherwise
    /// only be discovered on a player's machine, mid-install.
    /// </summary>
    static int ValidateManifest(string path)
    {
        ReleaseManifest m;
        try
        {
            m = ManifestParser.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"REJECTED: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"version          {m.Version}");
        Console.WriteLine($"releaseHash      {m.ReleaseHash}");
        Console.WriteLine($"requiredLauncher {m.RequiredLauncher}");
        Console.WriteLine($"baseUrl          {m.BaseUrl}");
        Console.WriteLine($"entries          {m.Entries.Count}");

        long total = 0;
        var problems = 0;
        foreach (var e in m.Entries)
        {
            total += e.Size;
            try
            {
                if (e.IsSplit)
                    foreach (var p in e.Parts!)
                        ManifestParser.ResolveOne(m, p.Url, e.Name, path);
                else if (e.Kind != EntryKind.Delete)
                    ManifestParser.ResolveUrl(m, e, path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  BAD URL  {e.Name}: {ex.Message}");
                problems++;
            }
            if (e.IsSplit)
                Console.WriteLine($"  split    {e.Name,-28} {e.Size,13:N0} in {e.Parts!.Count} parts");
        }

        Console.WriteLine($"total payload    {total / 1073741824.0:N2} GB");
        Console.WriteLine(problems == 0 ? "\nOK: the launcher accepts this manifest"
                                        : $"\n{problems} problem(s)");
        return problems == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- path safety

    static void PathSafetyTests()
    {
        T.Group("PathSafety: a manifest must never write outside the install folder");

        void Reject(string? rel, string why)
        {
            var ok = PathSafety.TryResolve(Root, rel, out _, out var err);
            T.Ok(!ok, $"rejects {Show(rel)} — {why}" + (ok ? "" : $" [{err}]"));
        }

        void Accept(string rel, string expected)
        {
            var ok = PathSafety.TryResolve(Root, rel, out var full, out var err);
            T.Ok(ok && string.Equals(full, expected, StringComparison.OrdinalIgnoreCase),
                $"accepts {Show(rel)} -> {(ok ? full : "rejected: " + err)}");
        }

        // escapes
        Reject("..\\evil.dll", "parent traversal");
        Reject("../evil.dll", "parent traversal, forward slashes");
        Reject("Resources\\..\\..\\evil.dll", "traversal in the middle");
        Reject("a/b/../../../../Windows/System32/evil.dll", "deep traversal");
        Reject("..", "bare ..");
        Reject(".\\x.dll", "single dot segment");
        // absolute / rooted
        Reject(@"C:\Windows\System32\evil.dll", "absolute path with drive");
        Reject(@"\Windows\evil.dll", "rooted path");
        Reject("/etc/passwd", "rooted posix path");
        Reject(@"\\server\share\evil.dll", "UNC path");
        Reject(@"C:evil.dll", "drive-relative path");
        Reject("file.txt:hidden", "alternate data stream");
        // windows oddities
        Reject("nul", "reserved device name");
        Reject("COM1.txt", "reserved device with extension");
        Reject("sub\\PRN", "reserved device in a subfolder");
        Reject("folder.\\x.dll", "segment ending in a dot");
        Reject("trailing \\x.dll", "segment ending in a space");
        Reject("x.dll.", "file ending in a dot");
        Reject("a\\\\b.dll", "empty segment");
        Reject("bad\0name", "NUL byte");
        Reject("bad\tname", "control character");
        Reject("*.pack2", "wildcard");
        Reject("a?b", "wildcard");
        Reject("", "empty");
        Reject(null, "null");
        Reject("   ", "whitespace only");

        // legitimate entries
        Accept("H1Z1.exe", @"C:\Games\ZRevive\H1Z1.exe");
        Accept("Resources/Assets/assets_x64_0.pack2", @"C:\Games\ZRevive\Resources\Assets\assets_x64_0.pack2");
        Accept("Resources\\Assets\\data_x64_0.pack2", @"C:\Games\ZRevive\Resources\Assets\data_x64_0.pack2");
        Accept("a/b/c/d/e.txt", @"C:\Games\ZRevive\a\b\c\d\e.txt");
        Accept("file.with.many.dots.dll", @"C:\Games\ZRevive\file.with.many.dots.dll");
        Accept("console.dll", @"C:\Games\ZRevive\console.dll"); // "console" != device "CON"

        T.Ok(!PathSafety.TryResolve("", "H1Z1.exe", out _, out _), "rejects an empty install root");

        // sibling-prefix trap: C:\Games\ZRevive2 must not count as inside C:\Games\ZRevive
        T.Ok(!PathSafety.IsSameOrInside(@"C:\Games\ZRevive", @"C:\Games\ZRevive2"), "IsSameOrInside: sibling with a shared prefix is outside");
        T.Ok(PathSafety.IsSameOrInside(@"C:\Games\ZRevive", @"C:\Games\ZRevive\Resources"), "IsSameOrInside: child is inside");
        T.Ok(PathSafety.IsSameOrInside(@"C:\Games\ZRevive", @"C:\Games\ZRevive\"), "IsSameOrInside: same folder with trailing slash");
        T.Ok(PathSafety.IsSameOrInside(@"c:\games\zrevive", @"C:\Games\ZRevive\x"), "IsSameOrInside: case-insensitive");
        T.Ok(!PathSafety.IsSameOrInside(@"C:\Games\ZRevive\Resources", @"C:\Games\ZRevive"), "IsSameOrInside: parent is not inside the child");
    }

    static string Show(string? s) => s == null ? "<null>" : "\"" + s.Replace("\0", "\\0").Replace("\t", "\\t") + "\"";

    // ---------------------------------------------------------------- manifest

    static string Manifest(string entries, string extra = "") => $@"
{{ ""version"": 1, ""releaseHash"": ""abcdef0123456789"", ""gameVersion"": ""1.0.326.439939"",
  ""baseUrl"": ""https://cdn.zrevive.test/r1/"" {extra}, ""entries"": [ {entries} ] }}";

    static string Entry(string target, string? sha = HelloSha, string kind = "file", string url = "hello.bin", long size = 5) =>
        $@"{{ ""name"": ""{Path.GetFileName(target)}"", ""url"": ""{url}"", ""targetPath"": ""{target}"",
              ""size"": {size}, ""sha256"": ""{sha}"", ""kind"": ""{kind}"" }}";

    static void ManifestTests()
    {
        T.Group("ManifestParser: untrusted input");

        var m = ManifestParser.Parse(Manifest(Entry("Resources/Assets/x.pack2")));
        T.Eq(m.Version, 1, "version parsed");
        T.Eq(m.ReleaseHash, "abcdef0123456789", "releaseHash lower-cased");
        T.Eq(m.Entries.Count, 1, "one entry");
        T.Eq(m.Entries[0].TargetPath, @"Resources\Assets\x.pack2", "targetPath normalised to backslashes");
        T.Eq(m.Entries[0].Kind, EntryKind.File, "default kind is file");
        T.Eq(m.Entries[0].Size, 5L, "size parsed");

        T.Throws<ManifestException>(() => ManifestParser.Parse(""), "empty manifest rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse("not json"), "garbage rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse("[]"), "array root rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(@"{""releaseHash"":""aabbccdd"",""entries"":[]}"), "missing version rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(@"{""version"":1,""entries"":[]}"), "missing releaseHash rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(@"{""version"":1,""releaseHash"":""zzzz zzzz"",""entries"":[]}"), "non-hex releaseHash rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(@"{""version"":1,""releaseHash"":""aabbccdd""}"), "missing entries rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(@"{""version"":1,""releaseHash"":""aabbccdd"",""entries"":[]}"), "empty entries rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(@"{""version"":99,""releaseHash"":""aabbccdd"",""entries"":[]}"), "future manifest version rejected");

        // path safety is enforced at parse time too, so a bad release never gets as far as disk
        T.Throws<ManifestException>(() => ManifestParser.Parse(Manifest(Entry("../../Windows/System32/evil.dll"))), "traversal targetPath rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(Manifest(Entry(@"C:/Windows/evil.dll"))), "absolute targetPath rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(Manifest(Entry("nul"))), "device targetPath rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(Manifest(Entry("x.dll", sha: "nothex"))), "bad sha256 rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(Manifest(Entry("x.dll", sha: "abcd"))), "short sha256 rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(Manifest(Entry("x.dll", size: -1))), "negative size rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(Manifest(Entry("x.dll", kind: "exec"))), "unknown kind rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(Manifest($"{Entry("a/x.dll")},{Entry("a\\\\x.dll")}")), "duplicate targetPath rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(Manifest(@"{""name"":""x"",""targetPath"":""x.dll"",""size"":5,""sha256"":""" + HelloSha + @"""}")), "entry without url rejected");
        T.Throws<ManifestException>(() => ManifestParser.Parse(Manifest(@"{""name"":""x"",""targetPath"":""x.dll"",""url"":""u"",""sha256"":""" + HelloSha + @"""}")), "entry without size rejected");

        // delete entries need no payload
        var del = ManifestParser.Parse(Manifest(@"{ ""name"":""steam_api64.dll"", ""targetPath"":""steam_api64.dll"", ""kind"":""delete"" }"));
        T.Eq(del.Entries[0].Kind, EntryKind.Delete, "delete entry parses without sha/url/size");

        // patch entries
        var patch = ManifestParser.Parse(Manifest($@"{{ ""name"":""p"", ""url"":""p.bin"", ""targetPath"":""Resources/Assets/p.pack2"",
            ""size"":5, ""sha256"":""{HelloSha}"", ""kind"":""patch"", ""baseSha256"":""{new string('a', 64)}"" }}"));
        T.Eq(patch.Entries[0].Kind, EntryKind.Patch, "patch entry parses");
        T.Eq(patch.Entries[0].BaseSha256, new string('a', 64), "patch baseSha256 parsed");
        T.Eq(patch.Entries[0].PatchFormat, "full", "patch defaults to the full-file format");
        T.Throws<ManifestException>(() => ManifestParser.Parse(Manifest($@"{{ ""name"":""p"", ""url"":""p.bin"", ""targetPath"":""p.pack2"",
            ""size"":5, ""sha256"":""{HelloSha}"", ""kind"":""patch"", ""patchFormat"":""bsdiff"" }}")),
            "unsupported patchFormat rejected with a clear error instead of a silent bad write");

        T.Group("ManifestParser.ResolveUrl");
        var rel = ManifestParser.Parse(Manifest(Entry("x.dll", url: "sub/hello.bin")));
        T.Eq(ManifestParser.ResolveUrl(rel, rel.Entries[0], "https://ignored.test/m.json").AbsoluteUri,
            "https://cdn.zrevive.test/r1/sub/hello.bin", "relative url resolves against baseUrl");

        var noBase = ManifestParser.Parse(@"{""version"":1,""releaseHash"":""aabbccdd"",""entries"":[" + Entry("x.dll", url: "hello.bin") + "]}");
        T.Eq(ManifestParser.ResolveUrl(noBase, noBase.Entries[0], "https://cdn.zrevive.test/rel/manifest.json").AbsoluteUri,
            "https://cdn.zrevive.test/rel/hello.bin", "relative url falls back to the manifest url");

        var abs = ManifestParser.Parse(Manifest(Entry("x.dll", url: "https://other.test/a.bin")));
        T.Eq(ManifestParser.ResolveUrl(abs, abs.Entries[0], "https://cdn.zrevive.test/m.json").AbsoluteUri,
            "https://other.test/a.bin", "absolute url kept");

        foreach (var scheme in new[] { "file:///C:/Windows/evil.dll", "ftp://x.test/a.bin", @"\\\\server\\share\\a.bin" })
        {
            var bad = ManifestParser.Parse(Manifest(Entry("x.dll", url: scheme.Replace(@"\", @"\\"))));
            T.Throws<ManifestException>(() => ManifestParser.ResolveUrl(bad, bad.Entries[0], "https://cdn.zrevive.test/m.json"),
                $"non-http url rejected: {scheme}");
        }
    }

    // ---------------------------------------------------------------- hashing

    static async Task HashTests()
    {
        T.Group("FileHasher");
        var dir = Temp("hash");
        var f = Path.Combine(dir, "hello.bin");
        await File.WriteAllTextAsync(f, "hello");

        T.Eq(await FileHasher.HashFileAsync(f), HelloSha, "sha256(\"hello\") matches the known digest");
        T.Ok(await FileHasher.VerifyAsync(f, HelloSha, 5), "verify passes on the right hash and size");
        T.Ok(await FileHasher.VerifyAsync(f, HelloSha.ToUpperInvariant(), 5), "verify is case-insensitive");
        T.Ok(!await FileHasher.VerifyAsync(f, HelloSha, 6), "verify fails on a size mismatch (without hashing)");
        T.Ok(!await FileHasher.VerifyAsync(f, new string('0', 64), 5), "verify fails on a hash mismatch");
        T.Ok(!await FileHasher.VerifyAsync(Path.Combine(dir, "missing.bin"), HelloSha, 5), "verify fails on a missing file");

        // one flipped bit must fail
        await File.WriteAllTextAsync(f, "hellp");
        T.Ok(!await FileHasher.VerifyAsync(f, HelloSha, 5), "verify fails when one byte changed but the size is identical");

        // a big-ish file crosses the 1 MiB buffer boundary
        var big = Path.Combine(dir, "big.bin");
        var bytes = new byte[(1 << 20) + 1234];
        new Random(7).NextBytes(bytes);
        await File.WriteAllBytesAsync(big, bytes);
        var sha = await FileHasher.HashFileAsync(big);
        using (var h = System.Security.Cryptography.SHA256.Create())
            T.Eq(sha, FileHasher.ToHex(h.ComputeHash(bytes)), "multi-buffer file hashes correctly");

        // Progress<T> marshals through a SynchronizationContext (none in a console app), so
        // assert against a synchronous IProgress instead.
        long seen = 0;
        await FileHasher.HashFileAsync(big, new SyncProgress<long>(n => seen = Math.Max(seen, n)));
        T.Ok(seen > 0, "hashing reports progress");

        T.Ok(FileHasher.HashesEqual("AbCd", "aBcD"), "HashesEqual ignores case");
        T.Ok(!FileHasher.HashesEqual("abcd", "abce"), "HashesEqual catches a difference");
        T.Ok(!FileHasher.HashesEqual(null, "abcd"), "HashesEqual handles null");
        T.Ok(!FileHasher.HashesEqual("abcd", null), "HashesEqual handles null on the right");
        T.Ok(!FileHasher.HashesEqual("abcd", "abcde"), "HashesEqual catches a length difference");

        Nuke(dir);
    }

    // ---------------------------------------------------------------- misc

    static void MiscTests()
    {
        T.Group("InstallService / SteamLocator helpers");

        var m = ManifestParser.Parse(Manifest(Entry("x.dll")));
        T.Ok(!InstallService.NeedsUpdate(m, "ABCDEF0123456789"), "same releaseHash (different case) is not an update");
        T.Ok(InstallService.NeedsUpdate(m, "0000000000000000"), "different releaseHash is an update");
        T.Ok(InstallService.NeedsUpdate(m, null), "no recorded releaseHash means update");

        T.Eq(InstallService.Sanitise(@"Resources\Assets\x.pack2"), "Resources_Assets_x.pack2", "staging name is flattened");
        T.Ok(!InstallService.Sanitise(@"Resources\Assets\x.pack2").Contains('\\'), "staging name has no separators");

        var vdf = """
        "libraryfolders"
        {
            "0" { "path" "C:\\Program Files (x86)\\Steam" "apps" { "433850" "123" } }
            "1" { "path" "D:\\SteamLibrary" }
        }
        """;
        var roots = SteamLocator.ParseLibraryFolders(vdf);
        T.Ok(roots.Contains(@"C:\Program Files (x86)\Steam"), "vdf: primary library parsed");
        T.Ok(roots.Contains(@"D:\SteamLibrary"), "vdf: secondary library parsed");
        T.Ok(!roots.Contains("123"), "vdf: app ids are not treated as paths");

        var old = "\"libraryfolders\"\n{\n\t\"1\"\t\t\"E:\\\\Games\\\\Steam\"\n}";
        T.Ok(SteamLocator.ParseLibraryFolders(old).Contains(@"E:\Games\Steam"), "vdf: old numeric layout parsed");
        T.Eq(SteamLocator.ParseLibraryFolders("").Count, 0, "vdf: empty input is empty");

        T.Eq(SteamLocator.ParseAppManifestInstallDir("\"AppState\" { \"installdir\"\t\"Z1 Battle Royale\" }"),
            "Z1 Battle Royale", "acf installdir parsed");
        T.Eq(SteamLocator.ParseAppManifestInstallDir("nothing here"), null, "acf without installdir returns null");

        T.Ok(SteamLocator.ValidateSource(null) != null, "ValidateSource rejects null");
        T.Ok(SteamLocator.ValidateSource(@"C:\definitely\not\here") != null, "ValidateSource rejects a missing folder");

        T.Eq(MainWindowFormat.Bytes(0), "0 B", "byte format: zero");
        T.Eq(MainWindowFormat.Bytes(1536), "1.5 KB", "byte format: KB");
        T.Eq(MainWindowFormat.Bytes(15L * 1024 * 1024 * 1024), "15 GB", "byte format: GB");
    }

    // ---------------------------------------------------------------- base build

    static async Task BuilderTests()
    {
        T.Group("BaseBuilder: the player's install is never modified");

        var dir = Temp("build");
        var source = Path.Combine(dir, "Z1 Battle Royale");
        var target = Path.Combine(dir, "ZRevive");
        MakeFakeGame(source);

        T.Eq(SteamLocator.ValidateSource(source), null, "fake Z1BR install validates");
        T.Ok(SteamLocator.ValidateSource(Path.Combine(dir, "nope")) != null, "a non-install is rejected");

        T.Ok(BaseBuilder.ValidatePair(source, Path.Combine(source, "sub")) != null, "refuses a target inside the source");
        T.Ok(BaseBuilder.ValidatePair(source, dir) != null, "refuses a target that contains the source");
        T.Ok(BaseBuilder.ValidatePair(source, "") != null, "refuses an empty target");
        T.Eq(BaseBuilder.ValidatePair(source, target), null, "accepts a sibling target");

        var rotk = Path.Combine(dir, "ROTK");
        Directory.CreateDirectory(rotk);
        await File.WriteAllTextAsync(Path.Combine(rotk, ".rotk-installation.json"), "{}");
        T.Ok(BaseBuilder.ValidatePair(rotk, target) != null, "refuses to build FROM the ROTK install");
        T.Ok(BaseBuilder.ValidatePair(source, rotk) != null, "refuses to build INTO the ROTK install");

        var before = BaseBuilder.Snapshot(source);
        var reported = 0;
        var snap = await BaseBuilder.BuildAsync(source, target, allowHardlinks: false,
            new SyncProgress<BuildProgress>(_ => reported++), CancellationToken.None);

        T.Eq(snap.FileCount, before.FileCount, "snapshot file count matches");
        T.Ok(File.Exists(Path.Combine(target, "H1Z1.exe")), "H1Z1.exe copied");
        T.Ok(File.Exists(Path.Combine(target, "Resources", "Assets", "assets_x64_0.pack2")), "pack2 copied into the subfolder");
        T.Eq(await File.ReadAllTextAsync(Path.Combine(target, "Resources", "Assets", "assets_x64_0.pack2")), "pack-data", "copied content matches");
        T.Eq(BaseBuilder.VerifySourceUntouched(source, before), null, "source install is byte-for-byte untouched after the build");
        T.Ok(reported > 0, "build reports progress");
        T.Ok(!Directory.EnumerateFiles(target, "*.zrpart", SearchOption.AllDirectories).Any(), "no .zrpart leftovers");

        // writing into the ZRevive copy must not reach back into the source
        var copied = Path.Combine(target, "H1Z1.exe");
        await File.WriteAllTextAsync(copied, "modified-by-zrevive");
        T.Eq(await File.ReadAllTextAsync(Path.Combine(source, "H1Z1.exe")), "exe-bytes", "editing the ZRevive copy leaves the Steam copy alone");

        // second run is a resume, not a re-copy
        var again = await BaseBuilder.BuildAsync(source, target, allowHardlinks: false, null, CancellationToken.None);
        T.Eq(again.FileCount, before.FileCount, "re-running the build is idempotent");
        T.Eq(await File.ReadAllTextAsync(copied), "exe-bytes", "a file whose size/mtime drifted is re-copied");

        T.Ok(BaseBuilder.SameVolume(source, target), "SameVolume true inside one drive");
        T.Ok(!BaseBuilder.SameVolume(@"C:\a", @"Q:\b"), "SameVolume false across drives");

        // cancellation leaves the source alone
        var target2 = Path.Combine(dir, "ZRevive2");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { await BaseBuilder.BuildAsync(source, target2, false, null, cts.Token); T.Ok(false, "cancelled build throws"); }
        catch (OperationCanceledException) { T.Ok(true, "cancelled build throws OperationCanceledException"); }
        T.Eq(BaseBuilder.VerifySourceUntouched(source, before), null, "source untouched after a cancelled build");

        Nuke(dir);
    }

    // ---------------------------------------------------------------- apply

    static async Task ApplyTests()
    {
        T.Group("InstallService: verify + apply (fake downloader, no network)");

        var dir = Temp("apply");
        var install = Path.Combine(dir, "ZRevive");
        Directory.CreateDirectory(Path.Combine(install, "Resources", "Assets"));

        var dl = new FakeDownloader();
        dl.Files["https://cdn.zrevive.test/r1/hello.bin"] = Encoding.UTF8.GetBytes("hello");
        var svc = new InstallService(dl);

        var manifestJson = Manifest(Entry("Resources/Assets/x.pack2"));
        dl.Manifest = manifestJson;
        var manifest = await svc.FetchManifestAsync("https://cdn.zrevive.test/r1/manifest.json", CancellationToken.None);

        var report = await InstallService.VerifyAsync(manifest, install, null, CancellationToken.None);
        T.Eq(report.All[0].Verdict, EntryVerdict.Missing, "missing file is reported Missing");
        T.Ok(!report.Healthy, "report is unhealthy before installing");
        T.Eq(report.RepairBytes, 5L, "repair size adds up");

        var changed = await svc.ApplyAsync(manifest, "https://cdn.zrevive.test/r1/manifest.json", install, report, null, CancellationToken.None);
        T.Eq(changed, 1, "one file installed");
        var landed = Path.Combine(install, "Resources", "Assets", "x.pack2");
        T.Ok(File.Exists(landed), "file landed at the manifest target path");
        T.Eq(await File.ReadAllTextAsync(landed), "hello", "installed content is the downloaded payload");
        T.Ok(!Directory.Exists(Path.Combine(install, InstallService.StagingDir)), "staging folder cleaned up");

        var after = await InstallService.VerifyAsync(manifest, install, null, CancellationToken.None);
        T.Ok(after.Healthy, "report is healthy after installing");

        // corrupted local file -> WrongHash -> repaired
        await File.WriteAllTextAsync(landed, "HELLO");
        var broken = await InstallService.VerifyAsync(manifest, install, null, CancellationToken.None);
        T.Eq(broken.All[0].Verdict, EntryVerdict.WrongHash, "tampered file of the same size is WrongHash");
        await svc.ApplyAsync(manifest, "https://cdn.zrevive.test/r1/manifest.json", install, broken, null, CancellationToken.None);
        T.Eq(await File.ReadAllTextAsync(landed), "hello", "repair restored the right content");

        // wrong size
        await File.WriteAllTextAsync(landed, "hello world");
        T.Eq((await InstallService.VerifyAsync(manifest, install, null, CancellationToken.None)).All[0].Verdict,
            EntryVerdict.WrongSize, "wrong-size file is WrongSize");

        // a payload that doesn't match its advertised sha256 must be refused, not installed
        var evilDir = Path.Combine(dir, "evil");
        Directory.CreateDirectory(evilDir);
        var dl2 = new FakeDownloader { Manifest = manifestJson };
        dl2.Files["https://cdn.zrevive.test/r1/hello.bin"] = Encoding.UTF8.GetBytes("EVIL!");  // 5 bytes, wrong hash
        var svc2 = new InstallService(dl2);
        var rep2 = await InstallService.VerifyAsync(manifest, evilDir, null, CancellationToken.None);
        var threw = false;
        try { await svc2.ApplyAsync(manifest, "https://cdn.zrevive.test/r1/manifest.json", evilDir, rep2, null, CancellationToken.None); }
        catch (IOException) { threw = true; }
        T.Ok(threw, "a payload whose sha256 doesn't match is rejected");
        T.Ok(!File.Exists(Path.Combine(evilDir, "Resources", "Assets", "x.pack2")), "the rejected payload never reached the install folder");

        // delete entries
        var delTarget = Path.Combine(install, "steam_api64.dll");
        await File.WriteAllTextAsync(delTarget, "x");
        var delManifest = ManifestParser.Parse(Manifest(@"{ ""name"":""steam_api64.dll"", ""targetPath"":""steam_api64.dll"", ""kind"":""delete"" }"));
        var delReport = await InstallService.VerifyAsync(delManifest, install, null, CancellationToken.None);
        T.Eq(delReport.All[0].Verdict, EntryVerdict.ShouldDelete, "a file that must not exist is ShouldDelete");
        await svc.ApplyAsync(delManifest, "https://cdn.zrevive.test/r1/manifest.json", install, delReport, null, CancellationToken.None);
        T.Ok(!File.Exists(delTarget), "delete entry removed the file");
        T.Ok((await InstallService.VerifyAsync(delManifest, install, null, CancellationToken.None)).Healthy, "delete entry verifies clean afterwards");

        // ApplyAsync must refuse outright if anything in the report is flagged unsafe
        var unsafeReport = new VerifyReport(new[]
        {
            new EntryStatus(manifest.Entries[0], EntryVerdict.Unsafe, "a symlink or junction is on that path")
        });
        var refused = false;
        try { await svc.ApplyAsync(manifest, "https://cdn.zrevive.test/r1/manifest.json", install, unsafeReport, null, CancellationToken.None); }
        catch (ManifestException) { refused = true; }
        T.Ok(refused, "ApplyAsync refuses an install containing an unsafe entry");

        // patch entry whose local base is a stranger
        var patchManifest = ManifestParser.Parse(Manifest($@"{{ ""name"":""x.pack2"", ""url"":""hello.bin"", ""targetPath"":""Resources/Assets/x.pack2"",
            ""size"":5, ""sha256"":""{HelloSha}"", ""kind"":""patch"", ""baseSha256"":""{new string('b', 64)}"" }}"));
        await File.WriteAllTextAsync(landed, "stran");   // neither base nor result
        var patchReport = await InstallService.VerifyAsync(patchManifest, install, null, CancellationToken.None);
        var patchRefused = false;
        try { await svc.ApplyAsync(patchManifest, "https://cdn.zrevive.test/r1/manifest.json", install, patchReport, null, CancellationToken.None); }
        catch (IOException) { patchRefused = true; }
        T.Ok(patchRefused, "a patch entry refuses to clobber a file that isn't the declared base");

        Nuke(dir);
    }

    // ---------------------------------------------------------------- helpers

    static void MakeFakeGame(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "Resources", "Assets"));
        File.WriteAllText(Path.Combine(folder, "H1Z1.exe"), "exe-bytes");
        File.WriteAllText(Path.Combine(folder, "Resources", "Assets", "assets_x64_0.pack2"), "pack-data");
        File.WriteAllText(Path.Combine(folder, "Resources", "Assets", "data_x64_0.pack2"), "more-pack-data");
        File.WriteAllText(Path.Combine(folder, "UserOptions.ini"), "[Display]\nMode=Fullscreen\n");
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

/// <summary>
/// The byte formatter lives in MainWindow.Install.cs (WPF), so it is mirrored here to keep
/// the test project free of a WPF reference. Kept identical on purpose.
/// </summary>
static class MainWindowFormat
{
    public static string Bytes(long n)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = n;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} B", n)
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#} {1}", v, units[i]);
    }
}
