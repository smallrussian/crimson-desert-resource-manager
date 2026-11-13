using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampLedger.Core;

namespace CampLedger.Reader;

/// <summary>
/// The local dashboard: a small HTTP server on http://localhost:5240 (this PC only) that serves the page and a
/// read-only JSON API over the snapshot log. No accounts, no network access beyond this machine.
///   GET /                 the page (embedded files under dashboard/)
///   GET /api/state?hoursPerDay=24
///   GET /api/plan?food=..&amp;funds=..&amp;stone=..&amp;timber=..&amp;armaments=..
/// </summary>
public sealed class DashboardServer : IDisposable
{
    public const int DefaultPort = 5240;

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    readonly SnapshotLog _log;
    readonly Func<bool> _gameRunning;
    readonly Action<string> _logLine;
    HttpListener? _listener;
    Thread? _thread;

    public int Port { get; private set; }
    public string Url => $"http://localhost:{Port}/";

    public DashboardServer(SnapshotLog log, Func<bool> gameRunning, Action<string> logLine)
    {
        _log = log;
        _gameRunning = gameRunning;
        _logLine = logLine;
    }

    /// <summary>Starts on the port, or returns false if it is taken (usually: another reader already serves it).</summary>
    public bool Start(int port = DefaultPort)
    {
        var l = new HttpListener();
        l.Prefixes.Add($"http://localhost:{port}/");
        try { l.Start(); }
        catch (HttpListenerException e)
        {
            _logLine($"dashboard not started on port {port}: {e.Message}");
            l.Close();
            return false;
        }
        _listener = l;
        Port = port;
        _thread = new Thread(Loop) { IsBackground = true, Name = "dashboard" };
        _thread.Start();
        _logLine($"dashboard at {Url}");
        return true;
    }

    void Loop()
    {
        while (_listener is { IsListening: true } l)
        {
            HttpListenerContext ctx;
            try { ctx = l.GetContext(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            try { Handle(ctx); }
            catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
            catch (Exception e)
            {
                _logLine("dashboard request failed: " + e.Message);
                try { Send(ctx, 500, "text/plain", Encoding.UTF8.GetBytes("internal error")); } catch { }
            }
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        // Only this machine, only by name: refuse other Host headers (DNS rebinding) and anything but GET.
        string host = req.Headers["Host"] ?? "";
        if (host != $"localhost:{Port}" || !req.IsLocal) { Send(ctx, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden")); return; }
        if (req.HttpMethod != "GET") { Send(ctx, 405, "text/plain", Encoding.UTF8.GetBytes("GET only")); return; }

        string path = req.Url?.AbsolutePath ?? "/";
        switch (path)
        {
            case "/api/state":
            {
                double hpd = double.TryParse(req.QueryString["hoursPerDay"], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : Calc.DefaultHoursPerDay;
                var state = _log.Current();
                bool running = _gameRunning();
                string etag = $"\"{state.Version}-{running}-{hpd.ToString(CultureInfo.InvariantCulture)}\"";
                if (req.Headers["If-None-Match"] == etag) { ctx.Response.Headers["ETag"] = etag; Send(ctx, 304, null, Array.Empty<byte>()); return; }
                var view = DashboardView.Build(state, hpd, running);
                ctx.Response.Headers["ETag"] = etag;
                SendJson(ctx, view);
                return;
            }
            case "/api/plan":
            {
                long Q(string k) => long.TryParse(req.QueryString[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : 0;
                var cost = new ReserveSet<long>(Q("food"), Q("funds"), Q("stone"), Q("timber"), Q("armaments"));
                SendJson(ctx, DashboardView.Plan(_log.Current(), cost));
                return;
            }
        }
        string file = path == "/" ? "index.html" : path.TrimStart('/');
        var asset = Asset(file);
        if (asset is null) { Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("not found")); return; }
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        Send(ctx, 200, ContentType(file), asset);
    }

    static readonly Dictionary<string, byte[]> Assets = new();

    /// <summary>An embedded page file (dashboard/...), or null. Names are matched exactly; no file system access.</summary>
    public static byte[]? Asset(string name)
    {
        lock (Assets)
        {
            if (Assets.TryGetValue(name, out var b)) return b;
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("dashboard/" + name);
            if (s is null) return null;
            using var m = new MemoryStream();
            s.CopyTo(m);
            return Assets[name] = m.ToArray();
        }
    }

    static string ContentType(string file) => Path.GetExtension(file) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };

    static void SendJson(HttpListenerContext ctx, object value)
    {
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        Send(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, Json));
    }

    static void Send(HttpListenerContext ctx, int status, string? type, byte[] body)
    {
        var res = ctx.Response;
        res.StatusCode = status;
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:";
        if (type is not null) res.ContentType = type;
        res.ContentLength64 = body.Length;
        if (body.Length > 0) res.OutputStream.Write(body);
        res.Close();
    }

    public void Dispose()
    {
        try { _listener?.Stop(); _listener?.Close(); } catch (ObjectDisposedException) { }
    }
}
