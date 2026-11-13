using System.Security.Cryptography;
using System.Text.Json;

namespace CampLedger.Reader;

/// <summary>A published desktop-app release: the installer asset and the SHA-256 GitHub computed for it.</summary>
public sealed record AppRelease(Version Version, string Tag, string SetupName, Uri SetupUrl, long Size, string Sha256, Uri Page);

/// <summary>
/// Desktop-app updates from GitHub Releases (see docs/release.md). The only request is an unauthenticated GET of
/// the repository's latest published release (no drafts, no prereleases) with a "CampLedger/&lt;version&gt;" user agent;
/// no user or game data is sent. Fails closed: a release without a tag of the form vX.Y.Z, a matching
/// CampLedgerSetup-X.Y.Z.exe asset from this repository and a SHA-256 digest is ignored, and a download whose size
/// or hash differs is deleted before anything runs it.
/// </summary>
public static class AppUpdates
{
    public const string Repo = "smallrussian/crimson-desert-resource-manager";
    public static readonly Uri LatestUrl = new($"https://api.github.com/repos/{Repo}/releases/latest");
    const string DownloadPrefix = $"https://github.com/{Repo}/releases/download/";

    /// <summary>
    /// Test feed for trying an update end to end without publishing (docs/release.md, "Testing an update"): the
    /// environment variable CAMPLEDGER_UPDATE_FEED, accepted only as an http(s) URL on localhost / 127.0.0.1. Its
    /// installer must then be served from the same origin as &lt;origin&gt;/&lt;tag&gt;/&lt;name&gt;. Null when unset or not local.
    /// </summary>
    public static Uri? TestFeed(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")
        && (u.Host == "localhost" || u.Host == "127.0.0.1") ? u : null;

    static string PrefixFor(Uri? testFeed) => testFeed is null ? DownloadPrefix : testFeed.GetLeftPart(UriPartial.Authority) + "/";

    /// <summary>"v1.2.3" → 1.2.3. Anything else (no v, prerelease suffix, two or four parts) → null.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (tag is null || tag.Length < 6 || tag[0] != 'v') return null;
        var parts = tag[1..].Split('.');
        if (parts.Length != 3) return null;
        var n = new int[3];
        for (int i = 0; i < 3; i++)
            if (parts[i].Length == 0 || parts[i].Length > 6 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], out n[i])) return null;
        return new Version(n[0], n[1], n[2]);
    }

    /// <summary>Three-part form of an assembly version (0.1.0.0 → 0.1.0), so it compares with a tag.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public static bool IsNewer(AppRelease release, Version current) => release.Version > Normalize(current);

    public static string SetupNameFor(Version v) => $"CampLedgerSetup-{v.Major}.{v.Minor}.{v.Build}.exe";

    /// <summary>Parses a GitHub "release" JSON object. Returns null with a reason when it is not a usable release.</summary>
    public static AppRelease? Parse(string json, out string? problem, Uri? testFeed = null)
    {
        string prefix = PrefixFor(testFeed);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) { problem = "not a release object"; return null; }
            if (Bool(r, "draft") || Bool(r, "prerelease")) { problem = "draft or prerelease"; return null; }
            string? tag = Str(r, "tag_name");
            var version = ParseTag(tag);
            if (version is null) { problem = $"tag '{tag}' is not vX.Y.Z"; return null; }
            string name = SetupNameFor(version);
            if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) { problem = "no assets"; return null; }
            foreach (var a in assets.EnumerateArray())
            {
                if (Str(a, "name") != name) continue;
                string? url = Str(a, "browser_download_url");
                if (url is null || !url.Equals(prefix + tag + "/" + name, StringComparison.Ordinal))
                { problem = $"{name} is not a download from {Repo}"; return null; }
                string? digest = Str(a, "digest");
                if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 7 + 64
                    || !digest[7..].All(Uri.IsHexDigit))
                { problem = $"{name} has no SHA-256 digest"; return null; }
                long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;
                if (size <= 0) { problem = $"{name} has no size"; return null; }
                string page = Str(r, "html_url") is { } h && h.StartsWith($"https://github.com/{Repo}/releases/", StringComparison.Ordinal)
                    ? h : $"https://github.com/{Repo}/releases/tag/{tag}";
                problem = null;
                return new AppRelease(version, tag!, name, new Uri(url), size, digest[7..].ToLowerInvariant(), new Uri(page));
            }
            problem = $"release {tag} has no {name}";
            return null;
        }
        catch (JsonException e) { problem = "bad release JSON: " + e.Message; return null; }
    }

    public static HttpClient CreateClient(Version current)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var v = Normalize(current);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"CampLedger/{v.Major}.{v.Minor}.{v.Build}");
        return http;
    }

    /// <summary>The latest published release if it is newer than <paramref name="current"/>; null otherwise (problem set on failure).</summary>
    public static async Task<(AppRelease? Newer, string? Problem)> CheckAsync(HttpClient http, Version current, CancellationToken ct, Uri? testFeed = null)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, testFeed ?? LatestUrl);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, null);   // nothing published yet
            if (!resp.IsSuccessStatusCode) return (null, $"GitHub answered {(int)resp.StatusCode}");
            var release = Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), out string? problem, testFeed);
            if (release is null) return (null, problem);
            return (IsNewer(release, current) ? release : null, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return (null, "update check failed: " + e.Message);
        }
    }

    /// <summary>
    /// Downloads the installer into <paramref name="dir"/> and returns its path once size and SHA-256 match the release.
    /// Throws <see cref="InvalidDataException"/> (and deletes the file) when they do not.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, AppRelease release, string dir, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        string final = Path.Combine(dir, release.SetupName), part = final + ".partial";
        try
        {
            using (var resp = await http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                var buf = new byte[81920];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    got += n;
                    if (got > release.Size) throw new InvalidDataException("download is larger than the release says");
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    progress?.Report((double)got / release.Size);
                }
            }
            Verify(part, release);
            File.Move(part, final, overwrite: true);
            return final;
        }
        catch
        {
            try { File.Delete(part); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public static void Verify(string path, AppRelease release)
    {
        long len = new FileInfo(path).Length;
        if (len != release.Size) throw new InvalidDataException($"download is {len} bytes, the release says {release.Size}");
        string hash;
        using (var f = File.OpenRead(path)) hash = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
        if (hash != release.Sha256) throw new InvalidDataException($"download SHA-256 {hash} does not match the release ({release.Sha256})");
    }

    /// <summary>Deletes leftover installers from earlier updates (one that is still running stays until next time).</summary>
    public static void CleanUp(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir))
            try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
