using System.Text.Json;

namespace CampLedger.Reader;

/// <summary>
/// Per-version layout files under offsets/&lt;game-version&gt;/ (copied next to the executable at build time).
/// A version is supported only when its folder has the files the reader needs; see docs/patch-update.md.
/// </summary>
public static class Offsets
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "offsets");

    public static string FileFor(string version, string name) => Path.Combine(Root, version, name);

    /// <summary>Versions that have a game-data layout (the minimum for a checked version).</summary>
    public static IReadOnlyCollection<string> Versions() =>
        Directory.Exists(Root)
            ? Directory.GetDirectories(Root).Where(d => File.Exists(Path.Combine(d, "gamedata.json")))
                       .Select(Path.GetFileName).OfType<string>().OrderBy(v => v).ToList()
            : Array.Empty<string>();
}

/// <summary>Layout of one drop-set record (dropsetinfo), from the start of the record after its comment.</summary>
public sealed record DropSetLayout(int CountOffset, int EntriesOffset, int EntrySize, int ItemOffset, int RateOffset,
                                   int MinOffset, int MaxOffset, int MarkOffset, int TrailerItemOffset);

/// <summary>Layout of comrade skill slots in characterinfo: the marker run, and where the skill key sits before it.</summary>
public sealed record SkillSlotLayout(int KeyBeforeMarker, int MinGap, int MaxGap);

/// <summary>
/// Game-data record layouts that moved between patches, loaded from offsets/&lt;version&gt;/gamedata.json.
/// Everything else in <see cref="Catalogue"/> has held across 2.04.08 and 2.05.00 (see docs/re-notes/patch-2.05.00.md).
/// </summary>
public sealed record GameDataLayout(string GameVersion, DropSetLayout DropSet, SkillSlotLayout SkillSlots)
{
    public static GameDataLayout Load(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        var d = root.GetProperty("dropSet");
        var k = root.GetProperty("skillSlots");
        int H(JsonElement e, string name) => Convert.ToInt32(e.GetProperty(name).GetString()!, 16);
        int I(JsonElement e, string name) => e.GetProperty(name).GetInt32();
        return new GameDataLayout(
            root.GetProperty("gameVersion").GetString()!,
            new DropSetLayout(H(d, "countOffset"), H(d, "entriesOffset"), H(d, "entrySize"), H(d, "itemOffset"),
                              H(d, "rateOffset"), H(d, "minOffset"), H(d, "maxOffset"), H(d, "markOffset"),
                              H(d, "trailerItemOffset")),
            new SkillSlotLayout(I(k, "keyBeforeMarker"), I(k, "minGap"), I(k, "maxGap")));
    }

    /// <summary>The layout for a game version, or null with a reason (unknown version: fail closed).</summary>
    public static GameDataLayout? TryLoad(string? version, out string? problem)
    {
        problem = null;
        if (version is null) { problem = "game version unknown"; return null; }
        string file = Offsets.FileFor(version, "gamedata.json");
        if (!File.Exists(file)) { problem = $"no game-data layout for game version {version} (see docs/patch-update.md)"; return null; }
        try { return Load(file); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or IOException)
        {
            problem = $"could not load {file}: {e.Message}";
            return null;
        }
    }
}
