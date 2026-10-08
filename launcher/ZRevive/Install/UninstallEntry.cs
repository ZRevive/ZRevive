using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ZRevive.Launcher.Install;

/// <summary>
/// Puts ZRevive in Windows' "Installed apps" / "Programs and Features" list, so it can be removed
/// the way people expect instead of being a stray .exe they have to hunt down.
/// <para>Registered under <b>HKEY_CURRENT_USER</b>, not HKLM: the launcher is a single file the
/// player runs from wherever they put it, with no elevation, so it is installed for that user only.
/// Writing to HKLM would need admin rights we do not ask for, and would lie about the scope.</para>
/// <para>The entry is refreshed on every start. That keeps it correct when the player moves the exe
/// or updates it, which is the common case for a self-updating single file.</para>
/// </summary>
public static class UninstallEntry
{
    /// <summary>Our key under the per-user uninstall list.</summary>
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ZRevive";

    /// <summary>Passed to the exe by Windows when the player clicks Uninstall.</summary>
    public const string UninstallSwitch = "--uninstall";

    /// <summary>
    /// Creates or refreshes the entry. Never throws: a launcher that cannot write its own
    /// uninstall entry must still start, so a locked-down or roaming profile degrades to
    /// "no Control Panel entry" rather than to "the launcher won't open".
    /// </summary>
    public static void Register()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
            var dir = Path.GetDirectoryName(exe) ?? "";
            var version = typeof(UninstallEntry).Assembly.GetName().Version;

            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (key == null) return;
            key.SetValue("DisplayName", "ZRevive Launcher");
            key.SetValue("DisplayVersion", version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}");
            key.SetValue("Publisher", "ZRevive");
            key.SetValue("DisplayIcon", exe);
            key.SetValue("InstallLocation", dir);
            key.SetValue("UninstallString", $"\"{exe}\" {UninstallSwitch}");
            key.SetValue("QuietUninstallString", $"\"{exe}\" {UninstallSwitch} --quiet");
            key.SetValue("URLInfoAbout", "https://zrevive.com");
            key.SetValue("HelpLink", "https://github.com/ZRevive/ZRevive");
            // Windows offers "Change" and "Repair" buttons unless these say there is nothing to do.
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            // Shown as the size in the list; Windows wants kilobytes.
            try { key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord); }
            catch { }
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }
        catch { }   // see the summary: never fatal
    }

    /// <summary>Removes the entry. Safe to call when it was never there.</summary>
    public static void Remove()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false); }
        catch { }
    }

    /// <summary>Where the launcher keeps its settings; removed on uninstall.</summary>
    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZRevive");

    /// <summary>Where the launcher writes the game's logs; removed on uninstall.</summary>
    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZRevive");

    /// <summary>
    /// Deletes the launcher's own data: settings (including the remembered sign-in key) and logs.
    /// The ZRevive game folder is deliberately NOT touched here - it is tens of gigabytes the player
    /// may want to keep, and deleting a folder they chose themselves, without asking, is the kind of
    /// thing an uninstaller must never do. <see cref="DeleteGameFolder"/> handles it on request.
    /// </summary>
    public static void RemoveUserData()
    {
        foreach (var dir in new[] { SettingsDirectory, LogDirectory })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { }
    }

    /// <summary>
    /// Deletes the built ZRevive install, only when the player asked for it. Refuses anything that
    /// is not plausibly ours: an empty path, a drive root, or a folder without our install marker.
    /// The marker check is what stops a settings file pointing at, say, the Steam copy from turning
    /// "uninstall ZRevive" into "delete the game you own".
    /// </summary>
    public static (bool Deleted, string Reason) DeleteGameFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return (false, "no folder was configured");
        string full;
        try { full = Path.GetFullPath(folder); }
        catch { return (false, "the folder path is not valid"); }
        if (!Directory.Exists(full)) return (false, "the folder is already gone");
        if (Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar).Equals(
                full.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true)
            return (false, "that is a drive root, not a ZRevive folder");
        if (GameLauncher.LooksLikeSteamLibrary(full)) return (false, "that is your Steam library");
        if (!File.Exists(Path.Combine(full, InstallState.MarkerFile)))
            return (false, $"it has no {InstallState.MarkerFile}, so it was not built by this launcher");
        try
        {
            Directory.Delete(full, recursive: true);
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Schedules the running exe to be deleted once this process exits. A single-file exe cannot
    /// delete itself while it is running, so a detached cmd waits for the handle to be released and
    /// then removes it. Best effort: if it fails, the player is left with one file to delete, which
    /// is far better than a half-removed install.
    /// </summary>
    public static void ScheduleSelfDelete()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = new ProcessStartInfo("cmd.exe")
            {
                // ping instead of timeout: timeout needs a console, which a detached process has not.
                Arguments = $"/c ping 127.0.0.1 -n 4 > nul & del /f /q \"{exe}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
        }
        catch { }
    }
}
