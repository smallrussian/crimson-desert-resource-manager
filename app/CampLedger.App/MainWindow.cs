using System.Diagnostics;
using System.Text.Json;
using CampLedger.Reader;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CampLedger.App;

/// <summary>The dashboard page (served by the in-process reader on localhost) in a native window via WebView2.</summary>
sealed class MainWindow : Form
{
    static readonly Color Background = Color.FromArgb(0x0d, 0x0b, 0x0a);   // --bg in dashboard/app.css

    readonly string _url;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
    readonly Panel _problem = new() { Dock = DockStyle.Fill, Visible = false, BackColor = Background, Padding = new Padding(32) };
    readonly Label _problemText = new() { Dock = DockStyle.Top, AutoSize = false, Height = 120, ForeColor = Color.FromArgb(0xec, 0xe2, 0xcf), Font = new Font("Segoe UI", 11f) };
    readonly FlowLayoutPanel _problemButtons = new() { Dock = DockStyle.Top, AutoSize = true };

    // Update banner (above the dashboard) while a newer release is available.
    static readonly Color BannerBack = Color.FromArgb(0x2a, 0x21, 0x14), BannerText = Color.FromArgb(0xe8, 0xc9, 0x7a);
    readonly Updater _updater;
    readonly Action _applyUpdate;
    readonly Panel _banner = new() { Dock = DockStyle.Top, Height = 44, Visible = false, BackColor = BannerBack, Padding = new Padding(16, 0, 8, 0) };
    readonly Label _bannerText = new() { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, ForeColor = BannerText, Font = new Font("Segoe UI", 10f) };
    readonly FlowLayoutPanel _bannerButtons = new() { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
    readonly Button _updateNow, _whatsNew, _later;

    static string BoundsFile => Path.Combine(ReaderHost.DataDir(), "window.json");

    public MainWindow(string url, Updater updater, Action applyUpdate)
    {
        _url = url;
        _updater = updater;
        _applyUpdate = applyUpdate;
        Text = "Camp Ledger";
        Icon = Program.AppIcon;
        BackColor = Background;
        MinimumSize = new Size(420, 480);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 860);
        RestoreBounds_();

        _problem.Controls.Add(_problemButtons);
        _problem.Controls.Add(_problemText);
        _updateNow = BannerButton("Update and restart", () => _applyUpdate());
        _whatsNew = BannerButton("What's new", () => { if (_updater.Available is { } r) Open(r.Page.ToString()); });
        _later = BannerButton("Later", () => { _updater.Dismissed = true; RefreshBanner(); });
        _bannerButtons.Controls.AddRange([_updateNow, _whatsNew, _later]);
        _banner.Controls.Add(_bannerText);
        _banner.Controls.Add(_bannerButtons);

        Controls.Add(_web);
        Controls.Add(_problem);
        Controls.Add(_banner);
        _updater.Changed += RefreshBanner;
        RefreshBanner();

        Load += async (_, _) => await InitWebView();
        FormClosing += (_, _) => SaveBounds();
    }

    async Task InitWebView()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(ReaderHost.DataDir(), "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ReaderHost.Log("WebView2 unavailable: " + e.Message);
            ShowProblem("Camp Ledger needs the Microsoft Edge WebView2 Runtime to show its window. It is part of Windows 11 " +
                        "and most Windows 10 PCs. Install it, or open the dashboard in your browser instead.",
                        ("Open in browser", () => Open(_url)),
                        ("Get WebView2", () => Open("https://developer.microsoft.com/microsoft-edge/webview2/")));
            return;
        }

        var core = _web.CoreWebView2;
        var s = core.Settings;
        s.IsStatusBarEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
#if !DEBUG
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
#endif
        // Only the local dashboard opens inside the window; any other link goes to the default browser.
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith(_url, StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http")) Open(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http")) Open(e.Uri);
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) { _problem.Visible = false; _web.Visible = true; return; }
            ShowProblem($"The dashboard did not load ({e.WebErrorStatus}). Another program may be using port {DashboardServer.DefaultPort}.",
                        ("Try again", () => { _problem.Visible = false; _web.Visible = true; core.Navigate(_url); }),
                        ("Open log folder", () => Open(ReaderHost.DataDir())));
        };
        core.Navigate(_url);
    }

    static Button BannerButton(string text, Action click)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 28, Margin = new Padding(6, 0, 0, 0), FlatStyle = FlatStyle.Flat,
                             ForeColor = BannerText, BackColor = BannerBack, Font = new Font("Segoe UI", 9.5f) };
        b.FlatAppearance.BorderColor = Color.FromArgb(0x8a, 0x6d, 0x3b);
        b.Click += (_, _) => click();
        return b;
    }

    void RefreshBanner()
    {
        if (IsDisposed) return;
        var r = _updater.Available;
        _banner.Visible = r is not null && (!_updater.Dismissed || _updater.Busy);
        if (r is null) return;
        _bannerText.Text = _updater.Progress ?? $"Camp Ledger {r.Version} is available (you have {Updater.CurrentText}). Updating keeps your data and settings.";
        _updateNow.Enabled = _later.Enabled = !_updater.Busy;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _updater.Changed -= RefreshBanner;
        base.Dispose(disposing);
    }

    void ShowProblem(string text, params (string Label, Action Click)[] buttons)
    {
        _problemText.Text = text;
        _problemButtons.Controls.Clear();
        foreach (var (label, click) in buttons)
        {
            var b = new Button { Text = label, AutoSize = true, Padding = new Padding(8, 4, 8, 4), ForeColor = Color.Black, BackColor = SystemColors.Control };
            b.Click += (_, _) => click();
            _problemButtons.Controls.Add(b);
        }
        _web.Visible = false;
        _problem.Visible = true;
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { ReaderHost.Log("could not open " + target + ": " + e.Message); }
    }

    sealed record SavedBounds(int X, int Y, int W, int H, bool Max);

    void RestoreBounds_()
    {
        try
        {
            if (!File.Exists(BoundsFile)) return;
            var b = JsonSerializer.Deserialize<SavedBounds>(File.ReadAllText(BoundsFile));
            if (b is null || b.W < MinimumSize.Width || b.H < MinimumSize.Height) return;
            var r = new Rectangle(b.X, b.Y, b.W, b.H);
            // Only restore onto a screen that still exists.
            if (!Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r))) return;
            StartPosition = FormStartPosition.Manual;
            Bounds = r;
            if (b.Max) WindowState = FormWindowState.Maximized;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }

    void SaveBounds()
    {
        try
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            File.WriteAllText(BoundsFile, JsonSerializer.Serialize(new SavedBounds(r.X, r.Y, r.Width, r.Height, WindowState == FormWindowState.Maximized)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
