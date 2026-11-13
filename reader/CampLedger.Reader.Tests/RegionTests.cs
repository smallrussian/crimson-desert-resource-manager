using System.Text;
using CampLedger.Reader;
using Xunit;

/// <summary>Mission regions from the regioninfo hierarchy (docs/re-notes/mission-regions.md). Hand-built records only.</summary>
public sealed class RegionTests
{
    const int After = 13;   // parent key position after the name, as on 2.05.00

    static byte[] Record(string name, ushort parent)
    {
        var m = new MemoryStream();
        var n = Encoding.ASCII.GetBytes(name);
        m.Write(BitConverter.GetBytes((uint)n.Length));
        m.Write(n);
        m.Write(new byte[After]);
        m.Write(BitConverter.GetBytes(parent));
        m.Write(new byte[10]);
        return m.ToArray();
    }

    /// <summary>u16-keyed table like regioninfo: u16 count, then (u16 key, u32 offset) per record.</summary>
    static Table U16Table(params (ushort Key, byte[] Record)[] records)
    {
        var body = new MemoryStream();
        var head = new MemoryStream();
        head.Write(BitConverter.GetBytes((ushort)records.Length));
        foreach (var (key, rec) in records)
        {
            head.Write(BitConverter.GetBytes(key));
            head.Write(BitConverter.GetBytes((uint)body.Length));
            body.Write(rec);
        }
        return new Table(body.ToArray(), head.ToArray(), keySize: 2);
    }

    // The real chain for Calphade Logging Camp (keys as on 2.05.00), plus Beighen's.
    static readonly Table Regions = U16Table(
        (1, Record("Region_Pywel", 1)),
        (4, Record("Region_Akapen", 1)),
        (2, Record("Region_Kweiden", 1)),
        (13, Record("Region_Hernandian_Territory", 4)),
        (12, Record("Region_Pailunese_Territory", 2)),
        (10033, Record("Region_Calphade", 13)),
        (14030, Record("Region_Beighen", 12)),
        (41236, Record("Region_Node_Cal_CalphadeLoggingCamp", 10033)),
        (43040, Record("Region_Node_Kwe_BeighenVillage", 14030)),
        (500, Record("Region_Node_Broken", 9999)));   // parent missing from the table

    static DisplayNames Names(int? after) => new(new(), new(), new() { Regions }, null, after);

    [Fact]
    public void CalphadeIsInHernandianTerritory() =>
        Assert.Equal("Region_Hernandian_Territory", Names(After).Territory("Node_Cal_CalphadeLoggingCamp"));

    [Fact]
    public void BeighenIsInPailuneseTerritory() =>
        Assert.Equal("Region_Pailunese_Territory", Names(After).Territory("Node_Kwe_BeighenVillage"));

    [Fact]
    public void MissingNodeOrBrokenChainIsUnknown()
    {
        Assert.Null(Names(After).Territory("Node_Nowhere"));
        Assert.Null(Names(After).Territory("Node_Broken"));
    }

    [Fact]
    public void NoLayoutMeansNoHierarchy() =>
        Assert.Null(Names(null).Territory("Node_Cal_CalphadeLoggingCamp"));

    [Fact]
    public void LayoutFileCarriesTheParentOffsetFor20500()
    {
        var layout = GameDataLayout.Load(Offsets.FileFor("2.05.00", "gamedata.json"));
        Assert.Equal(After, layout.RegionParentAfterName);
        Assert.Null(GameDataLayout.Load(Offsets.FileFor("2.04.08", "gamedata.json")).RegionParentAfterName);
    }
}
