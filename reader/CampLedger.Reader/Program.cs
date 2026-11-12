using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CampLedger.Reader;

/// <summary>
/// Camp Ledger reader. Read-only toward the game: it never writes to the game's process, saves or install.
///
///   CampLedgerReader snapshot [save file]   print one snapshot as JSON
///   CampLedgerReader watch [--stay]         wait for the game, emit snapshots while it runs, exit when it closes
///   (started automatically by the launcher ASI in the game folder; see launcher/)
/// </summary>
public static class Program
{
    const string GameProcess = "CrimsonDesert";

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };
    static readonly JsonSerializerOptions JsonLine = new(Json) { WriteIndented = false };

    public static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "snapshot";
        switch (cmd)
        {
            case "snapshot": return SnapshotOnce(args.Length > 1 ? args[1] : null);
            case "watch":
            {
                // One watcher at a time: the launcher ASI may start us while one is already running.
                using var single = new Mutex(initiallyOwned: true, @"Local\CampLedgerReader.watch", out bool first);
                if (!first)
                {
                    Console.Error.WriteLine("another watcher is already running");
                    return 0;
                }
                return Watch(stay: args.Contains("--stay"));
            }
            default:
                Console.Error.WriteLine("usage: CampLedgerReader snapshot [save file] | watch [--stay]");
                return 2;
        }
    }

    static int SnapshotOnce(string? file)
    {
        using var game = FindGame();
        var (catalogue, problem) = LoadCatalogue(game);
        var source = new SaveSource(gameVersion: GameVersion.Detect(game), catalogue: catalogue, catalogueProblem: problem);
        var result = file is null ? source.Read() : source.ReadFile(file);
        if (result is SourceResult.Ok ok)
        {
            Console.WriteLine(JsonSerializer.Serialize(ok.Snapshot, Json));
            return 0;
        }
        Console.Error.WriteLine("unavailable: " + ((SourceResult.Unavailable)result).Reason);
        return 1;
    }

    static int Watch(bool stay)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CampLedger");
        Directory.CreateDirectory(dir);
        string outFile = Path.Combine(dir, "snapshots.jsonl");
        Log($"snapshots go to {outFile}");

        while (true)
        {
            Process? game = FindGame();
            if (game is null)
            {
                Thread.Sleep(TimeSpan.FromSeconds(5));
                continue;
            }

            string? version = GameVersion.Detect(game);
            Log($"game found (pid {game.Id}, version {version ?? "unknown"})");
            if (version is null || !GameVersion.Supported.Contains(version))
                Log("this game version has not been checked; snapshots are still read from the save, by field name");

            var (catalogue, problem) = LoadCatalogue(game);
            Log(catalogue is null ? "game data not loaded: " + problem : $"game data loaded: {catalogue.OperationCount} missions");
            var save = new SaveSource(gameVersion: version, catalogue: catalogue, catalogueProblem: problem);
            using var memory = new MemorySource(game, version);
            if (memory.Problem is not null) Log("live memory unavailable: " + memory.Problem);

            DateTime lastSaveTime = DateTime.MinValue, nextLocate = DateTime.MinValue;
            Snapshot? lastSave = null;
            Reserves? lastWritten = null;
            while (!game.HasExited)
            {
                var newest = save.NewestSave();
                if (newest is not null && newest.LastWriteTimeUtc > lastSaveTime)
                {
                    // Give the game a moment to finish writing before copying the file.
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                    newest.Refresh();
                    lastSaveTime = newest.LastWriteTimeUtc;
                    switch (save.ReadFile(newest.FullName))
                    {
                        case SourceResult.Ok ok when lastSave is null || Changed(lastSave, ok.Snapshot):
                            lastSave = ok.Snapshot;
                            lastWritten = ok.Snapshot.Reserves;
                            Write(outFile, ok.Snapshot);
                            break;
                        case SourceResult.Unavailable u:
                            // Usually the game still has the file open. Forget the timestamp so the
                            // next pass tries again instead of waiting for a newer save.
                            Log("save unreadable, will retry: " + u.Reason);
                            lastSaveTime = DateTime.MinValue;
                            break;
                    }
                }

                if (memory.Problem is null)
                {
                    Reserves? live = memory.ReadReserves();
                    if (live is null && DateTime.UtcNow >= nextLocate)
                    {
                        var sw = Stopwatch.StartNew();
                        int n = memory.Locate();
                        Log($"memory scan: located {n} of 5 reserves in {sw.Elapsed.TotalSeconds:F1} s");
                        nextLocate = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                        live = memory.ReadReserves();
                    }
                    if (live is not null && live.Complete && (lastWritten is null || !live.SameAs(lastWritten)))
                    {
                        lastWritten = live;
                        var unknowns = new List<string> { "reserves are live from process memory" };
                        if (lastSave is null) unknowns.Add("no save read yet: missions and clock unknown");
                        else unknowns.Add($"missions, workers and clock are from the save read at {lastSave.CapturedAt:u}");
                        Write(outFile, new Snapshot
                        {
                            GameVersion = version,
                            Source = "process",
                            Reserves = live,
                            Workers = lastSave?.Workers ?? new Workers(),
                            Missions = lastSave?.Missions ?? new List<Mission>(),
                            InGameClock = lastSave?.InGameClock,
                            Unknowns = unknowns,
                        });
                    }
                }
                game.WaitForExit(5000);
            }

            Log("game closed");
            game.Dispose();
            if (!stay) return 0;
        }
    }

    static void Write(string outFile, Snapshot snapshot)
    {
        File.AppendAllText(outFile, JsonSerializer.Serialize(snapshot, JsonLine) + Environment.NewLine);
        var r = snapshot.Reserves;
        Log($"snapshot ({snapshot.Source}): food {r.Food}, funds {r.Funds}, stone {r.Stone}, timber {r.Timber}, " +
            $"armaments {r.Armaments}, {snapshot.Missions.Count} missions");
    }

    static (Catalogue?, string?) LoadCatalogue(Process? game)
    {
        string? install = GameInstall.Find(game);
        if (install is null) return (null, "game install folder not found");
        try
        {
            return (new Catalogue(new Archive(install)), null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                      or IndexOutOfRangeException or ArgumentException)
        {
            return (null, e.Message);
        }
    }

    static bool Changed(Snapshot a, Snapshot b) =>
        !a.Reserves.SameAs(b.Reserves) || a.InGameClock != b.InGameClock || a.Missions.Count != b.Missions.Count;

    static Process? FindGame()
    {
        var all = Process.GetProcessesByName(GameProcess);
        foreach (var p in all.Skip(1)) p.Dispose();
        return all.FirstOrDefault();
    }

    /// <summary>Console plus a log file, because the launcher starts the reader without a window.</summary>
    static void Log(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        Console.WriteLine(line);
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CampLedger");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "reader.log"), line + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Reads the game's version stamp (meta/0.paver) next to the running executable. Read-only.</summary>
public static class GameVersion
{
    /// <summary>Versions whose save fields were checked by hand. See docs/re-notes/.</summary>
    public static readonly HashSet<string> Supported = new() { "2.04.08" };

    public static string? Detect(Process? game)
    {
        try
        {
            string? exe = game?.MainModule?.FileName;
            if (exe is null) return null;
            string paver = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!, "meta", "0.paver");
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
