using System.Diagnostics;
using System.Text.Json;
using CampLedger.Reader;

namespace CampLedger.App;

/// <summary>
/// Checks GitHub Releases for a newer Camp Ledger and installs it in place: downloads the installer, checks its SHA-256
/// against the release, runs it silently over the current install (same folder, settings and snapshots kept) and lets it
/// start the new version. See <see cref="AppUpdates"/> and docs/release.md. All members run on the UI thread.
/// </summary>
sealed class Updater : IDisposable
{
    static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20), Interval = TimeSpan.FromHours(6);
    static string SettingsFile => Path.Combine(ReaderHost.DataDir(), "updates.json");
    static string DownloadDir => Path.Combine(ReaderHost.DataDir(), "updates");

    public static Version Current { get; } = AppUpdates.Normalize(typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0));
    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    readonly HttpClient _http = AppUpdates.CreateClient(Current);
    readonly Uri? _testFeed = AppUpdates.TestFeed(Environment.GetEnvironmentVariable("CAMPLEDGER_UPDATE_FEED"));
    readonly System.Windows.Forms.Timer _timer = new();
    readonly CancellationTokenSource _stop = new();
    bool _checking;

    /// <summary>A newer release, once a check has found one.</summary>
    public AppRelease? Available { get; private set; }
    /// <summary>Set while downloading or starting the installer ("Downloading update… 40%").</summary>
    public string? Progress { get; private set; }
    /// <summary>The window's banner was closed for this version.</summary>
    public bool Dismissed { get; set; }
    public bool Busy => Progress is not null;
    public event Action? Changed;
    /// <summary>A check (not a manual one) found a release; the tray shows a notice once.</summary>
    public event Action<AppRelease>? Found;

    public bool AutoCheck
    {
        get => _settings.AutoCheck;
        set { _settings = _settings with { AutoCheck = value }; SaveSettings(); if (value && Available is null) _ = CheckAsync(manual: false, owner: null); }
    }

    sealed record Settings(bool AutoCheck = true);
    Settings _settings = LoadSettings();

    public void Start()
    {
        if (_testFeed is not null) ReaderHost.Log("updates: using test feed " + _testFeed);
        AppUpdates.CleanUp(DownloadDir);
        _timer.Interval = (int)FirstCheck.TotalMilliseconds;
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = (int)Interval.TotalMilliseconds;
            if (AutoCheck && Available is null) await CheckAsync(manual: false, owner: null);
        };
        _timer.Start();
    }

    public async Task CheckAsync(bool manual, IWin32Window? owner)
    {
        if (_checking || Busy) return;
        _checking = true;
        try
        {
            var (newer, problem) = await AppUpdates.CheckAsync(_http, Current, _stop.Token, _testFeed);
            if (problem is not null) ReaderHost.Log("updates: " + problem);
            if (newer is not null && newer.Version != Available?.Version)
            {
                ReaderHost.Log($"updates: Camp Ledger {newer.Version} is available (running {CurrentText})");
                Available = newer;
                Dismissed = false;
                Changed?.Invoke();
                if (!manual) Found?.Invoke(newer);
            }
            if (manual && newer is null)
                Show(owner, problem is null
                    ? $"Camp Ledger {CurrentText} is the latest version."
                    : "Could not check for updates. Try again later.\n\n" + problem, warn: problem is not null);
        }
        catch (OperationCanceledException) { }
        finally { _checking = false; }
    }

    /// <summary>
    /// Downloads and verifies the installer, then hands over to it: it waits for this process to exit, replaces the app
    /// files and starts the new version (in the tray when <paramref name="relaunchInTray"/>). <paramref name="exit"/>
    /// shuts this app down; it is called only once the installer has started.
    /// </summary>
    public async Task ApplyAsync(bool relaunchInTray, Action exit, IWin32Window? owner)
    {
        if (Available is not { } release || Busy) return;
        try
        {
            SetProgress("Downloading update…");
            var progress = new Progress<double>(p => SetProgress($"Downloading update… {p:P0}"));
            string setup = await AppUpdates.DownloadAsync(_http, release, DownloadDir, progress, _stop.Token);
            SetProgress("Installing update…");
            AppUpdates.Verify(setup, release);   // again, right before running it

            string log = Path.Combine(ReaderHost.DataDir(), "update-setup.log");
            string args = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /LOG=\"{log}\" " +
                          $"/WAITPID={Environment.ProcessId} /RELAUNCH={(relaunchInTray ? "watch" : "window")}";
            ReaderHost.Log($"updates: installing {release.Version} from {setup} ({args})");
            Program.ReleaseInstance();   // so the installer's "app is running" check passes; it waits for our exit instead
            using (Process.Start(new ProcessStartInfo(setup, args) { UseShellExecute = false, WorkingDirectory = DownloadDir })) { }
            exit();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                      or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            if (_stop.IsCancellationRequested) return;
            ReaderHost.Log("updates: install failed: " + e);
            SetProgress(null);
            Show(owner, "The update could not be installed. Camp Ledger keeps running the current version.\n\n" + e.Message, warn: true);
        }
    }

    void SetProgress(string? text)
    {
        Progress = text;
        Changed?.Invoke();
    }

    static void Show(IWin32Window? owner, string text, bool warn) =>
        MessageBox.Show(owner, text, "Camp Ledger", MessageBoxButtons.OK, warn ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

    static Settings LoadSettings()
    {
        try { return File.Exists(SettingsFile) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    void SaveSettings()
    {
        try { File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_settings)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ReaderHost.Log("updates: could not save settings: " + e.Message); }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _timer.Dispose();
        _http.Dispose();
        _stop.Dispose();
    }
}
