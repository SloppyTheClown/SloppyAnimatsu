using System.Collections;

namespace SloppyAnimatsu.Core.Akt;

/// <summary>
/// Compact storage for the two value lists that make up most of an animated scene: key values
/// (a struct of floats plus references) and per-key tangent modes. A packed list keeps its
/// elements in flat arrays and hands out <see cref="AktStruct"/> views on demand, so code that
/// walks the generic value tree keeps working. A value that a packed list cannot hold turns it
/// back into a plain list of structs (it is "demoted"), so every edit stays correct.
/// </summary>
public static class AktPackedKeys
{
    /// <summary>
    /// Whether the reader packs key lists. <c>SLOPPYANIMATSU_PACKED_KEYS=0</c> turns it off, so a
    /// suspect result can be compared with the plain value tree.
    /// </summary>
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("SLOPPYANIMATSU_PACKED_KEYS") != "0";
}

/// <summary>
/// A vector value stored packed. It behaves as an <see cref="IList{T}"/> of element structs: an
/// element is a view, created on first access and kept, so the same element always returns the
/// same object and follows its element through inserts and removals. A view that leaves the list
/// keeps its values. Once demoted, the list forwards to a plain <see cref="List{T}"/>.
/// </summary>
public abstract class AktPackedList : IList<object?>, IReadOnlyList<object?>
{
    private List<object?>? _plain;
    // Cached views by element index; null or at least as long as the element count.
    private AktPackedView?[]? _views;

    /// <summary>False once the list has been turned into a plain list.</summary>
    public bool IsPacked => _plain == null;

    private protected abstract int SlotCount { get; }

    public int Count => _plain?.Count ?? SlotCount;

    public bool IsReadOnly => false;

    /// <summary>The concrete type name of the elements while packed (for schema selection), or null.</summary>
    public abstract string? ElementTypeName { get; }

    // ---- the storage of a concrete list --------------------------------------------------

    private protected abstract AktPackedView NewView(int index);

    /// <summary>Whether <paramref name="value"/> fits the storage (checked before anything changes).</summary>
    private protected abstract bool CanStore(object? value);

    /// <summary>Whether a detached view can be bound again after its values were stored.</summary>
    private protected abstract bool CanRebind(AktPackedView view);

    /// <summary>Stores a value that <see cref="CanStore"/> accepted, at a new slot when <paramref name="insert"/>.</summary>
    private protected abstract void StoreSlot(int index, object? value, bool insert);

    private protected abstract void RemoveSlot(int index);

    private protected abstract void ClearSlots();

    private protected abstract void PermuteSlots(int[] order);

    private protected abstract AktPackedList ClonePacked();

    private protected virtual void BeforeDemote() { }

    /// <summary>
    /// True for a list of scalars (a <c>vector&lt;uint&gt;</c>): its elements are boxed values, not
    /// views, so <see cref="ScalarAt"/> answers the indexer and nothing is cached.
    /// </summary>
    private protected virtual bool HoldsScalars => false;

    private protected virtual object? ScalarAt(int index) => throw new InvalidOperationException("Not a scalar list.");

    // ---- IList ----------------------------------------------------------------------------

    public object? this[int index]
    {
        get
        {
            if (_plain != null) return _plain[index];
            int n = SlotCount;
            CheckIndex(index, n);
            if (HoldsScalars) return ScalarAt(index);
            var views = Views(n);
            return views[index] ??= NewView(index);
        }
        set
        {
            if (_plain != null) { _plain[index] = value; return; }
            CheckIndex(index, SlotCount);
            Put(index, value, insert: false);
        }
    }

    public void Insert(int index, object? item)
    {
        if (_plain != null) { _plain.Insert(index, item); return; }
        CheckIndex(index, SlotCount + 1);
        Put(index, item, insert: true);
    }

    public void Add(object? item) => Insert(Count, item);

    public void RemoveAt(int index)
    {
        if (_plain != null) { _plain.RemoveAt(index); return; }
        int n = SlotCount;
        CheckIndex(index, n);
        if (_views != null)
        {
            _views[index]?.Detach();
            Array.Copy(_views, index + 1, _views, index, n - 1 - index);
            _views[n - 1] = null;
            for (int i = index; i < n - 1; i++)
                if (_views[i] is { } v) v.Index = i;
        }
        RemoveSlot(index);
    }

    public void Clear()
    {
        if (_plain != null) { _plain.Clear(); return; }
        if (_views != null)
            for (int i = 0; i < SlotCount; i++) _views[i]?.Detach();
        _views = null;
        ClearSlots();
    }

    public int IndexOf(object? item)
    {
        if (_plain != null) return _plain.IndexOf(item);
        if (HoldsScalars)
        {
            for (int i = 0, n = SlotCount; i < n; i++)
                if (Equals(ScalarAt(i), item)) return i;
            return -1;
        }
        return item is AktPackedView v && ReferenceEquals(v.Owner, this) ? v.Index : -1;
    }

    public bool Contains(object? item) => IndexOf(item) >= 0;

    public bool Remove(object? item)
    {
        int i = IndexOf(item);
        if (i < 0) return false;
        RemoveAt(i);
        return true;
    }

    public void CopyTo(object?[] array, int arrayIndex)
    {
        for (int i = 0; i < Count; i++) array[arrayIndex + i] = this[i];
    }

    public IEnumerator<object?> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---- whole-list operations ------------------------------------------------------------

    /// <summary>
    /// Reorders the list so that new element i is old element <paramref name="order"/>[i]. A
    /// permutation moves the packed data and the views together; anything else (a repeated
    /// index) is done on a plain list, as <see cref="List{T}"/> would.
    /// </summary>
    public void Permute(IReadOnlyList<int> order)
    {
        int n = Count;
        if (order.Count != n) return;
        if (_plain == null && !IsPermutation(order, n)) Demote();
        if (_plain != null)
        {
            var old = _plain.ToArray();
            for (int i = 0; i < n; i++) _plain[i] = old[order[i]];
            return;
        }
        var o = order.ToArray();
        PermuteSlots(o);
        if (_views != null)
        {
            var old = (AktPackedView?[])_views.Clone();
            for (int i = 0; i < n; i++)
            {
                _views[i] = old[o[i]];
                if (_views[i] is { } v) v.Index = i;
            }
        }
    }

    private static bool IsPermutation(IReadOnlyList<int> order, int n)
    {
        var seen = new bool[n];
        foreach (int i in order)
        {
            if ((uint)i >= (uint)n || seen[i]) return false;
            seen[i] = true;
        }
        return true;
    }

    /// <summary>A copy that shares no element with this list; references point at the same objects.</summary>
    public IList<object?> CloneList(bool shareLargeArrays)
    {
        if (_plain == null) return ClonePacked();
        var copy = new List<object?>(_plain.Count);
        foreach (var v in _plain) copy.Add(AktStruct.CopyValue(v, shareLargeArrays));
        return copy;
    }

    /// <summary>Turns the list into a plain list of structs; views keep their identity and values.</summary>
    internal void Demote()
    {
        if (_plain != null) return;
        BeforeDemote();
        int n = SlotCount;
        var list = new List<object?>(n);
        for (int i = 0; i < n; i++)
        {
            if (HoldsScalars) { list.Add(ScalarAt(i)); continue; }
            var v = _views?[i] ?? NewView(i);
            v.Detach();
            list.Add(v);
        }
        _views = null;
        ClearSlots();
        _plain = list;
    }

    private void Put(int index, object? value, bool insert)
    {
        if (value is AktPackedView own && ReferenceEquals(own.Owner, this))
        {
            if (!insert && own.Index == index) return;
            // The same element twice (or moved by hand): only a plain list can alias it.
            DemoteAndPut(index, value, insert);
            return;
        }
        if (!CanStore(value))
        {
            DemoteAndPut(index, value, insert);
            return;
        }
        int n = SlotCount;
        if (insert)
        {
            if (_views != null)
            {
                if (_views.Length < n + 1) Array.Resize(ref _views, Math.Max(n + 1, _views.Length * 2));
                Array.Copy(_views, index, _views, index + 1, n - index);
                _views[index] = null;
                for (int i = index + 1; i <= n; i++)
                    if (_views[i] is { } v) v.Index = i;
            }
        }
        else if (_views?[index] is { } old)
        {
            old.Detach();
            _views[index] = null;
        }
        StoreSlot(index, value, insert);
        if (value is AktPackedView view && view.Owner == null && CanRebind(view))
        {
            var views = Views(SlotCount);
            view.Owner = this;
            view.Index = index;
            view.Rebind();
            views[index] = view;
        }
    }

    private void DemoteAndPut(int index, object? value, bool insert)
    {
        Demote();
        if (insert) _plain!.Insert(index, value);
        else _plain![index] = value;
    }

    private AktPackedView?[] Views(int n)
    {
        if (_views == null) _views = new AktPackedView?[Math.Max(n, 4)];
        else if (_views.Length < n) Array.Resize(ref _views, Math.Max(n, _views.Length * 2));
        return _views;
    }

    private static void CheckIndex(int index, int limit)
    {
        if ((uint)index >= (uint)limit) throw new ArgumentOutOfRangeException(nameof(index));
    }

    public override string ToString() => $"packed[{Count}]";
}

/// <summary>An element struct of an <see cref="AktPackedList"/>, reading and writing its slot.</summary>
public abstract class AktPackedView : AktStruct
{
    /// <summary>The list whose slot this view shows; null once detached (it then holds its own values).</summary>
    internal AktPackedList? Owner;
    internal int Index;

    private protected AktPackedView(string typeName, AktMemberInfo[] layout, AktPackedList owner, int index)
        : base(typeName, layout, packed: true)
    {
        Owner = owner;
        Index = index;
    }

    /// <summary>Takes the slot's values as its own and leaves the list.</summary>
    internal abstract void Detach();

    /// <summary>After <see cref="Owner"/>/<see cref="Index"/> were set: drops its own values and shows the slot again.</summary>
    internal abstract void Rebind();

    private protected override void Unpack() => Owner?.Demote();
}

/// <summary>
/// One reference per element. While every element holds the same reference (the owning track,
/// mostly), only that one is stored.
/// </summary>
internal sealed class AktRefSlots
{
    private AktObject? _target;
    private int _raw = -1;
    private AktObject?[]? _targets;
    private int[]? _raws;

    public bool IsUniform => _targets == null;

    public static AktRefSlots Uniform(AktObject? target, int raw) => new() { _target = target, _raw = raw };

    /// <summary>Slots that take the two arrays (one entry per element) as their own.</summary>
    public static AktRefSlots FromArrays(AktObject?[] targets, int[] raws) => new() { _targets = targets, _raws = raws };

    public (AktObject? Target, int Raw) Get(int i) => _targets == null ? (_target, _raw) : (_targets[i], _raws![i]);

    public void Set(int i, int count, AktObject? target, int raw)
    {
        if (_targets == null)
        {
            if (count <= 1 || (ReferenceEquals(target, _target) && raw == _raw))
            {
                _target = target;
                _raw = raw;
                return;
            }
            Expand(count, count);
        }
        _targets![i] = target;
        _raws![i] = raw;
    }

    public void Insert(int i, int countBefore, AktObject? target, int raw)
    {
        if (_targets == null)
        {
            if (countBefore == 0)
            {
                _target = target;
                _raw = raw;
                return;
            }
            if (ReferenceEquals(target, _target) && raw == _raw) return;
            Expand(countBefore, countBefore + 1);
        }
        if (_targets!.Length <= countBefore)
        {
            int size = Math.Max(countBefore + 1, _targets.Length * 2);
            Array.Resize(ref _targets, size);
            Array.Resize(ref _raws, size);
        }
        Array.Copy(_targets, i, _targets, i + 1, countBefore - i);
        Array.Copy(_raws!, i, _raws!, i + 1, countBefore - i);
        _targets[i] = target;
        _raws![i] = raw;
    }

    public void RemoveAt(int i, int countBefore)
    {
        if (_targets == null) return;
        Array.Copy(_targets, i + 1, _targets, i, countBefore - 1 - i);
        Array.Copy(_raws!, i + 1, _raws!, i, countBefore - 1 - i);
        _targets[countBefore - 1] = null;
    }

    public void Clear()
    {
        _target = null;
        _raw = -1;
        _targets = null;
        _raws = null;
    }

    public void Permute(int[] order, int count)
    {
        if (_targets == null) return;
        var t = new AktObject?[Math.Max(count, 1)];
        var r = new int[t.Length];
        for (int i = 0; i < count; i++)
        {
            t[i] = _targets[order[i]];
            r[i] = _raws![order[i]];
        }
        _targets = t;
        _raws = r;
    }

    public AktRefSlots Clone(int count)
    {
        var c = new AktRefSlots { _target = _target, _raw = _raw };
        if (_targets != null)
        {
            c._targets = _targets.AsSpan(0, count).ToArray();
            c._raws = _raws!.AsSpan(0, count).ToArray();
        }
        return c;
    }

    /// <summary>Replaces every (target, raw index) pair with <paramref name="map"/>'s result; true when any changed.</summary>
    public bool Map(int count, Func<AktObject?, int, (AktObject? Target, int Raw)> map)
    {
        if (count == 0) return false;
        if (_targets == null)
        {
            var (t, r) = map(_target, _raw);
            if (ReferenceEquals(t, _target) && r == _raw) return false;
            _target = t;
            _raw = r;
            return true;
        }
        bool changed = false;
        for (int i = 0; i < count; i++)
        {
            var (t, r) = map(_targets[i], _raws![i]);
            if (ReferenceEquals(t, _targets[i]) && r == _raws[i]) continue;
            _targets[i] = t;
            _raws[i] = r;
            changed = true;
        }
        return changed;
    }

    public bool Any(int count, Func<AktObject?, bool> test)
    {
        if (count == 0) return false;
        if (_targets == null) return test(_target);
        for (int i = 0; i < count; i++)
            if (test(_targets[i])) return true;
        return false;
    }

    /// <summary>Points unresolved raw indices at the loaded objects, as the reader does for references.</summary>
    public void Resolve(int count, List<AktObject> objects)
    {
        if (count == 0) return;
        if (_targets == null)
        {
            if (_target == null && _raw >= 0 && _raw < objects.Count) _target = objects[_raw];
            return;
        }
        for (int i = 0; i < count; i++)
            if (_targets[i] == null && _raws![i] >= 0 && _raws[i] < objects.Count) _targets[i] = objects[_raws[i]];
    }

    private void Expand(int count, int capacity)
    {
        _targets = new AktObject?[Math.Max(capacity, 4)];
        _raws = new int[_targets.Length];
        Array.Fill(_targets, _target, 0, count);
        Array.Fill(_raws, _raw, 0, count);
    }
}

// ==== key values ==========================================================================

/// <summary>A struct whose members are all floats or float groups, stored as one run of floats.</summary>
internal sealed class AktFloatStruct
{
    internal readonly string Name;
    internal readonly AktMemberInfo[] Layout;
    /// <summary>Per member: its first float, and its float count (0 = a scalar float).</summary>
    internal readonly int[] Offset, Width;
    internal readonly int Stride;

    private AktFloatStruct(string name, AktMemberInfo[] layout, int[] offset, int[] width, int stride)
    {
        Name = name; Layout = layout; Offset = offset; Width = width; Stride = stride;
    }

    internal static AktFloatStruct? TryCreate(string name, AktMemberInfo[] layout)
    {
        if (layout.Length == 0) return null;
        var offset = new int[layout.Length];
        var width = new int[layout.Length];
        int stride = 0;
        for (int k = 0; k < layout.Length; k++)
        {
            var t = layout[k].Type;
            offset[k] = stride;
            if (t.Kind == AktTypeKind.Float) stride++;
            else if (t.Kind == AktTypeKind.FloatGroup && t.Count > 0) { width[k] = t.Count; stride += t.Count; }
            else return null;
        }
        return new AktFloatStruct(name, layout, offset, width, stride);
    }

    internal bool SameAs(AktFloatStruct o) =>
        ReferenceEquals(this, o) || (Name == o.Name && Layout.AsSpan().SequenceEqual(o.Layout));

    /// <summary>The index of the member called <paramref name="name"/>, or -1.</summary>
    internal int IndexOf(string name)
    {
        for (int k = Layout.Length - 1; k >= 0; k--)
            if (Layout[k].Name == name) return k;
        return -1;
    }

    /// <summary>Whether <paramref name="value"/> is a struct of this layout with values of the right widths.</summary>
    internal bool CanHold(object? value)
    {
        if (value is not AktStruct s || s.TypeName != Name || s.FieldCount != Layout.Length) return false;
        for (int k = 0; k < Layout.Length; k++)
        {
            if (!ReferenceEquals(s.MemberAt(k), Layout[k])) return false;
            var v = s.ValueAt(k);
            if (Width[k] == 0 ? v is not float : v is not float[] a || a.Length != Width[k]) return false;
        }
        return true;
    }

    /// <summary>Copies the floats of <paramref name="s"/> (checked by <see cref="CanHold"/>) into <paramref name="dst"/>.</summary>
    internal void Read(AktStruct s, Span<float> dst)
    {
        for (int k = 0; k < Layout.Length; k++)
        {
            int w = Width[k];
            if (w == 0) dst[Offset[k]] = (float)s.ValueAt(k)!;
            else ((float[])s.ValueAt(k)!).AsSpan(0, w).CopyTo(dst.Slice(Offset[k], w));
        }
    }

    internal object? Get(ReadOnlySpan<float> src, int k)
    {
        int w = Width[k];
        return w == 0 ? AktBox.Float(src[Offset[k]]) : src.Slice(Offset[k], w).ToArray();
    }

    internal bool TrySet(Span<float> dst, int k, object? value)
    {
        int w = Width[k];
        if (w == 0)
        {
            if (value is not float f) return false;
            dst[Offset[k]] = f;
            return true;
        }
        if (value is not float[] a || a.Length != w) return false;
        a.CopyTo(dst.Slice(Offset[k], w));
        return true;
    }

    /// <summary>Whether <paramref name="value"/> is a list whose elements all fit this layout.</summary>
    internal bool CanHoldAll(object? value)
    {
        if (value is RecordListView { IsPacked: true } packed) return packed.Struct.SameAs(this);
        if (value is not IList<object?> list) return false;
        for (int i = 0; i < list.Count; i++)
            if (!CanHold(list[i])) return false;
        return true;
    }

    /// <summary>The floats of a list accepted by <see cref="CanHoldAll"/>; do not change the array.</summary>
    internal float[] ReadAll(object? value)
    {
        if (value is RecordListView { IsPacked: true } packed) return packed.Floats;
        var list = (IList<object?>)value!;
        if (list.Count == 0) return [];
        var floats = new float[list.Count * Stride];
        for (int i = 0; i < list.Count; i++) Read((AktStruct)list[i]!, floats.AsSpan(i * Stride, Stride));
        return floats;
    }
}

/// <summary>How a member of a packed key is stored.</summary>
internal enum MrsPart : byte
{
    /// <summary>A reference, in an <see cref="AktRefSlots"/>.</summary>
    Ref,
    /// <summary>A struct of floats, inside the element's floats.</summary>
    Value,
    /// <summary>A scalar float, inside the element's floats.</summary>
    Float,
    /// <summary>A scalar uint, kept as its raw bits inside the element's floats.</summary>
    UInt,
    /// <summary>A vector of float structs: per element one float array of count x stride.</summary>
    Records,
}

/// <summary>
/// The element layout a <see cref="AktMrsKeyColumn"/> holds: a polymorphic key with one struct of
/// floats (its key value) and any number of references. Keys of the IK family may also hold more
/// float structs, scalar floats and uints, and vectors of float structs.
/// </summary>
public sealed class AktMrsShape
{
    internal readonly string TypeName;
    internal readonly AktMemberInfo[] Outer;
    /// <summary>Per outer member: how it is stored.</summary>
    internal readonly MrsPart[] Part;
    /// <summary>Per outer member: its first float in the element (Value, Float, UInt), or its slot (Ref, Records).</summary>
    internal readonly int[] At;
    /// <summary>Per outer member: the struct of a Value or Records member, else null.</summary>
    internal readonly AktFloatStruct?[] Struct;
    /// <summary>Per outer member: its reference slot, or -1.</summary>
    internal readonly int[] RefSlot;
    /// <summary>The first Value member: the key value.</summary>
    internal readonly int KeyMember;
    internal readonly int Stride, RefCount, RecordCount;

    private AktMrsShape(string typeName, AktMemberInfo[] outer, MrsPart[] part, int[] at, AktFloatStruct?[] structs,
        int[] refSlot, int keyMember, int stride, int refCount, int recordCount)
    {
        TypeName = typeName; Outer = outer; Part = part; At = at; Struct = structs; RefSlot = refSlot;
        KeyMember = keyMember; Stride = stride; RefCount = refCount; RecordCount = recordCount;
    }

    /// <summary>The key value's struct.</summary>
    internal AktFloatStruct Key => Struct[KeyMember]!;

    internal string InnerType => Key.Name;

    internal AktMemberInfo[] Inner => Key.Layout;

    /// <summary>The shape of <paramref name="outer"/>, or null when it is not one this column holds.</summary>
    internal static AktMrsShape? TryCreate(string typeName, AktMemberInfo[] outer,
        Func<AktType, (string Name, AktMemberInfo[] Layout)?> structLayout)
    {
        // Only the IK family's keys carry the extra member kinds; other keys keep the plain layout.
        bool ikFamily = AktNames.PolymorphicChain(AktNames.Key(typeName)).Contains(ControllerSchema.IkKeyStruct);
        var part = new MrsPart[outer.Length];
        var at = new int[outer.Length];
        var structs = new AktFloatStruct?[outer.Length];
        var refSlot = new int[outer.Length];
        int key = -1, stride = 0, refs = 0, records = 0;
        for (int m = 0; m < outer.Length; m++)
        {
            var t = outer[m].Type;
            refSlot[m] = -1;
            switch (t.Kind)
            {
                case AktTypeKind.Reference:
                    part[m] = MrsPart.Ref;
                    at[m] = refSlot[m] = refs++;
                    break;
                case AktTypeKind.Struct when key < 0 || ikFamily:
                    if (FloatStruct(t, structLayout) is not { } value) return null;
                    part[m] = MrsPart.Value;
                    structs[m] = value;
                    at[m] = stride;
                    stride += value.Stride;
                    if (key < 0) key = m;
                    break;
                case AktTypeKind.Float when ikFamily:
                    part[m] = MrsPart.Float;
                    at[m] = stride++;
                    break;
                case AktTypeKind.UInt when ikFamily:
                    part[m] = MrsPart.UInt;
                    at[m] = stride++;
                    break;
                case AktTypeKind.Vector when ikFamily && t.Element is { Kind: AktTypeKind.Struct } e:
                    if (FloatStruct(e, structLayout) is not { } record) return null;
                    part[m] = MrsPart.Records;
                    structs[m] = record;
                    at[m] = records++;
                    break;
                default:
                    return null;
            }
        }
        if (key < 0) return null;
        return new AktMrsShape(typeName, outer, part, at, structs, refSlot, key, stride, refs, records);
    }

    private static AktFloatStruct? FloatStruct(AktType t, Func<AktType, (string Name, AktMemberInfo[] Layout)?> structLayout) =>
        structLayout(t) is { } s ? AktFloatStruct.TryCreate(s.Name, s.Layout) : null;

    internal bool SameAs(AktMrsShape o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (TypeName != o.TypeName || !Outer.AsSpan().SequenceEqual(o.Outer)) return false;
        for (int m = 0; m < Struct.Length; m++)
            if (Struct[m] is { } s && (o.Struct[m] is not { } os || !s.SameAs(os))) return false;
        return true;
    }

    /// <summary>The index of the outer member called <paramref name="name"/>, or -1.</summary>
    internal int MemberIndex(string name)
    {
        for (int m = Outer.Length - 1; m >= 0; m--)
            if (Outer[m].Name == name) return m;
        return -1;
    }
}

/// <summary>
/// A list of key values (<c>CKeyMRS</c> and keys of the same shape): the fixed floats of all keys
/// in one array, the references in <see cref="AktRefSlots"/>, and per vector member one float
/// array per key.
/// </summary>
public sealed class AktMrsKeyColumn : AktPackedList
{
    internal readonly AktMrsShape Shape;
    private float[] _f;
    private int _n;
    private readonly AktRefSlots[] _refs;
    // Per Records member: each element's records. An array is never changed in place once
    // stored, so copies of the column can share it.
    private readonly float[][][] _rec;

    internal AktMrsKeyColumn(AktMrsShape shape, int capacity)
    {
        Shape = shape;
        _f = new float[capacity * shape.Stride];
        _refs = new AktRefSlots[shape.RefCount];
        for (int i = 0; i < _refs.Length; i++) _refs[i] = new AktRefSlots();
        _rec = new float[shape.RecordCount][][];
        for (int i = 0; i < _rec.Length; i++) _rec[i] = new float[capacity][];
    }

    internal AktMrsKeyColumn(AktMrsShape shape, float[] floats, int count, AktRefSlots[] refs, float[][][] records)
    {
        Shape = shape;
        _f = floats;
        _n = count;
        _refs = refs;
        _rec = records;
    }

    public override string? ElementTypeName => IsPacked ? Shape.TypeName : null;

    private protected override int SlotCount => _n;

    /// <summary>The fixed floats of all elements, <see cref="AktMrsShape.Stride"/> per element (only the first Count * Stride are used).</summary>
    internal float[] FloatData => _f;

    internal int Stride => Shape.Stride;

    internal AktRefSlots RefSlots(int slot) => _refs[slot];

    internal (AktObject? Target, int Raw) Ref(int slot, int index) => _refs[slot].Get(index);

    internal void SetRef(int slot, int index, AktObject? target, int raw) => _refs[slot].Set(index, _n, target, raw);

    /// <summary>The records of element <paramref name="index"/> in records slot <paramref name="slot"/>; do not change the array.</summary>
    internal float[] Records(int slot, int index)
    {
        if ((uint)index >= (uint)_n) throw new ArgumentOutOfRangeException(nameof(index));
        return _rec[slot][index] ?? [];
    }

    internal void SetRecords(int slot, int index, float[] floats)
    {
        if ((uint)index >= (uint)_n) throw new ArgumentOutOfRangeException(nameof(index));
        _rec[slot][index] = floats;
    }

    // ---- typed access without views (callers check IsPacked) ------------------------------

    /// <summary>The name of the element's value member (e.g. <c>Key</c>).</summary>
    internal string ValueMemberName => Shape.Outer[Shape.KeyMember].Name;

    /// <summary>
    /// The first float of member <paramref name="name"/> of the key value (or of the float struct
    /// <paramref name="member"/>) inside an element's floats, or -1 when there is none or its width
    /// is not <paramref name="width"/> (0 = a scalar float, n = a group of n).
    /// </summary>
    internal int FloatOffset(string name, int width, string? member = null)
    {
        int m = member == null ? Shape.KeyMember : Shape.MemberIndex(member);
        if (m < 0 || Shape.Part[m] != MrsPart.Value) return -1;
        var s = Shape.Struct[m]!;
        int k = s.IndexOf(name);
        return k >= 0 && s.Width[k] == width ? Shape.At[m] + s.Offset[k] : -1;
    }

    /// <summary>The float slot of scalar member <paramref name="member"/> (a float, or a uint kept as raw bits), or -1.</summary>
    internal int ScalarOffset(string member, bool isUInt)
    {
        int m = Shape.MemberIndex(member);
        return m >= 0 && Shape.Part[m] == (isUInt ? MrsPart.UInt : MrsPart.Float) ? Shape.At[m] : -1;
    }

    /// <summary>The uint kept at float slot <paramref name="offset"/> of element <paramref name="index"/>.</summary>
    internal uint UIntAt(int index, int offset) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<float, uint>(ElementFloats(index))[offset];

    internal void SetUIntAt(int index, int offset, uint value) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<float, uint>(ElementFloats(index))[offset] = value;

    /// <summary>Element <paramref name="index"/>'s fixed floats; writes go straight into the list (and its views).</summary>
    internal Span<float> ElementFloats(int index)
    {
        if ((uint)index >= (uint)_n) throw new ArgumentOutOfRangeException(nameof(index));
        return _f.AsSpan(index * Shape.Stride, Shape.Stride);
    }

    /// <summary>The object that reference member <paramref name="member"/> of element <paramref name="index"/> points at; null also when there is no such member.</summary>
    internal AktObject? RefTarget(int index, string member)
    {
        for (int m = 0; m < Shape.Outer.Length; m++)
            if (Shape.RefSlot[m] >= 0 && Shape.Outer[m].Name == member)
                return _refs[Shape.RefSlot[m]].Get(index).Target;
        return null;
    }

    /// <summary>A packed copy of this list; references point at the same objects.</summary>
    internal AktMrsKeyColumn CopyPacked() => (AktMrsKeyColumn)ClonePacked();

    // ---- reader ---------------------------------------------------------------------------

    /// <summary>
    /// The float slots of a new last element; fill them, add its references with
    /// <see cref="AppendRef"/> and its records with <see cref="AppendRecords"/>, then <see cref="EndAppend"/>.
    /// </summary>
    internal Span<float> BeginAppend()
    {
        EnsureCapacity(_n + 1);
        return _f.AsSpan(_n * Shape.Stride, Shape.Stride);
    }

    internal void AppendRef(int slot, int raw) => _refs[slot].Insert(_n, _n, null, raw);

    internal void AppendRecords(int slot, float[] floats) => _rec[slot][_n] = floats;

    internal void EndAppend() => _n++;

    internal void ResolveReferences(List<AktObject> objects)
    {
        foreach (var r in _refs) r.Resolve(_n, objects);
    }

    /// <summary>Maps every reference; true when any changed.</summary>
    internal bool MapReferences(Func<AktObject?, int, (AktObject? Target, int Raw)> map)
    {
        bool changed = false;
        foreach (var r in _refs) changed |= r.Map(_n, map);
        return changed;
    }

    internal bool AnyReference(Func<AktObject?, bool> test) => _refs.Any(r => r.Any(_n, test));

    // ---- AktPackedList --------------------------------------------------------------------

    private protected override AktPackedView NewView(int index) => new MrsKeyView(this, index);

    private protected override bool CanStore(object? value)
    {
        if (value is not AktStruct s || s.TypeName != Shape.TypeName || s.FieldCount != Shape.Outer.Length) return false;
        for (int m = 0; m < Shape.Outer.Length; m++)
        {
            if (!ReferenceEquals(s.MemberAt(m), Shape.Outer[m])) return false;
            var v = s.ValueAt(m);
            bool fits = Shape.Part[m] switch
            {
                MrsPart.Ref => v is AktReference,
                MrsPart.Value => Shape.Struct[m]!.CanHold(v),
                MrsPart.Float => v is float,
                MrsPart.UInt => v is uint,
                _ => Shape.Struct[m]!.CanHoldAll(v),
            };
            if (!fits) return false;
        }
        return true;
    }

    private protected override bool CanRebind(AktPackedView view) =>
        view is MrsKeyView k && k.Shape.SameAs(Shape) && k.HoldsOwnParts();

    private protected override void StoreSlot(int index, object? value, bool insert)
    {
        var s = (AktStruct)value!;
        int stride = Shape.Stride;
        // Read everything first: the value may show another element of this list.
        Span<float> tmp = stackalloc float[stride];
        var tmpBits = System.Runtime.InteropServices.MemoryMarshal.Cast<float, uint>(tmp);
        var refs = new (AktObject? Target, int Raw)[Shape.RefCount];
        var records = Shape.RecordCount == 0 ? null : new float[Shape.RecordCount][];
        for (int m = 0; m < Shape.Outer.Length; m++)
        {
            var v = s.ValueAt(m);
            int at = Shape.At[m];
            switch (Shape.Part[m])
            {
                case MrsPart.Ref:
                    var r = (AktReference)v!;
                    refs[at] = (r.Target, r.RawIndex);
                    break;
                case MrsPart.Value: Shape.Struct[m]!.Read((AktStruct)v!, tmp.Slice(at)); break;
                case MrsPart.Float: tmp[at] = (float)v!; break;
                case MrsPart.UInt: tmpBits[at] = (uint)v!; break;
                case MrsPart.Records: records![at] = Shape.Struct[m]!.ReadAll(v); break;
            }
        }
        if (insert)
        {
            EnsureCapacity(_n + 1);
            Array.Copy(_f, index * stride, _f, (index + 1) * stride, (_n - index) * stride);
            for (int r = 0; r < refs.Length; r++) _refs[r].Insert(index, _n, refs[r].Target, refs[r].Raw);
            foreach (var rec in _rec) Array.Copy(rec, index, rec, index + 1, _n - index);
            _n++;
        }
        else
        {
            for (int r = 0; r < refs.Length; r++) _refs[r].Set(index, _n, refs[r].Target, refs[r].Raw);
        }
        tmp.CopyTo(_f.AsSpan(index * stride));
        for (int r = 0; r < _rec.Length; r++) _rec[r][index] = records![r];
    }

    private protected override void RemoveSlot(int index)
    {
        int stride = Shape.Stride;
        Array.Copy(_f, (index + 1) * stride, _f, index * stride, (_n - index - 1) * stride);
        foreach (var r in _refs) r.RemoveAt(index, _n);
        foreach (var rec in _rec)
        {
            Array.Copy(rec, index + 1, rec, index, _n - index - 1);
            rec[_n - 1] = null!;
        }
        _n--;
    }

    private protected override void ClearSlots()
    {
        _n = 0;
        _f = [];
        foreach (var r in _refs) r.Clear();
        for (int r = 0; r < _rec.Length; r++) _rec[r] = [];
    }

    private protected override void PermuteSlots(int[] order)
    {
        int stride = Shape.Stride;
        var old = _f.AsSpan(0, _n * stride).ToArray();
        for (int i = 0; i < _n; i++)
            old.AsSpan(order[i] * stride, stride).CopyTo(_f.AsSpan(i * stride));
        foreach (var r in _refs) r.Permute(order, _n);
        foreach (var rec in _rec)
        {
            var was = rec.AsSpan(0, _n).ToArray();
            for (int i = 0; i < _n; i++) rec[i] = was[order[i]];
        }
    }

    private protected override AktPackedList ClonePacked() =>
        new AktMrsKeyColumn(Shape, _f.AsSpan(0, _n * Shape.Stride).ToArray(), _n,
            _refs.Select(r => r.Clone(_n)).ToArray(), _rec.Select(r => r.AsSpan(0, _n).ToArray()).ToArray());

    private void EnsureCapacity(int count)
    {
        int need = count * Shape.Stride;
        if (_f.Length < need) Array.Resize(ref _f, Math.Max(need, _f.Length * 2));
        for (int r = 0; r < _rec.Length; r++)
            if (_rec[r].Length < count) Array.Resize(ref _rec[r], Math.Max(count, _rec[r].Length * 2));
    }
}

/// <summary>An element of an <see cref="AktMrsKeyColumn"/>.</summary>
internal sealed class MrsKeyView : AktPackedView
{
    internal readonly AktMrsShape Shape;
    // Per outer member: the view handed out for a struct, reference or vector member.
    private object?[]? _parts;

    public MrsKeyView(AktMrsKeyColumn column, int index) : base(column.Shape.TypeName, column.Shape.Outer, column, index)
    {
        Shape = column.Shape;
    }

    internal AktMrsKeyColumn Column => (AktMrsKeyColumn)Owner!;

    private object PartView(int m)
    {
        _parts ??= new object?[Shape.Outer.Length];
        return _parts[m] ??= Shape.Part[m] switch
        {
            MrsPart.Value => new MrsValueView(this, m),
            MrsPart.Ref => new MrsRefView(this, Shape.At[m]),
            _ => new RecordListView(this, Shape.At[m], Shape.Struct[m]!),
        };
    }

    private protected override object? PackedGet(int m) => Shape.Part[m] switch
    {
        MrsPart.Float => AktBox.Float(Column.ElementFloats(Index)[Shape.At[m]]),
        MrsPart.UInt => AktBox.UInt(Column.UIntAt(Index, Shape.At[m])),
        _ => PartView(m),
    };

    private protected override void PackedSet(int m, object? value)
    {
        var part = _parts?[m];
        int at = Shape.At[m];
        switch (Shape.Part[m])
        {
            case MrsPart.Value:
                if (ReferenceEquals(value, part)) return;
                if (Shape.Struct[m]!.CanHold(value))
                {
                    var s = Shape.Struct[m]!;
                    Span<float> tmp = stackalloc float[s.Stride];
                    s.Read((AktStruct)value!, tmp);
                    tmp.CopyTo(Column.ElementFloats(Index).Slice(at));
                    return;
                }
                break;
            case MrsPart.Ref:
                if (value is AktReference r)
                {
                    if (!ReferenceEquals(value, part)) Column.SetRef(at, Index, r.Target, r.RawIndex);
                    return;
                }
                break;
            case MrsPart.Float:
                if (value is float f) { Column.ElementFloats(Index)[at] = f; return; }
                break;
            case MrsPart.UInt:
                if (value is uint u) { Column.SetUIntAt(Index, at, u); return; }
                break;
            case MrsPart.Records:
                if (ReferenceEquals(value, part)) return;
                if (Shape.Struct[m]!.CanHoldAll(value))
                {
                    var floats = Shape.Struct[m]!.ReadAll(value);
                    // The old list leaves this element with its own records, as a replaced list would.
                    ((RecordListView?)part)?.TakeOwn();
                    _parts![m] = null;
                    Column.SetRecords(at, Index, floats);
                    return;
                }
                break;
        }
        Owner!.Demote();
        SetValueAt(m, value);
    }

    internal override void Detach()
    {
        var values = new object?[Shape.Outer.Length];
        for (int m = 0; m < values.Length; m++)
        {
            var v = PackedGet(m);
            switch (v)
            {
                case MrsValueView value: value.TakeOwn(); break;
                case MrsRefView r: r.TakeOwn(); break;
                case RecordListView records: records.TakeOwn(); break;
            }
            values[m] = v;
        }
        TakeValues(values);
        Owner = null;
    }

    internal override void Rebind()
    {
        ReturnToPacked(Shape.Outer);
        if (_parts == null) return;
        foreach (var part in _parts)
        {
            switch (part)
            {
                case MrsValueView value: value.Rebind(); break;
                case MrsRefView r: r.Bind(); break;
                case RecordListView records: records.ReleaseOwn(); break;
            }
        }
    }

    /// <summary>Whether its members still hold the parts it was detached with (so binding it again loses nothing).</summary>
    internal bool HoldsOwnParts()
    {
        for (int m = 0; m < Shape.Outer.Length; m++)
        {
            if (Shape.Part[m] is MrsPart.Float or MrsPart.UInt) continue;
            var part = _parts?[m];
            if (part == null || !ReferenceEquals(ValueAt(m), part)) return false;
            if (part is RecordListView { IsPacked: false }) return false;
        }
        return true;
    }
}

/// <summary>A float struct member (<c>CMRSKey</c>) of a <see cref="MrsKeyView"/>.</summary>
internal sealed class MrsValueView : AktStruct
{
    private readonly MrsKeyView _outer;
    private readonly AktFloatStruct _struct;
    private readonly int _at;

    public MrsValueView(MrsKeyView outer, int member)
        : base(outer.Shape.Struct[member]!.Name, outer.Shape.Struct[member]!.Layout, packed: true)
    {
        _outer = outer;
        _struct = outer.Shape.Struct[member]!;
        _at = outer.Shape.At[member];
    }

    private Span<float> Floats => _outer.Column.ElementFloats(_outer.Index).Slice(_at, _struct.Stride);

    private protected override object? PackedGet(int k) => _struct.Get(Floats, k);

    private protected override void PackedSet(int k, object? value)
    {
        if (_struct.TrySet(Floats, k, value)) return;
        _outer.Owner!.Demote();
        SetValueAt(k, value);
    }

    private protected override void Unpack() => _outer.Owner?.Demote();

    internal void TakeOwn()
    {
        if (!HoldsPackedValues) return;
        var floats = Floats;
        var values = new object?[FieldCount];
        for (int k = 0; k < values.Length; k++) values[k] = _struct.Get(floats, k);
        TakeValues(values);
    }

    internal void Rebind() => ReturnToPacked(_struct.Layout);
}

/// <summary>A reference member of a <see cref="MrsKeyView"/>; writes go to the column while bound.</summary>
internal sealed class MrsRefView : AktReference
{
    private readonly MrsKeyView _outer;
    private readonly int _slot;
    private bool _bound = true;

    public MrsRefView(MrsKeyView outer, int slot)
    {
        _outer = outer;
        _slot = slot;
    }

    public override AktObject? Target
    {
        get => _bound ? _outer.Column.Ref(_slot, _outer.Index).Target : base.Target;
        set
        {
            if (!_bound) { base.Target = value; return; }
            var column = _outer.Column;
            column.SetRef(_slot, _outer.Index, value, column.Ref(_slot, _outer.Index).Raw);
        }
    }

    public override int RawIndex
    {
        get => _bound ? _outer.Column.Ref(_slot, _outer.Index).Raw : base.RawIndex;
        set
        {
            if (!_bound) { base.RawIndex = value; return; }
            var column = _outer.Column;
            column.SetRef(_slot, _outer.Index, column.Ref(_slot, _outer.Index).Target, value);
        }
    }

    internal void TakeOwn()
    {
        if (!_bound) return;
        var (target, raw) = _outer.Column.Ref(_slot, _outer.Index);
        base.Target = target;
        base.RawIndex = raw;
        _bound = false;
    }

    internal void Bind() => _bound = true;
}

/// <summary>
/// A vector-of-float-structs member (<c>RefArray</c>) of a key: a view of the element's record
/// array while its element is in a column, or a stand-alone packed list (a detached element, a
/// copy). Record arrays are never changed in place; a write stores a changed copy.
/// </summary>
internal sealed class RecordListView : AktPackedList
{
    internal readonly AktFloatStruct Struct;
    private readonly MrsKeyView? _parent;
    private readonly int _slot;
    private float[]? _own;

    public RecordListView(MrsKeyView parent, int slot, AktFloatStruct record)
    {
        _parent = parent;
        _slot = slot;
        Struct = record;
    }

    private RecordListView(AktFloatStruct record, float[] own)
    {
        Struct = record;
        _own = own;
    }

    /// <summary>The records' floats; do not change the array.</summary>
    internal float[] Floats => _own ?? _parent!.Column.Records(_slot, _parent.Index);

    private void SetFloats(float[] floats)
    {
        if (_own != null) _own = floats;
        else _parent!.Column.SetRecords(_slot, _parent.Index, floats);
    }

    internal void TakeOwn() => _own ??= _parent!.Column.Records(_slot, _parent.Index);

    internal void ReleaseOwn()
    {
        if (_parent != null) _own = null;
    }

    internal object? GetMember(int index, int k) => Struct.Get(Floats.AsSpan(index * Struct.Stride, Struct.Stride), k);

    internal bool TrySetMember(int index, int k, object? value)
    {
        var copy = (float[])Floats.Clone();
        if (!Struct.TrySet(copy.AsSpan(index * Struct.Stride, Struct.Stride), k, value)) return false;
        SetFloats(copy);
        return true;
    }

    public override string? ElementTypeName => Struct.Name;

    private protected override int SlotCount => Floats.Length / Struct.Stride;

    private protected override AktPackedView NewView(int index) => new RecordView(this, index);

    private protected override bool CanStore(object? value) => Struct.CanHold(value);

    private protected override bool CanRebind(AktPackedView view) => view is RecordView r && r.Struct.SameAs(Struct);

    private protected override void StoreSlot(int index, object? value, bool insert)
    {
        int s = Struct.Stride;
        Span<float> tmp = stackalloc float[s];
        Struct.Read((AktStruct)value!, tmp);
        var old = Floats;
        float[] copy;
        if (insert)
        {
            copy = new float[old.Length + s];
            old.AsSpan(0, index * s).CopyTo(copy);
            old.AsSpan(index * s).CopyTo(copy.AsSpan((index + 1) * s));
        }
        else copy = (float[])old.Clone();
        tmp.CopyTo(copy.AsSpan(index * s));
        SetFloats(copy);
    }

    private protected override void RemoveSlot(int index)
    {
        int s = Struct.Stride;
        var old = Floats;
        if (old.Length == s) { SetFloats([]); return; }
        var copy = new float[old.Length - s];
        old.AsSpan(0, index * s).CopyTo(copy);
        old.AsSpan((index + 1) * s).CopyTo(copy.AsSpan(index * s));
        SetFloats(copy);
    }

    private protected override void ClearSlots() => SetFloats([]);

    private protected override void PermuteSlots(int[] order)
    {
        int s = Struct.Stride;
        var old = Floats;
        var copy = new float[old.Length];
        for (int i = 0; i < order.Length; i++) old.AsSpan(order[i] * s, s).CopyTo(copy.AsSpan(i * s));
        SetFloats(copy);
    }

    private protected override AktPackedList ClonePacked() => new RecordListView(Struct, Floats);

    private protected override void BeforeDemote()
    {
        // A column slot holds only record arrays: the element leaves its column first.
        if (_own == null) _parent!.Owner!.Demote();
    }
}

/// <summary>One record (<c>IKBaseRefInfo</c>) of a <see cref="RecordListView"/>.</summary>
internal sealed class RecordView : AktPackedView
{
    internal readonly AktFloatStruct Struct;

    public RecordView(RecordListView list, int index) : base(list.Struct.Name, list.Struct.Layout, list, index)
    {
        Struct = list.Struct;
    }

    private RecordListView List => (RecordListView)Owner!;

    private protected override object? PackedGet(int k) => List.GetMember(Index, k);

    private protected override void PackedSet(int k, object? value)
    {
        if (List.TrySetMember(Index, k, value)) return;
        Owner!.Demote();
        SetValueAt(k, value);
    }

    internal override void Detach()
    {
        var values = new object?[Struct.Layout.Length];
        for (int k = 0; k < values.Length; k++) values[k] = List.GetMember(Index, k);
        TakeValues(values);
        Owner = null;
    }

    internal override void Rebind() => ReturnToPacked(Struct.Layout);
}

// ==== tangent modes =======================================================================

/// <summary>
/// The element layout a <see cref="AktTangentColumn"/> holds: a polymorphic struct whose only
/// member is a vector of one-<c>uint</c> structs (<c>CTangentInfoList*</c> of <c>CTangentInfo</c>).
/// </summary>
public sealed class AktTangentShape
{
    internal readonly string TypeName, ChannelType;
    internal readonly AktMemberInfo[] Outer, Channel;

    private AktTangentShape(string typeName, AktMemberInfo[] outer, string channelType, AktMemberInfo[] channel)
    {
        TypeName = typeName; Outer = outer; ChannelType = channelType; Channel = channel;
    }

    internal static AktTangentShape? TryCreate(string typeName, AktMemberInfo[] outer,
        Func<AktType, (string Name, AktMemberInfo[] Layout)?> structLayout)
    {
        if (outer.Length != 1 || outer[0].Type.Kind != AktTypeKind.Vector) return null;
        var element = outer[0].Type.Element!;
        if (element.Kind != AktTypeKind.Struct || structLayout(element) is not { } channel) return null;
        if (channel.Layout.Length != 1 || channel.Layout[0].Type.Kind != AktTypeKind.UInt) return null;
        return new AktTangentShape(typeName, outer, channel.Name, channel.Layout);
    }

    internal bool SameAs(AktTangentShape o) =>
        ReferenceEquals(this, o) || (TypeName == o.TypeName && ChannelType == o.ChannelType
                                     && ReferenceEquals(Outer[0], o.Outer[0]) && ReferenceEquals(Channel[0], o.Channel[0]));

    internal bool IsChannel(object? value) =>
        value is AktStruct c && c.TypeName == ChannelType && c.FieldCount == 1
        && ReferenceEquals(c.MemberAt(0), Channel[0]) && c.ValueAt(0) is uint;

    /// <summary>Whether <paramref name="value"/> is a channel vector this shape can store.</summary>
    internal bool CanHoldChannels(object? value)
    {
        if (value is ChannelListView { IsPacked: true } packed) return packed.Shape.SameAs(this);
        if (value is not (List<object?> or AktPackedList)) return false;
        foreach (var c in (IList<object?>)value)
            if (!IsChannel(c)) return false;
        return true;
    }

    /// <summary>The modes of a channel vector <see cref="CanHoldChannels"/> accepted; a packed one's array is shared.</summary>
    internal static uint[] ModesOf(object? value)
    {
        if (value is ChannelListView { IsPacked: true } packed) return packed.Modes;
        var list = (IList<object?>)value!;
        var modes = new uint[list.Count];
        for (int i = 0; i < modes.Length; i++) modes[i] = (uint)((AktStruct)list[i]!).ValueAt(0)!;
        return modes;
    }
}

/// <summary>
/// A list of tangent-mode lists (<c>TangentInfoArray</c>): one mode array per key. The arrays are
/// never changed in place (an edit stores a new one), so equal ones are shared.
/// </summary>
public sealed class AktTangentColumn : AktPackedList
{
    internal readonly AktTangentShape Shape;
    private uint[][] _modes;
    private int _n;

    internal AktTangentColumn(AktTangentShape shape, int capacity)
    {
        Shape = shape;
        _modes = new uint[capacity][];
    }

    internal AktTangentColumn(AktTangentShape shape, uint[][] modes, int count)
    {
        Shape = shape;
        _modes = modes;
        _n = count;
    }

    public override string? ElementTypeName => IsPacked ? Shape.TypeName : null;

    private protected override int SlotCount => _n;

    /// <summary>The modes of element <paramref name="index"/>; do not change the array.</summary>
    internal uint[] ModesAt(int index) => _modes[index];

    internal void SetModes(int index, uint[] modes) => _modes[index] = modes;

    internal void Append(uint[] modes)
    {
        if (_modes.Length == _n) Array.Resize(ref _modes, Math.Max(4, _n * 2));
        _modes[_n++] = modes;
    }

    private protected override AktPackedView NewView(int index) => new TangentListView(this, index);

    private protected override bool CanStore(object? value) =>
        value is AktStruct s && s.TypeName == Shape.TypeName && s.FieldCount == 1
        && ReferenceEquals(s.MemberAt(0), Shape.Outer[0]) && Shape.CanHoldChannels(s.ValueAt(0));

    private protected override bool CanRebind(AktPackedView view) =>
        view is TangentListView t && t.Shape.SameAs(Shape) && t.HoldsOwnParts();

    private protected override void StoreSlot(int index, object? value, bool insert)
    {
        var modes = AktTangentShape.ModesOf(((AktStruct)value!).ValueAt(0));
        if (insert)
        {
            if (_modes.Length == _n) Array.Resize(ref _modes, Math.Max(4, _n * 2));
            Array.Copy(_modes, index, _modes, index + 1, _n - index);
            _n++;
        }
        _modes[index] = modes;
    }

    private protected override void RemoveSlot(int index)
    {
        Array.Copy(_modes, index + 1, _modes, index, _n - index - 1);
        _modes[--_n] = null!;
    }

    private protected override void ClearSlots()
    {
        _n = 0;
        _modes = [];
    }

    private protected override void PermuteSlots(int[] order)
    {
        var old = _modes.AsSpan(0, _n).ToArray();
        for (int i = 0; i < _n; i++) _modes[i] = old[order[i]];
    }

    private protected override AktPackedList ClonePacked() => new AktTangentColumn(Shape, _modes.AsSpan(0, _n).ToArray(), _n);
}

/// <summary>An element of an <see cref="AktTangentColumn"/> (<c>CTangentInfoList*</c>).</summary>
internal sealed class TangentListView : AktPackedView
{
    internal readonly AktTangentShape Shape;
    private ChannelListView? _channels;

    public TangentListView(AktTangentColumn column, int index) : base(column.Shape.TypeName, column.Shape.Outer, column, index)
    {
        Shape = column.Shape;
    }

    internal AktTangentColumn Column => (AktTangentColumn)Owner!;

    private ChannelListView Channels => _channels ??= new ChannelListView(this, Shape, null);

    private protected override object? PackedGet(int m) => Channels;

    private protected override void PackedSet(int m, object? value)
    {
        if (ReferenceEquals(value, _channels)) return;
        if (Shape.CanHoldChannels(value))
        {
            var modes = AktTangentShape.ModesOf(value);
            // The old channel list leaves this element with its own modes, as a replaced list would.
            _channels?.TakeOwn();
            _channels = null;
            Column.SetModes(Index, modes);
            return;
        }
        Owner!.Demote();
        SetValueAt(m, value);
    }

    internal override void Detach()
    {
        var channels = Channels;
        channels.TakeOwn();
        TakeValues([channels]);
        Owner = null;
    }

    internal override void Rebind()
    {
        ReturnToPacked(Shape.Outer);
        _channels?.ReleaseOwn();
    }

    internal bool HoldsOwnParts() => _channels is { IsPacked: true } && ReferenceEquals(ValueAt(0), _channels);
}

/// <summary>
/// The channel vector (<c>TangentInfoChannels</c>) of a tangent element: a view of the element's
/// mode array while its element is in a column, or a stand-alone packed list (a detached element,
/// a copy).
/// </summary>
internal sealed class ChannelListView : AktPackedList
{
    internal readonly AktTangentShape Shape;
    private readonly TangentListView? _parent;
    private uint[]? _own;

    public ChannelListView(TangentListView? parent, AktTangentShape shape, uint[]? own)
    {
        _parent = parent;
        Shape = shape;
        _own = own;
    }

    /// <summary>The modes; do not change the array.</summary>
    internal uint[] Modes => _own ?? _parent!.Column.ModesAt(_parent.Index);

    private void SetModes(uint[] modes)
    {
        if (_own != null) _own = modes;
        else _parent!.Column.SetModes(_parent.Index, modes);
    }

    internal uint ModeAt(int index) => Modes[index];

    internal void SetMode(int index, uint mode)
    {
        var modes = Modes;
        if (modes[index] == mode) return;
        var copy = (uint[])modes.Clone();
        copy[index] = mode;
        SetModes(copy);
    }

    internal void TakeOwn() => _own ??= _parent!.Column.ModesAt(_parent.Index);

    internal void ReleaseOwn()
    {
        if (_parent != null) _own = null;
    }

    public override string? ElementTypeName => Shape.ChannelType;

    private protected override int SlotCount => Modes.Length;

    private protected override AktPackedView NewView(int index) => new TangentInfoView(this, index);

    private protected override bool CanStore(object? value) => Shape.IsChannel(value);

    private protected override bool CanRebind(AktPackedView view) => view is TangentInfoView t && t.Shape.SameAs(Shape);

    private protected override void StoreSlot(int index, object? value, bool insert)
    {
        uint mode = (uint)((AktStruct)value!).ValueAt(0)!;
        var modes = Modes;
        if (!insert)
        {
            SetMode(index, mode);
            return;
        }
        var copy = new uint[modes.Length + 1];
        modes.AsSpan(0, index).CopyTo(copy);
        modes.AsSpan(index).CopyTo(copy.AsSpan(index + 1));
        copy[index] = mode;
        SetModes(copy);
    }

    private protected override void RemoveSlot(int index)
    {
        var modes = Modes;
        var copy = new uint[modes.Length - 1];
        modes.AsSpan(0, index).CopyTo(copy);
        modes.AsSpan(index + 1).CopyTo(copy.AsSpan(index));
        SetModes(copy);
    }

    private protected override void ClearSlots() => SetModes([]);

    private protected override void PermuteSlots(int[] order)
    {
        var modes = Modes;
        var copy = new uint[modes.Length];
        for (int i = 0; i < copy.Length; i++) copy[i] = modes[order[i]];
        SetModes(copy);
    }

    private protected override AktPackedList ClonePacked() => new ChannelListView(null, Shape, Modes);

    private protected override void BeforeDemote()
    {
        // A column slot holds only mode arrays: the element leaves its column first.
        if (_own == null) _parent!.Owner!.Demote();
    }
}

/// <summary>One channel (<c>CTangentInfo</c>) of a <see cref="ChannelListView"/>.</summary>
internal sealed class TangentInfoView : AktPackedView
{
    internal readonly AktTangentShape Shape;

    public TangentInfoView(ChannelListView list, int index) : base(list.Shape.ChannelType, list.Shape.Channel, list, index)
    {
        Shape = list.Shape;
    }

    private ChannelListView List => (ChannelListView)Owner!;

    private protected override object? PackedGet(int m) => AktBox.UInt(List.ModeAt(Index));

    private protected override void PackedSet(int m, object? value)
    {
        if (value is uint mode) { List.SetMode(Index, mode); return; }
        Owner!.Demote();
        SetValueAt(m, value);
    }

    internal override void Detach()
    {
        TakeValues([AktBox.UInt(List.ModeAt(Index))]);
        Owner = null;
    }

    internal override void Rebind() => ReturnToPacked(Shape.Channel);
}

/// <summary>
/// Shares equal mode arrays across files and undo steps. Mode arrays are never changed in place,
/// and a scene uses few distinct ones (a handful of tangent modes over ~10 channels), so one
/// process-wide table removes nearly all of them. The table stops growing at
/// <see cref="MaxEntries"/>, after which new arrays are simply not shared.
/// </summary>
internal static class AktModeInterner
{
    private static readonly AktWordTable Table = new();

    public static uint[] Intern(ReadOnlySpan<uint> modes) => Table.Intern(modes);
}

/// <summary>
/// A table of shared, never-changed <c>uint</c> arrays: equal content returns the same array. It
/// stops growing at <see cref="MaxEntries"/>, after which new arrays are simply not shared.
/// </summary>
internal sealed class AktWordTable
{
    private const int MaxEntries = 1 << 16;
    private readonly Dictionary<uint[], uint[]> _seen = new(Comparer.Instance);
    private readonly Dictionary<uint[], uint[]>.AlternateLookup<ReadOnlySpan<uint>> _bySpan;
    private readonly object _gate = new();

    public AktWordTable() => _bySpan = _seen.GetAlternateLookup<ReadOnlySpan<uint>>();

    public uint[] Intern(ReadOnlySpan<uint> words)
    {
        if (words.Length == 0) return [];
        lock (_gate)
        {
            if (_bySpan.TryGetValue(words, out var known)) return known;
            var array = words.ToArray();
            if (_seen.Count < MaxEntries) _seen.Add(array, array);
            return array;
        }
    }

    private sealed class Comparer : IEqualityComparer<uint[]>, IAlternateEqualityComparer<ReadOnlySpan<uint>, uint[]>
    {
        public static readonly Comparer Instance = new();
        public bool Equals(uint[]? a, uint[]? b) => a.AsSpan().SequenceEqual(b);
        public int GetHashCode(uint[] a) => Hash(a);
        public bool Equals(ReadOnlySpan<uint> a, uint[] b) => a.SequenceEqual(b);
        public int GetHashCode(ReadOnlySpan<uint> a) => Hash(a);
        public uint[] Create(ReadOnlySpan<uint> a) => a.ToArray();

        private static int Hash(ReadOnlySpan<uint> a)
        {
            var h = new HashCode();
            h.Add(a.Length);
            foreach (uint x in a) h.Add(x);
            return h.ToHashCode();
        }
    }
}

/// <summary>
/// The element layout an <see cref="AktArrayColumn"/> holds: a struct of 32-bit scalars (uint,
/// int, float) plus one vector whose elements are such a scalar or a struct of them - a mesh's
/// polygons (<c>CPolygon</c>: its index list and material) and skin weights
/// (<c>CVertexWeightArray</c>: one list of bone/weight pairs per vertex). Each element is stored as
/// one array of 32-bit words: the scalar members in member order (the head), then the vector's
/// elements one after another. Floats are kept as their bits, so every value survives unchanged.
/// </summary>
public sealed class AktArrayShape
{
    // Only these element types are packed; every other struct vector stays a plain list.
    private static readonly HashSet<string> PackedTypes = new(StringComparer.Ordinal) { "CPolygon", "CVertexWeightArray" };
    // Word arrays up to this length are shared through one table when the element has no head: a
    // vertex bound to a single bone (bone, weight 1) is by far the most common weight list.
    private const int ShareLimit = 2;
    private static readonly AktWordTable SharedWords = new();

    internal readonly string TypeName;
    internal readonly AktMemberInfo[] Outer;
    /// <summary>The index of the vector member in <see cref="Outer"/>.</summary>
    internal readonly int ListMember;
    /// <summary>Per outer member, its word in the head; -1 for the vector member.</summary>
    internal readonly int[] HeadSlot;
    internal readonly AktTypeKind[] HeadKinds;
    /// <summary>The struct type of the vector's elements, or null when they are scalars.</summary>
    internal readonly string? ItemType;
    internal readonly AktMemberInfo[]? Item;
    internal readonly AktTypeKind[] ItemKinds;
    private readonly bool _share;

    private AktArrayShape(string typeName, AktMemberInfo[] outer, int listMember, int[] headSlot, AktTypeKind[] headKinds,
        string? itemType, AktMemberInfo[]? item, AktTypeKind[] itemKinds)
    {
        TypeName = typeName; Outer = outer; ListMember = listMember; HeadSlot = headSlot; HeadKinds = headKinds;
        ItemType = itemType; Item = item; ItemKinds = itemKinds;
        _share = headKinds.Length == 0;
    }

    /// <summary>The number of head words of every element.</summary>
    public int HeadCount => HeadKinds.Length;

    /// <summary>Words per vector element.</summary>
    public int Stride => ItemKinds.Length;

    /// <summary>True when the vector holds scalars rather than structs.</summary>
    public bool ScalarItems => Item == null;

    internal static AktArrayShape? TryCreate(string typeName, AktMemberInfo[] outer,
        Func<AktType, (string Name, AktMemberInfo[] Layout)?> structLayout)
    {
        if (!PackedTypes.Contains(AktNames.Key(typeName))) return null;
        int list = -1;
        var headSlot = new int[outer.Length];
        var headKinds = new List<AktTypeKind>();
        for (int m = 0; m < outer.Length; m++)
        {
            var kind = outer[m].Type.Kind;
            if (kind == AktTypeKind.Vector && list < 0)
            {
                list = m;
                headSlot[m] = -1;
                continue;
            }
            if (!IsWord(kind)) return null;
            headSlot[m] = headKinds.Count;
            headKinds.Add(kind);
        }
        if (list < 0) return null;
        var element = outer[list].Type.Element!;
        if (IsWord(element.Kind))
            return new AktArrayShape(typeName, outer, list, headSlot, headKinds.ToArray(), null, null, [element.Kind]);
        if (element.Kind != AktTypeKind.Struct || structLayout(element) is not { } item || item.Layout.Length == 0) return null;
        var itemKinds = new AktTypeKind[item.Layout.Length];
        for (int k = 0; k < itemKinds.Length; k++)
            if (!IsWord(itemKinds[k] = item.Layout[k].Type.Kind)) return null;
        return new AktArrayShape(typeName, outer, list, headSlot, headKinds.ToArray(), item.Name, item.Layout, itemKinds);
    }

    private static bool IsWord(AktTypeKind kind) => kind is AktTypeKind.UInt or AktTypeKind.Int or AktTypeKind.Float;

    internal bool SameAs(AktArrayShape o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (TypeName != o.TypeName || ItemType != o.ItemType || Outer.Length != o.Outer.Length || Stride != o.Stride) return false;
        for (int m = 0; m < Outer.Length; m++)
            if (!ReferenceEquals(Outer[m], o.Outer[m])) return false;
        if (Item != null)
            for (int k = 0; k < Item.Length; k++)
                if (!ReferenceEquals(Item[k], o.Item![k])) return false;
        return true;
    }

    /// <summary>The head word of the scalar member <paramref name="name"/>, or -1.</summary>
    internal int HeadSlotOf(string name)
    {
        for (int m = 0; m < Outer.Length; m++)
            if (m != ListMember && Outer[m].Name == name) return HeadSlot[m];
        return -1;
    }

    /// <summary>The word of member <paramref name="name"/> inside a vector element, or -1.</summary>
    internal int ItemSlotOf(string name) => Item == null ? -1 : Array.FindIndex(Item, i => i.Name == name);

    internal static object Box(AktTypeKind kind, uint word) => kind switch
    {
        AktTypeKind.UInt => AktBox.UInt(word),
        AktTypeKind.Int => AktBox.Int((int)word),
        _ => AktBox.Float(BitConverter.UInt32BitsToSingle(word)),
    };

    /// <summary>The word of a value of exactly the member's type; false for anything else.</summary>
    internal static bool TryWord(AktTypeKind kind, object? value, out uint word)
    {
        switch (kind)
        {
            case AktTypeKind.UInt when value is uint u: word = u; return true;
            case AktTypeKind.Int when value is int i: word = (uint)i; return true;
            case AktTypeKind.Float when value is float f: word = BitConverter.SingleToUInt32Bits(f); return true;
        }
        word = 0;
        return false;
    }

    /// <summary>A word array for <paramref name="head"/> followed by <paramref name="items"/>, shared when short.</summary>
    internal uint[] Compose(ReadOnlySpan<uint> head, ReadOnlySpan<uint> items)
    {
        int length = head.Length + items.Length;
        if (length == 0) return [];
        if (_share && length <= ShareLimit)
        {
            Span<uint> words = stackalloc uint[length];
            head.CopyTo(words);
            items.CopyTo(words[head.Length..]);
            return SharedWords.Intern(words);
        }
        var array = new uint[length];
        head.CopyTo(array);
        items.CopyTo(array.AsSpan(head.Length));
        return array;
    }

    internal bool IsItem(object? value)
    {
        if (Item == null) return TryWord(ItemKinds[0], value, out _);
        if (value is not AktStruct s || s.TypeName != ItemType || s.FieldCount != Item.Length) return false;
        for (int k = 0; k < Item.Length; k++)
            if (!ReferenceEquals(s.MemberAt(k), Item[k]) || !TryWord(ItemKinds[k], s.ValueAt(k), out _)) return false;
        return true;
    }

    /// <summary>Writes the words of a vector element <see cref="IsItem"/> accepted.</summary>
    internal void ItemWords(object? value, Span<uint> dst)
    {
        if (Item == null)
        {
            TryWord(ItemKinds[0], value, out dst[0]);
            return;
        }
        var s = (AktStruct)value!;
        for (int k = 0; k < dst.Length; k++) TryWord(ItemKinds[k], s.ValueAt(k), out dst[k]);
    }

    /// <summary>Whether <paramref name="value"/> is a vector this shape can store.</summary>
    internal bool CanHoldItems(object? value)
    {
        if (value is ArrayItemList { IsPacked: true } packed) return packed.Shape.SameAs(this);
        if (value is not (List<object?> or AktPackedList)) return false;
        foreach (var item in (IList<object?>)value)
            if (!IsItem(item)) return false;
        return true;
    }

    /// <summary>The words of a vector <see cref="CanHoldItems"/> accepted.</summary>
    internal ReadOnlySpan<uint> ItemsOf(object? value)
    {
        if (value is ArrayItemList { IsPacked: true } packed) return packed.Words;
        var list = (IList<object?>)value!;
        var words = new uint[list.Count * Stride];
        for (int i = 0; i < list.Count; i++) ItemWords(list[i], words.AsSpan(i * Stride, Stride));
        return words;
    }

    internal bool IsElement(object? value)
    {
        if (value is not AktStruct s || s.TypeName != TypeName || s.FieldCount != Outer.Length) return false;
        for (int m = 0; m < Outer.Length; m++)
        {
            if (!ReferenceEquals(s.MemberAt(m), Outer[m])) return false;
            var v = s.ValueAt(m);
            if (m == ListMember ? !CanHoldItems(v) : !TryWord(HeadKinds[HeadSlot[m]], v, out _)) return false;
        }
        return true;
    }

    /// <summary>The word array of an element <see cref="IsElement"/> accepted.</summary>
    internal uint[] WordsOf(AktStruct s)
    {
        Span<uint> head = stackalloc uint[HeadCount];
        for (int m = 0; m < Outer.Length; m++)
            if (m != ListMember) TryWord(HeadKinds[HeadSlot[m]], s.ValueAt(m), out head[HeadSlot[m]]);
        return Compose(head, ItemsOf(s.ValueAt(ListMember)));
    }
}

/// <summary>
/// A vector of <see cref="AktArrayShape"/> structs (a mesh's <c>PolygonArray</c> or a skin weight
/// array): one word array per element. The arrays are never changed in place (an edit stores a
/// new one), so a copy shares them and short ones are shared outright.
/// </summary>
public sealed class AktArrayColumn : AktPackedList
{
    internal readonly AktArrayShape Shape;
    private uint[][] _words;
    private int _n;

    internal AktArrayColumn(AktArrayShape shape, int capacity)
    {
        Shape = shape;
        _words = new uint[capacity][];
    }

    internal AktArrayColumn(AktArrayShape shape, uint[][] words, int count)
    {
        Shape = shape;
        _words = words;
        _n = count;
    }

    public override string? ElementTypeName => IsPacked ? Shape.TypeName : null;

    /// <summary>
    /// Packs a freshly built vector of <paramref name="elementType"/> structs (a whole weight or
    /// polygon array written by Bind Skin, the Skinning Workshop, the FBX import or the AutoRigger)
    /// the way the reader packs a loaded one, so a rewritten mesh does not stay a plain list until
    /// the scene is reloaded. Returns <paramref name="list"/> itself when the type is not packed,
    /// packing is off, the list is empty or any element does not fit the layout.
    /// </summary>
    public static IList<object?> Pack(AktSchema schema, string elementType, List<object?> list)
    {
        if (!AktPackedKeys.Enabled || list.Count == 0) return list;
        (string Name, AktMemberInfo[] Layout)? Layout(string name) =>
            schema.FindStruct(name) is { } def
                ? (def.Name, def.Members.Select(m => AktMemberInfo.Get(def.Name, m.Name, m.TypeName)).ToArray())
                : null;
        if (Layout(elementType) is not { } outer
            || AktArrayShape.TryCreate(outer.Name, outer.Layout, t => t.Target is { } target ? Layout(target) : null) is not { } shape)
            return list;
        var column = new AktArrayColumn(shape, list.Count);
        foreach (var item in list)
        {
            if (!shape.IsElement(item)) return list;
            column.Append(shape.WordsOf((AktStruct)item!));
        }
        return column;
    }

    private protected override int SlotCount => _n;

    /// <summary>The words of element <paramref name="index"/>: its head, then its vector's elements. Do not change the array.</summary>
    internal uint[] WordsAt(int index) => _words[index];

    /// <summary>The vector words of element <paramref name="index"/> (<see cref="AktArrayShape.Stride"/> per vector element).</summary>
    internal ReadOnlySpan<uint> ItemsAt(int index) => _words[index].AsSpan(Shape.HeadCount);

    internal void SetWords(int index, uint[] words) => _words[index] = words;

    internal void Append(uint[] words)
    {
        if (_words.Length == _n) Array.Resize(ref _words, Math.Max(4, _n * 2));
        _words[_n++] = words;
    }

    private protected override AktPackedView NewView(int index) => new ArrayElementView(this, index);

    private protected override bool CanStore(object? value) => Shape.IsElement(value);

    private protected override bool CanRebind(AktPackedView view) =>
        view is ArrayElementView e && e.Shape.SameAs(Shape) && e.HoldsOwnParts();

    private protected override void StoreSlot(int index, object? value, bool insert)
    {
        var words = Shape.WordsOf((AktStruct)value!);
        if (insert)
        {
            if (_words.Length == _n) Array.Resize(ref _words, Math.Max(4, _n * 2));
            Array.Copy(_words, index, _words, index + 1, _n - index);
            _n++;
        }
        _words[index] = words;
    }

    private protected override void RemoveSlot(int index)
    {
        Array.Copy(_words, index + 1, _words, index, _n - index - 1);
        _words[--_n] = null!;
    }

    private protected override void ClearSlots()
    {
        _n = 0;
        _words = [];
    }

    private protected override void PermuteSlots(int[] order)
    {
        var old = _words.AsSpan(0, _n).ToArray();
        for (int i = 0; i < _n; i++) _words[i] = old[order[i]];
    }

    private protected override AktPackedList ClonePacked() => new AktArrayColumn(Shape, _words.AsSpan(0, _n).ToArray(), _n);
}

/// <summary>An element of an <see cref="AktArrayColumn"/> (a <c>CPolygon</c>, a <c>CVertexWeightArray</c>).</summary>
internal sealed class ArrayElementView : AktPackedView
{
    internal readonly AktArrayShape Shape;
    private ArrayItemList? _items;

    public ArrayElementView(AktArrayColumn column, int index) : base(column.Shape.TypeName, column.Shape.Outer, column, index)
    {
        Shape = column.Shape;
    }

    internal AktArrayColumn Column => (AktArrayColumn)Owner!;

    private ArrayItemList Items => _items ??= new ArrayItemList(this, Shape, null);

    private protected override object? PackedGet(int m)
    {
        if (m == Shape.ListMember) return Items;
        int slot = Shape.HeadSlot[m];
        return AktArrayShape.Box(Shape.HeadKinds[slot], Column.WordsAt(Index)[slot]);
    }

    private protected override void PackedSet(int m, object? value)
    {
        if (m == Shape.ListMember)
        {
            if (ReferenceEquals(value, _items)) return;
            if (Shape.CanHoldItems(value))
            {
                var words = Shape.Compose(Column.WordsAt(Index).AsSpan(0, Shape.HeadCount), Shape.ItemsOf(value));
                // The old vector leaves this element with its own words, as a replaced list would.
                _items?.TakeOwn();
                _items = null;
                Column.SetWords(Index, words);
                return;
            }
        }
        else if (AktArrayShape.TryWord(Shape.HeadKinds[Shape.HeadSlot[m]], value, out uint word))
        {
            var words = Column.WordsAt(Index).ToArray();
            words[Shape.HeadSlot[m]] = word;
            Column.SetWords(Index, Shape.Compose(words.AsSpan(0, Shape.HeadCount), words.AsSpan(Shape.HeadCount)));
            return;
        }
        Owner!.Demote();
        SetValueAt(m, value);
    }

    internal override void Detach()
    {
        var values = new object?[Shape.Outer.Length];
        for (int m = 0; m < values.Length; m++) values[m] = PackedGet(m);
        Items.TakeOwn();
        TakeValues(values);
        Owner = null;
    }

    internal override void Rebind()
    {
        ReturnToPacked(Shape.Outer);
        _items?.ReleaseOwn();
    }

    internal bool HoldsOwnParts() => _items is { IsPacked: true } && ReferenceEquals(ValueAt(Shape.ListMember), _items);
}

/// <summary>
/// The vector of an <see cref="ArrayElementView"/> (<c>IndexArray</c>, <c>WeightArray</c>): a view of
/// the element's words while the element is in a column, or a stand-alone packed list (a detached
/// element, a copy).
/// </summary>
internal sealed class ArrayItemList : AktPackedList
{
    internal readonly AktArrayShape Shape;
    private readonly ArrayElementView? _parent;
    // The vector words alone (no head), when the list holds its own.
    private uint[]? _own;

    public ArrayItemList(ArrayElementView? parent, AktArrayShape shape, uint[]? own)
    {
        _parent = parent;
        Shape = shape;
        _own = own;
    }

    /// <summary>The vector words; do not change them.</summary>
    internal ReadOnlySpan<uint> Words
    {
        get
        {
            if (_own != null) return _own;
            return _parent!.Column.WordsAt(_parent.Index).AsSpan(Shape.HeadCount);
        }
    }

    private uint[] OwnCopy()
    {
        if (_own != null) return _own;
        var words = _parent!.Column.WordsAt(_parent.Index);
        return Shape.HeadCount == 0 ? words : words.AsSpan(Shape.HeadCount).ToArray();
    }

    private void SetWords(ReadOnlySpan<uint> items)
    {
        if (_own != null) _own = Shape.Compose([], items);
        else
        {
            var column = _parent!.Column;
            column.SetWords(_parent.Index, Shape.Compose(column.WordsAt(_parent.Index).AsSpan(0, Shape.HeadCount), items));
        }
    }

    internal uint WordAt(int position) => Words[position];

    internal void SetWord(int position, uint word)
    {
        var words = Words;
        if (words[position] == word) return;
        var copy = words.ToArray();
        copy[position] = word;
        SetWords(copy);
    }

    internal void TakeOwn() => _own ??= OwnCopy();

    internal void ReleaseOwn()
    {
        if (_parent != null) _own = null;
    }

    public override string? ElementTypeName => Shape.ItemType;

    private protected override int SlotCount => Words.Length / Shape.Stride;

    private protected override bool HoldsScalars => Shape.ScalarItems;

    private protected override object? ScalarAt(int index) => AktArrayShape.Box(Shape.ItemKinds[0], Words[index]);

    private protected override AktPackedView NewView(int index) => new ArrayItemView(this, index);

    private protected override bool CanStore(object? value) => Shape.IsItem(value);

    private protected override bool CanRebind(AktPackedView view) => view is ArrayItemView v && v.Shape.SameAs(Shape);

    private protected override void StoreSlot(int index, object? value, bool insert)
    {
        int stride = Shape.Stride, at = index * stride;
        Span<uint> item = stackalloc uint[stride];
        Shape.ItemWords(value, item);
        var words = Words;
        if (!insert)
        {
            var copy = words.ToArray();
            item.CopyTo(copy.AsSpan(at));
            SetWords(copy);
            return;
        }
        var grown = new uint[words.Length + stride];
        words[..at].CopyTo(grown);
        item.CopyTo(grown.AsSpan(at));
        words[at..].CopyTo(grown.AsSpan(at + stride));
        SetWords(grown);
    }

    private protected override void RemoveSlot(int index)
    {
        int stride = Shape.Stride, at = index * stride;
        var words = Words;
        var copy = new uint[words.Length - stride];
        words[..at].CopyTo(copy);
        words[(at + stride)..].CopyTo(copy.AsSpan(at));
        SetWords(copy);
    }

    private protected override void ClearSlots() => SetWords([]);

    private protected override void PermuteSlots(int[] order)
    {
        int stride = Shape.Stride;
        var words = Words;
        var copy = new uint[words.Length];
        for (int i = 0; i < order.Length; i++)
            words.Slice(order[i] * stride, stride).CopyTo(copy.AsSpan(i * stride));
        SetWords(copy);
    }

    private protected override AktPackedList ClonePacked() => new ArrayItemList(null, Shape, OwnCopy());

    private protected override void BeforeDemote()
    {
        // A column slot holds only word arrays: the element leaves its column first.
        if (_own == null) _parent!.Owner!.Demote();
    }
}

/// <summary>One struct element (<c>CVertexWeight</c>) of an <see cref="ArrayItemList"/>.</summary>
internal sealed class ArrayItemView : AktPackedView
{
    internal readonly AktArrayShape Shape;

    public ArrayItemView(ArrayItemList list, int index) : base(list.Shape.ItemType!, list.Shape.Item!, list, index)
    {
        Shape = list.Shape;
    }

    private ArrayItemList List => (ArrayItemList)Owner!;

    private protected override object? PackedGet(int m) => AktArrayShape.Box(Shape.ItemKinds[m], List.WordAt(Index * Shape.Stride + m));

    private protected override void PackedSet(int m, object? value)
    {
        if (AktArrayShape.TryWord(Shape.ItemKinds[m], value, out uint word))
        {
            List.SetWord(Index * Shape.Stride + m, word);
            return;
        }
        Owner!.Demote();
        SetValueAt(m, value);
    }

    internal override void Detach()
    {
        var values = new object?[Shape.Stride];
        for (int m = 0; m < values.Length; m++) values[m] = PackedGet(m);
        TakeValues(values);
        Owner = null;
    }

    internal override void Rebind() => ReturnToPacked(Shape.Item!);
}
