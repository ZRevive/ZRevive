using System.Windows;
using ZRevive.Launcher.Install;

namespace ZRevive.Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Windows runs the exe with --uninstall when the player clicks Uninstall in
        // "Installed apps". Handle it before any window exists: this run must not start the
        // launcher, and must not leave a window behind after the files are gone.
        if (e.Args.Any(a => string.Equals(a, UninstallEntry.UninstallSwitch, StringComparison.OrdinalIgnoreCase)))
        {
            RunUninstall(quiet: e.Args.Any(a => string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase)));
            Shutdown();
            return;
        }

        // Without these the launcher just vanishes on an unhandled exception - which is exactly what
        // a player reports ("it closed while copying game files") and all we can do is guess. The
        // install work runs on background tasks, where an exception otherwise kills the process with
        // no window, no message and no log.
        DispatcherUnhandledException += (_, args) =>
        {
            Report(args.Exception, "UI");
            args.Handled = true;              // keep the window alive; the error is on screen
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Report(args.ExceptionObject as Exception, "background");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Report(args.Exception, "task");
            args.SetObserved();
        };

        UninstallEntry.Register();   // refreshed every start, so it follows a moved or updated exe
        Shortcuts.Create();          // desktop + Start menu; the exe usually lives in Downloads
        base.OnStartup(e);
    }

    /// <summary>Where a crash is written, so "it just closed" becomes something we can read.</summary>
    public static string CrashLogPath => System.IO.Path.Combine(UninstallEntry.LogDirectory, "launcher-crash.log");

    static void Report(Exception? ex, string where)
    {
        if (ex == null) return;
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{where}]  launcher {Install.LauncherUpdate.CurrentVersion}\n{ex}\n\n";
        try
        {
            System.IO.Directory.CreateDirectory(UninstallEntry.LogDirectory);
            System.IO.File.AppendAllText(CrashLogPath, text);
        }
        catch { }

        try
        {
            MessageBox.Show(
                "ZRevive hit a problem:\n\n" + ex.Message +
                "\n\nIt has been written to:\n" + CrashLogPath,
                "ZRevive", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }

    /// <summary>
    /// Removes the launcher. The game folder is only deleted when the player says so, because it is
    /// tens of gigabytes they chose the location of, and because a settings file could point at an
    /// install we did not build (UninstallEntry.DeleteGameFolder refuses those outright).
    /// </summary>
    static void RunUninstall(bool quiet)
    {
        var settings = Settings.Load();
        var folder = settings.GameFolder ?? "";

        if (!quiet)
        {
            var ask = MessageBox.Show(
                "Remove the ZRevive launcher and its settings?\n\n" +
                "Your Steam copy of Z1 Battle Royale is never touched.",
                "Uninstall ZRevive",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ask != MessageBoxResult.OK) return;

            if (folder.Length > 0 && System.IO.Directory.Exists(folder))
            {
                var alsoGame = MessageBox.Show(
                    $"Also delete the ZRevive game files?\n\n{folder}\n\n" +
                    "Choose No to keep them - you can point a new launcher at this folder later.",
                    "Uninstall ZRevive",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (alsoGame == MessageBoxResult.Yes)
                {
                    var (deleted, reason) = UninstallEntry.DeleteGameFolder(folder);
                    if (!deleted)
                        MessageBox.Show(
                            $"The game folder was left alone: {reason}.\n\n{folder}",
                            "Uninstall ZRevive", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        Shortcuts.Remove();
        UninstallEntry.RemoveUserData();
        UninstallEntry.Remove();
        UninstallEntry.ScheduleSelfDelete();

        if (!quiet)
            MessageBox.Show(
                "ZRevive has been removed.\n\nThanks for playing.",
                "Uninstall ZRevive", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
