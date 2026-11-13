using CampLedger.Reader;
using Xunit;

public sealed class LauncherFilesTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "cl-launcher-" + Guid.NewGuid().ToString("N"));
    readonly string _src, _game, _bin64, _manifest;
    const string App = @"C:\Apps\Camp Ledger\CampLedger.exe";

    public LauncherFilesTests()
    {
        _src = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        _game = Path.Combine(_root, "Crimson Desert");
        _bin64 = Directory.CreateDirectory(Path.Combine(_game, "bin64")).FullName;
        _manifest = Path.Combine(_root, "data", "launcher.json");
        File.WriteAllText(Path.Combine(_src, LauncherFiles.AsiName), "asi");
        File.WriteAllText(Path.Combine(_src, LauncherFiles.LoaderName), "our loader");
        File.WriteAllText(Path.Combine(_bin64, "CrimsonDesert.exe"), "game");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Bin(string name) => Path.Combine(_bin64, name);

    [Fact]
    public void Bin64OfAcceptsGameFolderOrBin64()
    {
        Assert.Equal(_bin64, LauncherFiles.Bin64Of(_game));
        Assert.Equal(_bin64, LauncherFiles.Bin64Of(_bin64));
        Assert.Null(LauncherFiles.Bin64Of(_root));
        Assert.Null(LauncherFiles.Bin64Of(null));
    }

    [Fact]
    public void InstallThenRemoveLeavesOnlyTheGame()
    {
        var r = LauncherFiles.Install(_src, _bin64, App, _manifest);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(App, File.ReadAllText(Bin(LauncherFiles.IniName)));
        Assert.True(File.Exists(Bin(LauncherFiles.LoaderName)));
        Assert.True(LauncherFiles.IsInstalled(_bin64, App));
        Assert.False(LauncherFiles.IsInstalled(_bin64, @"C:\elsewhere\CampLedger.exe"));

        Assert.True(LauncherFiles.Remove(_src, _manifest, null).Ok);
        Assert.Equal(new[] { "CrimsonDesert.exe" }, Directory.GetFiles(_bin64).Select(Path.GetFileName));
        Assert.False(File.Exists(_manifest));
    }

    [Fact]
    public void ForeignLoaderIsNeverOverwrittenOrDeleted()
    {
        File.WriteAllText(Bin(LauncherFiles.LoaderName), "someone else's winmm");
        var r = LauncherFiles.Install(_src, _bin64, App, _manifest);
        Assert.True(r.Ok);
        Assert.Contains("left in place", r.Message);
        Assert.Equal("someone else's winmm", File.ReadAllText(Bin(LauncherFiles.LoaderName)));

        LauncherFiles.Remove(_src, _manifest, null);
        Assert.Equal("someone else's winmm", File.ReadAllText(Bin(LauncherFiles.LoaderName)));
        Assert.False(File.Exists(Bin(LauncherFiles.AsiName)));
    }

    [Fact]
    public void LoaderChangedAfterInstallIsKept()
    {
        LauncherFiles.Install(_src, _bin64, App, _manifest);
        File.WriteAllText(Bin(LauncherFiles.LoaderName), "updated by the user");
        LauncherFiles.Remove(_src, _manifest, null);
        Assert.Equal("updated by the user", File.ReadAllText(Bin(LauncherFiles.LoaderName)));
    }

    [Fact]
    public void RemoveWithoutManifestCleansByNameAndHash()
    {
        // Files placed by hand before the desktop app existed.
        File.Copy(Path.Combine(_src, LauncherFiles.AsiName), Bin(LauncherFiles.AsiName));
        File.Copy(Path.Combine(_src, LauncherFiles.LoaderName), Bin(LauncherFiles.LoaderName));
        File.WriteAllText(Bin(LauncherFiles.IniName), @"C:\old\CampLedgerReader.exe");
        Assert.True(LauncherFiles.Remove(_src, _manifest, _bin64).Ok);
        Assert.Equal(new[] { "CrimsonDesert.exe" }, Directory.GetFiles(_bin64).Select(Path.GetFileName));
    }

    [Fact]
    public void ReinstallRepointsTheIni()
    {
        LauncherFiles.Install(_src, _bin64, @"C:\old\CampLedgerReader.exe", _manifest);
        LauncherFiles.Install(_src, _bin64, App, _manifest);
        Assert.Equal(App, File.ReadAllText(Bin(LauncherFiles.IniName)));
        LauncherFiles.Remove(_src, _manifest, null);
        Assert.False(File.Exists(Bin(LauncherFiles.LoaderName)));
    }

    [Fact]
    public void RefusesAFolderWithoutTheGame()
    {
        var r = LauncherFiles.Install(_src, _root, App, _manifest);
        Assert.False(r.Ok);
        Assert.Empty(Directory.GetFiles(_root));
    }
}
