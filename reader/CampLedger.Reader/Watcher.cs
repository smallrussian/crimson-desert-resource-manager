using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CampLedger.Reader;

/// <summary>Paths, logging and game lookup shared by the console reader and the desktop app.</summary>
public static class ReaderHost
{
    public const string GameProcess = "CrimsonDesert";

    /// <summary>Held by whoever is watching the game (console "watch" or the desktop app), so only one records.</summary>
    public const string WatchMutexName = @"Local\CampLedgerReader.watch";

    public static string DataDir()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CampLedger");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string SnapshotFile => Path.Combine(DataDir(), "snapshots.jsonl");

    /// <summary>Console plus a log file, because the launcher starts the reader without a window.</summary>
    public static void Log(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        try { Console.WriteLine(line); } catch (IOException) { }
        try { File.AppendAllText(Path.Combine(DataDir(), "reader.log"), line + Environment.NewLine); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static Process? FindGame()
    {
        var all = Process.GetProcessesByName(GameProcess);
        foreach (var p in all.Skip(1)) p.Dispose();
        return all.FirstOrDefault();
    }

    public static bool GameRunning()
    {
        using var g = FindGame();
        return g is not null;
    }

    public static (Catalogue?, string?) LoadCatalogue(Process? game)
    {
        string? install = GameInstall.Find(game);
        if (install is null) return (null, "game install folder not found");
        // Not running (e.g. the app catching up from the last save): the install folder has the same version stamp.
        var layout = GameDataLayout.TryLoad(GameVersion.Detect(game) ?? GameVersion.FromInstall(install), out string? layoutProblem);
        if (layout is null) return (null, layoutProblem);
        try
        {
            return (new Catalogue(new Archive(install), layout), null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                      or IndexOutOfRangeException or ArgumentException)
        {
            return (null, e.Message);
        }
    }
}

/// <summary>
/// Waits for the game, appends a snapshot to the log each time a newer save appears or live reserves change,
/// and stops when the game closes (or keeps waiting with <c>stay</c>). Read-only toward the game.
/// </summary>
public sealed class Watcher
{
    static readonly JsonSerializerOptions JsonLine = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    readonly string _outFile;
    volatile bool _gameSeen;

    public Watcher(string outFile) => _outFile = outFile;

    /// <summary>True while the game process is being watched.</summary>
    public bool GameRunning => _gameSeen;

    /// <summary>Raised on the watcher's thread when the game process appears.</summary>
    public event Action? GameStarted;

    /// <summary>Raised on the watcher's thread after the game closes.</summary>
    public event Action? GameClosed;

    /// <summary>
    /// With the game closed: read the newest save once (from a copy, as always) and append it when it says something
    /// the log does not: a save written after the last save snapshot (game played without the reader), or the same
    /// save decoded differently (e.g. a reader update after a game patch now validates rewards the old build could
    /// not). Only the process that holds <see cref="ReaderHost.WatchMutexName"/> may call this.
    /// </summary>
    public bool CatchUpFromSave(Snapshot? lastSave)
    {
        if (ReaderHost.GameRunning()) return false;
        string? install = GameInstall.Find(null);
        string? version = GameVersion.FromInstall(install);
        var probe = new SaveSource(gameVersion: version);
        var newest = probe.NewestSave();
        if (newest is null) return false;
        bool newer = NeedsCatchUp(newest.LastWriteTimeUtc, lastSave);

        var (catalogue, problem) = ReaderHost.LoadCatalogue(null);
        var save = new SaveSource(gameVersion: version, catalogue: catalogue, catalogueProblem: problem);
        switch (save.ReadFile(newest.FullName))
        {
            case SourceResult.Ok ok when newer || !SameContent(lastSave!, ok.Snapshot):
                ReaderHost.Log(newer
                    ? $"caught up from a save written {newest.LastWriteTime:yyyy-MM-dd HH:mm} (game not running)"
                    : "re-read the newest save: this reader decodes it differently from the last snapshot");
                Write(ok.Snapshot);
                return true;
            case SourceResult.Ok:
                return false;
            case SourceResult.Unavailable u:
                ReaderHost.Log("could not catch up from the newest save: " + u.Reason);
                return false;
        }
        return false;
    }

    /// <summary>Same decoded content, ignoring the snapshot's own id and read time.</summary>
    public static bool SameContent(Snapshot a, Snapshot b)
    {
        static string Body(Snapshot s)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(s, JsonLine))!.AsObject();
            node.Remove("id");
            node.Remove("capturedAt");
            return node.ToJsonString();
        }
        return Body(a) == Body(b);
    }

    /// <summary>True when a save written at <paramref name="saveWrittenUtc"/> is newer than what the log holds.</summary>
    public static bool NeedsCatchUp(DateTime saveWrittenUtc, Snapshot? lastSave) =>
        lastSave is null || saveWrittenUtc > lastSave.CapturedAt.UtcDateTime;

    public int Run(bool stay, CancellationToken ct = default)
    {
        ReaderHost.Log($"snapshots go to {_outFile}");
        while (!ct.IsCancellationRequested)
        {
            Process? game = ReaderHost.FindGame();
            if (game is null)
            {
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                continue;
            }
            _gameSeen = true;
            GameStarted?.Invoke();
            try { WatchGame(game, ct); }
            finally
            {
                _gameSeen = false;
                game.Dispose();
            }
            if (ct.IsCancellationRequested) break;
            ReaderHost.Log("game closed");
            GameClosed?.Invoke();
            if (!stay) return 0;
        }
        return 0;
    }

    void WatchGame(Process game, CancellationToken ct)
    {
        string? version = GameVersion.Detect(game);
        ReaderHost.Log($"game found (pid {game.Id}, version {version ?? "unknown"})");
        if (version is null || !GameVersion.Supported.Contains(version))
            ReaderHost.Log("this game version has not been checked; snapshots are still read from the save, by field name");

        var (catalogue, problem) = ReaderHost.LoadCatalogue(game);
        ReaderHost.Log(catalogue is null ? "game data not loaded: " + problem : $"game data loaded: {catalogue.OperationCount} missions");
        var save = new SaveSource(gameVersion: version, catalogue: catalogue, catalogueProblem: problem);
        using var memory = new MemorySource(game, version);
        if (memory.Problem is not null) ReaderHost.Log("live memory unavailable: " + memory.Problem);

        DateTime lastSaveTime = DateTime.MinValue, nextLocate = DateTime.MinValue;
        Snapshot? lastSave = null;
        Reserves? lastWritten = null;
        while (!game.HasExited && !ct.IsCancellationRequested)
        {
            var newest = save.NewestSave();
            if (newest is not null && newest.LastWriteTimeUtc > lastSaveTime)
            {
                // Give the game a moment to finish writing before copying the file.
                if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(2))) return;
                newest.Refresh();
                lastSaveTime = newest.LastWriteTimeUtc;
                switch (save.ReadFile(newest.FullName))
                {
                    case SourceResult.Ok ok when lastSave is null || Changed(lastSave, ok.Snapshot):
                        lastSave = ok.Snapshot;
                        lastWritten = ok.Snapshot.Reserves;
                        Write(ok.Snapshot);
                        break;
                    case SourceResult.Unavailable u:
                        // Usually the game still has the file open. Forget the timestamp so the
                        // next pass tries again instead of waiting for a newer save.
                        ReaderHost.Log("save unreadable, will retry: " + u.Reason);
                        lastSaveTime = DateTime.MinValue;
                        break;
                }
            }

            if (memory.Problem is null)
            {
                Reserves? live = memory.ReadReserves();
                // Nothing located: rescan after a minute. Partly located: rescan every 5 minutes, since a scan
                // costs ~35 s of one CPU core.
                if ((live is null || !live.Complete) && DateTime.UtcNow >= nextLocate)
                {
                    var sw = Stopwatch.StartNew();
                    int n = memory.Locate();
                    live = memory.ReadReserves();
                    ReaderHost.Log($"memory scan: located {n} of 5 reserves in {sw.Elapsed.TotalSeconds:F1} s; " +
                                   $"readable now: {Readable(live)}");
                    nextLocate = DateTime.UtcNow + TimeSpan.FromSeconds(live is null ? 60 : 300);
                }
                if (live is not null && live.Any && (lastWritten is null || live.KnownDiffers(lastWritten)))
                {
                    lastWritten = live;
                    var unknowns = new List<string> { "reserves are live from process memory" };
                    if (!live.Complete) unknowns.Add("some reserves could not be read from memory this time (null)");
                    if (lastSave is null) unknowns.Add("no save read yet: missions and clock unknown");
                    else unknowns.Add($"missions, workers and clock are from the save read at {lastSave.CapturedAt:u}");
                    Write(new Snapshot
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
            if (WaitHandle.WaitAny(new[] { ct.WaitHandle }, 5000) == 0) return;
        }
    }

    void Write(Snapshot snapshot)
    {
        File.AppendAllText(_outFile, JsonSerializer.Serialize(snapshot, JsonLine) + Environment.NewLine);
        var r = snapshot.Reserves;
        ReaderHost.Log($"snapshot ({snapshot.Source}): food {r.Food}, funds {r.Funds}, stone {r.Stone}, timber {r.Timber}, " +
                       $"armaments {r.Armaments}, {snapshot.Missions.Count} missions");
    }

    static string Readable(Reserves? r) =>
        r is null ? "none"
        : string.Join(", ", new[] { ("food", r.Food), ("funds", r.Funds), ("stone", r.Stone), ("timber", r.Timber),
                                    ("armaments", r.Armaments) }.Where(x => x.Item2.HasValue).Select(x => x.Item1))
          is { Length: > 0 } s ? s : "none (ambiguous)";

    static bool Changed(Snapshot a, Snapshot b) =>
        !a.Reserves.SameAs(b.Reserves) || a.InGameClock != b.InGameClock || a.Missions.Count != b.Missions.Count;
}
