using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ZRevive.Launcher.Install;

public sealed record GameCandidate(string Folder, string? Source, string? Version, bool Valid, string? Problem);

/// <summary>
/// Finds the player's Z1 Battle Royale install. Steam library roots come from the registry
/// plus libraryfolders.vdf; the per-library lookup uses appmanifest_433850.acf first (it
/// records the real install directory) and falls back to the usual folder names.
/// </summary>
public static class SteamLocator
{
    public const string Z1BrAppId = "433850";

    static readonly string[] KnownFolderNames =
    {
        "Z1 Battle Royale", "z1 battle royale", "H1Z1", "H1Z1 Test Server",
        "H1Z1 King of the Kill", "Just Survive"
    };

    // ---------- validation of a *source* (player-owned) install ----------

    /// <summary>
    /// Returns null when the folder really is a Z1BR/KotK install we can build from:
    /// H1Z1.exe plus at least one Resources\Assets\*.pack2.
    /// </summary>
    public static string? ValidateSource(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Pick the folder that contains H1Z1.exe.";
        try
        {
            if (!Directory.Exists(folder)) return "That folder doesn't exist.";
            if (File.Exists(Path.Combine(folder, ".rotk-installation.json")))
                return "That's the ROTK install. ZRevive must not be built from it; use your own Steam copy.";
            if (!File.Exists(Path.Combine(folder, "H1Z1.exe"))) return "No H1Z1.exe in that folder.";
            var assets = Path.Combine(folder, "Resources", "Assets");
            if (!Directory.Exists(assets)) return "Game files look incomplete (no Resources\\Assets folder).";
            if (!Directory.EnumerateFiles(assets, "*.pack2").Any())
                return "Game files look incomplete (no .pack2 files in Resources\\Assets).";
            return null;
        }
        catch (Exception ex)
        {
            return "Can't read that folder: " + ex.Message;
        }
    }

    public static string? FileVersion(string folder)
    {
        try { return FileVersionInfo.GetVersionInfo(Path.Combine(folder, "H1Z1.exe")).FileVersion; }
        catch { return null; }
    }

    public static GameCandidate Describe(string folder, string? source)
    {
        var problem = ValidateSource(folder);
        return new GameCandidate(folder, source, FileVersion(folder), problem == null, problem);
    }

    // ---------- Steam discovery ----------

    public static string? SteamPath()
    {
        foreach (var (hive, key, value) in new (RegistryKey, string, string)[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath")
                 })
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                if (k?.GetValue(value) is string s && s.Length > 0)
                {
                    var p = s.Replace('/', '\\');
                    if (Directory.Exists(p)) return p;
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Pulls the library roots out of a libraryfolders.vdf. Handles both the old
    /// ("1" "D:\\SteamLibrary") and the current (nested object with a "path" key) layouts.
    /// Pure string work so it's unit-tested.
    /// </summary>
    public static List<string> ParseLibraryFolders(string vdf)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(vdf)) return found;

        foreach (Match m in Regex.Matches(vdf, "\"(?:path|\\d+)\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var raw = m.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\').Trim();
            if (raw.Length < 3) continue;                 // skip the numeric "1" "2" noise
            if (!raw.Contains('\\') && !raw.Contains(':')) continue;
            if (!found.Contains(raw, StringComparer.OrdinalIgnoreCase)) found.Add(raw);
        }
        return found;
    }

    public static List<string> LibraryRoots()
    {
        var roots = new List<string>();
        var steam = SteamPath();
        if (steam != null) roots.Add(steam);

        foreach (var vdf in roots.ToList()
                     .SelectMany(r => new[]
                     {
                         Path.Combine(r, "steamapps", "libraryfolders.vdf"),
                         Path.Combine(r, "config", "libraryfolders.vdf")
                     }))
        {
            try
            {
                if (!File.Exists(vdf)) continue;
                foreach (var p in ParseLibraryFolders(File.ReadAllText(vdf)))
                    if (Directory.Exists(p) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase))
                        roots.Add(p);
            }
            catch { }
        }
        return roots;
    }

    /// <summary>Reads the install directory out of an appmanifest_&lt;appid&gt;.acf.</summary>
    public static string? ParseAppManifestInstallDir(string acf)
    {
        var m = Regex.Match(acf ?? "", "\"installdir\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : null;
    }

    /// <summary>
    /// Every plausible Z1BR folder on this machine, valid ones first. Never throws.
    /// </summary>
    public static List<GameCandidate> Detect(params string?[] extraHints)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<GameCandidate>();

        void Add(string? folder, string? source)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            string full;
            try { full = Path.GetFullPath(folder); } catch { return; }
            if (!Directory.Exists(full) || !seen.Add(full)) return;
            list.Add(Describe(full, source));
        }

        foreach (var root in LibraryRoots())
        {
            var common = Path.Combine(root, "steamapps", "common");
            try
            {
                var acf = Path.Combine(root, "steamapps", $"appmanifest_{Z1BrAppId}.acf");
                if (File.Exists(acf) && ParseAppManifestInstallDir(File.ReadAllText(acf)) is string dir)
                    Add(Path.Combine(common, dir), "Steam library");
            }
            catch { }

            foreach (var name in KnownFolderNames) Add(Path.Combine(common, name), "Steam library");
        }

        foreach (var hint in extraHints) Add(hint, "Saved folder");

        return list.OrderByDescending(c => c.Valid).ToList();
    }
}
