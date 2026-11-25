using System.Buffers.Binary;

namespace SloppyAnimatsu.Core.Akt;

/// <summary>
/// The fields of one container (an object or a struct value) frozen into a compact byte encoding,
/// for undo. A copied value tree costs 100-200 bytes per struct (the struct, its value array and a
/// box per number); this holds a few bytes per value, so a step that rewrites every key of a large
/// scene keeps tens or hundreds of megabytes instead of gigabytes.
/// </summary>
/// <remarks>
/// Lossless for every value kind of the generic tree. Strings, member layouts, reference targets
/// and (when asked) bulk arrays are not copied: they go into a per-snapshot table and are shared
/// with the document, as <see cref="AktStruct.Clone(bool)"/> shares them. A restore decodes fresh
/// structs, lists and arrays, so the document never shares a mutable value with the snapshot.
/// The bytes can be moved to a <see cref="SnapshotSpillFile"/> and are read back when needed.
/// </remarks>
public sealed class AktFieldSnapshot
{
    // Encoded form: the bytes (null once spilled), the table of shared references and the length.
    private byte[]? _data;
    private readonly object[] _table;
    private readonly int _length;
    private SnapshotSpillFile? _spill;
    private long _spillOffset;

    // Shared form (see CaptureShared): the container's layout and a copy of its value array, whose
    // values are the document's own.
    private readonly AktMemberInfo[]? _sharedLayout;
    private readonly object?[]? _sharedValues;

    private AktFieldSnapshot(string typeName, byte[] data, object[] table)
    {
        TypeName = typeName;
        _data = data;
        _table = table;
        _length = data.Length;
    }

    private AktFieldSnapshot(string typeName, AktMemberInfo[] layout, object?[] values)
    {
        TypeName = typeName;
        _table = [];
        _sharedLayout = layout;
        _sharedValues = values;
    }

    /// <summary>The captured container's type name.</summary>
    public string TypeName { get; }

    /// <summary>The length of the encoding in bytes (0 for a shared snapshot).</summary>
    public int EncodedLength => _length;

    /// <summary>Whether this snapshot shares the container's values instead of encoding them (<see cref="CaptureShared"/>).</summary>
    public bool IsShared => _sharedValues != null;

    /// <summary>Whether the bytes live in a spill file rather than in memory.</summary>
    public bool IsSpilled => !IsShared && _data == null;

    /// <summary>
    /// The managed memory this snapshot holds on its own: its bytes (unless spilled), its table of
    /// shared references (or its value array) and the objects. Shared strings, layouts and arrays
    /// are the document's.
    /// </summary>
    public long EstimatedBytes => IsShared
        ? 64 + 24 + 8L * _sharedValues!.Length
        : (_data == null ? 0 : 24 + _data.Length) + 24 + 8L * _table.Length + 64;

    /// <summary>Encodes <paramref name="source"/>'s fields; arrays of <see cref="AktStruct.LargeArray"/> or more elements are shared when <paramref name="shareLargeArrays"/>.</summary>
    public static AktFieldSnapshot Capture(AktFieldContainer source, bool shareLargeArrays)
    {
        var writer = Writer.Rent(shareLargeArrays);
        try
        {
            writer.Container(source);
            return new AktFieldSnapshot(source.TypeName, writer.ToArray(), writer.Table());
        }
        finally
        {
            Writer.Return(writer);
        }
    }

    /// <summary>
    /// A snapshot that shares <paramref name="source"/>'s current values (only the value array is
    /// copied). Valid as long as nobody changes those values in place: for objects whose fields are
    /// only ever replaced, or that nothing can reach any more.
    /// </summary>
    public static AktFieldSnapshot CaptureShared(AktFieldContainer source)
    {
        var layout = source.SharedLayout();
        var values = new object?[layout.Length];
        for (int i = 0; i < values.Length; i++) values[i] = source.ValueAt(i);
        return new AktFieldSnapshot(source.TypeName, layout, values);
    }

    /// <summary>This snapshot in encoded form (itself when it already is).</summary>
    public AktFieldSnapshot Encode(bool shareLargeArrays) =>
        IsShared ? Capture(new AktStruct(TypeName, _sharedLayout!, _sharedValues!), shareLargeArrays) : this;

    /// <summary>A struct with a fresh copy of the captured fields (the shared values themselves for a shared snapshot).</summary>
    public AktStruct ToStruct()
    {
        var (layout, values) = Decode();
        return new AktStruct(TypeName, layout, values);
    }

    /// <summary>Replaces every field of <paramref name="target"/> with a fresh copy of the captured ones.</summary>
    public void RestoreInto(AktFieldContainer target)
    {
        var (layout, values) = Decode();
        target.ResetFields(layout, values);
    }

    /// <summary>
    /// Whether both hold the same values: byte-equal encodings over the same shared references, or
    /// for two shared snapshots the same value instances. Mixed forms are compared encoded.
    /// </summary>
    public bool ContentEquals(AktFieldSnapshot other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (TypeName != other.TypeName) return false;
        if (IsShared && other.IsShared)
        {
            var a = _sharedValues!;
            var b = other._sharedValues!;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!ReferenceEquals(_sharedLayout![i], other._sharedLayout![i]) && _sharedLayout[i].Name != other._sharedLayout[i].Name) return false;
                // Equal values in new instances (an edit that rewrites every list with the same
                // contents) are only equal encoded; without this, Trim kept ~17k unchanged objects of
                // a Normalize Scale step on a 0.8 M-key scene.
                if (!ReferenceEquals(a[i], b[i]) && !Equals(a[i], b[i])) return Encode(true).ContentEquals(other.Encode(true));
            }
            return true;
        }
        if (IsShared) return Encode(true).ContentEquals(other);
        if (other.IsShared) return ContentEquals(other.Encode(true));
        if (_length != other._length || _table.Length != other._table.Length) return false;
        for (int i = 0; i < _table.Length; i++)
            if (!ReferenceEquals(_table[i], other._table[i]) && !(_table[i] is string s && s == other._table[i] as string))
                return false;
        return Bytes().AsSpan().SequenceEqual(other.Bytes());
    }

    /// <summary>Moves the bytes into <paramref name="file"/>; they are read back on the next restore. A shared snapshot stays as it is.</summary>
    public void SpillTo(SnapshotSpillFile file)
    {
        if (IsShared) return;
        var data = Bytes();
        _spillOffset = file.Append(data);
        _spill = file;
        _data = null;
    }

    private byte[] Bytes() => _data ?? _spill!.Read(_spillOffset, _length);

    private (AktMemberInfo[] Layout, object?[] Values) Decode()
    {
        if (IsShared) return (_sharedLayout!, (object?[])_sharedValues!.Clone());
        var reader = new Reader(Bytes(), _table);
        var layout = (AktMemberInfo[])_table[reader.VarInt()];
        var values = new object?[layout.Length];
        for (int i = 0; i < values.Length; i++) values[i] = reader.Value();
        return (layout, values);
    }

    // Value tags. Numbers that AktBox shares (0 and 1 as floats) get their own tag: tangent and
    // key structs are full of them.
    private const byte TNull = 0, TFalse = 1, TTrue = 2, TU8 = 3, TI8 = 4, TU16 = 5, TI16 = 6, TU32 = 7,
        TI32 = 8, TF32 = 9, TF64 = 10, TString = 11, TRef = 12, TRefList = 13, TList = 14, TTuple = 15,
        TMap = 16, TStruct = 17, TPrimitiveArray = 18, TShared = 19, TClonedArray = 20, TI64 = 21,
        TU64 = 22, TF32Zero = 23, TF32One = 24, TMrsColumn = 25, TTangentColumn = 26, TArrayColumn = 27;

    // Element types of primitive arrays, by code.
    private static readonly Type[] ArrayTypes =
    [
        typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
        typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(bool), typeof(char),
    ];

    private static readonly int FOneBits = BitConverter.SingleToInt32Bits(1f);

    private sealed class Writer
    {
        [ThreadStatic] private static Writer? _cached;

        private byte[] _buffer = new byte[64 * 1024];
        private int _position;
        private readonly Dictionary<object, int> _index = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
        private readonly List<object> _table = [];
        private bool _shareLargeArrays;

        public static Writer Rent(bool shareLargeArrays)
        {
            var writer = _cached ?? new Writer();
            _cached = null;
            writer._shareLargeArrays = shareLargeArrays;
            return writer;
        }

        public static void Return(Writer writer)
        {
            writer._position = 0;
            writer._index.Clear();
            writer._strings.Clear();
            writer._table.Clear();
            // A writer that grew for one huge object does not stay that large.
            if (writer._buffer.Length > 16 << 20) writer._buffer = new byte[64 * 1024];
            _cached = writer;
        }

        public byte[] ToArray() => _buffer.AsSpan(0, _position).ToArray();
        public object[] Table() => _table.ToArray();

        public void Container(AktFieldContainer c)
        {
            VarInt(Ref(c.SharedLayout()));
            for (int i = 0; i < c.FieldCount; i++) Value(c.ValueAt(i));
        }

        private int Ref(object o)
        {
            if (_index.TryGetValue(o, out int i)) return i;
            i = _table.Count;
            _table.Add(o);
            _index.Add(o, i);
            return i;
        }

        private int String(string s)
        {
            if (_strings.TryGetValue(s, out int i)) return i;
            i = _table.Count;
            _table.Add(s);
            _strings.Add(s, i);
            return i;
        }

        private void Value(object? v)
        {
            switch (v)
            {
                case null: Byte(TNull); return;
                case AktStruct s:
                    Byte(TStruct);
                    VarInt(String(s.TypeName));
                    Container(s);
                    return;
                case AktReference r: Byte(TRef); Reference(r); return;
                case List<AktReference> refs:
                    Byte(TRefList);
                    VarInt(refs.Count);
                    foreach (var r in refs) Reference(r);
                    return;
                case AktMrsKeyColumn { IsPacked: true } keys: KeyColumn(keys); return;
                case AktTangentColumn { IsPacked: true } tangents: TangentColumn(tangents); return;
                case AktArrayColumn { IsPacked: true } arrays: ArrayColumn(arrays); return;
                case List<object?> or AktPackedList:
                {
                    // A demoted packed list, or a channel list of a tangent view, is stored as a
                    // plain list.
                    var list = (IList<object?>)v;
                    Byte(TList);
                    VarInt(list.Count);
                    for (int i = 0; i < list.Count; i++) Value(list[i]);
                    return;
                }
                case object?[] tuple:   // also matches covariant reference arrays, as AktStruct.CopyValue does
                    Byte(TTuple);
                    VarInt(tuple.Length);
                    foreach (var x in tuple) Value(x);
                    return;
                case AktMap map:
                    Byte(TMap);
                    VarInt(map.Count);
                    foreach (var e in map.Entries) { Value(e.Key); Value(e.Value); }
                    return;
                case Array array: ArrayValue(array); return;
                case string str: Byte(TString); VarInt(String(str)); return;
                case float f:
                    int bits = BitConverter.SingleToInt32Bits(f);
                    if (bits == 0) { Byte(TF32Zero); return; }
                    if (bits == FOneBits) { Byte(TF32One); return; }
                    Byte(TF32); Raw32(bits); return;
                case int i: Byte(TI32); Raw32(i); return;
                case uint u: Byte(TU32); Raw32((int)u); return;
                case bool b: Byte(b ? TTrue : TFalse); return;
                case byte b: Byte(TU8); Byte(b); return;
                case sbyte b: Byte(TI8); Byte((byte)b); return;
                case ushort u: Byte(TU16); Raw16((short)u); return;
                case short s: Byte(TI16); Raw16(s); return;
                case double d: Byte(TF64); Raw64(BitConverter.DoubleToInt64Bits(d)); return;
                case long l: Byte(TI64); Raw64(l); return;
                case ulong u: Byte(TU64); Raw64((long)u); return;
                default:
                    // Anything else is kept as is, as AktStruct.CopyValue keeps it.
                    Byte(TShared); VarInt(Ref(v)); return;
            }
        }

        private void Reference(AktReference r) => RefPair(r.Target, r.RawIndex);

        private void RefPair(AktObject? target, int raw)
        {
            VarInt(target == null ? 0 : Ref(target) + 1);
            VarInt((uint)(raw + 1));   // -1 (none) is the most common value
        }

        // A packed key list: its layout, the float block as raw bits, then per reference slot
        // either one shared (target, raw) pair or one pair per element.
        private void KeyColumn(AktMrsKeyColumn keys)
        {
            int n = keys.Count;
            Byte(TMrsColumn);
            VarInt(Ref(keys.Shape));
            VarInt(n);
            int bytes = n * keys.Stride * sizeof(float);
            Ensure(bytes);
            Buffer.BlockCopy(keys.FloatData, 0, _buffer, _position, bytes);
            _position += bytes;
            for (int slot = 0; slot < keys.Shape.RefCount; slot++)
            {
                var slots = keys.RefSlots(slot);
                bool uniform = slots.IsUniform || n == 0;
                Byte(uniform ? (byte)1 : (byte)0);
                for (int i = 0, count = uniform ? 1 : n; i < count; i++)
                {
                    var (target, raw) = slots.Get(i);
                    RefPair(target, raw);
                }
            }
            // Per records member and element: the float count, then the floats as raw bits.
            for (int slot = 0; slot < keys.Shape.RecordCount; slot++)
            {
                for (int i = 0; i < n; i++)
                {
                    var records = keys.Records(slot, i);
                    VarInt(records.Length);
                    int recordBytes = records.Length * sizeof(float);
                    Ensure(recordBytes);
                    Buffer.BlockCopy(records, 0, _buffer, _position, recordBytes);
                    _position += recordBytes;
                }
            }
        }

        private void TangentColumn(AktTangentColumn tangents)
        {
            int n = tangents.Count;
            Byte(TTangentColumn);
            VarInt(Ref(tangents.Shape));
            VarInt(n);
            for (int i = 0; i < n; i++)
            {
                var modes = tangents.ModesAt(i);
                VarInt(modes.Length);
                foreach (uint mode in modes) VarInt(mode);
            }
        }

        private void ArrayColumn(AktArrayColumn arrays)
        {
            int n = arrays.Count;
            Byte(TArrayColumn);
            VarInt(Ref(arrays.Shape));
            VarInt(n);
            for (int i = 0; i < n; i++)
            {
                var words = arrays.WordsAt(i);
                VarInt(words.Length);
                int bytes = words.Length * 4;
                Ensure(bytes);
                Buffer.BlockCopy(words, 0, _buffer, _position, bytes);
                _position += bytes;
            }
        }

        private void ArrayValue(Array array)
        {
            if (_shareLargeArrays && array.Length >= AktStruct.LargeArray)
            {
                Byte(TShared); VarInt(Ref(array)); return;
            }
            var type = array.GetType().GetElementType()!;
            int code = Array.IndexOf(ArrayTypes, type);
            if (code < 0)
            {
                // Arrays of structs or classes are rare here: hold a copy, cloned again on decode.
                Byte(TClonedArray); VarInt(Ref(array.Clone())); return;
            }
            int bytes = Buffer.ByteLength(array);
            Byte(TPrimitiveArray);
            Byte((byte)code);
            VarInt(array.Length);
            Ensure(bytes);
            Buffer.BlockCopy(array, 0, _buffer, _position, bytes);
            _position += bytes;
        }

        private void Ensure(int more)
        {
            if (_position + more <= _buffer.Length) return;
            long size = Math.Max((long)_buffer.Length * 2, (long)_position + more);
            if (size > Array.MaxLength) size = Array.MaxLength;
            Array.Resize(ref _buffer, (int)size);
        }

        private void Byte(byte b)
        {
            Ensure(1);
            _buffer[_position++] = b;
        }

        private void Raw16(short v)
        {
            Ensure(2);
            BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(_position), v);
            _position += 2;
        }

        private void Raw32(int v)
        {
            Ensure(4);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), v);
            _position += 4;
        }

        private void Raw64(long v)
        {
            Ensure(8);
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_position), v);
            _position += 8;
        }

        private void VarInt(int v) => VarInt((uint)v);

        private void VarInt(uint v)
        {
            Ensure(5);
            while (v >= 0x80)
            {
                _buffer[_position++] = (byte)(v | 0x80);
                v >>= 7;
            }
            _buffer[_position++] = (byte)v;
        }
    }

    private ref struct Reader(byte[] data, object[] table)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private readonly object[] _table = table;
        private int _position;

        public int VarInt()
        {
            uint v = 0;
            int shift = 0;
            byte b;
            do
            {
                b = _data[_position++];
                v |= (uint)(b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return (int)v;
        }

        private short Raw16() { var v = BinaryPrimitives.ReadInt16LittleEndian(_data[_position..]); _position += 2; return v; }
        private int Raw32() { var v = BinaryPrimitives.ReadInt32LittleEndian(_data[_position..]); _position += 4; return v; }
        private long Raw64() { var v = BinaryPrimitives.ReadInt64LittleEndian(_data[_position..]); _position += 8; return v; }

        public object? Value()
        {
            byte tag = _data[_position++];
            switch (tag)
            {
                case TNull: return null;
                case TFalse: return AktBox.Bool(false);
                case TTrue: return AktBox.Bool(true);
                case TU8: return AktBox.Byte(_data[_position++]);
                case TI8: return (sbyte)_data[_position++];
                case TU16: return AktBox.UShort((ushort)Raw16());
                case TI16: return AktBox.Short(Raw16());
                case TU32: return AktBox.UInt((uint)Raw32());
                case TI32: return AktBox.Int(Raw32());
                case TF32: return AktBox.Float(BitConverter.Int32BitsToSingle(Raw32()));
                case TF32Zero: return AktBox.Float(0f);
                case TF32One: return AktBox.Float(1f);
                case TF64: return BitConverter.Int64BitsToDouble(Raw64());
                case TI64: return Raw64();
                case TU64: return (ulong)Raw64();
                case TString: return _table[VarInt()];
                case TShared: return _table[VarInt()];
                case TClonedArray: return ((Array)_table[VarInt()]).Clone();
                case TRef: return Reference();
                case TRefList:
                {
                    int n = VarInt();
                    var refs = new List<AktReference>(n);
                    for (int i = 0; i < n; i++) refs.Add(Reference());
                    return refs;
                }
                case TList:
                {
                    int n = VarInt();
                    var list = new List<object?>(n);
                    for (int i = 0; i < n; i++) list.Add(Value());
                    return list;
                }
                case TMrsColumn: return KeyColumn();
                case TTangentColumn: return TangentColumn();
                case TArrayColumn: return ArrayColumn();
                case TTuple:
                {
                    int n = VarInt();
                    var tuple = new object?[n];
                    for (int i = 0; i < n; i++) tuple[i] = Value();
                    return tuple;
                }
                case TMap:
                {
                    int n = VarInt();
                    var map = new AktMap();
                    for (int i = 0; i < n; i++)
                    {
                        var key = Value();
                        map.Add(key, Value());
                    }
                    return map;
                }
                case TStruct:
                {
                    var typeName = (string)_table[VarInt()];
                    var layout = (AktMemberInfo[])_table[VarInt()];
                    var values = new object?[layout.Length];
                    for (int i = 0; i < values.Length; i++) values[i] = Value();
                    return new AktStruct(typeName, layout, values);
                }
                case TPrimitiveArray:
                {
                    var type = ArrayTypes[_data[_position++]];
                    int n = VarInt();
                    var array = Array.CreateInstance(type, n);
                    int bytes = Buffer.ByteLength(array);
                    _data.Slice(_position, bytes).CopyTo(
                        System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
                            ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(array), bytes));
                    _position += bytes;
                    return array;
                }
                default: throw new InvalidDataException($"Undo snapshot: unknown value tag {tag} at {_position - 1}.");
            }
        }

        private AktReference Reference()
        {
            var (target, raw) = RefPair();
            return new AktReference(target) { RawIndex = raw };
        }

        private (AktObject? Target, int Raw) RefPair()
        {
            int target = VarInt();
            int raw = VarInt() - 1;
            return (target == 0 ? null : (AktObject)_table[target - 1], raw);
        }

        private AktMrsKeyColumn KeyColumn()
        {
            var shape = (AktMrsShape)_table[VarInt()];
            int n = VarInt();
            var floats = new float[n * shape.Stride];
            int bytes = floats.Length * sizeof(float);
            _data.Slice(_position, bytes).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(floats.AsSpan()));
            _position += bytes;
            var refs = new AktRefSlots[shape.RefCount];
            for (int slot = 0; slot < refs.Length; slot++)
            {
                if (_data[_position++] == 1)
                {
                    var (target, raw) = RefPair();
                    refs[slot] = AktRefSlots.Uniform(target, raw);
                    continue;
                }
                var targets = new AktObject?[n];
                var raws = new int[n];
                for (int i = 0; i < n; i++) (targets[i], raws[i]) = RefPair();
                refs[slot] = AktRefSlots.FromArrays(targets, raws);
            }
            var records = new float[shape.RecordCount][][];
            for (int slot = 0; slot < records.Length; slot++)
            {
                var perElement = records[slot] = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    int length = VarInt();
                    if (length == 0) { perElement[i] = []; continue; }
                    var element = perElement[i] = new float[length];
                    int recordBytes = length * sizeof(float);
                    _data.Slice(_position, recordBytes).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(element.AsSpan()));
                    _position += recordBytes;
                }
            }
            return new AktMrsKeyColumn(shape, floats, n, refs, records);
        }

        private AktTangentColumn TangentColumn()
        {
            var shape = (AktTangentShape)_table[VarInt()];
            int n = VarInt();
            var modes = new uint[n][];
            Span<uint> buffer = stackalloc uint[64];
            for (int i = 0; i < n; i++)
            {
                int length = VarInt();
                Span<uint> span = length <= buffer.Length ? buffer[..length] : new uint[length];
                for (int k = 0; k < length; k++) span[k] = (uint)VarInt();
                modes[i] = AktModeInterner.Intern(span);
            }
            return new AktTangentColumn(shape, modes, n);
        }

        private AktArrayColumn ArrayColumn()
        {
            var shape = (AktArrayShape)_table[VarInt()];
            int n = VarInt();
            var words = new uint[n][];
            for (int i = 0; i < n; i++)
            {
                int length = VarInt();
                var bytes = _data.Slice(_position, length * 4);
                _position += length * 4;
                words[i] = shape.Compose(default, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes));
            }
            return new AktArrayColumn(shape, words, n);
        }
    }
}

/// <summary>
/// One temporary file that holds the bytes of undo steps moved out of memory. It is opened with
/// delete-on-close, so it disappears when the history is cleared or the program ends.
/// </summary>
public sealed class SnapshotSpillFile : IDisposable
{
    private readonly FileStream _stream;

    public SnapshotSpillFile(string? folder = null)
    {
        var path = Path.Combine(folder ?? Path.GetTempPath(), $"SloppyAnimatsu-undo-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
            FileOptions.DeleteOnClose);
        FilePath = path;
    }

    /// <summary>The file's path (for tests and the memory log).</summary>
    public string FilePath { get; }

    /// <summary>The bytes written so far.</summary>
    public long Length => _stream.Length;

    public long Append(byte[] data)
    {
        long offset = _stream.Seek(0, SeekOrigin.End);
        _stream.Write(data, 0, data.Length);
        return offset;
    }

    public byte[] Read(long offset, int length)
    {
        _stream.Flush();
        var data = new byte[length];
        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.ReadExactly(data);
        return data;
    }

    public void Dispose() => _stream.Dispose();
}
