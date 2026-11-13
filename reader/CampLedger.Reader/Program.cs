using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CampLedger.Reader;

/// <summary>
/// Camp Ledger reader. Read-only toward the game: it never writes to the game's process, saves or install.
///
///   CampLedgerReader snapshot [save file]   print one snapshot as JSON
///   CampLedgerReader watch [--stay]         wait for the game, emit snapshots while it runs, exit when it closes;
///                                           also serves the dashboard at http://localhost:5240
///   CampLedgerReader dashboard              serve the dashboard from the snapshot log and open it in the browser
///   (started automatically by the launcher ASI in the game folder; see launcher/)
/// </summary>
public static class Program
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    public static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "snapshot";
        switch (cmd)
        {
            case "snapshot": return SnapshotOnce(args.Length > 1 ? args[1] : null);
            case "watch":
            {
                // One watcher at a time: the launcher ASI may start us while one is already running.
                using var single = new Mutex(initiallyOwned: true, ReaderHost.WatchMutexName, out bool first);
                if (!first)
                {
                    Console.Error.WriteLine("another watcher is already running");
                    return 0;
                }
                return Watch(stay: args.Contains("--stay"));
            }
            case "dashboard":
            {
                int at = Array.IndexOf(args, "--log");
                return DashboardOnly(open: !args.Contains("--no-open"), logFile: at > 0 && at + 1 < args.Length ? args[at + 1] : null);
            }
            case "memory":
            {
                // One-shot live read, for checking a memory layout against what the game shows.
                using var game = ReaderHost.FindGame();
                if (game is null) { Console.Error.WriteLine("game not running"); return 1; }
                using var memory = new MemorySource(game, GameVersion.Detect(game));
                if (memory.Problem is not null) { Console.Error.WriteLine("unavailable: " + memory.Problem); return 1; }
                var sw = Stopwatch.StartNew();
                int n = memory.Locate();
                var r = memory.ReadReserves();
                Console.WriteLine($"located {n} of 5 in {sw.Elapsed.TotalSeconds:F1} s");
                Console.WriteLine(JsonSerializer.Serialize(r, Json));
                return r is null ? 1 : 0;
            }
            default:
                Console.Error.WriteLine("usage: CampLedgerReader snapshot [save file] | watch [--stay] | dashboard [--no-open] [--log file] | memory");
                return 2;
        }
    }

    static int SnapshotOnce(string? file)
    {
        using var game = ReaderHost.FindGame();
        var (catalogue, problem) = ReaderHost.LoadCatalogue(game);
        var source = new SaveSource(gameVersion: GameVersion.Detect(game) ?? GameVersion.FromInstall(GameInstall.Find(null)), catalogue: catalogue, catalogueProblem: problem);
        var result = file is null ? source.Read() : source.ReadFile(file);
        if (result is SourceResult.Ok ok)
        {
            Console.WriteLine(JsonSerializer.Serialize(ok.Snapshot, Json));
            return 0;
        }
        Console.Error.WriteLine("unavailable: " + ((SourceResult.Unavailable)result).Reason);
        return 1;
    }

    /// <summary>Serves the dashboard without watching the game (e.g. after the game closed). Opens the browser.</summary>
    static int DashboardOnly(bool open, string? logFile)
    {
        var log = new SnapshotLog(logFile ?? ReaderHost.SnapshotFile);
        using var server = new DashboardServer(log, ReaderHost.GameRunning, ReaderHost.Log);
        bool mine = server.Start();
        string url = $"http://localhost:{DashboardServer.DefaultPort}/";
        if (open) OpenBrowser(url);
        if (!mine)
        {
            Console.WriteLine($"the dashboard is already being served (by the watcher?): {url}");
            return 0;
        }
        Console.WriteLine($"Camp Ledger dashboard: {url}  (close this window to stop)");
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { ReaderHost.Log("could not open the browser: " + e.Message); }
    }

    static int Watch(bool stay)
    {
        var watcher = new Watcher(ReaderHost.SnapshotFile);
        using var dashboard = new DashboardServer(new SnapshotLog(ReaderHost.SnapshotFile), () => watcher.GameRunning, ReaderHost.Log);
        dashboard.Start();
        return watcher.Run(stay);
    }
}

/// <summary>Reads the game's version stamp (meta/0.paver) next to the running executable. Read-only.</summary>
public static class GameVersion
{
    /// <summary>
    /// Versions checked by hand: those with an offsets/&lt;version&gt;/gamedata.json. Adding a version is a data change
    /// plus a re-note, never a code edit here; see docs/patch-update.md.
    /// </summary>
    public static IReadOnlyCollection<string> Supported => Offsets.Versions();

    public static string? Detect(Process? game)
    {
        try
        {
            string? exe = game?.MainModule?.FileName;
            return exe is null ? null : FromInstall(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Version from an install folder (the one holding meta/ and bin64/), or null.</summary>
    public static string? FromInstall(string? install)
    {
        if (install is null) return null;
        try
        {
            string paver = Path.Combine(install, "meta", "0.paver");
            using var fs = new FileStream(paver, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var b = new byte[6];
            fs.ReadExactly(b);
            return $"{b[0] | (b[1] << 8)}.{b[2] | (b[3] << 8):D2}.{b[4] | (b[5] << 8):D2}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
