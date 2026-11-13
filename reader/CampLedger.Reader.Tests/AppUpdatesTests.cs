using System.Net;
using System.Security.Cryptography;
using CampLedger.Reader;
using Xunit;

public sealed class AppUpdatesTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cl-updates-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    static readonly byte[] Payload = "pretend installer"u8.ToArray();
    static string PayloadSha => Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

    static string Release(string tag = "v0.2.0", string? asset = null, string? url = null, string? digest = null,
                          long size = 17, bool draft = false, bool prerelease = false)
    {
        string ver = tag.TrimStart('v');
        asset ??= $"CampLedgerSetup-{ver}.exe";
        url ??= $"https://github.com/{AppUpdates.Repo}/releases/download/{tag}/{asset}";
        digest ??= "sha256:" + PayloadSha;
        return $$"""
        {
          "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/{{AppUpdates.Repo}}/releases/tag/{{tag}}",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://example.com/notes.txt", "size": 3 },
            { "name": "{{asset}}", "browser_download_url": "{{url}}", "size": {{size}}, "digest": "{{digest}}" }
          ]
        }
        """;
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("v0.10.0", "0.10.0")]
    [InlineData("1.2.3", null)]
    [InlineData("v1.2", null)]
    [InlineData("v1.2.3.4", null)]
    [InlineData("v1.2.3-beta", null)]
    [InlineData("v1..3", null)]
    [InlineData(null, null)]
    public void ParseTagAcceptsOnlyVxyz(string? tag, string? expected) =>
        Assert.Equal(expected is null ? null : Version.Parse(expected), AppUpdates.ParseTag(tag));

    [Fact]
    public void ParsesAReleaseAndPicksTheMatchingInstaller()
    {
        var r = AppUpdates.Parse(Release(), out string? problem);
        Assert.NotNull(r);
        Assert.Null(problem);
        Assert.Equal(new Version(0, 2, 0), r!.Version);
        Assert.Equal("CampLedgerSetup-0.2.0.exe", r.SetupName);
        Assert.Equal(PayloadSha, r.Sha256);
        Assert.Equal(17, r.Size);
        Assert.True(AppUpdates.IsNewer(r, new Version(0, 1, 0, 0)));
        Assert.False(AppUpdates.IsNewer(r, new Version(0, 2, 0, 0)));
        Assert.False(AppUpdates.IsNewer(r, new Version(0, 3, 0)));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("badtag")]
    [InlineData("otherasset")]
    [InlineData("otherrepo")]
    [InlineData("nodigest")]
    [InlineData("md5")]
    [InlineData("nosize")]
    public void RejectsAnythingNotClearlyOurs(string what)
    {
        string json = what switch
        {
            "draft" => Release(draft: true),
            "prerelease" => Release(prerelease: true),
            "badtag" => Release(tag: "latest"),
            "otherasset" => Release(asset: "CampLedgerSetup-0.1.9.exe"),
            "otherrepo" => Release(url: "https://github.com/someone/else/releases/download/v0.2.0/CampLedgerSetup-0.2.0.exe"),
            "nodigest" => Release(digest: ""),
            "md5" => Release(digest: "md5:0123456789abcdef0123456789abcdef"),
            "nosize" => Release(size: 0),
            _ => throw new ArgumentException(what),
        };
        Assert.Null(AppUpdates.Parse(json, out string? problem));
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("http://localhost:8765/latest", true)]
    [InlineData("http://127.0.0.1:8765/latest", true)]
    [InlineData("https://example.com/latest", false)]
    [InlineData("http://localhost.example.com/latest", false)]
    [InlineData("file:///C:/latest.json", false)]
    [InlineData(null, false)]
    public void TestFeedOnlyOnLocalhost(string? value, bool accepted) => Assert.Equal(accepted, AppUpdates.TestFeed(value) is not null);

    [Fact]
    public void TestFeedReleasesMustDownloadFromTheSameOrigin()
    {
        var feed = AppUpdates.TestFeed("http://localhost:8765/latest");
        Assert.NotNull(AppUpdates.Parse(Release(url: "http://localhost:8765/v0.2.0/CampLedgerSetup-0.2.0.exe"), out _, feed));
        Assert.Null(AppUpdates.Parse(Release(url: "http://localhost:9999/v0.2.0/CampLedgerSetup-0.2.0.exe"), out _, feed));
        Assert.Null(AppUpdates.Parse(Release(url: "http://localhost:8765/v0.2.0/CampLedgerSetup-0.2.0.exe"), out _));   // not without the feed
    }

    [Fact]
    public void BadJsonIsAProblemNotACrash()
    {
        Assert.Null(AppUpdates.Parse("{not json", out string? problem));
        Assert.StartsWith("bad release JSON", problem);
    }

    sealed class Serve(byte[] body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    [Fact]
    public async Task DownloadKeepsAFileWhoseHashMatches()
    {
        var r = AppUpdates.Parse(Release(), out _)!;
        using var http = new HttpClient(new Serve(Payload));
        string path = await AppUpdates.DownloadAsync(http, r, _dir, null, CancellationToken.None);
        Assert.Equal(Path.Combine(_dir, "CampLedgerSetup-0.2.0.exe"), path);
        Assert.Equal(Payload, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task DownloadWithTheWrongHashIsDeleted()
    {
        var r = AppUpdates.Parse(Release(), out _)!;
        using var http = new HttpClient(new Serve("tampered installer"u8.ToArray()[..17]));
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdates.DownloadAsync(http, r, _dir, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task DownloadLargerThanTheReleaseSaysIsDeleted()
    {
        var r = AppUpdates.Parse(Release(), out _)!;
        using var http = new HttpClient(new Serve([.. Payload, .. Payload]));
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdates.DownloadAsync(http, r, _dir, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir));
    }
}
