using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ZRevive.Launcher;

public partial class MainWindow : Window
{
    readonly Settings _settings = Settings.Load();
    readonly PortalClient _portal;
    LoginResult? _login;
    string? _authKey;
    CancellationTokenSource? _steamWait;

    // Discord Rich Presence. The application id is baked in AT BUILD TIME (see Branding), so the
    // launcher we ship has working presence while the id itself is not in the source tree.
    static readonly string DiscordAppId = Branding.DiscordAppId;
    readonly DiscordPresence _discord = new(DiscordAppId);
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    Process? _game;
    long _gameStartUnix;
    int? _playersOnline;
    bool _refreshing;
    /// <summary>True while ShowMode() sets the radio buttons, so Checked doesn't re-enter.</summary>
    bool _modeSwitching;

    static readonly string[] RankColors = { "#9B958B", "#6EA8D8", "#D9A441", "#E1262C" };
    static readonly string?[] RankImages = { null, "mod", "admin", "owner" };

    public MainWindow()
    {
        InitializeComponent();
        _portal = new PortalClient(_settings.EffectivePortalUrl());
        ShowMode();                       // sets the radio buttons and PortalBox for the saved mode
        FolderBox.Text = _settings.GameFolder;
        ManifestBox.Text = _settings.ManifestUrl ?? "";
        AutoUpdateBox.IsChecked = _settings.AutoUpdate;
        AutoUpdateBox.Checked += AutoUpdateBox_Changed;
        AutoUpdateBox.Unchecked += AutoUpdateBox_Changed;
        DiscordBox.IsChecked = _settings.DiscordStatus;
        DiscordBox.Checked += DiscordBox_Changed;   // hooked after the initial value is set
        DiscordBox.Unchecked += DiscordBox_Changed;
        // Diagnostics: set ZREVIVE_DISCORD_LOG=<file> to get a trace of the Discord IPC traffic.
        var discordLog = Environment.GetEnvironmentVariable("ZREVIVE_DISCORD_LOG");
        _discord.Log += msg =>
        {
            Debug.WriteLine("[discord] " + msg);
            if (discordLog != null)
                try { System.IO.File.AppendAllText(discordLog, $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}"); } catch { }
        };
        var ver = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = ver == null ? "" : $"Launcher {ver.Major}.{ver.Minor}.{ver.Build}";
        NewsList.ItemsSource = BuiltInNews;
        SetPlay("SIGN IN TO PLAY", false);
        _statusTimer.Tick += async (_, _) => await RefreshStatus();
        Loaded += async (_, _) =>
        {
            _statusTimer.Start();
            await RefreshStatus();
            _ = LoadNews();
            var saved = _settings.GetKey();
            if (saved != null) await SignIn(saved, quiet: true);
            await EnforceModeVisibility(); // hidden until an Owner signs in, so players never see it
            MaybeStartFirstRun();          // shows the install view when there's nothing to play yet
            ShowSecurityState();           // Secure Boot / HVCI, said up front rather than after a 7 GB download
            _ = CheckForUpdateAsync();     // silent when the manifest/CDN isn't reachable
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        _discord.Dispose(); // clears the presence
        base.OnClosed(e);
    }

    // ---------- Discord presence ----------
    void UpdatePresence()
    {
        if (_login == null || !_settings.DiscordStatus)
        {
            _discord.Clear();
            return;
        }

        if (_game != null)
        {
            _discord.Set(new DiscordPresence.Activity(
                Details: "H1Z1: King of the Kill",
                State: "In the lobby",
                LargeImage: "logo",
                LargeText: "ZRevive · Preseason 5",
                StartUnixSeconds: _gameStartUnix));
            return;
        }

        var level = Math.Clamp(_login.PermissionLevel, 0, 3);
        var state = "Getting ready";
        if (_playersOnline is int n)
            state += n == 1 ? " · 1 player online" : $" · {n} players online";
        _discord.Set(new DiscordPresence.Activity(
            Details: "In the launcher",
            State: state,
            LargeImage: "logo",
            LargeText: "ZRevive",
            SmallImage: RankImages[level],
            SmallText: RankImages[level] == null ? null : _login.Rank));
    }

    void DiscordBox_Changed(object sender, RoutedEventArgs e)
    {
        _settings.DiscordStatus = DiscordBox.IsChecked == true;
        _settings.Save();
        UpdatePresence();
    }

    void TrackGame(Process proc)
    {
        _game = proc;
        _gameStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => Dispatcher.BeginInvoke(() => GameExited(proc));
            if (proc.HasExited) GameExited(proc);
        }
        catch
        {
            // Can't watch the process (access denied etc.): don't get stuck showing "in game".
            _game = null;
        }
        UpdatePresence();
    }

    void GameExited(Process proc)
    {
        if (_game != proc) return;
        _game = null;
        PlayStatus.Text = "Game closed.";
        CheckFolder();
        UpdatePresence();
    }

    // ---------- play button state ----------
    void SetPlay(string label, bool enabled)
    {
        PlayLabel.Text = label;
        PlayBtn.IsEnabled = enabled;
    }

    // ---------- news ----------
    static readonly NewsItem[] BuiltInNews =
    {
        new("NOW", "Preseason 5 is live on ZRevive", "Solo matches on the Z1 map with the original Preseason 5 gameplay. Sign in through Steam, point the launcher at your game folder and press PLAY."),
        new("NEW", "Crowns, crates and the in-game store", "Earn Crowns by playing matches, open crates for skins and manage your inventory from the website store."),
        new("INFO", "Discord status", "Turn on Show Discord status to show your friends that you're in the lobby or in a match."),
        new("TIP", "Screenshots", "The game's screenshot key is moved to Ctrl+F9 and the client runs in windowed fullscreen so Discord and Windows capture tools can see it."),
    };

    async Task LoadNews()
    {
        var items = await _portal.NewsAsync();
        if (items != null) NewsList.ItemsSource = items;
    }

    void Site_Click(object sender, RoutedEventArgs e) => OpenUrl(_portal.BaseUrl + "/");
    void Store_Click(object sender, RoutedEventArgs e) => OpenUrl(_portal.BaseUrl + "/store");

    void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { PlayStatus.Text = "Can't open the browser: " + ex.Message; }
    }

    // ---------- server status ----------
    async Task RefreshStatus()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var s = await _portal.StatusAsync();
            _playersOnline = s?.Connected;
            var kotk = s?.Servers?.FirstOrDefault(x => x.Tag == "KOTK") ?? s?.Servers?.FirstOrDefault();
            StatOnline.Text = (s?.Connected ?? 0).ToString();
            StatSurvivors.Text = (s?.Players ?? 0).ToString();
            StatCapacity.Text = kotk == null ? "—" : $"{kotk.Population} / {kotk.MaxPopulation}";
            if (kotk == null)
                SetStatus(false, "No game server listed", "The site is up but no server is registered yet.");
            else
                SetStatus(kotk.Online,
                    kotk.Online ? $"Online  ·  {kotk.Population} / {kotk.MaxPopulation} players" : "Game server offline",
                    s!.ServerName ?? $"{s.Players} survivors registered");
        }
        catch
        {
            _playersOnline = null;
            StatOnline.Text = StatCapacity.Text = StatSurvivors.Text = "—";
            SetStatus(false, "Can't reach ZRevive", _portal.BaseUrl);
        }
        finally
        {
            _refreshing = false;
        }
        UpdatePresence();
    }

    void SetStatus(bool ok, string title, string sub)
    {
        StatusDot.Fill = (Brush)FindResource(ok ? "Ok" : "Red");
        if (StatusDot.Effect is System.Windows.Media.Effects.DropShadowEffect glow)
            glow.Color = ok ? Color.FromRgb(0x7C, 0xC4, 0x7F) : Color.FromRgb(0xE1, 0x26, 0x2C);
        StatusText.Text = title;
        StatusSub.Text = sub;
        _serverOnline = ok;
        if (_login != null && _game == null) CheckFolder();
    }
    bool _serverOnline;

    // ---------- sign in ----------
    async void Steam_Click(object sender, RoutedEventArgs e)
    {
        LoginError.Text = "";
        ApplyPortalUrl();
        try
        {
            var start = await _portal.SteamStartAsync();
            if (!start.Ok || start.Id == null || start.Url == null)
            {
                LoginError.Text = start.Error ?? "Couldn't start Steam sign-in.";
                return;
            }
            CodeText.Text = start.Code;
            WaitBox.Visibility = Visibility.Visible;
            SteamBtn.IsEnabled = false;
            Process.Start(new ProcessStartInfo(start.Url) { UseShellExecute = true });

            _steamWait?.Cancel();
            _steamWait = new CancellationTokenSource();
            var key = await WaitForSteam(start.Id, _steamWait.Token);
            if (key != null) await SignIn(key);
        }
        catch (Exception ex)
        {
            LoginError.Text = "Can't reach ZRevive: " + ex.Message;
        }
        finally
        {
            WaitBox.Visibility = Visibility.Collapsed;
            SteamBtn.IsEnabled = true;
        }
    }

    async Task<string?> WaitForSteam(string id, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddMinutes(10);
        while (!ct.IsCancellationRequested && DateTime.UtcNow < until)
        {
            try { await Task.Delay(2000, ct); } catch (TaskCanceledException) { return null; }
            var r = await _portal.SteamPollAsync(id);
            if (r.Ok && r.AuthKey != null) return r.AuthKey;
            if (!r.Pending)
            {
                LoginError.Text = r.Error ?? "Sign-in expired, try again.";
                return null;
            }
        }
        return null;
    }

    void CancelSteam_Click(object sender, RoutedEventArgs e) => _steamWait?.Cancel();

    void KeyToggle_Click(object sender, RoutedEventArgs e)
    {
        KeyPanel.Visibility = KeyPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (KeyPanel.Visibility == Visibility.Visible) KeyBox.Focus();
    }

    async void KeySignIn_Click(object sender, RoutedEventArgs e) => await SignIn(KeyBox.Text);

    async void KeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SignIn(KeyBox.Text);
    }

    async Task SignIn(string key, bool quiet = false)
    {
        key = key.Trim().ToUpperInvariant();
        if (key.Length == 0) return;
        LoginError.Text = "";
        KeyBtn.IsEnabled = false;
        try
        {
            ApplyPortalUrl();
            var r = await _portal.LoginAsync(key);
            if (!r.Ok)
            {
                if (!quiet || r.Error?.StartsWith("Banned") == true) LoginError.Text = r.Error ?? "Sign in failed.";
                if (r.Error?.StartsWith("Unknown") == true) { _settings.SetKey(null); _settings.Save(); }
                return;
            }
            _login = r;
            _authKey = key;
            _settings.SetKey(RememberBox.IsChecked == true ? key : null);
            _settings.Save();
            ShowAccount();
            await EnforceModeVisibility();   // LOCAL is owner-only; see CanUseLocalMode
        }
        catch (Exception ex)
        {
            if (!quiet) LoginError.Text = "Can't reach ZRevive: " + ex.Message;
        }
        finally
        {
            KeyBtn.IsEnabled = true;
        }
    }

    /// <summary>Writes the address box back into the slot for the CURRENT mode, not a shared one.</summary>
    void ApplyPortalUrl()
    {
        var url = PortalBox.Text.Trim().TrimEnd('/');
        if (url.Length == 0) return;
        if (url != _settings.EffectivePortalUrl())
        {
            if (_settings.Mode == ServerMode.Local) _settings.LocalPortalUrl = url;
            else _settings.LivePortalUrl = url;
            _settings.Save();
        }
        _portal.BaseUrl = url;
    }

    /// <summary>
    /// LOCAL is a developer tool: it points the launcher at a ZRevive stack on this PC. Only an
    /// Owner (level 3) is shown it. A player who lands in LOCAL gets a launcher that cannot reach
    /// anything, and the game it starts is told the login server is 127.0.0.1 - which looks like a
    /// server fault ("G29 - Unable to authenticate with Login Server") rather than a wrong setting.
    /// </summary>
    bool CanUseLocalMode => (_login?.PermissionLevel ?? 0) >= 3;

    /// <summary>
    /// Keeps the mode honest after sign-in: if whoever is signed in is not an Owner, LOCAL is
    /// hidden, and a settings file that already says LOCAL is moved back to LIVE rather than
    /// leaving them stuck on a stack they cannot reach.
    /// </summary>
    async Task EnforceModeVisibility()
    {
        var allowed = CanUseLocalMode;
        ModeLocalBox.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        ModeLiveBox.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        ModeHint.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        PortalLabel.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        PortalBox.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;

        if (!allowed && _settings.Mode == ServerMode.Local)
        {
            _settings.Mode = ServerMode.Live;
            _settings.Save();
            ShowMode();
            _portal.BaseUrl = _settings.EffectivePortalUrl();
            var saved = _settings.GetKey();
            if (saved != null) await SignIn(saved, quiet: true);
            await RefreshStatus();
        }
    }

    /// <summary>Puts the mode radio buttons and the address box in sync with the settings.</summary>
    void ShowMode()
    {
        _modeSwitching = true;
        try
        {
            ModeLiveBox.IsChecked = _settings.Mode == ServerMode.Live;
            ModeLocalBox.IsChecked = _settings.Mode == ServerMode.Local;
            PortalBox.Text = _settings.EffectivePortalUrl();
            PortalLabel.Text = _settings.Mode == ServerMode.Local ? "LOCAL SERVER ADDRESS" : "SERVER ADDRESS";
            ModeHint.Text = _settings.Mode == ServerMode.Local
                ? "Playing against this PC. Start the stack with START-SERVER.bat first."
                : "Playing on the public ZRevive servers.";
        }
        finally { _modeSwitching = false; }
    }

    /// <summary>
    /// Switching mode changes which portal issues the auth key, so the signed-in session cannot
    /// carry over: we sign out, point the client at the other portal, and sign back in with that
    /// mode's own remembered key if there is one. The game is never touched - a running game keeps
    /// its connection, and the next PLAY uses the new mode.
    /// </summary>
    async void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_modeSwitching) return;                       // our own ShowMode() write-back
        var wanted = ReferenceEquals(sender, ModeLocalBox) ? ServerMode.Local : ServerMode.Live;
        if (wanted == _settings.Mode) return;

        _settings.Mode = wanted;
        _settings.Save();

        _login = null;
        _authKey = null;
        _playersOnline = null;
        ShowLoginPanel();
        ShowMode();
        _portal.BaseUrl = _settings.EffectivePortalUrl();

        var saved = _settings.GetKey();
        if (saved != null) await SignIn(saved, quiet: true);

        await RefreshStatus();
        _manifest = null;
        _updateAvailable = false;
        await CheckForUpdateAsync();                      // silent when that stack isn't reachable
        UpdatePresence();
    }

    /// <summary>
    /// Back to the signed-out view WITHOUT forgetting any remembered key - unlike
    /// <see cref="SignOut_Click"/>, which is the player deliberately signing out. A mode switch
    /// must leave both modes' keys alone: clearing here would wipe the key for the mode being
    /// switched TO, since SetKey always writes the current mode's slot.
    /// </summary>
    void ShowLoginPanel()
    {
        AccountPanel.Visibility = Visibility.Collapsed;
        AccountActions.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        LoginError.Text = "";
        CrownsText.Text = "—";
        AvatarImg.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/zrevive.png"));
        SetPlay("SIGN IN TO PLAY", false);
        PlayStatus.Text = "";
    }

    // ---------- signed in ----------
    void ShowAccount()
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        AccountPanel.Visibility = Visibility.Visible;
        AccountActions.Visibility = Visibility.Visible;
        NameText.Text = _login!.Name;
        CrownsText.Text = _login.Crowns is int c ? c.ToString("N0") : "—";
        RankText.Text = (_login.Rank ?? "Player").ToUpperInvariant();
        var color = (Color)ColorConverter.ConvertFromString(RankColors[Math.Clamp(_login.PermissionLevel, 0, 3)]);
        RankText.Foreground = new SolidColorBrush(color);
        RankBadge.BorderBrush = new SolidColorBrush(color);
        SteamText.Text = _login.SteamName != null ? $"Steam: {_login.SteamName}" : "Key account";
        if (!string.IsNullOrEmpty(_login.SteamAvatar) && Uri.TryCreate(_login.SteamAvatar, UriKind.Absolute, out var avatar) && avatar.Scheme == "https")
            AvatarImg.Source = new BitmapImage(avatar);
        AdminBtn.Visibility = _login.PermissionLevel >= 1 ? Visibility.Visible : Visibility.Hidden;
        CheckFolder();
        UpdatePresence();
    }

    bool CheckFolder()
    {
        var problem = GameLauncher.Validate(FolderBox.Text.Trim());
        FolderStatus.Text = problem ?? $"Build {GameLauncher.ExpectedVersion} found.";
        FolderStatus.Foreground = (Brush)FindResource(problem == null ? "Ok" : "RedHi");
        // The button is still enabled while the server reads as offline: the status
        // endpoint can lag behind, and the game shows its own error if it can't connect.
        //
        // The security gate comes FIRST, ahead of install and update: a player with Secure Boot
        // off can never launch, so there is no sense letting them download 7 GB to find out.
        // The button stays clickable on purpose - clicking it is how they get the instructions.
        if (_security is { Ok: false } && !SecurityWaived) SetPlay("SECURITY CHECK", true);
        else if (_setupBusy) SetPlay("INSTALLING…", false);
        else if (_installBlocked) SetPlay("REPAIR", true);
        else if (problem != null && !_settings.InstallComplete) SetPlay("INSTALL", true);
        else if (_login == null) SetPlay("SIGN IN TO PLAY", false);
        else if (_game != null) SetPlay("PLAYING", false);
        else if (_updateAvailable) SetPlay("UPDATE", true);
        else if (problem != null) SetPlay("PLAY", false);
        else SetPlay(_serverOnline ? "PLAY" : "SERVER OFFLINE", true);
        return problem == null;
    }

    // ---------- Secure Boot / Memory integrity gate ----------

    /// <summary>
    /// Last result from <see cref="SecurityGate"/>. Null until it has been read once.
    /// </summary>
    SecurityGate.GateResult? _security;

    /// <summary>
    /// Has the portal waived the requirement for the signed-in account? Some PCs genuinely cannot
    /// turn Secure Boot on. The answer comes from the server with the sign-in reply - the launcher
    /// never decides this itself, or a player could grant it to themselves.
    /// </summary>
    bool SecurityWaived => _login?.SecurityExempt == true;

    /// <summary>
    /// Re-reads the gate and, when it fails, explains why and refuses. Returns true if the
    /// caller must stop. Called on every PLAY click rather than trusting the startup reading.
    /// </summary>
    /// <param name="advisory">
    /// Warn and let the player decide, instead of refusing. Used for the install, where a refusal
    /// would be wrong: the waiver travels with the sign-in reply, so before anyone has signed in
    /// the launcher cannot know whether this account is exempt, and hard-blocking would stop an
    /// exempt player from ever installing. Launching is never advisory.
    /// </param>
    bool SecurityBlocked(bool advisory = false)
    {
        _security = SecurityGate.Check();
        if (_security.Ok || SecurityWaived) return false;

        PlayStatus.Text = _security.Headline;
        CheckFolder();

        if (advisory && _login == null)
        {
            var go = MessageBox.Show(this,
                _security.Detail +
                "\n\nYou can still install, but you will not be able to start the game until this is " +
                "fixed - unless staff have waived it for your account.\n\nContinue with the install?",
                "ZRevive: this PC can't start the game yet",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return go != MessageBoxResult.Yes;
        }

        var answer = MessageBox.Show(this,
            _security.Detail + "\n\nOpen Windows Security now?",
            "ZRevive can't start on this PC",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) OpenCoreIsolation();
        return true;
    }

    void OpenCoreIsolation()
    {
        try
        {
            Process.Start(new ProcessStartInfo(SecurityGate.CoreIsolationUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // The URI handler is missing on some builds; the path through Settings still works.
            PlayStatus.Text = "Couldn't open Windows Security (" + ex.Message +
                              "). Open it from Start → Windows Security → Device security.";
        }
    }

    /// <summary>
    /// Reads the gate at startup so the requirement is visible before anything is downloaded,
    /// without a dialog in the player's face the moment the launcher opens.
    /// </summary>
    void ShowSecurityState()
    {
        _security = SecurityGate.Check();
        if (_security.Ok || SecurityWaived) return;
        PlayStatus.Text = _security.Headline + " Click PLAY for how to fix it.";
        CheckFolder();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ApplyPortalUrl();
        await RefreshStatus();
    }

    void FolderBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _settings.GameFolder = FolderBox.Text.Trim();
        _settings.Save();
        CheckFolder();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the folder that contains H1Z1.exe", InitialDirectory = FolderBox.Text };
        if (dlg.ShowDialog(this) != true) return;
        FolderBox.Text = dlg.FolderName;
        _settings.GameFolder = dlg.FolderName;
        _settings.Save();
        CheckFolder();
    }

    async void Play_Click(object sender, RoutedEventArgs e)
    {
        // Secure Boot + Memory integrity, re-read on every click (they only change over a reboot,
        // but a stale "pass" from startup must never be what lets a launch through).
        if (SecurityBlocked()) return;

        // The button doubles as INSTALL / UPDATE before it is PLAY.
        if (!_settings.InstallComplete && GameLauncher.Validate(FolderBox.Text.Trim()) != null) { ShowSetup(); return; }
        if (_installBlocked) { VerifyRepair_Click(sender, e); return; }

        // A button reading UPDATE must update. It used to be able to start the game instead
        // (owner, 2026-10-08: "it says update but is tryna start the game instead of updating"),
        // because the update only ran inside PreLaunchAsync, which bails out in two cases that
        // CheckFolder's label knows nothing about:
        //   - "Auto-update" unticked: PreLaunchAsync returns true on its very first line, so the
        //     launch went ahead with the old files. But that setting means "update before every
        //     launch without asking" - it was never meant to disable an update the player asked
        //     for by clicking the button.
        //   - a previous "skip this time" (_skipUpdateOnce): the label stays UPDATE for the rest
        //     of the session, so every later click silently launched instead.
        // Clicking the button is an explicit request, so it overrides both: the skip is cleared
        // and the update runs here, before PreLaunchAsync is ever reached.
        if (_updateAvailable)
        {
            _skipUpdateOnce = false;
            await RunUpdateIfNeeded();
            CheckFolder();          // UPDATE -> PLAY once it worked, or stays UPDATE if it did not
            return;                 // never fall through into a launch on the same click
        }
        if (_login == null || _authKey == null || !CheckFolder()) return;
        SetPlay("CHECKING…", false);
        PlayStatus.Text = "Checking the install…";

        // Fast verify + auto-update. Never launches with a file that failed its hash.
        if (!await PreLaunchAsync()) { CheckFolder(); return; }

        SetPlay("LAUNCHING…", false);
        PlayStatus.Text = "Checking your account…";
        try
        {
            // Re-check right before launch so a ban or disabled key takes effect immediately.
            var fresh = await _portal.PlayAsync(_authKey) ?? await _portal.LoginAsync(_authKey);
            if (!fresh.Ok)
            {
                PlayStatus.Text = fresh.Error;
                return;
            }
            // Say WHICH server the game is being pointed at. The client reports a wrong address as
            // "G29 - Unable to authenticate with Login Server", which reads as a server outage; with
            // this line it is obvious at a glance that the launcher sent it to 127.0.0.1.
            var server = fresh.LoginServer ?? "127.0.0.1:1115";
            var proc = GameLauncher.Launch(FolderBox.Text.Trim(), fresh, _authKey, _portal.BaseUrl);
            PlayStatus.Text = $"Game started (pid {proc.Id}) · {server}"
                + (_settings.Mode == ServerMode.Local ? " · LOCAL" : "");
            TrackGame(proc);
            WindowState = WindowState.Minimized;
        }
        catch (Exception ex)
        {
            PlayStatus.Text = "Launch failed: " + ex.Message;
        }
        finally
        {
            CheckFolder(); // restores PLAY, or shows PLAYING while the game is tracked
        }
    }

    async void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (_authKey == null) return;
        try
        {
            var r = await _portal.AdminLinkAsync(_authKey);
            if (!r.Ok || r.Url == null) { PlayStatus.Text = r.Error; return; }
            Process.Start(new ProcessStartInfo(_portal.BaseUrl + r.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PlayStatus.Text = "Can't open the staff panel: " + ex.Message;
        }
    }

    void SignOut_Click(object sender, RoutedEventArgs e)
    {
        _login = null;
        _authKey = null;
        _settings.SetKey(null);
        _settings.Save();
        KeyBox.Text = "";
        AvatarImg.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/zrevive.png"));
        AccountPanel.Visibility = Visibility.Collapsed;
        AccountActions.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        CrownsText.Text = "—";
        SetPlay("SIGN IN TO PLAY", false);
        PlayStatus.Text = "";
        UpdatePresence(); // _login is null -> clears
    }

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
