using System.Buffers.Binary;
using System.Text;

namespace CampLedger.Reader;

/// <summary>
/// Strict decoder for the decompressed save body. The body carries its own schema (type and field names),
/// so callers look fields up by name. Any byte that does not fit raises, so a format change fails loudly.
/// See docs/re-notes/save-contents.md.
/// </summary>
public sealed class SaveBody
{
    public sealed record Field(string Name, string TypeName, int Kind, int Size);
    public sealed record TypeDef(string Name, Field[] Fields);
    public sealed record Block(int Index, int TypeIndex, string TypeName, int Offset, int Size);

    readonly byte[] _raw;
    public TypeDef[] Types { get; }
    public Block[] Blocks { get; }

    public SaveBody(byte[] raw)
    {
        _raw = raw;
        int pos = 0x0E + 4;
        int typeCount = U16(pos); pos += 2;
        string name = Str(ref pos);
        var types = new TypeDef[typeCount];
        for (int t = 0; t < typeCount; t++)
        {
            int fieldCount = U16(pos); pos += 2;
            var fields = new Field[fieldCount];
            for (int f = 0; f < fieldCount; f++)
            {
                string fn = Str(ref pos), tn = Str(ref pos);
                fields[f] = new Field(fn, tn, U16(pos), U16(pos + 2));
                pos += 8;
            }
            types[t] = new TypeDef(name, fields);
            if (t != typeCount - 1) name = Str(ref pos);
        }
        Types = types;
        int count = (int)U32(pos + 4);
        var blocks = new Block[count];
        for (int i = 0; i < count; i++)
        {
            int e = pos + 12 + i * 20;
            int ti = (int)U32(e);
            if (ti >= typeCount) throw new InvalidDataException($"TOC entry {i} has unknown type {ti}");
            blocks[i] = new Block(i, ti, types[ti].Name, (int)U32(e + 12), (int)U32(e + 16));
        }
        Blocks = blocks;
    }

    /// <summary>Decode the first top-level block of the given type, or null if the save has none.</summary>
    public Dictionary<string, object?>? Decode(string typeName)
    {
        foreach (var b in Blocks)
        {
            if (b.TypeName != typeName) continue;
            int maskCount = U16(b.Offset);
            var mask = _raw.AsSpan(b.Offset + 2, maskCount).ToArray();
            int c = b.Offset + 2 + maskCount + 4;
            var obj = Fields(b.TypeIndex, mask, ref c, b.Offset + b.Size);
            if (c != b.Offset + b.Size)
                throw new InvalidDataException($"{typeName}: {b.Offset + b.Size - c} bytes left undecoded");
            return obj;
        }
        return null;
    }

    Dictionary<string, object?> Fields(int typeIndex, byte[] mask, ref int c, int end)
    {
        var type = Types[typeIndex];
        var obj = new Dictionary<string, object?>();
        for (int i = 0; i < type.Fields.Length; i++)
        {
            var f = type.Fields[i];
            bool present = i / 8 < mask.Length && ((mask[i / 8] >> (i % 8)) & 1) != 0;
            if (!present)
            {
                if (f.Kind is 3 or 6 or 7)
                {
                    if (_raw[c] != 1) throw Bad(type, f, c, "expected absence marker");
                    c++;
                }
                continue;
            }
            switch (f.Kind)
            {
                case 0 or 2:
                    obj[f.Name] = Scalar(f, c); c += f.Size; break;
                case 1:
                {
                    int n = (int)U32(c); c += 4;
                    obj[f.Name] = f.Size == 1
                        ? Encoding.UTF8.GetString(_raw, c, n).TrimEnd('\0')
                        : Convert.ToHexString(_raw, c, n * f.Size);
                    c += n * f.Size; break;
                }
                case 3:
                {
                    if (_raw[c] != 0) throw Bad(type, f, c, "array tag");
                    int n = (int)U32(c + 1); c += 5;
                    if (n < 0 || n > 1_000_000) throw Bad(type, f, c, "array count");
                    var list = new List<object?>(n);
                    for (int k = 0; k < n; k++) { list.Add(Scalar(f, c)); c += f.Size; }
                    obj[f.Name] = list; break;
                }
                case 4 or 5:
                    if (f.Kind == 5)
                    {
                        if (_raw[c] != 1) throw Bad(type, f, c, "pointer prefix");
                        c++;
                    }
                    obj[f.Name] = Locator(ref c, end, type, f); break;
                case 6 or 7:
                {
                    if (_raw[c] != 0) throw Bad(type, f, c, "list tag");
                    int n = (int)U32(c + 1); c += 18;
                    if (n < 0 || n > 1_000_000) throw Bad(type, f, c, "list count");
                    var list = new List<object?>(n);
                    for (int k = 0; k < n; k++) list.Add(Locator(ref c, end, type, f));
                    obj[f.Name] = list; break;
                }
                default: throw Bad(type, f, c, $"unknown kind {f.Kind}");
            }
            if (c > end) throw Bad(type, f, c, "overran its block");
        }
        return obj;
    }

    Dictionary<string, object?> Locator(ref int c, int end, TypeDef owner, Field f)
    {
        int maskCount = U16(c);
        if (maskCount is < 1 or > 16) throw Bad(owner, f, c, "child mask count");
        var mask = _raw.AsSpan(c + 2, maskCount).ToArray();
        int p = c + 2 + maskCount;
        int typeIndex = U16(p);
        if (typeIndex >= Types.Length) throw Bad(owner, f, c, "child type");
        int start = p + 2 + 1 + 4 + 4 + 4;
        c = start + 4;
        var obj = Fields(typeIndex, mask, ref c, end);
        if (U32(c) != (uint)(c - start)) throw Bad(owner, f, c, "trailing size mismatch");
        c += 4;
        obj["__type"] = Types[typeIndex].Name;
        return obj;
    }

    object Scalar(Field f, int o)
    {
        string t = f.TypeName.ToLowerInvariant();
        var s = _raw.AsSpan(o, f.Size);
        if (t == "bool" && f.Size == 1) return s[0] != 0;
        if (t == "float" && f.Size == 4) return (double)BinaryPrimitives.ReadSingleLittleEndian(s);
        bool signed = t.StartsWith("int");
        return f.Size switch
        {
            1 => signed ? (sbyte)s[0] : (long)s[0],
            2 => signed ? BinaryPrimitives.ReadInt16LittleEndian(s) : (long)BinaryPrimitives.ReadUInt16LittleEndian(s),
            4 => signed ? BinaryPrimitives.ReadInt32LittleEndian(s) : (long)BinaryPrimitives.ReadUInt32LittleEndian(s),
            8 => signed ? BinaryPrimitives.ReadInt64LittleEndian(s) : (object)BinaryPrimitives.ReadUInt64LittleEndian(s),
            _ => Convert.ToHexString(s),
        };
    }

    InvalidDataException Bad(TypeDef t, Field f, int at, string what) =>
        new($"{t.Name}.{f.Name}: {what} at 0x{at:X}");

    int U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(_raw.AsSpan(o));
    uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(_raw.AsSpan(o));
    string Str(ref int pos)
    {
        int n = (int)U32(pos); pos += 4;
        string s = Encoding.ASCII.GetString(_raw, pos, n); pos += n;
        return s;
    }
}
