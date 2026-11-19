using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SloppyAnimatsu.Core.Akt;

/// <summary>Reads.akt bytes into an <see cref="AktDocument"/>.</summary>
public sealed class AktReader
{
    private const int VersionLimit = 30;

    private readonly byte[] _data;
    private int _pos;
    private string[] _atoms = Array.Empty<string>();
    private readonly AktSchema _schema = new();
    private readonly List<AktObject> _objects = new();
    private readonly List<AktReference> _references = new();
    // Member declarations per class, struct and polymorphic concrete type, shared by every value
    // read with that layout.
    private readonly Dictionary<AktClassDef, AktMemberInfo[]> _classLayouts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AktStructDef, AktMemberInfo[]> _structLayouts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, (AktMemberInfo[] Layout, AktStructDef[] Defs)> _polyLayouts = new(StringComparer.Ordinal);

    private AktReader(byte[] data) => _data = data;

    /// <summary>
    /// Reads a plain .akt file or a SloppyAnimatsu container (<see cref="AktContainer"/>); the result's
    /// <see cref="AktDocument.SourceFormat"/> says which.
    /// </summary>
    public static AktDocument Read(byte[] data)
    {
        var (payload, format) = AktContainer.Unwrap(data);
        var doc = new AktReader(payload).ReadDocument();
        doc.SourceFormat = format;
        return doc;
    }

    /// <summary>
    /// The root class key of a file, without building its hierarchy. Used to tell scenes from
    /// preferences files when a sample folder is scanned: parsing a 90 MB scene just to look at
    /// its root class costs seconds and gigabytes, while the schema in front of the hierarchy is
    /// small.
    /// </summary>
    public static string ProbeRootClass(byte[] data)
    {
        var reader = new AktReader(AktContainer.Unwrap(data).Payload);
        reader.ReadHeader();
        reader.ReadAtomTable();
        reader.ReadClassList(null);
        reader.ReadStructSchema();
        if (reader.Count(4) == 0)
            throw reader.Error("Empty hierarchy");
        return AktNames.Key(reader.Atom() ?? throw reader.Error("Null class name in hierarchy"));
    }

    private AktDocument ReadDocument()
    {
        var (version, hasHeader) = ReadHeader();
        ReadAtomTable();
        ReadClassList(null);
        ReadStructSchema();

        uint rootCount = Count(4);
        if (rootCount == 0)
            throw Error("Empty hierarchy");
        var roots = new List<AktObject>((int)rootCount);
        for (int i = 0; i < rootCount; i++)
            roots.Add(ReadObject());

        if (_pos != _data.Length)
            throw Error($"{_data.Length - _pos} trailing bytes after the hierarchy");

        foreach (var r in _references)
            if (r.RawIndex >= 0 && r.RawIndex < _objects.Count)
                r.Target = _objects[r.RawIndex];

        return new AktDocument(_schema, roots)
        {
            Version = version,
            HasHeader = hasHeader,
            SourceAtoms = _atoms,
        };
    }

    // ---- sections -------------------------------------------------------------------------

    private (AktVersion, bool) ReadHeader()
    {
        if (_data.Length < 10)
            throw Error("File too short for a header");
        Span<short> v = stackalloc short[5];
        for (int i = 0; i < 5; i++)
            v[i] = (short)U16();
        bool valid = v[0] >= -1 && v[0] <= VersionLimit
                  && v[1] >= 0 && v[1] <= VersionLimit
                  && v[2] >= 0 && v[2] <= VersionLimit
                  && v[3] >= 0 && v[3] <= VersionLimit;
        if (valid)
            return (new AktVersion(v[0], v[1], v[2], v[3], v[4]), true);
        _pos = 0;
        return (AktVersion.Legacy, false);
    }

    private void ReadAtomTable()
    {
        uint count = U32();
        if (count > (_data.Length - _pos) / 2)
            throw Error($"Implausible atom count {count}");
        _atoms = new string[count];
        for (int i = 0; i < count; i++)
            _atoms[i] = WString();
    }

    private void ReadClassList(AktClassDef? parent)
    {
        while (true)
        {
            string? name = Atom();
            if (name == null)
                return;
            var def = new AktClassDef(name);
            ReadMembers(def.Members);
            _schema.AddClass(def, parent);
            ReadClassList(def);
        }
    }

    private void ReadStructSchema()
    {
        uint count = U32();
        for (int i = 0; i < count; i++)
        {
            var def = new AktStructDef(Atom() ?? throw Error("Null struct name"));
            ReadMembers(def.Members);
            _schema.AddStruct(def);
        }
    }

    private void ReadMembers(List<AktMember> members)
    {
        uint count = U32();
        for (int i = 0; i < count; i++)
        {
            string name = Atom() ?? throw Error("Null member name");
            string type = Atom() ?? throw Error($"Null type for member '{name}'");
            members.Add(new AktMember(name, type));
        }
    }

    private AktObject ReadObject()
    {
        long start = _pos;
        string className = Atom() ?? throw Error("Null class name in hierarchy");
        var def = _schema.FindClass(className) ?? throw new AktFormatException($"Class '{className}' is not in the class schema", start);

        if (!_classLayouts.TryGetValue(def, out var layout))
        {
            layout = def.Chain().SelectMany(c => c.Members.Select(m => AktMemberInfo.Get(c.Name, m.Name, m.TypeName))).ToArray();
            _classLayouts[def] = layout;
        }
        var values = new object?[layout.Length];
        var obj = new AktObject(className, layout, values);
        _objects.Add(obj);
        for (int i = 0; i < layout.Length; i++)
            values[i] = ReadValue(layout[i].Type);

        uint childCount = U32();
        for (int i = 0; i < childCount; i++)
            obj.AddChild(ReadObject());
        return obj;
    }

    // ---- values ---------------------------------------------------------------------------

    private object? ReadValue(AktType t)
    {
        switch (t.Kind)
        {
            case AktTypeKind.Bool:
            {
                // Non-0/1 bytes occur (uninitialised memory saved by the original); keep them raw.
                byte b = U8();
                return b <= 1 ? AktBox.Bool(b == 1) : AktBox.Byte(b);
            }
            case AktTypeKind.UChar: return AktBox.Byte(U8());
            case AktTypeKind.Char: return (sbyte)U8();
            case AktTypeKind.UShort: return AktBox.UShort(U16());
            case AktTypeKind.Short: return AktBox.Short((short)U16());
            case AktTypeKind.UInt:
            case AktTypeKind.ULong: return AktBox.UInt(U32());
            case AktTypeKind.Int:
            case AktTypeKind.Long: return AktBox.Int((int)U32());
            case AktTypeKind.Float: return AktBox.Float(F32());
            case AktTypeKind.Double:
            {
                Need(8);
                double d = BinaryPrimitives.ReadDoubleLittleEndian(_data.AsSpan(_pos));
                _pos += 8;
                return d;
            }
            case AktTypeKind.FloatGroup:
            {
                var f = new float[t.Count];
                for (int i = 0; i < f.Length; i++) f[i] = F32();
                return f;
            }
            case AktTypeKind.RGBAi: return Bytes(4);
            case AktTypeKind.Atom: return Atom();
            case AktTypeKind.WString: return WString();
            case AktTypeKind.Reference: return Reference();
            case AktTypeKind.RefArray:
            {
                uint n = Count(4);
                var list = new List<AktReference>((int)n);
                for (int i = 0; i < n; i++) list.Add(Reference());
                return list;
            }
            case AktTypeKind.Vector:
            {
                var e = t.Element!;
                if (e.Kind == AktTypeKind.UChar)
                    return Bytes((int)Count(1));
                uint n = Count(1);
                var list = new List<object?>((int)n);
                for (int i = 0; i < n; i++) list.Add(ReadValue(e));
                return list;
            }
            case AktTypeKind.Tuple:
            {
                var arr = new object?[t.Count];
                for (int i = 0; i < arr.Length; i++) arr[i] = ReadValue(t.Element!);
                return arr;
            }
            case AktTypeKind.Map:
            {
                uint n = Count(2);
                var map = new AktMap();
                for (int i = 0; i < n; i++)
                    map.Add(ReadValue(t.KeyElement!), ReadValue(t.Element!));
                return map;
            }
            case AktTypeKind.Struct:
            {
                var def = _schema.FindStruct(t.Target!) ?? throw Error($"Struct '{t.Spelling}' is not in the struct schema");
                if (!_structLayouts.TryGetValue(def, out var layout))
                {
                    layout = def.Members.Select(m => AktMemberInfo.Get(def.Name, m.Name, m.TypeName)).ToArray();
                    _structLayouts[def] = layout;
                }
                return ReadStruct(def.Name, layout);
            }
            case AktTypeKind.Polymorphic:
            {
                string? concrete = Atom();
                if (concrete == null)
                    return null;
                if (!_polyLayouts.TryGetValue(concrete, out var poly))
                {
                    var defs = AktNames.PolymorphicChain(AktNames.Key(concrete))
                        .Select(key => _schema.FindStruct(key)).OfType<AktStructDef>().ToArray();
                    poly = (defs.SelectMany(d => d.Members.Select(m => AktMemberInfo.Get(d.Name, m.Name, m.TypeName))).ToArray(), defs);
                    _polyLayouts[concrete] = poly;
                }
                return ReadStruct(concrete, poly.Layout);
            }
            default:
                throw Error($"Unsupported member type '{t.Spelling}'");
        }
    }

    private AktStruct ReadStruct(string typeName, AktMemberInfo[] layout)
    {
        var values = new object?[layout.Length];
        for (int i = 0; i < layout.Length; i++)
            values[i] = ReadValue(layout[i].Type);
        return new AktStruct(typeName, layout, values);
    }

    private AktReference Reference()
    {
        var r = new AktReference((int)U32());
        _references.Add(r);
        return r;
    }

    // ---- primitives -----------------------------------------------------------------------

    private void Need(int n)
    {
        if (_pos + n > _data.Length)
            throw Error($"Unexpected end of file (need {n} bytes)");
    }

    /// <summary>Reads a u32 count and checks that at least count * minElementSize bytes remain.</summary>
    private uint Count(int minElementSize)
    {
        uint n = U32();
        if ((long)n * minElementSize > _data.Length - _pos)
            throw Error($"Implausible element count {n}");
        return n;
    }

    private byte U8() { Need(1); return _data[_pos++]; }

    private ushort U16()
    {
        Need(2);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_pos));
        _pos += 2;
        return v;
    }

    private uint U32()
    {
        Need(4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos));
        _pos += 4;
        return v;
    }

    private float F32()
    {
        Need(4);
        float v = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(_pos));
        _pos += 4;
        return v;
    }

    private byte[] Bytes(int n)
    {
        Need(n);
        var b = _data.AsSpan(_pos, n).ToArray();
        _pos += n;
        return b;
    }

    private string? Atom()
    {
        long at = _pos;
        uint i = U32();
        if (i == 0) return null;
        if (i > _atoms.Length)
            throw new AktFormatException($"Atom index {i} out of range (table has {_atoms.Length})", at);
        return _atoms[i - 1];
    }

    private string WString()
    {
        int start = _pos;
        int end = start;
        while (true)
        {
            if (end + 2 > _data.Length)
                throw Error("Unterminated string");
            if (_data[end] == 0 && _data[end + 1] == 0)
                break;
            end += 2;
        }
        _pos = end + 2;
        return new string(MemoryMarshal.Cast<byte, char>(_data.AsSpan(start, end - start)));
    }

    private AktFormatException Error(string message) => new(message, _pos);
}
