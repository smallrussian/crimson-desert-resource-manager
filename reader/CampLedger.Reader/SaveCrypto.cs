using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CampLedger.Reader;

/// <summary>Decrypts and decompresses a save container. Read-only; callers pass a COPY of the save.</summary>
public static class SaveCrypto
{
    static readonly byte[] BaseKey = Convert.FromHexString("C41B8E730DF259A637CC04E9B12F9668DA107A853E61F9224DB80AD75C13EF90");

    static byte[] KeyFor(int version)
    {
        string prefix = version switch
        {
            1 => "^Qgbrm/.#@`zsr]\\@rvfal#\"",
            2 => "^Pearl--#Abyss__@!!",
            _ => throw new InvalidDataException($"unsupported save version {version}"),
        };
        byte[] material = Encoding.ASCII.GetBytes(prefix + "PRIVATE_HMAC_SECRET_CHECK");
        var key = new byte[32];
        for (int i = 0; i < 31 && i < material.Length; i++) key[i] = (byte)(BaseKey[i] ^ material[i]);
        return key;
    }

    public static byte[] Decode(byte[] file)
    {
        if (file.Length < 0x90 || file[0] != 'S' || file[1] != 'A' || file[2] != 'V' || file[3] != 'E')
            throw new InvalidDataException("not a SAVE container");
        int version = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4));
        int rawSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x12));
        int payloadSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x16));
        if (0x80 + payloadSize > file.Length) throw new InvalidDataException("payload size exceeds file");
        byte[] key = KeyFor(version);
        byte[] comp = file.AsSpan(0x80, payloadSize).ToArray();
        ChaCha20(key, file.AsSpan(0x1A, 16), comp);
        byte[] mac = HMACSHA256.HashData(key, comp);
        if (!CryptographicOperations.FixedTimeEquals(mac, file.AsSpan(0x2A, 32)))
            throw new InvalidDataException("HMAC mismatch: wrong key, or the file is damaged");
        return Lz4.DecompressBlock(comp, rawSize);
    }

    static void ChaCha20(byte[] key, ReadOnlySpan<byte> nonce16, Span<byte> data)
    {
        Span<uint> s = stackalloc uint[16];
        s[0] = 0x61707865; s[1] = 0x3320646e; s[2] = 0x79622d32; s[3] = 0x6b206574;
        for (int i = 0; i < 8; i++) s[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4));
        for (int i = 0; i < 4; i++) s[12 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce16.Slice(i * 4));
        Span<uint> w = stackalloc uint[16];
        Span<byte> block = stackalloc byte[64];
        for (int pos = 0; pos < data.Length; pos += 64)
        {
            s.CopyTo(w);
            for (int r = 0; r < 10; r++)
            {
                Qr(w, 0, 4, 8, 12); Qr(w, 1, 5, 9, 13); Qr(w, 2, 6, 10, 14); Qr(w, 3, 7, 11, 15);
                Qr(w, 0, 5, 10, 15); Qr(w, 1, 6, 11, 12); Qr(w, 2, 7, 8, 13); Qr(w, 3, 4, 9, 14);
            }
            for (int i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(i * 4), w[i] + s[i]);
            int n = Math.Min(64, data.Length - pos);
            for (int i = 0; i < n; i++) data[pos + i] ^= block[i];
            s[12]++;
        }
    }

    static void Qr(Span<uint> s, int a, int b, int c, int d)
    {
        s[a] += s[b]; s[d] = uint.RotateLeft(s[d] ^ s[a], 16);
        s[c] += s[d]; s[b] = uint.RotateLeft(s[b] ^ s[c], 12);
        s[a] += s[b]; s[d] = uint.RotateLeft(s[d] ^ s[a], 8);
        s[c] += s[d]; s[b] = uint.RotateLeft(s[b] ^ s[c], 7);
    }
}

public static class Lz4
{
    public static byte[] DecompressBlock(ReadOnlySpan<byte> src, int rawSize)
    {
        var dst = new byte[rawSize];
        int s = 0, d = 0;
        while (s < src.Length)
        {
            int token = src[s++];
            int lit = token >> 4;
            if (lit == 15) { int b; do { b = src[s++]; lit += b; } while (b == 255); }
            src.Slice(s, lit).CopyTo(dst.AsSpan(d));
            s += lit; d += lit;
            if (s >= src.Length) break;
            int offset = src[s] | (src[s + 1] << 8); s += 2;
            if (offset == 0 || offset > d) throw new InvalidDataException("bad LZ4 offset");
            int len = token & 15;
            if (len == 15) { int b; do { b = src[s++]; len += b; } while (b == 255); }
            len += 4;
            for (int i = 0; i < len; i++) { dst[d] = dst[d - offset]; d++; }
        }
        if (d != rawSize) throw new InvalidDataException($"LZ4 produced {d} bytes, expected {rawSize}");
        return dst;
    }
}
