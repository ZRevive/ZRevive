using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ZRevive.Launcher.Install;

namespace ZRevive.Launcher;

/// <summary>
/// First-run install / update / verify-repair flow.
///
/// Three steps, shown as an overlay over the normal launcher body so nothing about the
/// existing sign-in, play, status or Discord behaviour changes:
///   1  find the player's Z1 Battle Royale install (Steam detection, or browse)
///   2  choose the ZRevive folder (copy by default, hardlinks opt-in)
///   3  build the base folder, then download + verify our delta from the manifest
/// </summary>
public partial class MainWindow
{
    public sealed record CandidateRow(string Folder, string Caption, bool Valid);

    enum SetupStep { Source, Target, Work, Done, Problem }

    SetupStep _step = SetupStep.Source;
    CancellationTokenSource? _setupCts;
    bool _setupBusy;
    string _setupSource = "";
    string _setupTarget = "";
    ReleaseManifest? _manifest;
    bool _updateAvailable;
    bool _skipUpdateOnce;
    bool _installBlocked;        // a hash check failed: PLAY must not launch
    string? _blockedReason;

    // ETag-conditional manifest fetch + on-disk cache, so the per-launch check is tiny.
    readonly InstallService _install = new(new HttpDownloader(), new ManifestSource(new HttpManifestFetcher()));

    // ---------- entry points ----------

    /// <summary>
    /// Startup check. Rules the owner asked for:
    ///   - GameFolder is NEVER repointed on its own once InstallComplete is true;
    ///   - a configured folder that fails validation shows WHY plus Repair / Choose folder,
    ///     instead of dumping the player into full setup;
    ///   - detection is only a suggestion, and only on a genuine first run;
    ///   - a folder that looks mid-build is never adopted.
    /// </summary>
    void MaybeStartFirstRun()
    {
        var folder = _settings.GameFolder ?? "";
        var report = GameLauncher.Inspect(folder);

        if (_settings.InstallComplete)
        {
            // Configured and claimed complete: trust the configured folder, full stop.
            if (report.Problem == null) return;

            // ...unless the folder is not one of ours at all (Repairable == false): the Steam copy,
            // the ROTK install, a folder that was adopted before a newer launcher learned to refuse
            // it. "CAN'T START THE GAME" with VERIFY / REPAIR on top is the wrong offer there -
            // there is nothing to repair, and repairing would write into a folder we must not touch.
            // The fix is always to choose a different folder, which is what setup is for.
            if (!report.Repairable)
            {
                ShowSetup();
                SetupError.Text = report.Problem;
                return;
            }

            ShowFolderProblem(report);
            return;
        }

        if (folder.Length == 0)
        {
            ShowSetup();            // genuine first run, nothing configured
            return;
        }

        // A folder that was never built and isn't there is also a first run, not a fault: there is
        // nothing to repair and nothing the player did wrong, so asking "what's wrong with your
        // install" is the wrong question. Belt and braces next to the empty-default above, because
        // a settings.json carried over from another PC lands here too.
        if (!Directory.Exists(folder))
        {
            ShowSetup();
            return;
        }

        if (report.Problem == null && !report.LooksMidBuild)
        {
            // Pre-existing hand-made install (how the repo has been run so far): adopt the
            // CONFIGURED folder only. We never go hunting for a "better" one.
            _settings.InstallComplete = true;
            _settings.Save();
            PlayStatus.Text = $"Using the ZRevive install at {folder}.";
            return;
        }

        if (report.Problem == null && report.LooksMidBuild)
        {
            // Another process is assembling that folder right now (this is what bit the
            // owner: GameFolder pointed at a half-built "ZRevive-Stock"). Don't adopt it,
            // don't rebuild it, don't repoint anything — just explain and let him choose.
            ShowFolderProblem(report with
            {
                Problem = "That folder is still being assembled right now (unfinished .zrpart/.tmp files, or core files " +
                          "written seconds ago). The launcher won't adopt a half-built install. Wait for it to finish, " +
                          "or choose the folder you actually play from."
            });
            return;
        }

        // Anything else, with nothing ever installed, is still a first run: the configured folder is
        // just a bad guess (the Steam copy, a leftover path, a folder that is not ours). "CAN'T START
        // THE GAME" claims something broke, which has never been true for this player, and the
        // Verify/Repair it offers has nothing to repair. Take them through setup instead.
        ShowSetup();
    }

    /// <summary>Shows the "what's wrong" step. Does not touch Settings.</summary>
    void ShowFolderProblem(GameLauncher.FolderReport report)
    {
        _setupSource = _settings.SourceGameFolder ?? "";
        _setupTarget = _settings.GameFolder ?? "";
        SourceBox.Text = _setupSource;
        TargetBox.Text = _setupTarget;
        ProblemText.Text = report.Problem ?? "Unknown problem.";
        ProblemFolder.Text = report.Folder.Length == 0
            ? "No folder is configured."
            : $"Configured folder: {report.Folder}" + (report.Repairable ? "  ·  this looks like a ZRevive install" : "");
        SetupError.Text = "";
        _step = SetupStep.Problem;
        InstallOverlay.Visibility = Visibility.Visible;
        RenderStep();
    }

    void ProblemChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select your ZRevive folder (the one with H1Z1.exe)",
            InitialDirectory = Directory.Exists(_settings.GameFolder) ? _settings.GameFolder : ""
        };
        if (dlg.ShowDialog(this) != true) return;

        var picked = dlg.FolderName;
        var report = GameLauncher.Inspect(picked);
        if (report.Problem != null)
        {
            SetupError.Text = report.Problem;
            return;
        }
        // Explicit user choice is the only thing allowed to repoint GameFolder.
        _settings.GameFolder = picked;
        _settings.Save();
        FolderBox.Text = picked;
        InstallOverlay.Visibility = Visibility.Collapsed;
        PlayStatus.Text = $"ZRevive folder set to {picked}.";
        CheckFolder();
    }

    void ProblemFullSetup_Click(object sender, RoutedEventArgs e) => ShowSetup();

    void RerunSetup_Click(object sender, RoutedEventArgs e) => ShowSetup();

    void ShowSetup()
    {
        _step = SetupStep.Source;
        _setupSource = _settings.SourceGameFolder ?? "";
        _setupTarget = _settings.GameFolder ?? "";
        SourceBox.Text = _setupSource;
        TargetBox.Text = _setupTarget;
        HardlinkBox.IsChecked = _settings.AllowHardlinks;
        SetupError.Text = "";
        ProgLog.Text = "";
        Rescan_Click(this, new RoutedEventArgs());
        InstallOverlay.Visibility = Visibility.Visible;
        RenderStep();
    }

    void RenderStep()
    {
        Step1.Visibility = _step == SetupStep.Source ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == SetupStep.Target ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step is SetupStep.Work or SetupStep.Done ? Visibility.Visible : Visibility.Collapsed;
        StepProblem.Visibility = _step == SetupStep.Problem ? Visibility.Visible : Visibility.Collapsed;
        SetupBackBtn.Visibility = _step == SetupStep.Target ? Visibility.Visible : Visibility.Collapsed;
        SetupCancelBtn.Content = _step == SetupStep.Work ? "CANCEL" : "CLOSE";
        SetupCancelBtn.Visibility = _step == SetupStep.Done ? Visibility.Collapsed : Visibility.Visible;
        SetupNextBtn.Visibility = _step == SetupStep.Problem ? Visibility.Collapsed : Visibility.Visible;

        switch (_step)
        {
            case SetupStep.Problem:
                SetupStepLabel.Text = "INSTALL  ·  NEEDS ATTENTION";
                SetupTitle.Text = "CAN'T START THE GAME";
                SetupBlurb.Text = "The ZRevive folder the launcher is pointed at didn't pass its check.";
                break;
            case SetupStep.Source:
                SetupStepLabel.Text = "SETUP  ·  STEP 1 OF 3";
                SetupTitle.Text = "FIND YOUR GAME";
                SetupBlurb.Text = "ZRevive needs the Z1 Battle Royale files you already own on Steam. Your Steam install is only ever read from — never changed.";
                SetupNextBtn.Content = "CONTINUE";
                ValidateSource();
                break;
            case SetupStep.Target:
                SetupStepLabel.Text = "SETUP  ·  STEP 2 OF 3";
                SetupTitle.Text = "WHERE TO INSTALL";
                SetupBlurb.Text = "ZRevive gets its own copy of the game so Steam can keep updating yours. Pick a drive with enough free space.";
                SetupNextBtn.Content = "INSTALL";
                ValidateTarget();
                break;
            case SetupStep.Work:
                SetupStepLabel.Text = "SETUP  ·  STEP 3 OF 3";
                SetupTitle.Text = "INSTALLING";
                SetupBlurb.Text = "Copying the base game, then downloading the ZRevive files. You can leave this running.";
                SetupNextBtn.IsEnabled = false;
                SetupNextBtn.Content = "INSTALL";
                break;
            case SetupStep.Done:
                SetupStepLabel.Text = "SETUP  ·  DONE";
                SetupTitle.Text = "READY TO PLAY";
                SetupBlurb.Text = "ZRevive is installed. Sign in through Steam and press PLAY.";
                SetupNextBtn.IsEnabled = true;
                SetupNextBtn.Content = "LET'S GO";
                break;
        }
    }

    void SkipOnce_Click(object sender, RoutedEventArgs e)
    {
        _skipUpdateOnce = true;
        if (_manifest != null)
        {
            _settings.SkippedReleaseHash = _manifest.ReleaseHash;
            _settings.Save();
        }
        _setupCts?.Cancel();
        SkipOnceBtn.Visibility = Visibility.Collapsed;
        InstallOverlay.Visibility = Visibility.Collapsed;
        PlayStatus.Text = "Update skipped for now. It will be offered again next time.";
        CheckFolder();
    }

    void SetupCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_setupBusy) { _setupCts?.Cancel(); return; }
        InstallOverlay.Visibility = Visibility.Collapsed;
        CheckFolder();
    }

    void SetupBack_Click(object sender, RoutedEventArgs e)
    {
        if (_setupBusy) return;
        _step = SetupStep.Source;
        RenderStep();
    }

    async void SetupNext_Click(object sender, RoutedEventArgs e)
    {
        // Checked here as well as on PLAY so nobody copies and patches 7 GB of game files and only
        // then finds out they cannot start the game. Advisory, not a refusal: an exempt account's
        // waiver is only known once they sign in.
        if (SecurityBlocked(advisory: true)) return;

        switch (_step)
        {
            case SetupStep.Source:
                if (!ValidateSource()) return;
                _setupSource = SourceBox.Text.Trim();
                if (TargetBox.Text.Trim().Length == 0 || TargetBox.Text.Trim() == _setupSource)
                    TargetBox.Text = DefaultTargetFor(_setupSource);
                _step = SetupStep.Target;
                RenderStep();
                break;

            case SetupStep.Target:
                if (!ValidateTarget()) return;
                _setupTarget = TargetBox.Text.Trim();
                _step = SetupStep.Work;
                RenderStep();
                await RunInstallAsync(buildBase: true);
                break;

            case SetupStep.Done:
                InstallOverlay.Visibility = Visibility.Collapsed;
                CheckFolder();
                break;
        }
    }

    /// <summary>
    /// This launcher is too old for the current release. Offers to replace itself and restart.
    /// Declining is allowed - the player can still use the install they already have - but the game
    /// files are deliberately NOT updated with an old launcher, which is the whole point of the
    /// manifest's requiredLauncher field.
    /// </summary>
    async Task OfferLauncherUpdateAsync(string required, CancellationToken ct)
    {
        var ask = MessageBox.Show(
            $"A new ZRevive launcher is needed for this update.\n\n" +
            $"You have {LauncherUpdate.CurrentVersion}, the servers are on {required}.\n\n" +
            "Update now? The launcher will restart itself.",
            "ZRevive update",
            MessageBoxButton.OKCancel, MessageBoxImage.Information);

        if (ask != MessageBoxResult.OK)
        {
            PlayStatus.Text = $"Launcher {required} is needed for the newest update (you have {LauncherUpdate.CurrentVersion}).";
            return;
        }

        PlayStatus.Text = "Downloading the new launcher…";
        var (started, reason) = await LauncherUpdate.DownloadAndRestartAsync(
            new Progress<double>(p => PlayStatus.Text = $"Downloading the new launcher… {p:P0}"),
            ct);

        if (started)
        {
            Application.Current.Shutdown();   // the detached shell swaps the file and starts it again
            return;
        }

        PlayStatus.Text = "Couldn't update automatically: " + reason;
        if (MessageBox.Show(
                "The launcher couldn't update itself: " + reason +
                "\n\nOpen the download page instead?",
                "ZRevive update", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            LauncherUpdate.OpenReleasePage();
    }

    /// <inheritdoc cref="GameLauncher.DefaultTargetFor"/>
    public static string DefaultTargetFor(string source) => GameLauncher.DefaultTargetFor(source);

    // ---------- step 1: source ----------

    void Rescan_Click(object sender, RoutedEventArgs e)
    {
        List<GameCandidate> found;
        try { found = SteamLocator.Detect(_settings.SourceGameFolder); }
        catch { found = new List<GameCandidate>(); }

        var rows = found.Select(c => new CandidateRow(
            c.Folder,
            c.Valid
                ? $"{c.Source ?? "Found"}  ·  build {c.Version ?? "unknown"}"
                : c.Problem ?? "Not usable",
            c.Valid)).ToList();

        FoundList.ItemsSource = rows;
        NoneFound.Visibility = rows.Any(r => r.Valid) ? Visibility.Collapsed : Visibility.Visible;

        if (SourceBox.Text.Trim().Length == 0 && rows.FirstOrDefault(r => r.Valid) is CandidateRow first)
            SourceBox.Text = first.Folder;
        ValidateSource();
    }

    void Found_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string folder }) SourceBox.Text = folder;
    }

    void SourceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_step == SetupStep.Source) ValidateSource();
    }

    void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select your Z1 Battle Royale folder (the one with H1Z1.exe)",
            InitialDirectory = Directory.Exists(SourceBox.Text) ? SourceBox.Text : SteamLocator.SteamPath() ?? ""
        };
        if (dlg.ShowDialog(this) == true) SourceBox.Text = dlg.FolderName;
    }

    bool ValidateSource()
    {
        var folder = SourceBox.Text.Trim();
        var problem = SteamLocator.ValidateSource(folder);
        if (problem == null)
        {
            var version = SteamLocator.FileVersion(folder);
            var warn = version != null && version != GameLauncher.ExpectedVersion
                ? $"  ·  warning: build {version}, ZRevive expects {GameLauncher.ExpectedVersion}"
                : "";
            SourceStatus.Text = $"Z1 Battle Royale found (build {version ?? "unknown"}).{warn}";
            SourceStatus.Foreground = (Brush)FindResource(warn.Length == 0 ? "Ok" : "Gold");
        }
        else
        {
            SourceStatus.Text = problem;
            SourceStatus.Foreground = (Brush)FindResource("RedHi");
        }
        SetupNextBtn.IsEnabled = problem == null;
        return problem == null;
    }

    // ---------- step 2: target ----------

    void TargetBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_step == SetupStep.Target) ValidateTarget();
    }

    void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose an empty folder for ZRevive",
            InitialDirectory = Directory.Exists(TargetBox.Text) ? TargetBox.Text : ""
        };
        if (dlg.ShowDialog(this) == true) TargetBox.Text = Path.Combine(dlg.FolderName, "ZRevive");
    }

    void Hardlink_Changed(object sender, RoutedEventArgs e)
    {
        _settings.AllowHardlinks = HardlinkBox.IsChecked == true;
        _settings.Save();
        if (_step == SetupStep.Target) ValidateTarget();
    }

    bool ValidateTarget()
    {
        var source = SourceBox.Text.Trim();
        var target = TargetBox.Text.Trim();
        // Caught here as well as at PLAY, so the player is told at the step where they can still
        // change it, instead of after the whole base has been copied into the Steam library.
        var problem = GameLauncher.LooksLikeSteamLibrary(target)
            ? "ZRevive can't be built inside your Steam library - PLAY and updates write into this "
              + "folder, and your Steam copy must stay untouched. Pick somewhere else, like C:\\Games\\ZRevive."
            : BaseBuilder.ValidatePair(source, target);
        if (problem != null)
        {
            TargetStatus.Text = problem;
            TargetStatus.Foreground = (Brush)FindResource("RedHi");
            SetupNextBtn.IsEnabled = false;
            return false;
        }

        var link = HardlinkBox.IsChecked == true;
        var sameVolume = BaseBuilder.SameVolume(source, target);
        var needed = SafeFolderSize(source);
        var note = link
            ? sameVolume
                ? "Hardlinks will be used: almost no extra disk space, but the base files are shared with your Steam copy."
                : "Hardlinks aren't possible across drives — the files will be copied instead."
            : $"About {Bytes(needed)} will be copied.";
        var free = FreeSpace(target);
        if (!link || !sameVolume)
        {
            if (free is long f && f < needed)
            {
                TargetStatus.Text = $"{note} Only {Bytes(f)} free on that drive.";
                TargetStatus.Foreground = (Brush)FindResource("RedHi");
                SetupNextBtn.IsEnabled = false;
                return false;
            }
            if (free is long ok) note += $" {Bytes(ok)} free.";
        }
        TargetStatus.Text = note;
        TargetStatus.Foreground = (Brush)FindResource("Ash");
        SetupNextBtn.IsEnabled = true;
        return true;
    }

    static long SafeFolderSize(string folder)
    {
        try { return BaseBuilder.Snapshot(folder).TotalBytes; }
        catch { return 0; }
    }

    static long? FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root == null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }
    }

    // ---------- step 3: build + delta ----------

    async Task RunInstallAsync(bool buildBase, VerifyReport? precomputed = null)
    {
        if (_setupBusy) return;
        _setupBusy = true;
        _setupCts = new CancellationTokenSource();
        var ct = _setupCts.Token;
        SetupError.Text = "";
        SetupNextBtn.IsEnabled = false;
        var target = _setupTarget.Length > 0 ? _setupTarget : _settings.GameFolder;

        try
        {
            var state = InstallState.Load(target);

            if (buildBase)
            {
                var hardlink = HardlinkBox.IsChecked == true;
                SetStage(hardlink ? "Linking base game files…" : "Copying base game files…", "");
                BaseBuilder.CopyHeartbeat = LogToFile;   // survives the window dying; see LogToFile
                LogToFile($"base build start: source={_setupSource} target={target} hardlink={hardlink}");
                // Progress<T> captures SynchronizationContext.Current WHEN IT IS CONSTRUCTED.
                // Constructed inside the Task.Run lambda there is none, so every report ran
                // OnBuildProgress on a thread-pool thread and WPF threw "The calling thread cannot
                // access this object because a different thread owns it" - which killed the launcher
                // mid-copy with no window and no message (players, 2026-10-07). Build it here, on
                // the UI thread, and the reports marshal back correctly.
                var buildProgress = new Progress<BuildProgress>(OnBuildProgress);
                var snapshot = await Task.Run(() => BaseBuilder.BuildAsync(_setupSource, target, hardlink,
                    buildProgress, ct), ct);

                state.SourceFolder = _setupSource;
                state.UsedHardlinks = hardlink && BaseBuilder.SameVolume(_setupSource, target);
                state.BaseBuiltUtc = DateTime.UtcNow;
                state.Save(target);

                _settings.SourceGameFolder = _setupSource;
                _settings.GameFolder = target;
                _settings.AllowHardlinks = hardlink;
                _settings.Save();
                FolderBox.Text = target;
                Log($"Base install built from {_setupSource} ({snapshot.FileCount} files, {Bytes(snapshot.TotalBytes)}). Your Steam copy was verified unchanged.");
            }

            // ---- our delta ----
            var manifestUrl = _settings.EffectiveManifestUrl();
            SetStage("Checking for ZRevive files…", manifestUrl);
            ReleaseManifest manifest;
            try
            {
                var fetched = await _install.GetManifestAsync(manifestUrl, ct);
                manifest = fetched.Manifest;
                _manifest = manifest;
                if (fetched.Warning != null) Log(fetched.Warning);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // No CDN yet / offline: the base install is still usable and PLAY decides on
                // its own whether the folder is launchable.
                Log("Couldn't fetch the update manifest: " + ex.Message);
                Log("You can press VERIFY / REPAIR later to finish the ZRevive files.");
                FinishSetup(complete: false, target);
                return;
            }

            if (manifest.GameVersion is string want && SteamLocator.FileVersion(target) is string have && want != have)
                Log($"Note: this release targets game build {want}, the install reports {have}.");

            VerifyReport report;
            if (precomputed != null)
            {
                report = precomputed;   // the fast check already told us what's wrong
            }
            else
            {
                SetStage("Verifying files…", "");
                var verifyProgress = new Progress<InstallProgress>(OnInstallProgress);  // see above: never inside Task.Run
                report = await Task.Run(() => InstallService.VerifyAsync(manifest, target,
                    verifyProgress, ct), ct);
            }

            if (report.Healthy)
            {
                Log($"All {report.All.Count} ZRevive files are already correct.");
            }
            else
            {
                Log($"{report.Broken.Count()} of {report.All.Count} files need work ({Bytes(report.RepairBytes)} to download).");
                var changed = await _install.ApplyAsync(manifest, manifestUrl, target, report,
                    new Progress<InstallProgress>(OnInstallProgress), ct);
                Log($"{changed} file(s) installed.");

                SetStage("Re-checking files…", "");
                var after = await Task.Run(() => InstallService.VerifyAsync(manifest, target, null, ct), ct);
                if (!after.Healthy)
                    throw new IOException($"{after.Broken.Count()} file(s) still don't match after installing. Try VERIFY / REPAIR again.");
            }

            state = InstallState.Load(target);
            state.ReleaseHash = manifest.ReleaseHash;
            state.ManifestVersion = manifest.Version;
            state.GameVersion = manifest.GameVersion;
            state.DeltaAppliedUtc = DateTime.UtcNow;
            state.Complete = true;
            state.Save(target);

            // Only an explicit setup run may repoint GameFolder; a verify/repair/update must
            // never move the player's install out from under them.
            if (buildBase) _settings.GameFolder = target;
            _settings.CurrentReleaseHash = manifest.ReleaseHash;   // per-mode: see Settings.CurrentReleaseHash
            _settings.InstallComplete = true;
            _settings.SkippedReleaseHash = null;
            _settings.Save();
            _updateAvailable = false;
            _skipUpdateOnce = false;
            _installBlocked = false;
            _blockedReason = null;

            var fresh = FilePrints.Load(target);
            fresh.ReleaseHash = manifest.ReleaseHash;
            fresh.Save(target);

            FinishSetup(complete: true, target);
        }
        catch (OperationCanceledException)
        {
            SetStage("Cancelled.", "");
            Log("Nothing was launched. Press RE-RUN SETUP or VERIFY / REPAIR to carry on; finished files are kept.");
            SetupNextBtn.IsEnabled = false;
            SetupCancelBtn.Content = "CLOSE";
        }
        catch (Exception ex)
        {
            SetStage("Install failed.", "");
            SetupError.Text = ex.Message;
            SetupCancelBtn.Content = "CLOSE";
        }
        finally
        {
            _setupBusy = false;
            _setupCts?.Dispose();
            _setupCts = null;
            CheckFolder();
        }
    }

    void FinishSetup(bool complete, string target)
    {
        ProgBar.Value = 1;
        ProgFileBar.Value = 1;
        _step = SetupStep.Done;
        RenderStep();
        if (!complete)
        {
            SetupTitle.Text = "BASE INSTALL READY";
            SetupBlurb.Text = "The game files are in place, but the ZRevive update files haven't been applied yet.";
        }
        FolderBox.Text = target;
        CheckFolder();
    }

    // ---------- verify / repair + update check ----------

    async void VerifyRepair_Click(object sender, RoutedEventArgs e)
    {
        if (_setupBusy) return;
        if (_game != null) { PlayStatus.Text = "Close the game before verifying the install."; return; }
        _setupSource = _settings.SourceGameFolder ?? SourceBox.Text.Trim();
        _setupTarget = _settings.GameFolder ?? "";
        if (_setupTarget.Length == 0) { ShowSetup(); return; }
        _step = SetupStep.Work;
        ProgLog.Text = "";
        InstallOverlay.Visibility = Visibility.Visible;
        RenderStep();
        SetupStepLabel.Text = "INSTALL  ·  VERIFY & REPAIR";
        SetupTitle.Text = "CHECKING FILES";
        SetupBlurb.Text = "Every ZRevive file is re-checked against the release manifest; anything missing or wrong is downloaded again.";
        await RunInstallAsync(buildBase: false);
    }

    // ---------- fast verify + auto update ----------

    /// <summary>
    /// The per-launch check, also wired to the FAST VERIFY button.
    ///
    /// 1  conditional manifest fetch (If-None-Match; a 304 is a few hundred bytes, and an
    ///    unreachable server falls back to the cached manifest so the player can still play)
    /// 2  releaseHash compare -> "update available"
    /// 3  quick verify: size vs the manifest, then size+mtime vs our fingerprint file,
    ///    hashing only what looks changed or is flagged critical
    ///
    /// Non-blocking on network failure; hard-blocking when a local file fails its hash.
    /// </summary>
    async Task<QuickVerifyResult?> FastCheckAsync(bool quiet, CancellationToken ct)
    {
        _installBlocked = false;
        _blockedReason = null;
        if (!_settings.InstallComplete) return null;

        var folder = _settings.GameFolder ?? "";
        if (GameLauncher.Inspect(folder).Problem != null) return null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ManifestSource.Result manifestResult;
        try
        {
            manifestResult = await _install.GetManifestAsync(_settings.EffectiveManifestUrl(), ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            // No manifest at all (no CDN yet, or a bad URL): say so, let him play.
            if (!quiet) PlayStatus.Text = "Update check skipped: " + ex.Message;
            Debug.WriteLine("[install] manifest unavailable: " + ex.Message);
            return null;
        }

        _manifest = manifestResult.Manifest;
        var fetchMs = sw.ElapsedMilliseconds;

        // The launcher itself first: a release can need a newer launcher than the one running, and
        // applying it with an old one is how a player ends up with a half-patched install and an
        // error that looks like a server fault. Checked before the game files on purpose.
        if (LauncherUpdate.IsOlderThan(_manifest.RequiredLauncher))
        {
            await OfferLauncherUpdateAsync(_manifest.RequiredLauncher!, ct);
            return null;
        }

        _updateAvailable = InstallService.NeedsUpdate(_manifest, _settings.CurrentReleaseHash);
        if (_updateAvailable && !FileHasher.HashesEqual(_settings.SkippedReleaseHash, _manifest.ReleaseHash))
            _skipUpdateOnce = false;   // a NEW release clears a previous skip

        var prints = FilePrints.Load(folder);
        var quick = await Task.Run(() => QuickVerifier.RunAsync(_manifest, folder, prints, ct), ct);
        prints.ReleaseHash = _settings.CurrentReleaseHash;
        prints.Save(folder);

        _installBlocked = quick.HashFailure || quick.Suspect.Any(s => s.Verdict == EntryVerdict.Unsafe);
        if (_installBlocked)
            _blockedReason = $"{quick.Suspect.Count} file(s) don't match the release. Run VERIFY / REPAIR before playing.";

        var note = $"Checked {quick.Checked} files in {quick.ElapsedMs} ms"
                   + (quick.Hashed > 0 ? $" ({quick.Hashed} hashed)" : " (no hashing needed)")
                   + $"; manifest {(manifestResult.Offline ? "offline, cached" : manifestResult.Unchanged ? "unchanged" : "updated")} in {fetchMs} ms.";
        Debug.WriteLine("[install] " + note);

        if (!quiet || _installBlocked || _updateAvailable)
        {
            PlayStatus.Text = _installBlocked
                ? _blockedReason
                : _updateAvailable
                    ? "An update is ready. Press UPDATE to install it."
                    : manifestResult.Warning ?? note;
        }

        CheckFolder();
        return quick;
    }

    /// <summary>Background check at startup; quiet unless something needs saying.</summary>
    async Task CheckForUpdateAsync()
    {
        // The launcher's OWN version is checked first and unconditionally. FastCheckAsync gives up
        // before it fetches anything when the install is incomplete or the folder has a problem -
        // which is precisely the player who most needs a newer launcher, since that is often the
        // thing that fixes their install. Checking it there only meant they never heard about it.
        try { if (await CheckLauncherVersionAsync(CancellationToken.None)) return; }
        catch { /* offline, bad manifest: never block startup over it */ }

        try { await FastCheckAsync(quiet: true, CancellationToken.None); }
        catch { /* never let the startup check break the launcher */ }
    }

    /// <summary>
    /// Fetches the manifest purely to compare <c>requiredLauncher</c> with this build. Returns true
    /// when an update was offered, so the caller stops: the game files are not touched by an
    /// out-of-date launcher.
    /// </summary>
    async Task<bool> CheckLauncherVersionAsync(CancellationToken ct)
    {
        // Fetch the RAW manifest and read only requiredLauncher. Parsing it properly would throw
        // on a manifest version this build doesn't know - which is exactly the launcher that needs
        // telling - so the "you must update" message would be unreachable by the only people it is
        // for. Measured live 2026-10-07: a 0.2.x launcher never offered the 0.3.0 update because it
        // could not read the v2 manifest carrying the request.
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var json = await http.GetStringAsync(_settings.EffectiveManifestUrl(), ct);
        var required = ManifestParser.PeekRequiredLauncher(json);
        if (!LauncherUpdate.IsOlderThan(required)) return false;

        LogToFile($"launcher {LauncherUpdate.CurrentVersion} is older than the required {required}");
        await OfferLauncherUpdateAsync(required!, ct);
        return true;
    }

    async void FastVerify_Click(object sender, RoutedEventArgs e)
    {
        if (_setupBusy) return;
        if (!_settings.InstallComplete) { PlayStatus.Text = "Nothing installed yet — run setup first."; return; }
        PlayStatus.Text = "Fast verifying…";
        var quick = await FastCheckAsync(quiet: false, CancellationToken.None);
        if (quick == null) return;
        if (quick.Ok && !_updateAvailable)
            PlayStatus.Text = $"Install is good. {quick.Checked} files checked in {quick.ElapsedMs} ms" +
                              (quick.Hashed > 0 ? $", {quick.Hashed} hashed." : ", nothing needed hashing.");
    }

    /// <summary>
    /// Runs before the game starts. Returns false to abort the launch.
    /// Applies a pending update (unless skipped), and refuses to launch when a file failed
    /// its hash check.
    /// </summary>
    async Task<bool> PreLaunchAsync()
    {
        if (!_settings.AutoUpdate) return true;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var quick = await FastCheckAsync(quiet: true, cts.Token);

        if (_installBlocked)
        {
            PlayStatus.Text = _blockedReason;
            if (quick != null && _manifest != null) await RepairFromQuickAsync(quick);
            return !_installBlocked;      // only launch if the repair cleared it
        }

        if (_updateAvailable && !_skipUpdateOnce)
        {
            await RunUpdateIfNeeded();
            return !_updateAvailable && !_installBlocked;
        }

        return true;
    }

    /// <summary>Repairs exactly the files the quick verify flagged (no full re-hash pass).</summary>
    async Task RepairFromQuickAsync(QuickVerifyResult quick)
    {
        if (_manifest == null) return;
        _setupTarget = _settings.GameFolder ?? "";
        _step = SetupStep.Work;
        ProgLog.Text = "";
        InstallOverlay.Visibility = Visibility.Visible;
        RenderStep();
        SetupStepLabel.Text = "INSTALL  ·  REPAIR";
        SetupTitle.Text = "FIXING FILES";
        SetupBlurb.Text = "The fast check found files that don't match the release. They're being downloaded again.";
        Log($"{quick.Suspect.Count} file(s) flagged by the fast check.");
        await RunInstallAsync(buildBase: false, QuickVerifier.ToReport(_manifest, quick));
    }

    void ManifestBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var url = ManifestBox.Text.Trim();
        if (url == (_settings.ManifestUrl ?? "")) return;
        _settings.ManifestUrl = url.Length == 0 ? null : url;
        _settings.Save();
        _ = CheckForUpdateAsync();   // new source -> re-check (its own cache entry)
    }

    void AutoUpdateBox_Changed(object sender, RoutedEventArgs e)
    {
        _settings.AutoUpdate = AutoUpdateBox.IsChecked == true;
        _settings.Save();
    }

    /// <summary>PLAY turns into UPDATE when the manifest moved on. Returns true when handled.</summary>
    async Task<bool> RunUpdateIfNeeded()
    {
        if (!_updateAvailable) return false;
        _setupSource = _settings.SourceGameFolder ?? "";
        _setupTarget = _settings.GameFolder ?? "";
        _step = SetupStep.Work;
        ProgLog.Text = "";
        InstallOverlay.Visibility = Visibility.Visible;
        RenderStep();
        SetupStepLabel.Text = "INSTALL  ·  UPDATE";
        SetupTitle.Text = "UPDATING";
        SetupBlurb.Text = "Downloading the newest ZRevive release.";
        SkipOnceBtn.Visibility = Visibility.Visible;   // "play the old build this once"
        await RunInstallAsync(buildBase: false);
        SkipOnceBtn.Visibility = Visibility.Collapsed;
        return true;
    }

    // ---------- progress plumbing ----------

    void OnBuildProgress(BuildProgress p)
    {
        ProgStage.Text = $"{p.Stage}  ·  {p.DoneFiles} / {p.TotalFiles} files";
        ProgFile.Text = p.CurrentFile;
        ProgBar.Value = p.TotalBytes <= 0 ? 0 : Math.Clamp(p.DoneBytes / (double)p.TotalBytes, 0, 1);
        ProgFileBar.Value = p.TotalFiles <= 0 ? 0 : Math.Clamp(p.DoneFiles / (double)p.TotalFiles, 0, 1);
        ProgBytes.Text = $"{Bytes(p.DoneBytes)} / {Bytes(p.TotalBytes)}";
        ProgSpeed.Text = Speed(p.BytesPerSecond);
    }

    void OnInstallProgress(InstallProgress p)
    {
        ProgStage.Text = p.TotalItems > 0 ? $"{p.Stage}  ·  {p.DoneItems} / {p.TotalItems}" : p.Stage;
        ProgFile.Text = p.CurrentFile;
        ProgBar.Value = Math.Clamp(p.OverallFraction, 0, 1);
        ProgFileBar.Value = Math.Clamp(p.FileFraction, 0, 1);
        ProgBytes.Text = p.TotalBytes > 0 ? $"{Bytes(p.DoneBytes)} / {Bytes(p.TotalBytes)}" : "";
        ProgSpeed.Text = Speed(p.BytesPerSecond);
    }

    void SetStage(string stage, string detail)
    {
        ProgStage.Text = stage;
        ProgFile.Text = detail;
        ProgSpeed.Text = "";
    }

    void Log(string line)
    {
        ProgLog.Text = ProgLog.Text.Length == 0 ? line : ProgLog.Text + "\n" + line;
        LogToFile(line);
    }

    /// <summary>
    /// Mirrors the setup log to disk. The on-screen log dies with the window, and a launcher that
    /// disappears mid-install ("it closed while copying game files") leaves nothing to look at -
    /// including when something outside the process kills it, such as antivirus reacting to an
    /// unsigned binary copying tens of gigabytes, which no in-process handler can catch.
    /// Written unbuffered so the last line before a kill is still on disk.
    /// </summary>
    internal static void LogToFile(string line)
    {
        try
        {
            Directory.CreateDirectory(Install.UninstallEntry.LogDirectory);
            File.AppendAllText(
                Path.Combine(Install.UninstallEntry.LogDirectory, "launcher.log"),
                $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch { }
    }

    public static string Bytes(long n)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = n;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        // Invariant: the launcher must print "1.5 GB" on a sv-SE machine too.
        return i == 0
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} B", n)
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#} {1}", v, units[i]);
    }

    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond <= 1 ? "" : $"{Bytes((long)bytesPerSecond)}/s";
}
