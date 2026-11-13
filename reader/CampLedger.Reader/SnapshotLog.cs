using System.Text;
using System.Text.Json;

namespace CampLedger.Reader;

/// <summary>
/// Follows snapshots.jsonl (one JSON snapshot per line, written by the watcher). Reads only the bytes added since
/// the last call, so polling stays cheap as the file grows. A file that shrank is read again from the start.
/// </summary>
public sealed class SnapshotLog
{
    public sealed class State
    {
        public Snapshot? LatestSave { get; set; }
        public Snapshot? LatestProcess { get; set; }
        public List<DashboardView.HistoryPoint> History { get; } = new();
        /// <summary>Changes whenever a new snapshot line has been read.</summary>
        public long Version { get; set; }
    }

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly string _path;
    readonly object _gate = new();
    long _offset;
    readonly MemoryStream _partial = new();
    State _state = new();

    public SnapshotLog(string path) { _path = path; }

    public string Path => _path;

    public State Current()
    {
        lock (_gate)
        {
            try { ReadNew(); }
            catch (IOException) { /* the watcher is writing; next poll catches up */ }
            catch (UnauthorizedAccessException) { }
            return _state;
        }
    }

    void ReadNew()
    {
        if (!File.Exists(_path)) return;
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _offset)
        {
            _offset = 0;
            _partial.SetLength(0);
            _state = new State();
        }
        if (fs.Length == _offset) return;
        fs.Seek(_offset, SeekOrigin.Begin);
        var buf = new byte[fs.Length - _offset];
        fs.ReadExactly(buf);
        _offset = fs.Length;
        int start = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            if (buf[i] != (byte)'\n') continue;
            _partial.Write(buf, start, i - start);
            Line(_partial.ToArray());
            _partial.SetLength(0);
            start = i + 1;
        }
        _partial.Write(buf, start, buf.Length - start);   // incomplete last line: kept for the next read
    }

    void Line(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).Trim();
        if (text.Length == 0) return;
        Snapshot? s;
        try { s = JsonSerializer.Deserialize<Snapshot>(text, Json); }
        catch (JsonException) { return; }   // a damaged line is skipped, never guessed
        if (s is null) return;
        Add(_state, s);
    }

    /// <summary>Adds one snapshot to a state. Public for tests.</summary>
    public static void Add(State state, Snapshot s)
    {
        if (s.Source == "process") state.LatestProcess = s;
        else state.LatestSave = s;
        var r = s.Reserves;
        state.History.Add(new DashboardView.HistoryPoint(s.CapturedAt, s.Source, r.Food, r.Funds, r.Stone, r.Timber, r.Armaments));
        state.Version++;
    }
}
