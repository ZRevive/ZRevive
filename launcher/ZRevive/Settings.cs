using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZRevive.Launcher;

// Stored in %APPDATA%\PS5Launcher\settings.json. The auth key is encrypted with
// DPAPI so it only decrypts for this Windows user.
/// <summary>Which ZRevive the launcher talks to. See <see cref="Settings.Mode"/>.</summary>
public enum ServerMode
{
    /// <summary>The public servers. What every player uses.</summary>
    Live,

    /// <summary>A ZRevive stack running on this PC (START-SERVER.bat). Owner/dev only.</summary>
    Local
}

public sealed class Settings
{
    /// <summary>
    /// Which stack to use. Everything that differs between the two - the portal API, the sign-in
    /// key, the release manifest, and the login server the portal hands back at PLAY - follows
    /// from this one value, so switching it is the whole "mode".
    /// </summary>
    public ServerMode Mode { get; set; } = ServerMode.Live;

    /// <summary>The public portal. <see cref="ServerMode.Live"/> uses this.</summary>
    public string LivePortalUrl { get; set; } = "https://zrevive.com";

    /// <summary>The portal on this PC. <see cref="ServerMode.Local"/> uses this.</summary>
    public string LocalPortalUrl { get; set; } = "http://127.0.0.1:8080";

    /// <summary>
    /// Pre-mode setting, kept only so an existing settings.json still means something. On load it
    /// is folded into <see cref="LivePortalUrl"/>/<see cref="LocalPortalUrl"/> and the matching
    /// mode is selected; see <see cref="Migrate"/>. Nothing reads it afterwards.
    /// </summary>
    public string? PortalUrl { get; set; }

    /// <summary>The portal for the current <see cref="Mode"/>.</summary>
    public string EffectivePortalUrl() =>
        (Mode == ServerMode.Local ? LocalPortalUrl : LivePortalUrl).Trim().TrimEnd('/');

    /// <summary>Is this URL a loopback address? Used to pick a mode for an old settings file.</summary>
    public static bool LooksLocal(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && (url.Contains("127.0.0.1", StringComparison.Ordinal)
            || url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || url.Contains("[::1]", StringComparison.Ordinal));

    /// <summary>What the setup screen proposes when the player has not chosen a folder yet.</summary>
    public const string DefaultGameFolder = @"C:\Games\ZRevive";

    /// <summary>
    /// The ZRevive install we build and launch. PLAY always uses this folder.
    /// <para>Empty until the player picks one. It deliberately does NOT default to
    /// <see cref="DefaultGameFolder"/>: a non-empty value here means "configured", and on a
    /// machine that has never run ZRevive that made first run report "The ZRevive folder is gone:
    /// C:\Games\ZRevive" instead of asking for the Z1 Battle Royale folder (seen on a new player's
    /// PC, 2026-10-07). The default is offered by the setup screen instead, where it is a
    /// suggestion the player can accept or change.</para>
    /// </summary>
    public string GameFolder { get; set; } = "";
    /// <summary>The remembered sign-in key for <see cref="ServerMode.Live"/>, DPAPI-protected.</summary>
    public string? ProtectedKey { get; set; }

    /// <summary>
    /// The remembered sign-in key for <see cref="ServerMode.Local"/>. Kept apart from
    /// <see cref="ProtectedKey"/> because an auth key is issued by ONE portal: the live key is
    /// meaningless to a local stack and vice versa. Sharing one slot would make switching mode
    /// look like "your sign-in expired" and would overwrite the other mode's key on every sign-in.
    /// </summary>
    public string? ProtectedKeyLocal { get; set; }

    public bool DiscordStatus { get; set; } = true;

    // ---- first-run install state (see Install\) ----

    /// <summary>The player's own Z1 Battle Royale install; read-only source for the build.</summary>
    public string? SourceGameFolder { get; set; }

    /// <summary>Where the release manifest lives. Empty = derive from PortalUrl.</summary>
    public string? ManifestUrl { get; set; }

    /// <summary>releaseHash of the LIVE manifest currently applied to GameFolder.</summary>
    public string? InstalledReleaseHash { get; set; }

    /// <summary>
    /// releaseHash of the LOCAL manifest currently applied to GameFolder. Separate for the same
    /// reason as the key: the two stacks publish their own manifests, so one shared value would
    /// report "up to date" right after switching mode and skip the update that mode needs.
    /// Both modes still build the SAME GameFolder, so switching mode re-applies that mode's delta.
    /// </summary>
    public string? InstalledReleaseHashLocal { get; set; }

    /// <summary>The applied releaseHash for the current <see cref="Mode"/>.</summary>
    public string? CurrentReleaseHash
    {
        get => Mode == ServerMode.Local ? InstalledReleaseHashLocal : InstalledReleaseHash;
        set
        {
            if (Mode == ServerMode.Local) InstalledReleaseHashLocal = value;
            else InstalledReleaseHash = value;
        }
    }

    /// <summary>True once the base build + delta have both completed at least once.</summary>
    public bool InstallComplete { get; set; }

    /// <summary>
    /// Opt-in only. Hardlinking the base files to the Steam copy saves ~15 GB but makes the
    /// two installs share bytes; the repo has already been bitten by that (backup\ notes).
    /// </summary>
    public bool AllowHardlinks { get; set; }

    /// <summary>Check the manifest and apply the delta before every launch.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>releaseHash the player chose to skip once; cleared when it changes again.</summary>
    public string? SkippedReleaseHash { get; set; }

    /// <summary>
    /// Where LIVE gets its release manifest when nothing is set explicitly: the newest GitHub
    /// release. GitHub serves the downloads, not our box, so a release does not cost the game
    /// server bandwidth and a patch day cannot take the game down with it. "latest" resolves on
    /// GitHub's side, so publishing a release is all it takes to roll an update out.
    /// </summary>
    public const string GitHubManifestUrl = "https://github.com/ZRevive/ZRevive/releases/latest/download/manifest.json";

    public string EffectiveManifestUrl() =>
        !string.IsNullOrWhiteSpace(ManifestUrl) ? ManifestUrl!.Trim()
        : Mode == ServerMode.Local ? EffectivePortalUrl() + "/assets/manifest.json"
        : GitHubManifestUrl;

    /// <summary>
    /// Folds a pre-mode settings.json into the two-URL layout: the old single PortalUrl becomes
    /// whichever slot it looks like, and the mode is set to match so the launcher keeps pointing
    /// where it did before the upgrade. Writing nothing back would silently move an owner who was
    /// testing against 127.0.0.1 onto the live servers.
    /// </summary>
    public void Migrate()
    {
        var old = PortalUrl?.Trim();
        PortalUrl = null;
        if (string.IsNullOrWhiteSpace(old)) return;
        if (LooksLocal(old))
        {
            LocalPortalUrl = old!;
            Mode = ServerMode.Local;
        }
        else
        {
            LivePortalUrl = old!;
            Mode = ServerMode.Live;
        }
    }

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZRevive");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                s.Migrate();
                return s;
            }
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The remembered key for the current <see cref="Mode"/>, or null.</summary>
    public string? GetKey()
    {
        var stored = Mode == ServerMode.Local ? ProtectedKeyLocal : ProtectedKey;
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            var raw = ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch { return null; }
    }

    /// <summary>Remembers (or forgets) the key for the current <see cref="Mode"/> only.</summary>
    public void SetKey(string? key)
    {
        var stored = key == null
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
        if (Mode == ServerMode.Local) ProtectedKeyLocal = stored;
        else ProtectedKey = stored;
    }
}
