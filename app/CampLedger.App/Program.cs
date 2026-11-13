using CampLedger.Reader;

namespace CampLedger.App;

/// <summary>
/// Camp Ledger desktop app: the dashboard in its own window, with the reader running in the same process.
///
///   CampLedger.exe                       open the window (or bring the running one forward)
///   CampLedger.exe watch                 start in the tray (what the launcher ASI runs when the game loads);
///                                        exits after the game closes unless the window was opened
///   CampLedger.exe --install-launcher [--game dir] [--quiet]   make it start with the game
///   CampLedger.exe --remove-launcher [--quiet]                 undo that
///
/// Read-only toward the game. The only files it ever places in the game folder are the launcher-only ASI,
/// its ini and the ASI loader (AGENTS.md hard rule 1 exception), and only when asked.
/// </summary>
static class Program
{
    const string InstanceMutex = "CampLedger.App";
    internal const string ShowEvent = "CampLedger.App.show";

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool quiet = args.Contains("--quiet");

        if (args.Contains("--install-launcher"))
        {
            int at = Array.IndexOf(args, "--game");
            return Launcher.Install(at >= 0 && at + 1 < args.Length ? args[at + 1] : null, quiet, owner: null) ? 0 : 1;
        }
        if (args.Contains("--remove-launcher"))
            return Launcher.Remove(quiet, owner: null) ? 0 : 1;

        bool background = args.Contains("watch") || args.Contains("--background");
        using var instance = new Mutex(initiallyOwned: true, InstanceMutex, out bool first);
        if (!first)
        {
            // Already running: bring it forward, unless this is the launcher starting us again.
            if (!background)
                try { using var e = EventWaitHandle.OpenExisting(ShowEvent); e.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReaderHost.Log("app error: " + e.Exception);
        using var app = new TrayApp(startHidden: background);
        Application.Run(app);
        return 0;
    }

    public static Icon AppIcon { get; } = LoadIcon();

    static Icon LoadIcon()
    {
        using var s = typeof(Program).Assembly.GetManifestResourceStream("app.ico");
        return s is null ? SystemIcons.Application : new Icon(s);
    }
}
