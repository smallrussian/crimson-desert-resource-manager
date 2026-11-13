using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CampLedger.Reader;

/// <summary>
/// Places or removes the launcher-only ASI, its ini and the ASI loader in the game's bin64 folder
/// (AGENTS.md hard rule 1, launcher-only exception of 2026-11-11). Nothing else in the game folder is touched.
///
/// Safety rules:
///  - An existing winmm.dll that is not the loader we ship is never overwritten or deleted (it may be the
///    user's own ASI loader or another mod).
///  - Removal deletes only files recorded in our manifest, and winmm.dll only while its hash still matches.
/// </summary>
public static class LauncherFiles
{
    public const string AsiName = "CampLedgerLauncher.asi";
    public const string IniName = "CampLedgerLauncher.ini";
    public const string LoaderName = "winmm.dll";

    public sealed record Result(bool Ok, string Message);

    sealed class Manifest
    {
        public string Bin64 { get; set; } = "";
        public Dictionary<string, string> Files { get; set; } = new();
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>bin64 under a game folder, or null when it does not hold CrimsonDesert.exe.</summary>
    public static string? Bin64Of(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return null;
        // Accept either the game folder or bin64 itself.
        foreach (var dir in new[] { Path.Combine(gameDir, "bin64"), gameDir })
            if (File.Exists(Path.Combine(dir, ReaderHost.GameProcess + ".exe"))) return Path.GetFullPath(dir);
        return null;
    }

    /// <summary>True when our ASI is in bin64 and its ini points at <paramref name="appExe"/>.</summary>
    public static bool IsInstalled(string bin64, string appExe)
    {
        try
        {
            string ini = Path.Combine(bin64, IniName);
            if (!File.Exists(Path.Combine(bin64, AsiName)) || !File.Exists(ini)) return false;
            return string.Equals(File.ReadAllText(ini).Trim(), appExe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static Result Install(string sourceDir, string bin64, string appExe, string manifestPath)
    {
        string srcAsi = Path.Combine(sourceDir, AsiName), srcLoader = Path.Combine(sourceDir, LoaderName);
        if (!File.Exists(srcAsi) || !File.Exists(srcLoader))
            return new Result(false, $"launcher files missing from {sourceDir}");
        if (!File.Exists(Path.Combine(bin64, ReaderHost.GameProcess + ".exe")))
            return new Result(false, $"{bin64} does not contain {ReaderHost.GameProcess}.exe");

        var manifest = ReadManifest(manifestPath) is { } m && PathEq(m.Bin64, bin64) ? m : new Manifest();
        manifest.Bin64 = bin64;
        var notes = new List<string>();
        try
        {
            File.Copy(srcAsi, Path.Combine(bin64, AsiName), overwrite: true);
            manifest.Files[AsiName] = Sha256(Path.Combine(bin64, AsiName));

            string loader = Path.Combine(bin64, LoaderName);
            string ours = Sha256(srcLoader);
            if (!File.Exists(loader))
            {
                File.Copy(srcLoader, loader);
                manifest.Files[LoaderName] = ours;
            }
            else if (Sha256(loader) == ours)
                manifest.Files[LoaderName] = ours;   // same loader we ship (e.g. placed by hand earlier)
            else if (!manifest.Files.ContainsKey(LoaderName))
                notes.Add($"an existing {LoaderName} was left in place; the launcher only runs if it is an ASI loader");

            string ini = Path.Combine(bin64, IniName);
            File.WriteAllText(ini, appExe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            manifest.Files[IniName] = Sha256(ini);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            WriteManifest(manifestPath, manifest);
            return new Result(false, "could not write to the game folder: " + e.Message);
        }
        WriteManifest(manifestPath, manifest);
        return new Result(true, string.Join(" ", new[] { $"Camp Ledger will start with the game ({bin64})." }.Concat(notes)));
    }

    /// <summary>Removes what <see cref="Install"/> placed. Without a manifest, cleans <paramref name="fallbackBin64"/> by name and hash.</summary>
    public static Result Remove(string sourceDir, string manifestPath, string? fallbackBin64)
    {
        var manifest = ReadManifest(manifestPath);
        string? bin64 = manifest?.Bin64 is { Length: > 0 } b ? b : fallbackBin64;
        if (bin64 is null || !Directory.Exists(bin64))
        {
            TryDelete(manifestPath);
            return new Result(true, "nothing to remove");
        }

        string srcLoader = Path.Combine(sourceDir, LoaderName);
        var loaderHashes = new HashSet<string>();
        if (manifest?.Files.TryGetValue(LoaderName, out var h) == true) loaderHashes.Add(h);
        if (File.Exists(srcLoader)) loaderHashes.Add(Sha256(srcLoader));

        int removed = 0;
        try
        {
            // Our own names: removed whatever their content.
            foreach (var name in new[] { AsiName, IniName })
            {
                string f = Path.Combine(bin64, name);
                if (File.Exists(f)) { File.Delete(f); removed++; }
            }
            // The loader only if we placed it (manifest) or no manifest exists, and it is still byte-identical.
            string loader = Path.Combine(bin64, LoaderName);
            bool ourLoader = manifest is null || manifest.Files.ContainsKey(LoaderName);
            if (ourLoader && File.Exists(loader) && loaderHashes.Contains(Sha256(loader)))
            {
                File.Delete(loader);
                removed++;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Result(false, "could not remove the launcher files: " + e.Message);
        }
        TryDelete(manifestPath);
        return new Result(true, removed == 0 ? "nothing to remove" : $"removed {removed} launcher file(s) from {bin64}");
    }

    static Manifest? ReadManifest(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    static void WriteManifest(string path, Manifest m)
    {
        if (m.Files.Count == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(m, Json));
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static bool PathEq(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static string Sha256(string file)
    {
        using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }
}
