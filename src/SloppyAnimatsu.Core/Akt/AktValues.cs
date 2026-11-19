using System.Collections;
using System.Collections.Concurrent;

namespace SloppyAnimatsu.Core.Akt;

/// <summary>
/// The declaration of one member value: the owning class or struct, the member name and its type.
/// Instances are interned, so every container with the same layout shares them (a scene holds
/// tens of millions of member values but only a few hundred distinct declarations).
/// </summary>
public sealed class AktMemberInfo
{
    public string Owner { get; }
    public string Name { get; }
    public string TypeName { get; }
    /// <summary><see cref="AktNames.Key"/> of <see cref="Owner"/>.</summary>
    public string OwnerKey { get; }
    public AktType Type { get; }

    private AktMemberInfo(string owner, string name, string typeName)
    {
        Owner = owner; Name = name; TypeName = typeName;
        OwnerKey = AktNames.Key(owner);
        Type = AktType.Parse(typeName);
    }

    private static readonly ConcurrentDictionary<(string, string, string), AktMemberInfo> Interned = new();

    public static AktMemberInfo Get(string owner, string name, string typeName) =>
        Interned.GetOrAdd((owner, name, typeName), k => new AktMemberInfo(k.Item1, k.Item2, k.Item3));

    public override string ToString() => $"{Owner}.{Name} : {TypeName}";
}

/// <summary>
/// One member value. <see cref="Owner"/> is the class or struct that declares the member (needed
/// because a chain could declare the same name twice).
/// </summary>
/// <remarks>
/// <para>Value representation by <see cref="AktTypeKind"/>: Bool=bool (or the raw byte when the file
/// holds a value other than 0/1), UChar=byte, Char=sbyte,
/// UShort=ushort, Short=short, UInt/ULong=uint, Int/Long=int, Float=float, Double=double,
/// FloatGroup=float[], RGBAi=byte[4], Atom=string? (null = null atom), WString=string,
/// Reference=<see cref="AktReference"/>, RefArray=List&lt;AktReference&gt;,
/// vector&lt;uchar&gt;=byte[], other Vector=List&lt;object?&gt;, Tuple=object?[],
/// map=see <see cref="AktMap"/>, Struct=<see cref="AktStruct"/>,
/// Polymorphic=<see cref="AktStruct"/>? (null pointer = null).</para>
/// <para>A container does not hold <see cref="AktField"/> objects: it stores its member
/// declarations and values in two arrays. An <see cref="AktField"/> read from
/// <see cref="AktFieldContainer.Fields"/> is a view of one slot, and writing its
/// <see cref="Value"/> writes the container. A field made with the public constructor is
/// standalone until it is added to a container, which then takes it over as a view.</para>
/// </remarks>
public sealed class AktField : IEquatable<AktField>
{
    private AktFieldContainer? _container;
    private int _hint;
    private object? _value;

    public AktMemberInfo Info { get; }
    public string Owner => Info.Owner;
    public string Name => Info.Name;
    public string TypeName => Info.TypeName;
    public AktType Type => Info.Type;

    public AktField(string owner, string name, string typeName, object? value)
    {
        Info = AktMemberInfo.Get(owner, name, typeName);
        _value = value;
    }

    internal AktField(AktFieldContainer container, int index)
    {
        _container = container;
        _hint = index;
        Info = container.MemberAt(index);
        _value = container.ValueAt(index);
    }

    public object? Value
    {
        get
        {
            int i = Locate();
            if (i >= 0) _value = _container!.ValueAt(i);
            return _value;
        }
        set
        {
            _value = value;
            int i = Locate();
            if (i >= 0) _container!.SetValueAt(i, value);
        }
    }

    /// <summary>The slot this field views, or -1 when it is standalone or its slot was removed.</summary>
    internal int Locate()
    {
        var c = _container;
        if (c == null) return -1;
        if (_hint < c.FieldCount && ReferenceEquals(c.MemberAt(_hint), Info)) return _hint;
        int i = c.IndexOfMember(Info);
        if (i < 0)
        {
            _container = null;
            return -1;
        }
        _hint = i;
        return i;
    }

    internal AktFieldContainer? Container => Locate() >= 0 ? _container : null;

    internal object? StandaloneValue => _value;

    internal void Bind(AktFieldContainer container, int index)
    {
        _container = container;
        _hint = index;
    }

    public bool Equals(AktField? other) =>
        other != null && (ReferenceEquals(this, other)
                          || (ReferenceEquals(Info, other.Info) && Container is { } c && ReferenceEquals(c, other.Container)));

    public override bool Equals(object? obj) => Equals(obj as AktField);

    public override int GetHashCode() => Info.GetHashCode();

    public override string ToString() => $"{Name} : {TypeName}";
}

/// <summary>
/// The fields of a container as a list. Reading yields views (see <see cref="AktField"/>); adding
/// a standalone field makes it a view of the new slot, adding a field of another container copies
/// its declaration and value.
/// </summary>
public readonly struct AktFieldList : IList<AktField>, IReadOnlyList<AktField>
{
    private readonly AktFieldContainer _c;

    internal AktFieldList(AktFieldContainer c) => _c = c;

    public int Count => _c.FieldCount;
    public bool IsReadOnly => false;

    public AktField this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_c.FieldCount) throw new ArgumentOutOfRangeException(nameof(index));
            return new AktField(_c, index);
        }
        set
        {
            if ((uint)index >= (uint)_c.FieldCount) throw new ArgumentOutOfRangeException(nameof(index));
            object? v = value.Value;
            bool bind = value.Container == null;
            _c.ReplaceAt(index, value.Info, v);
            if (bind) value.Bind(_c, index);
        }
    }

    public void Add(AktField field) => Insert(_c.FieldCount, field);

    public void AddRange(IEnumerable<AktField> fields)
    {
        // Materialised first: the source may be this list.
        foreach (var f in fields.ToList())
            Add(f);
    }

    public void Insert(int index, AktField field)
    {
        if ((uint)index > (uint)_c.FieldCount) throw new ArgumentOutOfRangeException(nameof(index));
        bool bind = field.Container == null;
        object? v = bind ? field.StandaloneValue : field.Value;
        _c.InsertAt(index, field.Info, v);
        if (bind) field.Bind(_c, index);
    }

    public void Clear() => _c.ClearFields();

    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_c.FieldCount) throw new ArgumentOutOfRangeException(nameof(index));
        _c.RemoveFieldAt(index);
    }

    public int IndexOf(AktField item)
    {
        if (!ReferenceEquals(item.Container, _c)) return -1;
        return item.Locate();
    }

    public bool Contains(AktField item) => IndexOf(item) >= 0;

    public bool Remove(AktField item)
    {
        int i = IndexOf(item);
        if (i < 0) return false;
        RemoveAt(i);
        return true;
    }

    public int RemoveAll(Predicate<AktField> match)
    {
        int removed = 0;
        for (int i = _c.FieldCount - 1; i >= 0; i--)
            if (match(new AktField(_c, i)))
            {
                _c.RemoveFieldAt(i);
                removed++;
            }
        return removed;
    }

    public void CopyTo(AktField[] array, int arrayIndex)
    {
        for (int i = 0; i < _c.FieldCount; i++) array[arrayIndex + i] = new AktField(_c, i);
    }

    public Enumerator GetEnumerator() => new(_c);
    IEnumerator<AktField> IEnumerable<AktField>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<AktField>
    {
        private readonly AktFieldContainer _c;
        private int _i;
        private AktField? _current;

        internal Enumerator(AktFieldContainer c) { _c = c; _i = -1; _current = null; }

        public bool MoveNext()
        {
            if (++_i >= _c.FieldCount) { _current = null; return false; }
            _current = new AktField(_c, _i);
            return true;
        }

        public AktField Current => _current!;
        object IEnumerator.Current => Current;
        public void Reset() { _i = -1; _current = null; }
        public void Dispose() { }
    }
}

/// <summary>
/// Shared boxes for the scalar values a scene repeats millions of times (0, 1, small flags). Boxes
/// are immutable, so one instance per value serves every field; the reader boxes through here.
/// </summary>
public static class AktBox
{
    private const int SmallLimit = 1024;
    private static readonly object True = true, False = false;
    private static readonly object[] Bytes = Enumerable.Range(0, 256).Select(i => (object)(byte)i).ToArray();
    private static readonly object[] UShorts = Enumerable.Range(0, SmallLimit).Select(i => (object)(ushort)i).ToArray();
    private static readonly object[] Shorts = Enumerable.Range(0, SmallLimit).Select(i => (object)(short)i).ToArray();
    private static readonly object[] UInts = Enumerable.Range(0, SmallLimit).Select(i => (object)(uint)i).ToArray();
    private static readonly object[] Ints = Enumerable.Range(0, SmallLimit).Select(i => (object)i).ToArray();
    private static readonly object IntMinusOne = -1;
    private static readonly object FZero = 0f, FOne = 1f, FMinusOne = -1f, FHalf = 0.5f;
    private static readonly int FZeroBits = BitConverter.SingleToInt32Bits(0f), FOneBits = BitConverter.SingleToInt32Bits(1f),
        FMinusOneBits = BitConverter.SingleToInt32Bits(-1f), FHalfBits = BitConverter.SingleToInt32Bits(0.5f);

    public static object Bool(bool v) => v ? True : False;
    public static object Byte(byte v) => Bytes[v];
    public static object UShort(ushort v) => v < SmallLimit ? UShorts[v] : v;
    public static object Short(short v) => v >= 0 && v < SmallLimit ? Shorts[v] : v;
    public static object UInt(uint v) => v < SmallLimit ? UInts[v] : v;
    public static object Int(int v) => v >= 0 && v < SmallLimit ? Ints[v] : v == -1 ? IntMinusOne : v;

    /// <summary>Shares 0, 1, -1 and 0.5 by bit pattern (so -0 and NaN payloads keep their own box).</summary>
    public static object Float(float v)
    {
        int bits = BitConverter.SingleToInt32Bits(v);
        if (bits == FZeroBits) return FZero;
        if (bits == FOneBits) return FOne;
        if (bits == FMinusOneBits) return FMinusOne;
        if (bits == FHalfBits) return FHalf;
        return v;
    }

    /// <summary>The shared box of an already boxed scalar, or the value itself.</summary>
    public static object? Share(object? v) => v switch
    {
        bool b => Bool(b),
        byte b => Byte(b),
        ushort u => UShort(u),
        short s => Short(s),
        uint u => UInt(u),
        int i => Int(i),
        float f => Float(f),
        _ => v,
    };
}

/// <summary>Common base of objects and struct values: an ordered list of member values.</summary>
public abstract class AktFieldContainer
{
    private static readonly AktMemberInfo[] NoMembers = Array.Empty<AktMemberInfo>();
    private static readonly object?[] NoValues = Array.Empty<object?>();

    /// <summary>Class name (objects) or struct name (structs; the concrete type for polymorphic values).</summary>
    public string TypeName { get; set; }

    // The declarations may be shared with other containers of the same layout (the reader and
    // Clone share them); _ownsMembers says whether this container may change the array in place.
    private AktMemberInfo[] _members;
    private object?[] _values;
    private int _count;
    private bool _ownsMembers;

    protected AktFieldContainer(string typeName)
    {
        TypeName = typeName;
        _members = NoMembers;
        _values = NoValues;
    }

    /// <summary>A container over a shared <paramref name="layout"/>; <paramref name="values"/> becomes its own.</summary>
    protected AktFieldContainer(string typeName, AktMemberInfo[] layout, object?[] values)
    {
        if (values.Length != layout.Length) throw new ArgumentException("One value per member is needed.", nameof(values));
        TypeName = typeName;
        _members = layout;
        _values = values;
        _count = layout.Length;
    }

    /// <summary>The fields as a list of views; prefer the indexed accessors below on hot paths.</summary>
    public AktFieldList Fields => new(this);

    public int FieldCount => _count;
    public AktMemberInfo MemberAt(int index) => _members[index];
    public object? ValueAt(int index) => _values[index];
    public void SetValueAt(int index, object? value) => _values[index] = value;

    /// <summary>The members, shared; callers must not change the array.</summary>
    internal AktMemberInfo[] SharedLayout()
    {
        if (_members.Length != _count) Array.Resize(ref _members, _count);
        _ownsMembers = false;
        return _members;
    }

    internal int IndexOfMember(AktMemberInfo info)
    {
        for (int i = 0; i < _count; i++)
            if (ReferenceEquals(_members[i], info)) return i;
        return -1;
    }

    /// <summary>The index of the last member called <paramref name="name"/>, or -1.</summary>
    public int IndexOf(string name)
    {
        // Last match wins: the most derived declaration shadows a base one.
        for (int i = _count - 1; i >= 0; i--)
            if (_members[i].Name == name) return i;
        return -1;
    }

    /// <summary>The index of member <paramref name="name"/> declared by <paramref name="ownerKey"/>, or -1.</summary>
    public int IndexOf(string ownerKey, string name)
    {
        for (int i = 0; i < _count; i++)
            if (_members[i].Name == name && _members[i].OwnerKey == ownerKey) return i;
        return -1;
    }

    public AktField? FindField(string name)
    {
        int i = IndexOf(name);
        return i < 0 ? null : new AktField(this, i);
    }

    public AktField? FindField(string ownerKey, string name)
    {
        int i = IndexOf(ownerKey, name);
        return i < 0 ? null : new AktField(this, i);
    }

    public bool Has(string name) => IndexOf(name) >= 0;

    public object? this[string name]
    {
        get
        {
            int i = IndexOf(name);
            return i < 0 ? null : _values[i];
        }
    }

    public T Get<T>(string name)
    {
        int i = IndexOf(name);
        return i >= 0 && _values[i] is T v ? v : throw new KeyNotFoundException($"{TypeName} has no {typeof(T).Name} field '{name}'.");
    }

    public T GetOr<T>(string name, T fallback)
    {
        int i = IndexOf(name);
        return i >= 0 && _values[i] is T v ? v : fallback;
    }

    /// <summary>Replaces the value of an existing member (the value must use the representation listed on <see cref="AktField"/>).</summary>
    public void Set(string name, object? value)
    {
        int i = IndexOf(name);
        if (i < 0) throw new KeyNotFoundException($"{TypeName} has no field '{name}'.");
        _values[i] = value;
    }

    // ---- structural changes (copy-on-write of the shared declarations) -------------------

    private void OwnMembers(int capacity)
    {
        // Owned arrays keep _values.Length == _members.Length.
        if (_ownsMembers && _members.Length >= capacity) return;
        int size = capacity <= _count ? _count : Math.Max(capacity, Math.Max(4, _count * 2));
        var members = new AktMemberInfo[size];
        Array.Copy(_members, members, _count);
        _members = members;
        if (_values.Length != size)
        {
            var values = new object?[size];
            Array.Copy(_values, values, _count);
            _values = values;
        }
        _ownsMembers = true;
    }

    internal void InsertAt(int index, AktMemberInfo info, object? value)
    {
        OwnMembers(_count + 1);
        if (index < _count)
        {
            Array.Copy(_members, index, _members, index + 1, _count - index);
            Array.Copy(_values, index, _values, index + 1, _count - index);
        }
        _members[index] = info;
        _values[index] = value;
        _count++;
    }

    internal void ReplaceAt(int index, AktMemberInfo info, object? value)
    {
        if (!ReferenceEquals(_members[index], info))
        {
            OwnMembers(_count);
            _members[index] = info;
        }
        _values[index] = value;
    }

    internal void RemoveFieldAt(int index)
    {
        OwnMembers(_count);
        _count--;
        if (index < _count)
        {
            Array.Copy(_members, index + 1, _members, index, _count - index);
            Array.Copy(_values, index + 1, _values, index, _count - index);
        }
        _members[_count] = null!;
        _values[_count] = null;
    }

    internal void ClearFields()
    {
        _members = NoMembers;
        _values = NoValues;
        _count = 0;
        _ownsMembers = false;
    }

    /// <summary>Replaces all members with <paramref name="layout"/> (shared) and <paramref name="values"/> (taken over).</summary>
    internal void ResetFields(AktMemberInfo[] layout, object?[] values)
    {
        _members = layout;
        _values = values;
        _count = layout.Length;
        _ownsMembers = false;
    }

    /// <summary>The declarations (shared) and a copy of the values, for a container of the same layout.</summary>
    internal (AktMemberInfo[] Layout, object?[] Values) CopyFields(bool deep, bool shareLargeArrays)
    {
        var layout = SharedLayout();
        var values = new object?[_count];
        for (int i = 0; i < _count; i++)
            values[i] = deep ? AktStruct.CopyValue(_values[i], shareLargeArrays) : _values[i];
        return (layout, values);
    }
}

/// <summary>One entry of an <see cref="AktMap"/>.</summary>
public sealed class AktMapEntry
{
    public object? Key { get; set; }
    public object? Value { get; set; }

    public AktMapEntry(object? key, object? value)
    {
        Key = key; Value = value;
    }

    public override string ToString() => $"{Key} = {Value}";
}

/// <summary>
/// A <c>std::map</c> value: a u32 count followed by that many key/value pairs. The entries keep
/// the file's order; <c>std::map</c> writes them sorted by key.
/// </summary>
public sealed class AktMap
{
    public List<AktMapEntry> Entries { get; } = new();

    public int Count => Entries.Count;

    public AktMapEntry? Find(object? key) =>
        Entries.FirstOrDefault(e => Equals(e.Key, key));

    public object? this[object? key] => Find(key)?.Value;

    public void Add(object? key, object? value) => Entries.Add(new AktMapEntry(key, value));

    public bool Remove(object? key)
    {
        int at = Entries.FindIndex(e => Equals(e.Key, key));
        if (at < 0) return false;
        Entries.RemoveAt(at);
        return true;
    }

    /// <summary>Sorts the entries by key with <paramref name="compare"/>, as <c>std::map</c> writes them.</summary>
    public void Sort(Comparison<object?> compare) => Entries.Sort((a, b) => compare(a.Key, b.Key));

    public override string ToString() => $"map[{Entries.Count}]";
}

/// <summary>A struct value (or a polymorphic value, whose <see cref="AktFieldContainer.TypeName"/> is the concrete type).</summary>
public sealed class AktStruct : AktFieldContainer
{
    public AktStruct(string typeName) : base(typeName) { }

    /// <summary>A struct over a shared <paramref name="layout"/> (the reader builds values this way).</summary>
    public AktStruct(string typeName, AktMemberInfo[] layout, object?[] values) : base(typeName, layout, values) { }

    /// <summary>
    /// A deep copy: nested structs, lists and arrays are copied, references point at the same
    /// objects through new <see cref="AktReference"/> instances.
    /// </summary>
    public AktStruct Clone() => Clone(shareLargeArrays: false);

    /// <summary>An array at least this long is treated as bulk data (geometry, weights) by <see cref="Clone(bool)"/>.</summary>
    public const int LargeArray = 256;

    /// <summary>
    /// A copy; with <paramref name="shareLargeArrays"/> the arrays of bulk data are shared instead
    /// of copied. An undo snapshot of a whole character uses that: a rig edit never changes mesh
    /// geometry, and copying it would cost hundreds of megabytes per edit, but the small arrays
    /// that rig edits do write (a matrix, a pose slot) are still copied.
    /// </summary>
    public AktStruct Clone(bool shareLargeArrays)
    {
        var (layout, values) = CopyFields(deep: true, shareLargeArrays);
        return new AktStruct(TypeName, layout, values);
    }

    internal static object? CopyValue(object? value, bool shareLargeArrays)
    {
        switch (value)
        {
            case null: return null;
            case AktStruct s: return s.Clone(shareLargeArrays);
            case AktReference r: return new AktReference(r.Target) { RawIndex = r.RawIndex };
            case List<AktReference> refs:
            {
                var copy = new List<AktReference>(refs.Count);
                foreach (var r in refs) copy.Add(new AktReference(r.Target) { RawIndex = r.RawIndex });
                return copy;
            }
            case List<object?> list:
            {
                var copy = new List<object?>(list.Count);
                foreach (var v in list) copy.Add(CopyValue(v, shareLargeArrays));
                return copy;
            }
            case object?[] tuple:
            {
                var copy = new object?[tuple.Length];
                for (int i = 0; i < tuple.Length; i++) copy[i] = CopyValue(tuple[i], shareLargeArrays);
                return copy;
            }
            case AktMap map: return CopyMap(map, shareLargeArrays);
            case Array array: return shareLargeArrays && array.Length >= LargeArray ? array : array.Clone();
            default: return value;
        }
    }

    private static AktMap CopyMap(AktMap map, bool shareLargeArrays)
    {
        var copy = new AktMap();
        foreach (var e in map.Entries)
            copy.Add(CopyValue(e.Key, shareLargeArrays), CopyValue(e.Value, shareLargeArrays));
        return copy;
    }

    public override string ToString() => TypeName;
}

/// <summary>A node of the object hierarchy.</summary>
public sealed class AktObject : AktFieldContainer
{
    public AktObject? Parent { get; internal set; }
    public List<AktObject> Children { get; } = new();

    public AktObject(string className) : base(className) { }

    /// <summary>An object over a shared <paramref name="layout"/> (the reader builds objects this way).</summary>
    public AktObject(string className, AktMemberInfo[] layout, object?[] values) : base(className, layout, values) { }

    /// <summary><c>CDataObject.Name</c>.</summary>
    public string? Name => this["Name"] as string;

    public string ClassKey => AktNames.Key(TypeName);

    public void AddChild(AktObject child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    /// <summary>Attaches <paramref name="child"/> at place <paramref name="index"/> (clamped) of the child list.</summary>
    public void InsertChild(int index, AktObject child)
    {
        child.Parent = this;
        Children.Insert(Math.Clamp(index, 0, Children.Count), child);
    }

    /// <summary>Detaches <paramref name="child"/>; its sub-tree is kept intact so that it can be put back.</summary>
    public bool RemoveChild(AktObject child)
    {
        if (!Children.Remove(child)) return false;
        child.Parent = null;
        return true;
    }

    /// <summary>Replaces the child list (e.g. when restoring a snapshot), re-parenting each child.</summary>
    public void SetChildren(IEnumerable<AktObject> children)
    {
        var list = children.ToList();
        // A child already put back under another parent (a restored move) keeps that parent.
        foreach (var c in Children)
            if (c.Parent == this) c.Parent = null;
        Children.Clear();
        foreach (var c in list)
        {
            c.Parent = this;
            Children.Add(c);
        }
    }

    /// <summary>
    /// A deep copy of this object and its sub-tree, not attached to any parent. References to
    /// objects inside the sub-tree point at their copies; references outside are kept.
    /// </summary>
    public AktObject CloneHierarchy()
    {
        var map = new Dictionary<AktObject, AktObject>(ReferenceEqualityComparer.Instance);
        AktObject CopyNode(AktObject o)
        {
            var (layout, values) = o.CopyFields(deep: true, shareLargeArrays: false);
            var copy = new AktObject(o.TypeName, layout, values);
            map[o] = copy;
            foreach (var c in o.Children) copy.AddChild(CopyNode(c));
            return copy;
        }
        var root = CopyNode(this);
        foreach (var copy in root.PreOrder())
            for (int i = 0; i < copy.FieldCount; i++)
                Remap(copy.ValueAt(i), map);
        return root;
    }

    /// <summary>Points every reference in <paramref name="value"/> at its copy, when it has one.</summary>
    internal static void Remap(object? value, Dictionary<AktObject, AktObject> map)
    {
        switch (value)
        {
            case AktReference r when r.Target != null && map.TryGetValue(r.Target, out var to):
                r.Target = to;
                break;
            case List<AktReference> refs:
                foreach (var r in refs) Remap(r, map);
                break;
            case AktStruct s:
                for (int i = 0; i < s.FieldCount; i++) Remap(s.ValueAt(i), map);
                break;
            case List<object?> list:
                foreach (var v in list) Remap(v, map);
                break;
            case object?[] tuple:
                foreach (var v in tuple) Remap(v, map);
                break;
            case AktMap m:
                foreach (var e in m.Entries)
                {
                    Remap(e.Key, map);
                    Remap(e.Value, map);
                }
                break;
        }
    }

    /// <summary>This object and all descendants in pre-order (the file's numbering order).</summary>
    public IEnumerable<AktObject> PreOrder()
    {
        var stack = new Stack<AktObject>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            yield return o;
            for (int i = o.Children.Count - 1; i >= 0; i--)
                stack.Push(o.Children[i]);
        }
    }

    public override string ToString() => $"{ClassKey} '{Name}'";
}

/// <summary>
/// A reference to another object. Resolved to <see cref="Target"/> after loading; the writer
/// renumbers from the target, so references survive tree edits.
/// </summary>
public sealed class AktReference
{
    public AktObject? Target { get; set; }
    /// <summary>The index read from the file (-1 = null). Used only when <see cref="Target"/> is unresolved.</summary>
    public int RawIndex { get; set; }

    public AktReference(AktObject? target) { Target = target; RawIndex = -1; }
    public AktReference(int rawIndex) { RawIndex = rawIndex; }

    public bool IsNull => Target == null && RawIndex < 0;

    public override string ToString() => Target?.ToString() ?? (RawIndex < 0 ? "null" : $"#{RawIndex} (unresolved)");
}
