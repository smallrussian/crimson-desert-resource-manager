using System.Text;
using CampLedger.Reader;
using Xunit;

namespace CampLedger.Reader.Tests;

/// <summary>
/// Small hand-written fixtures in the layouts documented in docs/re-notes/. No game files, saves or game data
/// are used, so the suite runs without the game installed (AGENTS.md).
/// </summary>
public class ReaderTests
{
    sealed class Bytes
    {
        readonly MemoryStream _m = new();
        public Bytes U8(int v) { _m.WriteByte((byte)v); return this; }
        public Bytes U16(int v) { _m.Write(BitConverter.GetBytes((ushort)v)); return this; }
        public Bytes U32(uint v) { _m.Write(BitConverter.GetBytes(v)); return this; }
        public Bytes U64(ulong v) { _m.Write(BitConverter.GetBytes(v)); return this; }
        public Bytes Zero(int n) { _m.Write(new byte[n]); return this; }
        public Bytes Raw(byte[] b) { _m.Write(b); return this; }
        public Bytes Str(string s) { var b = Encoding.ASCII.GetBytes(s); U32((uint)b.Length); _m.Write(b); return this; }
        public int Length => (int)_m.Length;
        public byte[] Done() => _m.ToArray();
    }

    static Table TableOf(params (uint Key, byte[] Record)[] records)
    {
        var body = new Bytes();
        var head = new Bytes().U16(records.Length);
        foreach (var (key, rec) in records)
        {
            head.U32(key).U32((uint)body.Length);
            body.Raw(rec);
        }
        return new Table(body.Done(), head.Done());
    }

    [Fact]
    public void Lz4_copies_literals_and_overlapping_match()
    {
        // token 0x44: 4 literals, match length 4+4 = 8, offset 4  ->  "abcd" + "abcdabcd"
        byte[] block = { 0x44, (byte)'a', (byte)'b', (byte)'c', (byte)'d', 4, 0 };
        Assert.Equal("abcdabcdabcd", Encoding.ASCII.GetString(Lz4.DecompressBlock(block, 12)));
    }

    [Fact]
    public void Lz4_rejects_wrong_size()
    {
        byte[] block = { 0x44, (byte)'a', (byte)'b', (byte)'c', (byte)'d', 4, 0 };
        Assert.Throws<InvalidDataException>(() => Lz4.DecompressBlock(block, 13));
    }

    [Fact]
    public void Save_container_with_bad_magic_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => SaveCrypto.Decode(new byte[0x100]));
    }

    static byte[] SaveBodyFixture(bool trailingGarbage)
    {
        var block = new Bytes()
            .U16(1).U8(0b101).U32(0)          // mask: _amount and _name present, _tags absent
            .U64(31126)                       // _amount
            .U8(1)                            // absence marker for the absent array _tags
            .U32(3).Raw("abc"u8.ToArray());   // _name
        if (trailingGarbage) block.U8(0xEE);
        byte[] blockBytes = block.Done();

        var head = new Bytes().Raw(new byte[] { 0xFF, 0xFF, 4, 0 }).Zero(10)
            .U16(1).U16(0).U16(1)             // tag, zero, one type
            .Str("Thing")
            .U16(3)
            .Str("_amount").Str("uint64").U16(0).U16(8).U32(0)
            .Str("_tags").Str("uint32").U16(3).U16(4).U32(4096)
            .Str("_name").Str("staticstringA").U16(1).U16(1).U32(0);
        int tocEnd = head.Length + 12 + 20;
        head.U32(0).U32(1).U32((uint)blockBytes.Length)
            .U32(0).U32(0).U32(0).U32((uint)tocEnd).U32((uint)blockBytes.Length);
        return head.Raw(blockBytes).Done();
    }

    [Fact]
    public void Save_body_decodes_fields_by_name_and_honours_absence_markers()
    {
        var body = new SaveBody(SaveBodyFixture(trailingGarbage: false));
        var thing = body.Decode("Thing");
        Assert.NotNull(thing);
        Assert.Equal(31126UL, thing!["_amount"]);
        Assert.Equal("abc", thing["_name"]);
        Assert.False(thing.ContainsKey("_tags"));
        Assert.Null(body.Decode("Missing"));
    }

    [Fact]
    public void Save_body_fails_loudly_on_undecoded_bytes()
    {
        var body = new SaveBody(SaveBodyFixture(trailingGarbage: true));
        Assert.Throws<InvalidDataException>(() => body.Decode("Thing"));
    }

    static byte[] DropEntry(uint item, ulong amount, uint rate = 1_000_000, bool corrupt = false)
    {
        var e = new Bytes().U8(1).U32(item).Zero(0x1A - 5).U32(rate).Zero(0x2A - 0x1E)
            .U64(amount).U64(amount).U8(0xFF).U8(0xFF).U32(corrupt ? item + 1 : item);
        return e.Done();
    }

    static byte[] DropRecord(uint key, params byte[][] entries)
    {
        var r = new Bytes().Str("Item(comment)").U32(key).Zero(13).U32((uint)entries.Length);
        foreach (var e in entries) r.Raw(e);
        return r.Done();
    }

    static byte[] OperationRecord(uint opKey, uint dropKey)
    {
        return new Bytes()
            .U32(opKey).Zero(9).Str("123456789012345678")
            .U16(0x4243).U32(2)
            .U32(11).Zero(8).U64(1300).Zero(10)
            .U32(12).Zero(8).U64(140).Zero(10)
            .Zero(12).U32(dropKey).Zero(12)
            .U32(1).U32(5).U32(7).U32(0).Str("Mining")
            .Zero(8).Str("Node_Her_SouthQuarry_Work_Stone_Tier2")
            .Done();
    }

    static Catalogue CatalogueFixture(bool corruptDrop = false)
    {
        var nodes = TableOf((1000070, OperationRecord(42107545, 1015091)));
        var drops = TableOf((1015091, DropRecord(1015091,
            DropEntry(14, 1400), DropEntry(1000555, 5, corrupt: corruptDrop), DropEntry(1001241, 1, rate: 150_000))));
        var items = TableOf((1000555, new Bytes().Str("Fine_Stone").Done()));
        var characters = TableOf((1000253, new Bytes().Str("NHM_Unique_Marius_468").Done()));
        return new Catalogue(new() { nodes }, new() { drops }, new() { items }, new() { characters });
    }

    [Fact]
    public void Operation_record_gives_cost_members_category_and_reward_table()
    {
        var op = CatalogueFixture().Operation(42107545);
        Assert.NotNull(op);
        Assert.Equal("Node_Her_SouthQuarry_Work_Stone_Tier2", op!.InternalName);
        Assert.Equal("Mining", op.Category);
        Assert.Equal("Stone", op.Role);
        Assert.Equal((5, 7), (op.MembersMin, op.MembersMax));
        Assert.Equal(new[] { (11u, 1300L), (12u, 140L) }, op.Cost);
        Assert.Equal(new[] { 1015091u }, op.DropSets);
    }

    [Fact]
    public void Drop_set_gives_amounts_and_chances()
    {
        var items = CatalogueFixture().DropSet(1015091);
        Assert.NotNull(items);
        Assert.Equal(3, items!.Count);
        Assert.Equal(new DropItem(14, 1400, 1400, 1.0), items[0]);
        Assert.Equal(0.15, items[2].Chance, 6);
    }

    [Fact]
    public void Drop_set_that_fails_its_self_check_is_unknown()
    {
        Assert.Null(CatalogueFixture(corruptDrop: true).DropSet(1015091));
    }

    [Fact]
    public void Names_and_regions_resolve()
    {
        var c = CatalogueFixture();
        Assert.Equal("Fine_Stone", c.ItemName(1000555));
        Assert.Equal("NHM_Unique_Marius_468", c.CharacterName(1000253));
        Assert.Null(c.ItemName(1));
        Assert.Equal("Hernand", Catalogue.RegionOf("Node_Her_SouthQuarry_Work_Stone_Tier2"));
        Assert.Null(Catalogue.RegionOf("Node_Zzz_Unknown"));
    }
}
