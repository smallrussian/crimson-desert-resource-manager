using System.Buffers.Binary;
using System.Text;

namespace CampLedger.Reader;

/// <summary>
/// Per-file encryption used by some archive entries (the string tables among them): ChaCha20 with key and IV
/// derived from the entry's lowercase file name. Method published by lazorr410/crimson-desert-unpacker
/// (PAZ_DECRYPTION.md), reimplemented here. Checked on 2.04.08: see docs/re-notes/localisation.md.
/// </summary>
public static class EntryCrypto
{
    static readonly uint[] Deltas =
        { 0x00000000, 0x0A0A0A0A, 0x0C0C0C0C, 0x06060606, 0x0E0E0E0E, 0x0A0A0A0A, 0x06060606, 0x02020202 };

    public static byte[] Decrypt(ReadOnlySpan<byte> data, string fileName)
    {
        uint seed = HashLittle(Encoding.UTF8.GetBytes(fileName.ToLowerInvariant()), 0xC5EDE);
        var key = new byte[32];
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(i * 4), seed ^ 0x60616263 ^ Deltas[i]);
        var iv = new byte[16];
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(iv.AsSpan(i * 4), seed);
        byte[] plain = data.ToArray();
        SaveCrypto.ChaCha20(key, iv, plain);
        return plain;
    }

    /// <summary>Bob Jenkins' lookup3 hashlittle (the primary 32-bit result).</summary>
    public static uint HashLittle(ReadOnlySpan<byte> k, uint initval)
    {
        uint a, b, c;
        a = b = c = 0xdeadbeef + (uint)k.Length + initval;
        while (k.Length > 12)
        {
            a += U32(k, 0); b += U32(k, 4); c += U32(k, 8);
            a -= c; a ^= uint.RotateLeft(c, 4); c += b;
            b -= a; b ^= uint.RotateLeft(a, 6); a += c;
            c -= b; c ^= uint.RotateLeft(b, 8); b += a;
            a -= c; a ^= uint.RotateLeft(c, 16); c += b;
            b -= a; b ^= uint.RotateLeft(a, 19); a += c;
            c -= b; c ^= uint.RotateLeft(b, 4); b += a;
            k = k.Slice(12);
        }
        if (k.Length == 0) return c;
        Span<byte> tail = stackalloc byte[12];
        tail.Clear();
        k.CopyTo(tail);
        a += U32(tail, 0); b += U32(tail, 4); c += U32(tail, 8);
        c ^= b; c -= uint.RotateLeft(b, 14);
        a ^= c; a -= uint.RotateLeft(c, 11);
        b ^= a; b -= uint.RotateLeft(a, 25);
        c ^= b; c -= uint.RotateLeft(b, 16);
        a ^= c; a -= uint.RotateLeft(c, 4);
        b ^= a; b -= uint.RotateLeft(a, 14);
        c ^= b; c -= uint.RotateLeft(b, 24);
        return c;
    }

    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o));
}

/// <summary>
/// One decoded string table (a .paloc file): numeric key → display text. Layout on 2.04.08, after decryption:
/// "paloc" magic, u32 packed size at 9, u32 raw size at 13, LZ4 block at 0x200. The raw body is a run of
/// (u64 tag, u32 length + key digits, u32 length + UTF-8 text) entries followed by a u32 entry count.
/// Anything that does not add up is rejected, never partly used.
/// </summary>
public sealed class StringTable
{
    public IReadOnlyDictionary<string, string> Entries { get; }

    public StringTable(IReadOnlyDictionary<string, string> entries) => Entries = entries;

    public string? this[string key] => Entries.GetValueOrDefault(key);

    public static StringTable Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < 0x200 || !file.StartsWith("paloc"u8)) throw new InvalidDataException("not a string table");
        int packed = (int)U32(file, 9), raw = (int)U32(file, 13);
        if (packed <= 0 || 0x200 + packed > file.Length || raw <= 4) throw new InvalidDataException("string table sizes do not fit");
        return new StringTable(ParseBody(Lz4.DecompressBlock(file.Slice(0x200, packed), raw)));
    }

    public static Dictionary<string, string> ParseBody(ReadOnlySpan<byte> b)
    {
        int end = b.Length - 4, count = (int)U32(b, end), i = 0;
        var entries = new Dictionary<string, string>(count);
        while (i < end)
        {
            if (i + 12 > end) throw new InvalidDataException("string table entry cut short");
            int kn = (int)U32(b, i + 8);
            if (kn is <= 0 or > 200 || i + 12 + kn + 4 > end) throw new InvalidDataException("bad string table key");
            string key = Encoding.ASCII.GetString(b.Slice(i + 12, kn));
            i += 12 + kn;
            int tn = (int)U32(b, i);
            if (tn < 0 || i + 4 + tn > end) throw new InvalidDataException("bad string table text");
            entries[key] = Encoding.UTF8.GetString(b.Slice(i + 4, tn));
            i += 4 + tn;
        }
        if (entries.Count != count) throw new InvalidDataException($"string table has {entries.Count} entries, header says {count}");
        return entries;
    }

    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o));
}
