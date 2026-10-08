using System.IO;
using System.Text;

namespace ZRevive.Launcher.Install;

/// <summary>
/// Desktop and Start-menu shortcuts for the launcher.
/// <para>The launcher is a single .exe the player saves wherever their browser put it, usually
/// Downloads. Without a shortcut the usual way back in is to go digging, and a file in Downloads is
/// the kind of thing people delete when tidying up. The shortcuts are created on first run and
/// refreshed on every start, so they follow the exe if it is moved or replaced by an update.</para>
/// <para>Written as .lnk files through IShellLink via COM, with no extra dependency: a .url file
/// would not carry the icon, and a .bat would flash a console window.</para>
/// </summary>
public static class Shortcuts
{
    public const string Name = "ZRevive.lnk";

    static string Desktop => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name);

    static string StartMenu => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name);

    /// <summary>
    /// Creates or refreshes both shortcuts. Never throws: a launcher that cannot write a shortcut
    /// must still start, and a locked-down or redirected profile is not an error worth stopping for.
    /// </summary>
    public static void Create()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
            foreach (var path in new[] { Desktop, StartMenu })
                TryWrite(path, exe);
        }
        catch { }
    }

    /// <summary>Removes both shortcuts. Safe when they were never created.</summary>
    public static void Remove()
    {
        foreach (var path in new[] { Desktop, StartMenu })
            try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static void TryWrite(string linkPath, string exe)
    {
        try
        {
            // Skip the rewrite when it already points at this exe: a shortcut rewritten on every
            // start shows up as a changed file to backup tools and to the player.
            if (File.Exists(linkPath) && TargetOf(linkPath) == exe) return;

            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            link.SetIconLocation(exe, 0);
            link.SetDescription("ZRevive - H1Z1: King of the Kill, Preseason 5");
            ((IPersistFile)link).Save(linkPath, true);
        }
        catch { }
    }

    static string? TargetOf(string linkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(linkPath, 0);
            var sb = new StringBuilder(260);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
        catch { return null; }
    }

    // ---- the minimum of IShellLink needed to write a .lnk ----

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    [System.Runtime.InteropServices.Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFile);
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    [System.Runtime.InteropServices.Guid("0000010b-0000-0000-C000-000000000046")]
    interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [System.Runtime.InteropServices.PreserveSig] int IsDirty();
        void Load([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string? pszFileName,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
