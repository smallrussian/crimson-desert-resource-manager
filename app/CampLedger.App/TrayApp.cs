using System.Diagnostics;
using CampLedger.Reader;

namespace CampLedger.App;

/// <summary>
/// Owns the reader (watcher thread + dashboard server), the tray icon and the window.
/// Lifetime: the app stays while its window is open or the game is running; closing the window while the
/// game runs sends it to the tray so recording continues.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    readonly Control _ui = new();
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _startWithGame, _update, _autoUpdate;
    readonly Updater _updater = new();
    readonly CancellationTokenSource _stop = new();
    readonly Mutex? _watchLock;
    readonly Watcher? _watcher;
    readonly Thread? _watchThread;
    readonly DashboardServer _server;
    readonly EventWaitHandle _showEvent;
    readonly RegisteredWaitHandle _showWait;
    MainWindow? _window;
    bool _toldAboutTray, _exiting;

    public TrayApp(bool startHidden)
    {
        _ = _ui.Handle;   // force a handle to marshal background events onto the UI thread

        // One recorder at a time: if the console reader is already watching, just show its dashboard.
        _watchLock = new Mutex(initiallyOwned: true, ReaderHost.WatchMutexName, out bool ownsWatch);
        if (ownsWatch)
        {
            _watcher = new Watcher(ReaderHost.SnapshotFile);
            _watcher.GameStarted += () => _ui.BeginInvoke(UpdateStatus);
            _watcher.GameClosed += () => _ui.BeginInvoke(OnGameClosed);
            _watchThread = new Thread(() =>
            {
                try
                {
                    // Values saved while the reader was not running (game played without the launcher).
                    if (_watcher.CatchUpFromSave(new SnapshotLog(ReaderHost.SnapshotFile).Current().LatestSave))
                        _ui.BeginInvoke(UpdateStatus);
                    _watcher.Run(stay: true, _stop.Token);
                }
                catch (Exception e) { ReaderHost.Log("watcher stopped: " + e); }
            }) { IsBackground = true, Name = "watcher" };
            _watchThread.Start();
        }
        else
        {
            _watchLock.Dispose();
            _watchLock = null;
            ReaderHost.Log("another Camp Ledger reader is already recording; showing its dashboard");
        }

        _server = new DashboardServer(new SnapshotLog(ReaderHost.SnapshotFile), GameRunning, ReaderHost.Log);
        _server.Start();   // false if the port is taken, e.g. by the console reader: the window still shows that one

        _startWithGame = new ToolStripMenuItem("Start with the game", null, (_, _) => ToggleStartWithGame());
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open Camp Ledger", null, (_, _) => ShowWindow()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add("Open in browser", null, (_, _) => OpenExternal(DashboardUrl));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startWithGame);
        menu.Items.Add("Open data folder", null, (_, _) => OpenExternal(ReaderHost.DataDir()));
        menu.Items.Add(new ToolStripSeparator());
        _update = new ToolStripMenuItem("", null, (_, _) => ApplyUpdate()) { Visible = false, Font = new Font(menu.Font, FontStyle.Bold) };
        _autoUpdate = new ToolStripMenuItem("Check for updates automatically", null, (_, _) => _updater.AutoCheck = !_updater.AutoCheck);
        menu.Items.Add(_update);
        menu.Items.Add($"Check for updates (version {Updater.CurrentText})", null, async (_, _) => await _updater.CheckAsync(manual: true, _window));
        menu.Items.Add(_autoUpdate);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        menu.Opening += (_, _) =>
        {
            _startWithGame.Checked = Launcher.IsInstalled();
            _autoUpdate.Checked = _updater.AutoCheck;
            _update.Visible = _updater.Available is not null;
            _update.Enabled = !_updater.Busy;
            _update.Text = _updater.Progress ?? $"Update to version {_updater.Available?.Version} and restart";
        };

        _tray = new NotifyIcon { Icon = Program.AppIcon, ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        UpdateStatus();

        // A second launch of CampLedger.exe signals this event to bring the window forward.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEvent);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => _ui.BeginInvoke(ShowWindow), null, -1, executeOnlyOnce: false);

        _updater.Found += r =>
        {
            if (_window is not { Visible: true })
                _tray.ShowBalloonTip(6000, $"Camp Ledger {r.Version} is available",
                    "Open Camp Ledger, or right-click the tray icon, to update. Your data and settings are kept.", ToolTipIcon.Info);
        };
        _updater.Start();

        if (startHidden)
        {
            // Started by the launcher with the game. If no game shows up (e.g. it crashed while loading), leave.
            var t = new System.Windows.Forms.Timer { Interval = 120_000 };
            t.Tick += (_, _) => { t.Dispose(); if (!GameRunning() && _window is not { Visible: true }) Exit(); };
            t.Start();
        }
        else ShowWindow();
    }

    public static string DashboardUrl => $"http://localhost:{DashboardServer.DefaultPort}/";

    bool GameRunning() => _watcher?.GameRunning ?? ReaderHost.GameRunning();

    void UpdateStatus()
    {
        string state = _watcher is null ? "another reader is recording" : GameRunning() ? "recording the game" : "waiting for the game";
        _tray.Text = "Camp Ledger: " + state;
    }

    void OnGameClosed()
    {
        UpdateStatus();
        if (_window is not { Visible: true }) Exit();
    }

    void ShowWindow()
    {
        if (_exiting) return;
        if (_window is null || _window.IsDisposed)
        {
            _window = new MainWindow(DashboardUrl, _updater, ApplyUpdate);
            _window.FormClosing += OnWindowClosing;
        }
        _window.Show();
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    void OnWindowClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting) return;
        if (e.CloseReason != CloseReason.UserClosing)
        {
            // Task Manager, Windows shutdown or another program closing us: quit properly, not into the tray.
            _ui.BeginInvoke(Exit);
            return;
        }
        if (GameRunning() && _watcher is not null)
        {
            // Keep recording in the tray while the game runs.
            e.Cancel = true;
            _window!.Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                _tray.ShowBalloonTip(4000, "Camp Ledger is still recording",
                    "It keeps reading your camp while the game runs and closes after the game does. Open it again from the tray.", ToolTipIcon.Info);
            }
            return;
        }
        e.Cancel = true;
        _ui.BeginInvoke(Exit);
    }

    void ToggleStartWithGame()
    {
        if (Launcher.IsInstalled()) Launcher.Remove(quiet: false, _window);
        else Launcher.Install(null, quiet: false, _window);
    }

    /// <summary>Installs the available update and restarts: in the window if it is open, else in the tray (recording continues).</summary>
    async void ApplyUpdate()
    {
        bool inTray = _window is not { Visible: true };
        await _updater.ApplyAsync(inTray, Exit, _window);
    }

    static void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { ReaderHost.Log("could not open " + target + ": " + e.Message); }
    }

    void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _tray.Visible = false;
        _window?.Close();
        _stop.Cancel();
        _watchThread?.Join(TimeSpan.FromSeconds(5));
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showWait.Unregister(null);
            _showEvent.Dispose();
            _updater.Dispose();
            _server.Dispose();
            _tray.Dispose();
            _window?.Dispose();
            if (_watchLock is not null)
            {
                try { _watchLock.ReleaseMutex(); } catch (ApplicationException) { }
                _watchLock.Dispose();
            }
            _stop.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
