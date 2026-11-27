using System.Buffers.Binary;
using System.Text;

namespace SloppyAnimatsu.Core.Akt;

/// <summary>
/// Writes an <see cref="AktDocument"/>. Sections are produced in the original's order
/// (hierarchy, class schema, struct schema, then atom table and header) so atoms get indices in
/// first-use order.
/// </summary>
public sealed class AktWriter
{
    private readonly AktDocument _doc;
    private readonly Dictionary<string, uint> _atomIndex = new(StringComparer.Ordinal);
    private readonly List<string> _atoms = new();
    private readonly Dictionary<AktObject, int> _objectIndex = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<AktClassDef> _usedClasses = new();

    private readonly IProgress<double>? _progress;
    private int _objectCount, _written;

    private readonly IReadOnlySet<string>? _omit;
    private readonly IReadOnlyDictionary<AktObject, IReadOnlySet<string>>? _emptied;
    private readonly IReadOnlyDictionary<AktObject, IReadOnlyDictionary<string, object?>>? _replaced;

    private AktWriter(AktDocument doc, IProgress<double>? progress, IReadOnlySet<string>? omit,
        IReadOnlyDictionary<AktObject, IReadOnlySet<string>>? emptied = null,
        IReadOnlyDictionary<AktObject, IReadOnlyDictionary<string, object?>>? replaced = null)
    {
        _replaced = replaced is { Count: > 0 } ? replaced : null;
        _doc = doc;
        _progress = progress;
        _omit = omit is { Count: > 0 } ? omit : null;
        _emptied = emptied is { Count: > 0 } ? emptied : null;
    }

    /// <summary>The file bytes; <paramref name="progress"/> follows the objects written (0 to 1).</summary>
    public static byte[] Write(AktDocument doc, IProgress<double>? progress = null) => new AktWriter(doc, progress, null).WriteDocument();

    /// <summary>
    /// The file bytes without the class members named in <paramref name="omitMembers"/>: they are
    /// left out of the class schema and of every object, as if the schema never had them (the
    /// .akt-compatible save drops SloppyAnimatsu's own members this way; <see cref="AkeytsuCompatibility"/>).
    /// The vector members listed for an object in <paramref name="emptiedVectors"/> are written with
    /// no elements, and the members listed for an object in <paramref name="replacedValues"/> are
    /// written with the listed value; the document itself is not changed.
    /// </summary>
    public static byte[] Write(AktDocument doc, IReadOnlySet<string>? omitMembers, IProgress<double>? progress = null,
        IReadOnlyDictionary<AktObject, IReadOnlySet<string>>? emptiedVectors = null,
        IReadOnlyDictionary<AktObject, IReadOnlyDictionary<string, object?>>? replacedValues = null) =>
        new AktWriter(doc, progress, omitMembers, emptiedVectors, replacedValues).WriteDocument();

    private bool Omitted(string name) => _omit != null && _omit.Contains(name);

    private byte[] WriteDocument()
    {
        int n = 0;
        foreach (var o in _doc.AllObjects())
            _objectIndex[o] = n++;
        _objectCount = n;

        var hierarchy = new Buf();
        hierarchy.U32((uint)_doc.Roots.Count);
        foreach (var root in _doc.Roots)
            WriteObject(hierarchy, root);

        var classes = new Buf();
        WriteClassSchema(classes);

        var structs = new Buf();
        WriteStructSchema(structs);

        var head = new Buf();
        if (_doc.HasHeader)
        {
            var v = _doc.Version;
            foreach (short s in new[] { v.Major, v.Minor, v.Build, v.Revision, v.Extra })
                head.U16((ushort)s);
        }
        head.U32((uint)_atoms.Count);
        foreach (var a in _atoms)
            head.WString(a);

        var result = new MemoryStream(head.Length + classes.Length + structs.Length + hierarchy.Length);
        head.CopyTo(result);
        classes.CopyTo(result);
        structs.CopyTo(result);
        hierarchy.CopyTo(result);
        return result.ToArray();
    }

    // ---- hierarchy ------------------------------------------------------------------------

    private void WriteObject(Buf b, AktObject obj)
    {
        var def = _doc.Schema.FindClass(obj.TypeName)
                  ?? throw new InvalidOperationException($"Class '{obj.TypeName}' is missing from the document schema.");
        b.U32(Atom(def.Name));
        int cursor = 0;
        IReadOnlySet<string>? emptied = null;
        _emptied?.TryGetValue(obj, out emptied);
        IReadOnlyDictionary<string, object?>? replaced = null;
        _replaced?.TryGetValue(obj, out replaced);
        foreach (var c in def.Chain())
            foreach (var m in c.Members)
            {
                if (Omitted(m.Name))
                {
                    if (cursor < obj.FieldCount && obj.MemberAt(cursor).Name == m.Name) cursor++;
                    continue;
                }
                var value = Field(obj, ref cursor, c.Key, m.Name);
                if (emptied != null && m.Type.Kind == AktTypeKind.Vector && emptied.Contains(m.Name))
                {
                    b.U32(0); // a vector with no elements: just its count
                    continue;
                }
                if (replaced != null && replaced.TryGetValue(m.Name, out var replacement)) value = replacement;
                WriteValue(b, m.Type, value);
            }
        if (_progress != null && (++_written & 1023) == 0)
            _progress.Report((double)_written / _objectCount);
        b.U32((uint)obj.Children.Count);
        foreach (var child in obj.Children)
            WriteObject(b, child);
    }

    private void WriteValue(Buf b, AktType t, object? v)
    {
        switch (t.Kind)
        {
            case AktTypeKind.Bool: b.U8(v is byte raw ? raw : (bool)v! ? (byte)1 : (byte)0); break;
            case AktTypeKind.UChar: b.U8((byte)v!); break;
            case AktTypeKind.Char: b.U8((byte)(sbyte)v!); break;
            case AktTypeKind.UShort: b.U16((ushort)v!); break;
            case AktTypeKind.Short: b.U16((ushort)(short)v!); break;
            case AktTypeKind.UInt:
            case AktTypeKind.ULong: b.U32((uint)v!); break;
            case AktTypeKind.Int:
            case AktTypeKind.Long: b.U32((uint)(int)v!); break;
            case AktTypeKind.Float: b.F32((float)v!); break;
            case AktTypeKind.Double: b.F64((double)v!); break;
            case AktTypeKind.FloatGroup:
            {
                var f = (float[])v!;
                if (f.Length != t.Count)
                    throw new InvalidOperationException($"{t.Spelling} needs {t.Count} floats, got {f.Length}.");
                foreach (float x in f) b.F32(x);
                break;
            }
            case AktTypeKind.RGBAi:
            {
                var c = (byte[])v!;
                if (c.Length != 4) throw new InvalidOperationException("RGBAi needs 4 bytes.");
                b.Bytes(c);
                break;
            }
            case AktTypeKind.Atom: b.U32(Atom((string?)v)); break;
            case AktTypeKind.WString: b.WString((string)v!); break;
            case AktTypeKind.Reference: b.U32((uint)RefIndex((AktReference?)v)); break;
            case AktTypeKind.RefArray:
            {
                var list = (List<AktReference>)v!;
                b.U32((uint)list.Count);
                foreach (var r in list) b.U32((uint)RefIndex(r));
                break;
            }
            case AktTypeKind.Vector:
            {
                if (v is byte[] bytes)
                {
                    b.U32((uint)bytes.Length);
                    b.Bytes(bytes);
                    break;
                }
                if (v is AktMrsKeyColumn { IsPacked: true } keys && t.Element!.Kind == AktTypeKind.Polymorphic
                    && SchemaMatches(keys.Shape))
                {
                    WriteKeyColumn(b, keys);
                    break;
                }
                if (v is AktTangentColumn { IsPacked: true } tangents && t.Element!.Kind == AktTypeKind.Polymorphic
                    && SchemaMatches(tangents.Shape))
                {
                    WriteTangentColumn(b, tangents);
                    break;
                }
                if (v is AktArrayColumn { IsPacked: true } arrays && t.Element!.Kind == AktTypeKind.Struct
                    && SchemaMatches(arrays.Shape, t.Element))
                {
                    WriteArrayColumn(b, arrays);
                    break;
                }
                var list = (IList<object?>)v!;
                b.U32((uint)list.Count);
                for (int i = 0; i < list.Count; i++) WriteValue(b, t.Element!, list[i]);
                break;
            }
            case AktTypeKind.Tuple:
            {
                var arr = (object?[])v!;
                if (arr.Length != t.Count) throw new InvalidOperationException($"{t.Spelling} needs {t.Count} elements.");
                foreach (var e in arr) WriteValue(b, t.Element!, e);
                break;
            }
            case AktTypeKind.Map:
            {
                var map = (AktMap)v!;
                b.U32((uint)map.Entries.Count);
                foreach (var e in map.Entries)
                {
                    WriteValue(b, t.KeyElement!, e.Key);
                    WriteValue(b, t.Element!, e.Value);
                }
                break;
            }
            case AktTypeKind.Struct:
            {
                var def = _doc.Schema.FindStruct(t.Target!)
                          ?? throw new InvalidOperationException($"Struct '{t.Spelling}' is missing from the document schema.");
                int cursor = 0;
                WriteStructMembers(b, def, (AktStruct)v!, ref cursor);
                break;
            }
            case AktTypeKind.Polymorphic:
            {
                if (v is not AktStruct s)
                {
                    b.U32(0);
                    break;
                }
                b.U32(Atom(s.TypeName));
                int cursor = 0;
                foreach (string key in AktNames.PolymorphicChain(AktNames.Key(s.TypeName)))
                    if (_doc.Schema.FindStruct(key) is { } def)
                        WriteStructMembers(b, def, s, ref cursor);
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported member type '{t.Spelling}'.");
        }
    }

    private void WriteStructMembers(Buf b, AktStructDef def, AktStruct s, ref int cursor)
    {
        foreach (var m in def.Members)
            WriteValue(b, m.Type, Field(s, ref cursor, def.Key, m.Name));
    }

    /// <summary>
    /// Finds a member value. Fields are normally stored in schema order, so the next field is
    /// tried first; otherwise falls back to a search by owner and name.
    /// </summary>
    private static object? Field(AktFieldContainer c, ref int cursor, string ownerKey, string name)
    {
        if (cursor < c.FieldCount && c.MemberAt(cursor).Name == name)
            return c.ValueAt(cursor++);
        int i = c.IndexOf(ownerKey, name);
        if (i < 0) throw new InvalidOperationException($"{c.TypeName}: missing value for {ownerKey}.{name}.");
        return c.ValueAt(i);
    }

    // ---- packed key lists -------------------------------------------------------------------
    // Written straight from their arrays. The bytes equal the generic path's as long as the
    // document schema lists the element's members in the order the list stores them, which
    // SchemaMatches checks once per layout; otherwise the generic path writes the views.

    private readonly Dictionary<object, bool> _packedSchemaOk = new(ReferenceEqualityComparer.Instance);

    private bool SchemaMatches(AktMrsShape shape)
    {
        if (_packedSchemaOk.TryGetValue(shape, out bool ok)) return ok;
        ok = ChainMatches(shape.TypeName, shape.Outer);
        for (int m = 0; ok && m < shape.Outer.Length; m++)
        {
            if (shape.Struct[m] is not { } s) continue;
            var type = shape.Outer[m].Type;
            var target = shape.Part[m] == MrsPart.Records ? type.Element!.Target! : type.Target!;
            ok = _doc.Schema.FindStruct(target) is { } def && MembersMatch(def.Members, s.Layout, 0) == s.Layout.Length;
        }
        return _packedSchemaOk[shape] = ok;
    }

    private bool SchemaMatches(AktTangentShape shape)
    {
        if (_packedSchemaOk.TryGetValue(shape, out bool ok)) return ok;
        ok = ChainMatches(shape.TypeName, shape.Outer)
             && _doc.Schema.FindStruct(shape.Outer[0].Type.Element!.Target!) is { } channel
             && MembersMatch(channel.Members, shape.Channel, 0) == 1;
        return _packedSchemaOk[shape] = ok;
    }

    private bool SchemaMatches(AktArrayShape shape, AktType element)
    {
        if (_packedSchemaOk.TryGetValue(shape, out bool ok)) return ok;
        ok = _doc.Schema.FindStruct(element.Target!) is { } def && def.Name == shape.TypeName
             && MembersMatch(def.Members, shape.Outer, 0) == shape.Outer.Length;
        if (ok && shape.Item != null)
            ok = _doc.Schema.FindStruct(shape.Outer[shape.ListMember].Type.Element!.Target!) is { } item
                 && item.Name == shape.ItemType && MembersMatch(item.Members, shape.Item, 0) == shape.Item.Length;
        return _packedSchemaOk[shape] = ok;
    }

    /// <summary>Each element's members in order: its scalars from the head, its vector as a count and the words.</summary>
    private static void WriteArrayColumn(Buf b, AktArrayColumn column)
    {
        var shape = column.Shape;
        int n = column.Count;
        b.U32((uint)n);
        for (int i = 0; i < n; i++)
        {
            var words = column.WordsAt(i);
            for (int m = 0; m < shape.Outer.Length; m++)
            {
                if (m != shape.ListMember)
                {
                    b.U32(words[shape.HeadSlot[m]]);
                    continue;
                }
                b.U32((uint)((words.Length - shape.HeadCount) / shape.Stride));
                for (int k = shape.HeadCount; k < words.Length; k++) b.U32(words[k]);
            }
        }
    }

    /// <summary>Whether the schema's polymorphic chain of <paramref name="typeName"/> declares exactly <paramref name="layout"/>.</summary>
    private bool ChainMatches(string typeName, AktMemberInfo[] layout)
    {
        int at = 0;
        foreach (string key in AktNames.PolymorphicChain(AktNames.Key(typeName)))
        {
            if (_doc.Schema.FindStruct(key) is not { } def) continue;
            int matched = MembersMatch(def.Members, layout, at);
            if (matched < 0) return false;
            at = matched;
        }
        return at == layout.Length;
    }

    /// <summary>Matches <paramref name="members"/> against <paramref name="layout"/> from <paramref name="at"/>; the index after them, or -1.</summary>
    private static int MembersMatch(List<AktMember> members, AktMemberInfo[] layout, int at)
    {
        foreach (var m in members)
        {
            if (at >= layout.Length || layout[at].Name != m.Name || layout[at].Type.Spelling != m.Type.Spelling) return -1;
            at++;
        }
        return at;
    }

    private void WriteKeyColumn(Buf b, AktMrsKeyColumn keys)
    {
        var shape = keys.Shape;
        int n = keys.Count, stride = shape.Stride;
        uint atom = Atom(shape.TypeName);
        var floats = keys.FloatData;
        b.U32((uint)n);
        for (int i = 0; i < n; i++)
        {
            b.U32(atom);
            int baseAt = i * stride;
            for (int m = 0; m < shape.Outer.Length; m++)
            {
                int at = shape.At[m];
                // Raw float writes keep NaN payloads, as the generic path does.
                switch (shape.Part[m])
                {
                    case MrsPart.Ref:
                        var (target, raw) = keys.Ref(at, i);
                        b.U32((uint)RefIndex(target, raw));
                        break;
                    case MrsPart.Value:
                        for (int f = baseAt + at, end = f + shape.Struct[m]!.Stride; f < end; f++) b.F32(floats[f]);
                        break;
                    case MrsPart.Float:
                        b.F32(floats[baseAt + at]);
                        break;
                    case MrsPart.UInt:
                        b.U32(BitConverter.SingleToUInt32Bits(floats[baseAt + at]));
                        break;
                    case MrsPart.Records:
                        var records = keys.Records(at, i);
                        b.U32((uint)(records.Length / shape.Struct[m]!.Stride));
                        foreach (float f in records) b.F32(f);
                        break;
                }
            }
        }
    }

    private void WriteTangentColumn(Buf b, AktTangentColumn tangents)
    {
        int n = tangents.Count;
        uint atom = Atom(tangents.Shape.TypeName);
        b.U32((uint)n);
        for (int i = 0; i < n; i++)
        {
            b.U32(atom);
            var modes = tangents.ModesAt(i);
            b.U32((uint)modes.Length);
            foreach (uint mode in modes) b.U32(mode);
        }
    }

    private int RefIndex(AktObject? target, int raw) =>
        target != null ? (_objectIndex.TryGetValue(target, out int i) ? i : -1) : raw;

    private int RefIndex(AktReference? r)
    {
        if (r == null) return -1;
        if (r.Target != null)
            return _objectIndex.TryGetValue(r.Target, out int i) ? i : -1;
        return r.RawIndex;
    }

    // ---- schemas --------------------------------------------------------------------------

    private void WriteClassSchema(Buf b)
    {
        // Instantiated classes plus all their ancestors.
        foreach (var o in _doc.AllObjects())
            for (var c = _doc.Schema.FindClass(o.TypeName); c != null && _usedClasses.Add(c); c = c.Parent) { }

        WriteClassList(b, _doc.Schema.RootClasses, _usedClasses);
    }

    private void WriteClassList(Buf b, List<AktClassDef> list, HashSet<AktClassDef> used)
    {
        foreach (var c in list.Where(used.Contains).OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            b.U32(Atom(c.Name));
            WriteMembers(b, _omit == null ? c.Members : c.Members.Where(m => !Omitted(m.Name)).ToList());
            WriteClassList(b, c.Derived, used);
        }
        b.U32(0);
    }

    private void WriteStructSchema(Buf b)
    {
        // Selected from the schema's definitions by the inclusion rule
        // so a schema built from a catalog writes like the original.
        var structs = _doc.Schema.SelectStructs(_usedClasses, _doc.AllObjects());
        b.U32((uint)structs.Count);
        foreach (var s in structs)
        {
            b.U32(Atom(s.Name));
            WriteMembers(b, s.Members);
        }
    }

    private void WriteMembers(Buf b, List<AktMember> members)
    {
        b.U32((uint)members.Count);
        foreach (var m in members)
        {
            b.U32(Atom(m.Name));
            b.U32(Atom(m.TypeName));
        }
    }

    private uint Atom(string? s)
    {
        if (s == null) return 0;
        if (!_atomIndex.TryGetValue(s, out uint i))
        {
            _atoms.Add(s);
            i = (uint)_atoms.Count;
            _atomIndex[s] = i;
        }
        return i;
    }

    // ---- buffer ---------------------------------------------------------------------------

    private sealed class Buf
    {
        private byte[] _buf = new byte[4096];
        public int Length { get; private set; }

        private Span<byte> Grow(int n)
        {
            if (Length + n > _buf.Length)
                Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + n));
            var span = _buf.AsSpan(Length, n);
            Length += n;
            return span;
        }

        public void U8(byte v) => Grow(1)[0] = v;
        public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), v);
        public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), v);
        public void F32(float v) => BinaryPrimitives.WriteSingleLittleEndian(Grow(4), v);
        public void F64(double v) => BinaryPrimitives.WriteDoubleLittleEndian(Grow(8), v);
        public void Bytes(ReadOnlySpan<byte> v) => v.CopyTo(Grow(v.Length));

        public void WString(string s)
        {
            if (s.Contains('\0'))
                throw new InvalidOperationException("Strings cannot contain NUL characters.");
            int n = Encoding.Unicode.GetByteCount(s);
            Encoding.Unicode.GetBytes(s, Grow(n));
            U16(0);
        }

        public void CopyTo(Stream s) => s.Write(_buf, 0, Length);
    }
}
