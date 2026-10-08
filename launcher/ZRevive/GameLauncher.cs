using System.Diagnostics;
using System.IO;

namespace ZRevive.Launcher;

public static class GameLauncher
{
    public const string ExpectedVersion = "1.0.326.439939";

    public static string? Validate(string folder) => Inspect(folder).Problem;

    /// <summary>
    /// What's wrong with an install folder, in enough detail to show the player instead of
    /// dumping them back into full setup. <see cref="Repairable"/> means "the folder is ours
    /// and nearly right" (so offer Verify / repair); otherwise the folder is the wrong one.
    /// </summary>
    public sealed record FolderReport(string Folder, string? Problem, bool Repairable, bool LooksMidBuild, int FileCount);

    public static FolderReport Inspect(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return new FolderReport("", "No ZRevive folder is set.", false, false, 0);

        try
        {
            if (!Directory.Exists(folder))
                return new FolderReport(folder, $"The ZRevive folder is gone: {folder}", false, false, 0);

            // ROTK's install has the same exe build: refuse it, Play would write into ROTK's folder.
            if (File.Exists(Path.Combine(folder, ".rotk-installation.json")))
                return new FolderReport(folder, "That folder is the ROTK install. Point the launcher at a separate copy.", false, false, 0);

            // The player's own Steam copy. It validates perfectly - it is the right build, which is
            // exactly why this is easy to get wrong (seen on a new player's PC, 2026-10-07: they put
            // the Steam path in GAME FOLDER and nothing was ever built). Refuse it: PLAY writes
            // steam_persona_name.txt, InputProfile_User.xml and UserOptions.ini into this folder, and
            // an update would patch game files in place, so accepting it would modify the one install
            // we promise never to touch - and ZRevive would still not be installed anywhere.
            if (LooksLikeSteamLibrary(folder))
                return new FolderReport(folder,
                    "That is your Steam copy of Z1 Battle Royale. ZRevive has to be built in a folder of its own, " +
                    "so your Steam install is never changed. Choose somewhere else, like C:\\Games\\ZRevive.",
                    false, false, 0);

            var ours = File.Exists(Path.Combine(folder, Install.InstallState.MarkerFile));
            var midBuild = LooksMidBuild(folder);

            var exe = Path.Combine(folder, "H1Z1.exe");
            if (!File.Exists(exe))
                return new FolderReport(folder, $"H1Z1.exe is missing from {folder}", ours, midBuild, 0);

            var version = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            if (version != ExpectedVersion)
                return new FolderReport(folder, $"H1Z1.exe is build {version ?? "unknown"}; ZRevive needs {ExpectedVersion}.", ours, midBuild, 0);

            var assets = Path.Combine(folder, "Resources", "Assets");
            if (!Directory.Exists(assets))
                return new FolderReport(folder, $"Game files look incomplete: {assets} is missing.", ours, midBuild, 0);

            var packs = Directory.EnumerateFiles(assets, "*.pack2").Count();
            if (packs == 0)
                return new FolderReport(folder, $"Game files look incomplete: no .pack2 files in {assets}.", ours, midBuild, 0);

            // NOTE: midBuild is reported but is NOT a validation failure. An established
            // install gets written to all the time (pack2 edits, config tweaks); only the
            // first-run adoption decision is allowed to care about it.
            return new FolderReport(folder, null, ours, midBuild, packs);
        }
        catch (Exception ex)
        {
            return new FolderReport(folder, "Can't read the ZRevive folder: " + ex.Message, false, false, 0);
        }
    }

    /// <summary>
    /// Where to suggest building ZRevive, given the player's Z1 Battle Royale folder. Beside the
    /// source is the friendly answer - except for a Steam install, where "beside it" is
    /// <c>steamapps\common</c>, inside the Steam library, which <see cref="Inspect"/> refuses. The
    /// launcher used to propose exactly that and then reject it, so a new player could not finish
    /// setup at all. The fallback stays on the same drive, so the copy is still same-volume.
    /// </summary>
    public static string DefaultTargetFor(string source)
    {
        try
        {
            var full = Path.GetFullPath(source.TrimEnd(Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(full);
            if (parent == null) return Settings.DefaultGameFolder;

            var beside = Path.Combine(parent, "ZRevive");
            if (!LooksLikeSteamLibrary(beside)) return beside;

            var root = Path.GetPathRoot(full);
            return string.IsNullOrEmpty(root)
                ? Settings.DefaultGameFolder
                : Path.Combine(root, "Games", "ZRevive");
        }
        catch { return Settings.DefaultGameFolder; }
    }

    /// <summary>
    /// Is this path inside a Steam library? Matched on the <c>steamapps</c> path segment, which is
    /// the one part Steam always uses, on every library and every drive - the library root itself is
    /// freely chosen, so matching "Steam" or "Program Files" would miss most of them and would also
    /// catch a folder the player merely named "steam". Also accepts a library root that still has its
    /// <c>steamapps</c> directory inside it.
    /// </summary>
    public static bool LooksLikeSteamLibrary(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            var parts = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => string.Equals(p, "steamapps", StringComparison.OrdinalIgnoreCase))) return true;
            return Directory.Exists(Path.Combine(full, "steamapps"));
        }
        catch { return false; }
    }

    /// <summary>
    /// Cheap "somebody is assembling this right now" heuristic: half-written temp files, or
    /// H1Z1.exe/pack2 files that changed in the last couple of minutes.
    /// Used to stop the launcher adopting a folder another process is mid-way through.
    /// </summary>
    public static bool LooksMidBuild(string folder)
    {
        try
        {
            foreach (var pattern in new[] { "*.zrpart", "*.zrevive.tmp", "*.part" })
                if (Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories).Any())
                    return true;

            var cutoff = DateTime.UtcNow.AddMinutes(-2);
            var exe = Path.Combine(folder, "H1Z1.exe");
            if (File.Exists(exe) && File.GetLastWriteTimeUtc(exe) > cutoff) return true;
            var assets = Path.Combine(folder, "Resources", "Assets");
            if (Directory.Exists(assets))
                foreach (var f in Directory.EnumerateFiles(assets, "*.pack2"))
                    if (File.GetLastWriteTimeUtc(f) > cutoff) return true;
            return false;
        }
        catch { return false; }
    }

    public static Process Launch(string folder, LoginResult login, string authKey, string portalUrl)
    {
        // Backstop. The UI already refuses on this in Play_Click, with instructions; this is here so
        // the requirement holds for any future call path too, instead of depending on one button's
        // code staying correct. Secure Boot / Memory integrity only change over a reboot, so the
        // extra read costs nothing.
        //
        // login.SecurityExempt is the portal's waiver for an account whose PC cannot turn Secure
        // Boot on (and for staff). It arrives with the signed-in reply, so the server decides it -
        // this check honours that answer rather than second-guessing it.
        if (!login.SecurityExempt)
        {
            var gate = SecurityGate.Check();
            if (!gate.Ok)
                throw new InvalidOperationException(gate.Headline + " " + SecurityGate.RequirementSummary);
        }

        // The offline Steam shim reads the display name from this file.
        WriteReplacing(Path.Combine(folder, "steam_persona_name.txt"), (login.Name ?? "Player") + "\n");
        PrepareClientSettings(folder);

        var loginServer = login.LoginServer ?? "127.0.0.1:1115";
        var host = loginServer.Split(':')[0];
        PointClientConfigAt(folder, loginServer, host);
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZRevive", "logs");
        Directory.CreateDirectory(logs);

        var psi = new ProcessStartInfo(Path.Combine(folder, "H1Z1.exe"))
        {
            WorkingDirectory = folder,
            UseShellExecute = false
        };
        foreach (var a in new[]
                 {
                     $"sessionid={login.SessionId ?? authKey}",
                     $"server={loginServer}",
                     $"SteamGatewayUrl=http://{host}:1116/rest/auth/session/create",
                     "thirdPartyCommandLine=+connect_lobby109775241000000001",
                     $"CommandQueue:motd_uri=http://{host}:1116/",
                     $"CommandQueue:cb_uri=http://{host}:1116/",
                     $"CommandQueue:eula_uri=http://{host}:1116/",
                     $"LaunchTelemetry:Url=http://{host}:1116/h1z1xx/live/",
                     // The in-game MARKETPLACE button opens this URL in the browser.
                     $"Marketplace:MarketplaceUrl={portalUrl.TrimEnd('/')}/store",
                     "Internationalization:Locale=en_us",
                     "Logging:FileLogLevel=999",
                     "Logging:LocalLogLevel=999",
                     $"Logging:Directory={logs}",
                     $"Logging:LocalDirectory={Path.Combine(logs, "local")}",
                     $"Logging:FailureDirectory={Path.Combine(logs, "failure")}"
                 })
            psi.ArgumentList.Add(a);

        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start H1Z1.exe");
    }

    /// <summary>
    /// Rewrites the loopback addresses in <c>ClientConfig.ini</c> to the server this launch is
    /// actually for.
    /// <para>The shipped ClientConfig.ini has <c>127.0.0.1</c> baked into every endpoint, because it
    /// was captured on a development box. The command line passes the real addresses, but the file
    /// still decides where some of them point - the game's own error page proved it: a failed login
    /// against the live server was reported at
    /// <c>http://127.0.0.1:1116/king-of-the-kill/game-error?code=G29</c>, which is the file's
    /// <c>GameCrashUrl</c> verbatim. Leaving it alone means every player's client carries a config
    /// pointing at their own PC.</para>
    /// <para>Only the loopback host is replaced, line by line: a config the player has edited, or a
    /// future one that already names a real host, is left exactly as it is.</para>
    /// </summary>
    public static void PointClientConfigAt(string folder, string loginServer, string host)
    {
        try
        {
            var path = Path.Combine(folder, "ClientConfig.ini");
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);

            // "Server=" carries host:port; everything else is a URL on the gateway port.
            // ${1}, not $1: the replacement is followed by digits, and "$1" + "5.83..." reads as
            // group 15, which does not exist - so the line silently kept its loopback address.
            var updated = System.Text.RegularExpressions.Regex.Replace(
                text, @"(?m)^(\s*Server\s*=\s*)127\.0\.0\.1:\d+\s*$", "${1}" + loginServer);
            updated = updated.Replace("http://127.0.0.1:", $"http://{host}:");

            if (updated != text) WriteReplacing(path, updated);
        }
        catch (IOException) { }               // never block a launch over a config tweak
        catch (UnauthorizedAccessException) { }
    }

    // Screenshots: the game's own Screenshot action (PrintScreen) goes through Steam,
    // which the offline shim lacks, so it just swallows the key. Move it to Ctrl+F9
    // and run borderless so Windows/Discord capture tools see the game.
    static void PrepareClientSettings(string folder)
    {
        try
        {
            var input = Path.Combine(folder, "InputProfile_User.xml");
            if (File.Exists(input))
            {
                var t = File.ReadAllText(input);
                var fixedText = System.Text.RegularExpressions.Regex.Replace(t,
                    @"(<Action name=""Screenshot"">\s*<Trigger>)PrintScreen(</Trigger>)", "$1Control+F9$2");
                if (fixedText != t) WriteReplacing(input, fixedText);
            }
            var options = Path.Combine(folder, "UserOptions.ini");
            if (File.Exists(options))
            {
                var t = File.ReadAllText(options);
                var fixedText = System.Text.RegularExpressions.Regex.Replace(t,
                    @"(?m)^(Mode|FullscreenMode)=Fullscreen(?=\r?$)", "$1=WindowedFullscreen");
                if (fixedText != t) WriteReplacing(options, fixedText);
            }
        }
        catch (IOException) { }  // settings are a convenience; never block the launch
        catch (UnauthorizedAccessException) { }
    }

    // Write a new file and rename it over the old one instead of writing in place: if the
    // game folder shares NTFS hardlinks with another install, only our copy changes.
    static void WriteReplacing(string path, string text)
    {
        var tmp = path + ".zrevive.tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
