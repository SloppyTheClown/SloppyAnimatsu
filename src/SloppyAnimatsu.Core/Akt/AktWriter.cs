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

    private AktWriter(AktDocument doc, IProgress<double>? progress, IReadOnlySet<string>? omit)
    {
        _doc = doc;
        _progress = progress;
        _omit = omit is { Count: > 0 } ? omit : null;
    }

    /// <summary>The file bytes; <paramref name="progress"/> follows the objects written (0 to 1).</summary>
    public static byte[] Write(AktDocument doc, IProgress<double>? progress = null) => new AktWriter(doc, progress, null).WriteDocument();

    /// <summary>
    /// The file bytes without the class members named in <paramref name="omitMembers"/>: they are
    /// left out of the class schema and of every object, as if the schema never had them (the
    /// .akt-compatible save drops SloppyAnimatsu's own members this way; <see cref="AkeytsuCompatibility"/>).
    /// </summary>
    public static byte[] Write(AktDocument doc, IReadOnlySet<string>? omitMembers, IProgress<double>? progress = null) =>
        new AktWriter(doc, progress, omitMembers).WriteDocument();

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
        foreach (var c in def.Chain())
            foreach (var m in c.Members)
            {
                if (Omitted(m.Name))
                {
                    if (cursor < obj.FieldCount && obj.MemberAt(cursor).Name == m.Name) cursor++;
                    continue;
                }
                WriteValue(b, m.Type, Field(obj, ref cursor, c.Key, m.Name));
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
                var list = (List<object?>)v!;
                b.U32((uint)list.Count);
                foreach (var e in list) WriteValue(b, t.Element!, e);
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
