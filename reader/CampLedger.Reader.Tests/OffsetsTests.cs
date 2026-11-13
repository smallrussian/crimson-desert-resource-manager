using System.Text.Json;
using CampLedger.Reader;
using Xunit;

namespace CampLedger.Reader.Tests;

/// <summary>
/// Checks the per-version layout files in offsets/ (data written by hand after each patch; docs/patch-update.md).
/// Reads only the repo's own JSON, never the game.
/// </summary>
public class OffsetsTests
{
    static string OffsetsDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "offsets")) && File.Exists(Path.Combine(d.FullName, "CLAUDE.md")))
                return Path.Combine(d.FullName, "offsets");
        throw new DirectoryNotFoundException("repo offsets/ not found above " + AppContext.BaseDirectory);
    }

    public static IEnumerable<object[]> VersionDirs() =>
        Directory.GetDirectories(OffsetsDir()).Select(d => new object[] { Path.GetFileName(d) });

    [Theory]
    [MemberData(nameof(VersionDirs))]
    public void Every_version_folder_is_complete_and_names_its_own_version(string version)
    {
        string dir = Path.Combine(OffsetsDir(), version);
        Assert.Matches(@"^\d+\.\d{2}\.\d{2}$", version);
        foreach (var name in new[] { "gamedata.json", "memory.json" })
        {
            string file = Path.Combine(dir, name);
            Assert.True(File.Exists(file), $"{version}/{name} missing");
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            Assert.Equal(version, doc.RootElement.GetProperty("gameVersion").GetString());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("checked").GetString()));
            string evidence = doc.RootElement.GetProperty("evidence").GetString()!;
            Assert.Contains("docs/re-notes/", evidence);
        }
        var layout = GameDataLayout.Load(Path.Combine(dir, "gamedata.json"));
        Assert.Equal(version, layout.GameVersion);
        Assert.True(layout.DropSet.TrailerItemOffset + 4 <= layout.DropSet.EntrySize);
        Assert.True(layout.SkillSlots.MinGap < layout.SkillSlots.MaxGap);
    }

    [Theory]
    [MemberData(nameof(VersionDirs))]
    public void Evidence_note_exists(string version)
    {
        string repo = Directory.GetParent(OffsetsDir())!.FullName;
        foreach (var name in new[] { "gamedata.json", "memory.json" })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(OffsetsDir(), version, name)));
            foreach (var part in doc.RootElement.GetProperty("evidence").GetString()!.Split(new[] { ',', ' ', '(' }))
                if (part.StartsWith("docs/re-notes/") && part.EndsWith(".md"))
                    Assert.True(File.Exists(Path.Combine(repo, part)), $"{version}/{name} cites missing {part}");
        }
    }

    [Fact]
    public void Layouts_used_in_reader_tests_match_the_files()
    {
        Assert.Equal(new DropSetLayout(0x11, 0x15, 0x40, 0x1, 0x1A, 0x2A, 0x32, 0x3A, 0x3C),
                     GameDataLayout.Load(Path.Combine(OffsetsDir(), "2.04.08", "gamedata.json")).DropSet);
        var v205 = GameDataLayout.Load(Path.Combine(OffsetsDir(), "2.05.00", "gamedata.json"));
        Assert.Equal(new DropSetLayout(0x12, 0x16, 0x41, 0x1, 0x1A, 0x2A, 0x32, 0x3A, 0x3D), v205.DropSet);
        Assert.Equal(new SkillSlotLayout(768, 1480, 1680), v205.SkillSlots);
    }
}
