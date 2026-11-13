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

    static byte[] OperationRecord(uint opKey, uint dropKey, uint? durationTenths = null)
    {
        var b = new Bytes();
        // Duration sits 90 bytes before the key, behind a 0x02 byte (docs/re-notes/mission-static-data.md).
        if (durationTenths is uint d) b.Zero(10).U8(2).Zero(4).U32(d).Zero(86);
        return b
            .U32(opKey).Zero(9).Str("123456789012345678")
            .U16(0x4243).U32(2)
            .U32(11).Zero(8).U64(1300).Zero(10)
            .U32(12).Zero(8).U64(140).Zero(10)
            .Zero(12).U32(dropKey).Zero(12)
            .U32(1).U32(5).U32(7).U32(0).Str("Mining")
            .U32(1000052).Zero(4).Str("Node_Her_SouthQuarry_Work_Stone_Tier2")   // skill status key after the category
            .Done();
    }

    static Catalogue CatalogueFixture(bool corruptDrop = false, uint? durationTenths = null, DisplayNames? names = null)
    {
        // The node record starts with its internal name, as in the game's factionnode table.
        var nodes = TableOf((1000070, new Bytes().Str("Node_Her_SouthQuarry").Raw(OperationRecord(42107545, 1015091, durationTenths)).Done()));
        var drops = TableOf((1015091, DropRecord(1015091,
            DropEntry(14, 1400), DropEntry(1000555, 5, corrupt: corruptDrop), DropEntry(1001241, 1, rate: 150_000))));
        var items = TableOf((1000555, new Bytes().Str("Fine_Stone").Done()));
        var characters = TableOf((1000253, new Bytes().Str("NHM_Unique_Marius_468").Done()));
        var skills = TableOf(
            (66010, new Bytes().Str("Skill_Mining_I").Zero(178).U32(1000052).U32(10000).Zero(30).Done()),
            (66011, new Bytes().Str("Skill_Mining_II").Zero(171).U32(1000052).U32(30000).Zero(30).Done()),   // pair not at +178
            (66034, new Bytes().Str("Skill_Building_I").Zero(200).Done()),                                  // gate skill
            (10079, new Bytes().Str("Skill_TurnBash_II").Zero(20).U32(1000001).U32(1000).U32(1000002).U32(2000).Done()));
        return new Catalogue(new() { nodes }, new() { drops }, new() { items }, new() { characters }, names, new() { skills });
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
    public void Operation_duration_is_read_in_tenths_of_an_hour()
    {
        // South Quarry stone: 120 -> 12 h, as shown in game.
        Assert.Equal(12.0, CatalogueFixture(durationTenths: 120).Operation(42107545)!.DurationHours);
    }

    [Fact]
    public void Operation_duration_is_unknown_without_its_marker()
    {
        Assert.Null(CatalogueFixture().Operation(42107545)!.DurationHours);
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
    public void Live_reserve_uses_the_balance_object_only()
    {
        // Observed 2026-11-12: funds balance 51756 beside a pending cost of -20000; armaments balance 3207 beside a
        // pending gain of +24 (a positive pending object, which the sign alone cannot tell apart).
        Assert.Equal(51756L, MemorySource.Resolve(new[] { (-20000L, false), (51756L, true) }));
        Assert.Equal(3207L, MemorySource.Resolve(new[] { (24L, false), (3207L, true) }));
        Assert.Equal(0L, MemorySource.Resolve(new[] { (0L, true), (0L, false) }));
    }

    [Fact]
    public void Live_reserve_is_unknown_without_one_agreeing_balance()
    {
        Assert.Null(MemorySource.Resolve(new[] { (31746L, true), (71756L, true) }));
        Assert.Null(MemorySource.Resolve(new[] { (24L, false), (3207L, false) }));
        Assert.Null(MemorySource.Resolve(Array.Empty<(long, bool)>()));
        Assert.Null(MemorySource.Resolve(new[] { (-5L, true) }));
        Assert.Null(MemorySource.Resolve(new[] { (5_000_000_000_000L, true) }));
    }

    [Fact]
    public void Partial_live_reserves_compare_only_known_values()
    {
        var saved = new Reserves { Food = 11364, Funds = 31746, Stone = 64161, Timber = 3015, Armaments = 3183 };
        Assert.False(new Reserves { Funds = 31746 }.KnownDiffers(saved));
        Assert.True(new Reserves { Funds = 71756 }.KnownDiffers(saved));
        Assert.False(new Reserves().Any);
    }

    static byte[] StringBody(params (string Key, string Text)[] entries)
    {
        var b = new Bytes();
        foreach (var (k, t) in entries) b.U64(0x21).Str(k).U32((uint)Encoding.UTF8.GetByteCount(t)).Raw(Encoding.UTF8.GetBytes(t));
        return b.U32((uint)entries.Length).Done();
    }

    static StringTable Strings(params (string Key, string Text)[] entries) => new(StringTable.ParseBody(StringBody(entries)));

    /// <summary>u16-keyed table, like regioninfo (6-byte header entries).</summary>
    static Table U16Table(ushort key, byte[] record) =>
        new(record, new Bytes().U16(1).U16(key).U32(0).Done(), keySize: 2);

    static DisplayNames NamesFixture() => new(
        new() { Strings(("123456789012345678", "Hasty Mining")) },
        new() { Strings(("176926882791696", "Southern Quarry")) },
        new() { U16Table(0xA0EA, new Bytes().Str("Region_Node_Her_SouthQuarry").U32(7).Zero(6)
                                            .Str("999999").Str("176926882791696").Zero(8).Done()) });

    [Fact]
    public void Jenkins_hash_matches_lookup3_test_vectors()
    {
        // From lookup3.c's driver5: "Four score and seven years ago", 30 bytes.
        var text = "Four score and seven years ago"u8;
        Assert.Equal(0x17770551u, EntryCrypto.HashLittle(text, 0));
        Assert.Equal(0xcd628161u, EntryCrypto.HashLittle(text, 1));
        Assert.Equal(0xdeadbeefu, EntryCrypto.HashLittle(ReadOnlySpan<byte>.Empty, 0));
    }

    [Fact]
    public void Entry_decryption_is_keyed_by_file_name_and_reverses_itself()
    {
        byte[] plain = Encoding.ASCII.GetBytes("paloc and some text that spans more than one sixty-four byte ChaCha block....");
        byte[] once = EntryCrypto.Decrypt(plain, "faction.paloc");
        Assert.NotEqual(plain, once);
        Assert.Equal(plain, EntryCrypto.Decrypt(once, "FACTION.paloc"));
        Assert.NotEqual(plain, EntryCrypto.Decrypt(once, "region.paloc"));
    }

    [Fact]
    public void String_table_container_decodes_entries()
    {
        byte[] body = StringBody(("72842645340692", "Hernand"), ("1311359652963025424", "Advanced Mining"));
        // LZ4 block holding only literals: token 0xF0, then (length - 15) as 255-runs.
        var block = new Bytes().U8(0xF0);
        int extra = body.Length - 15;
        for (; extra >= 255; extra -= 255) block.U8(255);
        block.U8(extra).Raw(body);
        byte[] packed = block.Done();
        byte[] file = new Bytes().Raw("paloc"u8.ToArray()).Zero(4).U32((uint)packed.Length).U32((uint)body.Length)
            .Zero(0x200 - 17).Raw(packed).Done();
        var table = StringTable.Parse(file);
        Assert.Equal("Advanced Mining", table["1311359652963025424"]);
        Assert.Equal("Hernand", table["72842645340692"]);
        Assert.Null(table["1"]);
    }

    [Fact]
    public void String_table_with_wrong_count_is_rejected()
    {
        byte[] body = StringBody(("1", "a"));
        body[^4] = 2;
        Assert.Throws<InvalidDataException>(() => StringTable.ParseBody(body));
        Assert.Throws<InvalidDataException>(() => StringTable.Parse(new byte[0x300]));
    }

    [Fact]
    public void Mission_type_and_location_come_from_the_string_tables()
    {
        var c = CatalogueFixture(names: NamesFixture());
        Assert.Equal("123456789012345678", c.Operation(42107545)!.NameKey);
        Assert.Equal("Hasty Mining", c.OperationDisplayName(42107545));
        Assert.Equal("Southern Quarry", c.NodeLocation(1000070));
        Assert.Null(c.NodeLocation(1));
        Assert.Null(CatalogueFixture().OperationDisplayName(42107545));   // no string tables: unknown
    }

    [Fact]
    public void Region_string_key_must_encode_its_own_record_key()
    {
        byte[] rec = new Bytes().Str("176926882791696").Done();
        Assert.Equal("176926882791696", DisplayNames.RegionStringKey(rec, 0xA0EA));
        Assert.Null(DisplayNames.RegionStringKey(rec, 0xA0EB));
    }

    [Fact]
    public void Camp_skills_give_type_tier_and_percent()
    {
        var c = CatalogueFixture();
        Assert.Equal(new CampSkill(66010, "Mining", 1, 1000052, 10), c.Skill(66010));
        Assert.Equal(30, c.Skill(66011)!.Percent);
        Assert.Equal(new CampSkill(66034, "Building", 1, null, 0), c.Skill(66034));
        Assert.Null(c.Skill(10079));   // two candidate pairs: not understood, left out
        Assert.Equal(1000052u, c.Operation(42107545)!.SkillStatus);
        Assert.Equal("Mining", c.SkillTypeOf(1000052));
    }

    static readonly byte[] Marker = { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0xA0, 0x86, 0x01, 0x00 };

    static byte[] SlotRecord(int gap, params uint[] keys)
    {
        var rec = new byte[1000 + gap * keys.Length + 100];
        for (int s = 0; s < keys.Length; s++)
        {
            int marker = 1000 + gap * s;
            BitConverter.GetBytes(keys[s]).CopyTo(rec, marker - 696);
            Marker.CopyTo(rec, marker);
            Marker.CopyTo(rec, marker + 40);   // later entries of the same run
        }
        return rec;
    }

    [Fact]
    public void Skill_slots_are_read_in_order()
    {
        var camp = new HashSet<uint> { 66004, 66005, 66020 };
        Assert.Equal(new uint[] { 66004, 66020, 66005, 0, 66005 },
                     Catalogue.ParseSkillSlots(SlotRecord(1504, 66004, 66020, 66005, 0, 66005), camp.Contains));
    }

    [Fact]
    public void Skill_slots_that_break_the_layout_are_unknown()
    {
        var camp = new HashSet<uint> { 66004, 66005 };
        Assert.Null(Catalogue.ParseSkillSlots(SlotRecord(1200, 66004, 66005), camp.Contains));     // wrong spacing
        Assert.Null(Catalogue.ParseSkillSlots(SlotRecord(1504, 66004, 12345), camp.Contains));     // not a skill
        Assert.Null(Catalogue.ParseSkillSlots(SlotRecord(1504, 66004), camp.Contains));            // one slot only
    }

    [Fact]
    public void Missions_at_a_node_are_listed()
    {
        var c = CatalogueFixture();
        Assert.Equal(new uint[] { 42107545 }, c.OperationsAt(1000070).Select(o => o.Key));
        Assert.Empty(c.OperationsAt(1));
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
