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
    // Keyed by the member's type instance, not its spelling: resolving the spelling to a schema
    // key builds new strings, and a scene with long animations holds millions of struct values.
    private readonly Dictionary<AktType, (string Name, AktMemberInfo[] Layout)> _structLayouts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, (AktMemberInfo[] Layout, AktStructDef[] Defs)> _polyLayouts = new(StringComparer.Ordinal);
    private readonly Dictionary<AktType, AktArrayShape?> _arrayShapes = new(ReferenceEqualityComparer.Instance);

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
        foreach (var column in _keyColumns)
            if (column.IsPacked) column.ResolveReferences(_objects);

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
                if (n > 0 && e.Kind == AktTypeKind.Polymorphic && AktPackedKeys.Enabled
                    && TryReadPacked((int)n, e) is { } packed)
                    return packed;
                if (n > 0 && e.Kind == AktTypeKind.Struct && AktPackedKeys.Enabled && ArrayShape(e) is { } arrayShape)
                    return ReadArrayColumn((int)n, arrayShape);
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
                var known = StructLayout(t) ?? throw Error($"Struct '{t.Spelling}' is not in the struct schema");
                return ReadStruct(known.Name, known.Layout);
            }
            case AktTypeKind.Polymorphic:
            {
                string? concrete = Atom();
                if (concrete == null)
                    return null;
                return ReadStruct(concrete, PolymorphicLayout(concrete));
            }
            default:
                throw Error($"Unsupported member type '{t.Spelling}'");
        }
    }

    private (string Name, AktMemberInfo[] Layout)? StructLayout(AktType t)
    {
        if (_structLayouts.TryGetValue(t, out var known)) return known;
        if (_schema.FindStruct(t.Target!) is not { } def) return null;
        known = (def.Name, def.Members.Select(m => AktMemberInfo.Get(def.Name, m.Name, m.TypeName)).ToArray());
        _structLayouts[t] = known;
        return known;
    }

    private AktArrayShape? ArrayShape(AktType element)
    {
        if (_arrayShapes.TryGetValue(element, out var shape)) return shape;
        shape = StructLayout(element) is { } outer ? AktArrayShape.TryCreate(outer.Name, outer.Layout, StructLayout) : null;
        _arrayShapes[element] = shape;
        return shape;
    }

    /// <summary>Reads a vector of <paramref name="shape"/> structs straight into word arrays.</summary>
    private AktArrayColumn ReadArrayColumn(int n, AktArrayShape shape)
    {
        var column = new AktArrayColumn(shape, n);
        int head = shape.HeadCount, stride = shape.Stride;
        var words = new uint[head + 64];
        for (int i = 0; i < n; i++)
        {
            int items = 0;
            for (int m = 0; m < shape.Outer.Length; m++)
            {
                if (m != shape.ListMember)
                {
                    words[shape.HeadSlot[m]] = U32();
                    continue;
                }
                items = checked((int)Count(stride * 4) * stride);
                if (words.Length < head + items) Array.Resize(ref words, head + items);
                RawWords(words.AsSpan(head, items));
            }
            column.Append(shape.Compose(words.AsSpan(0, head), words.AsSpan(head, items)));
        }
        return column;
    }

    private void RawWords(Span<uint> dst)
    {
        int bytes = dst.Length * 4;
        Need(bytes);
        _data.AsSpan(_pos, bytes).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(dst));
        if (!BitConverter.IsLittleEndian)
            for (int k = 0; k < dst.Length; k++)
                dst[k] = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos + 4 * k));
        _pos += bytes;
    }

    private AktMemberInfo[] PolymorphicLayout(string concrete)
    {
        if (!_polyLayouts.TryGetValue(concrete, out var poly))
        {
            var defs = AktNames.PolymorphicChain(AktNames.Key(concrete))
                .Select(key => _schema.FindStruct(key)).OfType<AktStructDef>().ToArray();
            poly = (defs.SelectMany(d => d.Members.Select(m => AktMemberInfo.Get(d.Name, m.Name, m.TypeName))).ToArray(), defs);
            _polyLayouts[concrete] = poly;
        }
        return poly.Layout;
    }

    // ---- packed key lists ------------------------------------------------------------------
    // A vector of polymorphic keys whose type is a struct of floats plus references, or a list
    // of tangent modes, is read straight into packed arrays (AktPackedKeys.cs) instead of one
    // struct per element. The bytes read are the same as the generic path's.

    // Per concrete type: its AktMrsShape, its AktTangentShape, or NotPackable.
    private readonly Dictionary<string, object> _packShapes = new(StringComparer.Ordinal);
    private static readonly object NotPackable = new();
    private readonly List<AktMrsKeyColumn> _keyColumns = new();

    private object PackShape(string concrete)
    {
        if (_packShapes.TryGetValue(concrete, out var shape)) return shape;
        var layout = PolymorphicLayout(concrete);
        shape = (object?)AktMrsShape.TryCreate(concrete, layout, StructLayout)
                ?? (object?)AktTangentShape.TryCreate(concrete, layout, StructLayout)
                ?? NotPackable;
        _packShapes[concrete] = shape;
        return shape;
    }

    /// <summary>
    /// Reads a vector of <paramref name="n"/> polymorphic elements into a packed list when its
    /// first element's type allows it; null (nothing read) otherwise.
    /// </summary>
    private AktPackedList? TryReadPacked(int n, AktType element)
    {
        int start = _pos;
        string? first = Atom();
        _pos = start;
        if (first == null) return null;
        return PackShape(first) switch
        {
            AktMrsShape keys => ReadKeyColumn(n, first, keys, element),
            AktTangentShape tangents => ReadTangentColumn(n, first, tangents, element),
            _ => null,
        };
    }

    private AktPackedList ReadKeyColumn(int n, string concrete, AktMrsShape shape, AktType element)
    {
        var column = new AktMrsKeyColumn(shape, n);
        _keyColumns.Add(column);
        for (int i = 0; i < n; i++)
        {
            int start = _pos;
            if (Atom() != concrete)
            {
                // Another type (or null) inside the list: keep what was read and continue generically.
                _pos = start;
                return ContinueGenerically(column, n - i, element);
            }
            var floats = column.BeginAppend();
            for (int m = 0; m < shape.Outer.Length; m++)
            {
                int at = shape.At[m];
                switch (shape.Part[m])
                {
                    case MrsPart.Ref:
                        column.AppendRef(at, (int)U32());
                        break;
                    case MrsPart.Value:
                        RawFloats(floats.Slice(at, shape.Struct[m]!.Stride));
                        break;
                    case MrsPart.Float:
                    case MrsPart.UInt:
                        // A uint is kept as its raw bits in the float slot.
                        RawFloats(floats.Slice(at, 1));
                        break;
                    case MrsPart.Records:
                        int recordStride = shape.Struct[m]!.Stride;
                        int count = (int)Count(recordStride * 4);
                        if (count == 0) { column.AppendRecords(at, []); break; }
                        var records = new float[count * recordStride];
                        RawFloats(records);
                        column.AppendRecords(at, records);
                        break;
                }
            }
            column.EndAppend();
        }
        return column;
    }

    /// <summary>Reads <paramref name="dst"/>.Length floats as raw bits, so NaN payloads survive.</summary>
    private void RawFloats(Span<float> dst)
    {
        int bytes = dst.Length * 4;
        Need(bytes);
        _data.AsSpan(_pos, bytes).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(dst));
        if (!BitConverter.IsLittleEndian)
            for (int k = 0; k < dst.Length; k++)
                dst[k] = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(_pos + 4 * k));
        _pos += bytes;
    }

    private AktPackedList ReadTangentColumn(int n, string concrete, AktTangentShape shape, AktType element)
    {
        var column = new AktTangentColumn(shape, n);
        Span<uint> buffer = stackalloc uint[64];
        for (int i = 0; i < n; i++)
        {
            int start = _pos;
            if (Atom() != concrete)
            {
                _pos = start;
                return ContinueGenerically(column, n - i, element);
            }
            int count = (int)Count(4);
            Span<uint> modes = count <= buffer.Length ? buffer[..count] : new uint[count];
            for (int k = 0; k < count; k++) modes[k] = U32();
            column.Append(AktModeInterner.Intern(modes));
        }
        return column;
    }

    /// <summary>Demotes <paramref name="column"/> to a plain list and reads the remaining elements into it.</summary>
    private AktPackedList ContinueGenerically(AktPackedList column, int remaining, AktType element)
    {
        column.Demote();
        // The demoted keys' references are not resolved yet: hand them to the resolve pass.
        foreach (var item in column)
            if (item is AktStruct s)
                for (int f = 0; f < s.FieldCount; f++)
                    if (s.ValueAt(f) is AktReference r) _references.Add(r);
        for (int i = 0; i < remaining; i++) column.Add(ReadValue(element));
        return column;
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
