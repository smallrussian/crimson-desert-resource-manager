using CampLedger.Reader;

namespace CampLedger.App;

/// <summary>"Start with the game": UI around <see cref="LauncherFiles"/>. Used by the installer and the tray menu.</summary>
static class Launcher
{
    static string SourceDir => Path.Combine(AppContext.BaseDirectory, "launcher");
    static string ManifestPath => Path.Combine(ReaderHost.DataDir(), "launcher.json");
    static string AppExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CampLedger.exe");

    public static string? FindBin64() => LauncherFiles.Bin64Of(GameInstall.Find(null));

    public static bool IsInstalled() => FindBin64() is { } bin64 && LauncherFiles.IsInstalled(bin64, AppExe);

    public static bool Install(string? gameDir, bool quiet, IWin32Window? owner)
    {
        if (GameRunningRefused(owner)) return false;
        string? bin64 = LauncherFiles.Bin64Of(gameDir) ?? FindBin64();
        if (bin64 is null)
        {
            using var pick = new FolderBrowserDialog
            {
                Description = "Camp Ledger could not find Crimson Desert. Choose the game's folder (the one that contains bin64).",
                UseDescriptionForTitle = true,
            };
            if (pick.ShowDialog(owner) != DialogResult.OK) return false;
            bin64 = LauncherFiles.Bin64Of(pick.SelectedPath);
            if (bin64 is null)
            {
                Show(owner, "That folder does not contain bin64\\CrimsonDesert.exe. Nothing was changed.", error: true);
                return false;
            }
        }
        var r = LauncherFiles.Install(SourceDir, bin64, AppExe, ManifestPath);
        ReaderHost.Log("install launcher: " + r.Message);
        if (!r.Ok || !quiet) Show(owner, r.Ok ? r.Message : "Could not set Camp Ledger to start with the game.\n\n" + r.Message, error: !r.Ok);
        return r.Ok;
    }

    public static bool Remove(bool quiet, IWin32Window? owner)
    {
        if (GameRunningRefused(owner)) return false;
        var r = LauncherFiles.Remove(SourceDir, ManifestPath, FindBin64());
        ReaderHost.Log("remove launcher: " + r.Message);
        if (!r.Ok || !quiet) Show(owner, r.Ok ? "Camp Ledger will no longer start with the game." : r.Message, error: !r.Ok);
        return r.Ok;
    }

    /// <summary>The game holds the loaded ASI open; never change its folder while it runs.</summary>
    static bool GameRunningRefused(IWin32Window? owner)
    {
        if (!ReaderHost.GameRunning()) return false;
        Show(owner, "Close Crimson Desert first, then try again. Nothing was changed.", error: true);
        return true;
    }

    static void Show(IWin32Window? owner, string text, bool error) =>
        MessageBox.Show(owner, text, "Camp Ledger", MessageBoxButtons.OK, error ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
}
