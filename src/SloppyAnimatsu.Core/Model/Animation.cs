using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary><c>CAnimation</c>: one entry of the Anim Bank, made of stacks.</summary>
public sealed class Animation : SceneObject
{
    internal Animation(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary>Stacks (layers) in their stored order.</summary>
    public IReadOnlyList<AnimStack> Stacks => Refs<AnimStack>("StackList");

    public AnimStack? CurrentStack
    {
        get => Ref<AnimStack>("CurrentStack");
        set => SetRef("CurrentStack", value);
    }

    /// <summary>The <c>StackList</c> references themselves, for editing the stack order (index 0 = top).</summary>
    internal List<AktReference> StackRefs
    {
        get
        {
            if (Source["StackList"] is List<AktReference> refs) return refs;
            refs = [];
            Source.Set("StackList", refs);
            return refs;
        }
    }

    /// <summary>
    /// The Mixer's row selection (a set, separate from <see cref="CurrentStack"/>;
    ///). It is the runtime-only
    /// <c>CAnimation::__SelectedStackList</c> - a member atom with no serializer - so it is **not**
    /// written to the file, and rows removed from the animation drop out of it.
    /// </summary>
    public IReadOnlyList<AnimStack> SelectedStacks =>
        _selectedStacks == null ? [] : Stacks.Where(_selectedStacks.Contains).ToArray();

    private HashSet<AnimStack>? _selectedStacks;

    /// <summary>Replaces the Mixer's row selection with <paramref name="stacks"/> (in <see cref="Stacks"/> order; not saved).</summary>
    public void SelectStacks(IEnumerable<AnimStack> stacks) =>
        _selectedStacks = stacks.Where(s => s.Animation == this).ToHashSet();

    /// <summary>Drops <paramref name="stack"/> from the Mixer's selection (a removed stack).</summary>
    internal void DeselectStack(AnimStack stack) => _selectedStacks?.Remove(stack);

    public int CurrentTime => Source.GetOr("CurrentTime", 0);

    public Character? Character => Ancestor<Character>();

    /// <summary>Anim Bank folders between <c>__Anims</c> and this animation, outermost first.</summary>
    public IReadOnlyList<string> FolderPath
    {
        get
        {
            var path = new List<string>();
            for (var o = Parent; o is Folder f && !f.IsInternal; o = o.Parent)
                path.Insert(0, f.Name ?? "");
            return path;
        }
    }

    /// <summary>First and last key time over all stacks (every stored key, the end markers included).</summary>
    public (int Start, int End) TimeRange
    {
        get
        {
            var times = Stacks.SelectMany(s => s.Keys).Select(k => k.Time).ToList();
            return times.Count == 0 ? (0, 0) : (times.Min(), times.Max());
        }
    }

    /// <summary>
    /// The frames the timeline spans: the earliest first key and the latest last
    /// <see cref="AnimStack.ConsideredKeyCount">considered</see> key over all stacks, so the length
    /// follows the last key and a non-cycle stack's end marker does not stretch it
    /// </summary>
    public (int Start, int End) KeyRange
    {
        get
        {
            int start = int.MaxValue, end = int.MinValue;
            foreach (var stack in Stacks)
            {
                var keys = stack.Keys;
                int n = stack.ConsideredKeyCount;
                if (n == 0) continue;
                start = Math.Min(start, keys[0].Time);
                end = Math.Max(end, keys[n - 1].Time);
            }
            return start > end ? (0, 0) : (start, end);
        }
    }

    /// <summary>
    /// The frames Play runs through: <see cref="KeyRange"/>'s start to the latest of each stack's
    /// last considered key, one less for a cycle (its loop key is key 0 again) - the FBX export's
    /// stop frame.
    /// </summary>
    public (int Start, int End) PlayRange
    {
        get
        {
            int start = int.MaxValue, end = int.MinValue;
            foreach (var stack in Stacks)
            {
                var keys = stack.Keys;
                int n = stack.ConsideredKeyCount;
                if (n == 0) continue;
                start = Math.Min(start, keys[0].Time);
                end = Math.Max(end, keys[n - 1].Time - (stack.IsCycle && n > 1 ? 1 : 0));
            }
            return start > end ? (0, 0) : (start, Math.Max(start, end));
        }
    }
}

/// <summary>An entry of <c>CAnimStack.TimeArray</c>. Times are integer frames.</summary>
public readonly record struct StackKey(int Time, uint Flags, ushort FadeIn, ushort FadeOut);

/// <summary>
/// <c>CAnimStack</c>: a layer of an animation. The app is pose-to-pose: all tracks of a stack are
/// keyed at the same times, so key <c>i</c> of every track sits at <c>Keys[i].Time</c>.
/// </summary>
public class AnimStack : SceneObject
{
    internal AnimStack(SceneModel model, AktObject source) : base(model, source) { }

    public IReadOnlyList<StackKey> Keys =>
        SceneValues.Items(Source, "TimeArray")
            .Select(k => (AktStruct)k!)
            .Select(k => new StackKey(k.Get<int>("Time"), k.Get<uint>("Flags"), k.Get<ushort>("FadeIn"), k.Get<ushort>("FadeOut")))
            .ToArray();

    /// <summary>Moves stack key <paramref name="index"/> to <paramref name="time"/> (tracks are unchanged).</summary>
    public void SetKeyTime(int index, int time) => ((AktStruct)SceneValues.Items(Source, "TimeArray")[index]!).Set("Time", time);

    /// <summary>
    /// Inserts a default stack key (<c>Flags</c> 0, no fades) at <paramref name="index"/> with
    /// <paramref name="time"/>.
    /// Tracks are not changed; callers keep their lists the same length (<see cref="AnimTrack.InsertKeyCopy"/>).
    /// </summary>
    public void InsertDefaultKeyTime(int index, int time)
    {
        var entry = Model.Document.Schema.NewStruct("CAnimStackKey");
        entry.Set("Time", time);
        entry.Set("Flags", 0u);
        entry.Set("FadeIn", (ushort)0);
        entry.Set("FadeOut", (ushort)0);
        SceneValues.Items(Source, "TimeArray").Insert(index, entry);
    }

    /// <summary>
    /// Inserts a stack key at <paramref name="index"/> as a copy of entry <paramref name="copyOf"/>
    /// (flags and fades) with <paramref name="time"/>. Tracks are not changed; callers keep their
    /// lists the same length (<see cref="AnimTrack.InsertKeyCopy"/>).
    /// </summary>
    public void InsertKeyTime(int index, int copyOf, int time)
    {
        var entries = SceneValues.Items(Source, "TimeArray");
        var entry = ((AktStruct)entries[copyOf]!).Clone();
        entry.Set("Time", time);
        entries.Insert(index, entry);
    }

    /// <summary>
    /// Removes stack key <paramref name="index"/>. Tracks are not changed; callers keep their lists
    /// the same length (<see cref="AnimTrack.RemoveKey"/>).
    /// </summary>
    public void RemoveKeyTime(int index) => SceneValues.Items(Source, "TimeArray").RemoveAt(index);

    /// <summary>
    /// Reorders the stack keys so that new entry i is old entry <paramref name="order"/>[i]
    /// (a permutation). Tracks are not changed (<see cref="AnimTrack.ReorderKeys"/>).
    /// </summary>
    public void ReorderKeyTimes(IReadOnlyList<int> order) => Reorder(SceneValues.Items(Source, "TimeArray"), order);

    /// <summary>Removes stack key <paramref name="index"/> and returns its entry for <see cref="PutKeyTime"/>.</summary>
    internal object? TakeKeyTime(int index)
    {
        var entries = SceneValues.Items(Source, "TimeArray");
        var entry = entries[index];
        entries.RemoveAt(index);
        return entry;
    }

    internal void PutKeyTime(int index, object? entry) => SceneValues.Items(Source, "TimeArray").Insert(index, entry);

    internal static void Reorder(IList<object?> list, IReadOnlyList<int> order)
    {
        if (order.Count != list.Count) return;
        if (list is AktPackedList packed)
        {
            packed.Permute(order);
            return;
        }
        var old = list.ToArray();
        for (int i = 0; i < order.Count; i++) list[i] = old[order[i]];
    }

    /// <summary>Sets the flags and fades of stack key <paramref name="index"/>.</summary>
    public void SetKeyFlags(int index, uint flags, ushort fadeIn, ushort fadeOut)
    {
        var entry = (AktStruct)SceneValues.Items(Source, "TimeArray")[index]!;
        entry.Set("Flags", flags);
        entry.Set("FadeIn", fadeIn);
        entry.Set("FadeOut", fadeOut);
    }

    public bool IsCycle => (Flags & 0x1) != 0;

    /// <summary>
    /// The keys the timeline draws and counts: all of a cycle stack's (its last is the loop key),
    /// all but the end marker otherwise; a lone key always counts.
    /// </summary>
    public int ConsideredKeyCount
    {
        get
        {
            int n = SceneValues.Items(Source, "TimeArray").Count;
            return IsCycle || n <= 1 ? n : n - 1;
        }
    }
    public bool IsAdditive => (Flags & 0x8) != 0;
    public bool IsEnabled => (Flags & 0x2) != 0;

    /// <summary>Bit 1 (0x2): enabled. Bit 3 (0x8): additive layer, else override. Others unknown.</summary>
    public uint Flags => Source.GetOr("Flags", 0u);
    /// <summary>
    /// View state kept with the stack but not an edit of the animation. Bit 0 (0x1): previous
    /// ghosts shown; bit 1 (0x2): next ghosts shown. Others unknown.
    /// </summary>
    public uint FlagsNoModify => Source.GetOr("FlagsNoModify", 0u);

    /// <summary><see cref="FlagsNoModify"/> bit 0: the Stacker's previous ghosts are on for this stack.</summary>
    public const uint PreviousGhostsFlag = 0x1;
    /// <summary><see cref="FlagsNoModify"/> bit 1: the Stacker's next ghosts are on for this stack.</summary>
    public const uint NextGhostsFlag = 0x2;

    /// <summary>Sets or clears <paramref name="flag"/> in <see cref="FlagsNoModify"/>; returns whether it changed.</summary>
    public bool SetNoModifyFlag(uint flag, bool on)
    {
        uint before = FlagsNoModify;
        uint after = on ? before | flag : before & ~flag;
        if (after == before) return false;
        Source.Set("FlagsNoModify", after);
        return true;
    }
    /// <summary>UI colour, RGBA bytes.</summary>
    public byte[] Color => Source.GetOr<byte[]>("Color", [0, 0, 0, 0]);
    /// <summary>Layer weight (1 in all samples); see <see cref="PoseEvaluator"/>.</summary>
    public float Opacity => Source.GetOr("Opacity", 1f);
    public float Tweenage => Source.GetOr("Tweenage", 0.5f);

    public Animation? Animation => Ancestor<Animation>();

    public IEnumerable<AnimTrack> Tracks => Children.OfType<AnimTrack>();

    public AnimTrack? TrackFor(Object3D target) => Tracks.FirstOrDefault(t => t.Target == target);
}

/// <summary>
/// Animated channels of a track. Bits 0-8 are
/// confirmed by the evaluator's mixers; the IK and reverse-foot bits are inferred from samples.
/// </summary>
[Flags]
public enum AnimChannels : uint
{
    None = 0,
    TranslateX = 1 << 0, TranslateY = 1 << 1, TranslateZ = 1 << 2,
    RotateX = 1 << 3, RotateY = 1 << 4, RotateZ = 1 << 5,
    ScaleX = 1 << 6, ScaleY = 1 << 7, ScaleZ = 1 << 8,
    PoleAngle = 1 << 9,
    Roll = 1 << 10, Twist = 1 << 11, Bank = 1 << 12,
    /// <summary>Channel 13: a blendshape controller's X value.</summary>
    BlendshapeX = 1 << 13,
    /// <summary>Channel 14: a blendshape controller's Y value.</summary>
    BlendshapeY = 1 << 14,
    /// <summary>Bit 15: the camera FOV channel.</summary>
    Fov = 1 << 15,

    Translate = TranslateX | TranslateY | TranslateZ,
    Rotate = RotateX | RotateY | RotateZ,
    Scale = ScaleX | ScaleY | ScaleZ,
}

/// <summary>
/// One channel's tangent setting: the in side's type in bits 0-3, the out side's in bits 4-7 and
/// Unified in bit 8.
/// </summary>
public readonly record struct TangentMode(uint Raw)
{
    public uint LowMode => Raw & 0xF;
    public uint HighMode => (Raw >> 4) & 0xF;
    public bool Bit8 => (Raw & 0x100) != 0;
    public uint In => LowMode;
    public uint Out => HighMode;
    public bool Unified => Bit8;

    public TangentMode WithIn(uint type) => new((Raw & ~0xFu) | (type & 0xF));
    public TangentMode WithOut(uint type) => new((Raw & ~0xF0u) | ((type & 0xF) << 4));
    public TangentMode WithUnified(bool unified) => new(unified ? Raw | 0x100 : Raw & ~0x100u);
    public override string ToString() => $"0x{Raw:X3}";
}

/// <summary>
/// <c>CAnimTrackBase</c> and subclasses: the keys of one controller within a stack. The lists
/// <see cref="Keys"/>, <see cref="TangentsIn"/>, <see cref="TangentsOut"/> and
/// <see cref="TangentModes"/> have one entry per <see cref="AnimStack.Keys"/> entry.
/// </summary>
public class AnimTrack : SceneObject
{
    internal AnimTrack(SceneModel model, AktObject source) : base(model, source) { }

    public AnimChannels Channels => (AnimChannels)Source.GetOr("ChannelMask", 0u);

    public Object3D? Target => Ref<Object3D>("Target");

    /// <summary>Whether this is a <c>CAnimTrackCamera</c> (its keys are <see cref="CameraKey"/>s).</summary>
    public bool IsCameraTrack => Target is CameraAnimated
        || (PackedKeys("KeyList") is { } packed ? PackedHolds(packed, "CKeyCamera") : Keys.Any(k => k is CameraKey));

    /// <summary>The packed key list in <paramref name="field"/>, or null.</summary>
    private AktMrsKeyColumn? PackedKeys(string field) =>
        SceneValues.Items(Source, field) is AktMrsKeyColumn { IsPacked: true } c ? c : null;

    /// <summary>Whether a packed list's keys are of (a subclass of) <paramref name="keyStruct"/>; a packed list holds one type.</summary>
    private static bool PackedHolds(AktMrsKeyColumn column, string keyStruct) =>
        AktNames.PolymorphicChain(AktNames.Key(column.Shape.TypeName)).Contains(keyStruct);

    /// <summary>Whether the value keys are a packed list of keys that are not camera keys (so they carry no FOV).</summary>
    internal bool HasPackedNonCameraKeys => PackedKeys("KeyList") is { } c && !PackedHolds(c, "CKeyCamera");

    /// <summary>Per value key: <c>CKeyIK.Flags</c>, 0 for other key types. A packed list is read without per-key objects.</summary>
    internal uint[] ReadIKFlags()
    {
        if (PackedKeys("KeyList") is { } packed)
        {
            var result = new uint[packed.Count];
            int at = PackedHolds(packed, "CKeyIK") ? packed.ScalarOffset("Flags", isUInt: true) : -1;
            if (at >= 0)
                for (int i = 0; i < result.Length; i++) result[i] = packed.UIntAt(i, at);
            return result;
        }
        return Keys.Select(k => (k as IKKey)?.Flags ?? 0u).ToArray();
    }

    /// <summary>
    /// Scalar float <paramref name="member"/> (e.g. <c>RollAngle</c>) of entry <paramref name="i"/>
    /// of <paramref name="field"/>; 0 when the entry or the member is missing. A packed list is
    /// read without per-key objects.
    /// </summary>
    internal float KeyScalar(string field, int i, string member)
    {
        var items = SceneValues.Items(Source, field);
        if ((uint)i >= (uint)items.Count) return 0f;
        if (items is AktMrsKeyColumn { IsPacked: true } packed)
        {
            int at = packed.ScalarOffset(member, isUInt: false);
            return at >= 0 ? packed.ElementFloats(i)[at] : 0f;
        }
        return items[i] is AktStruct s ? s.GetOr(member, 0f) : 0f;
    }

    /// <summary>Whether the track keys its target's FOV (channel 15, camera tracks only).</summary>
    public bool HasFov => (Channels & AnimChannels.Fov) != 0;

    public AnimStack? Stack => Parent as AnimStack;

    /// <summary>Key values. Entries are null where the file stores a null key pointer.</summary>
    public IReadOnlyList<AnimKey?> Keys => KeyList("KeyList");

    /// <summary>Incoming tangents, stored as key objects used as per-channel vectors.</summary>
    public IReadOnlyList<AnimKey?> TangentsIn => KeyList("TInList");

    public IReadOnlyList<AnimKey?> TangentsOut => KeyList("TOutList");

    /// <summary>Per key, one mode per channel.</summary>
    public IReadOnlyList<TangentMode[]> TangentModes =>
        SceneValues.Items(Source, "TangentInfoArray") is AktTangentColumn { IsPacked: true } column
            ? Enumerable.Range(0, column.Count).Select(i => Array.ConvertAll(column.ModesAt(i), m => new TangentMode(m))).ToArray()
            : SceneValues.Items(Source, "TangentInfoArray")
            .Select(l => l is AktStruct list
                ? SceneValues.Items(list, "TangentInfoChannels").Select(c => new TangentMode(((AktStruct)c!).Get<uint>("TangentMode"))).ToArray()
                : [])
            .ToArray();

    /// <summary>
    /// Inserts copies of key <paramref name="source"/>'s value, tangents and tangent modes at index
    /// <paramref name="at"/> (indices as before the insert).
    /// </summary>
    public void InsertKeyCopy(int source, int at)
    {
        foreach (var field in new[] { "KeyList", "TInList", "TOutList", "TangentInfoArray" })
        {
            var list = SceneValues.Items(Source, field);
            if (source >= list.Count) continue;
            list.Insert(Math.Min(at, list.Count), list[source] is AktStruct s ? s.Clone() : null);
        }
    }

    /// <summary>Removes key <paramref name="index"/>'s value, tangents and tangent modes.</summary>
    public void RemoveKey(int index)
    {
        foreach (var field in KeyFields)
        {
            var list = SceneValues.Items(Source, field);
            if (index < list.Count) list.RemoveAt(index);
        }
    }

    /// <summary>
    /// Reorders the keys, tangents and tangent modes so that new key i is old key
    /// <paramref name="order"/>[i] (a permutation of the stack's key indices).
    /// </summary>
    public void ReorderKeys(IReadOnlyList<int> order)
    {
        foreach (var field in KeyFields) AnimStack.Reorder(SceneValues.Items(Source, field), order);
    }

    /// <summary>Removes key <paramref name="index"/> and returns its entries for <see cref="PutKey"/>.</summary>
    internal (bool Taken, object? Entry)[] TakeKey(int index) => KeyFields.Select(field =>
    {
        var list = SceneValues.Items(Source, field);
        if (index >= list.Count) return (false, null);
        var entry = list[index];
        list.RemoveAt(index);
        return (true, entry);
    }).ToArray();

    internal void PutKey(int index, (bool Taken, object? Entry)[] entries)
    {
        for (int f = 0; f < KeyFields.Length; f++)
            if (entries[f].Taken) SceneValues.Items(Source, KeyFields[f]).Insert(index, entries[f].Entry);
    }

    private static readonly string[] KeyFields = ["KeyList", "TInList", "TOutList", "TangentInfoArray"];

    /// <summary>Sets one channel's tangent mode of key <paramref name="key"/>.</summary>
    public void SetTangentMode(int key, int channel, TangentMode mode)
    {
        var list = (AktStruct)SceneValues.Items(Source, "TangentInfoArray")[key]!;
        ((AktStruct)SceneValues.Items(list, "TangentInfoChannels")[channel]!).Set("TangentMode", mode.Raw);
    }

    private IReadOnlyList<AnimKey?> KeyList(string field) =>
        SceneValues.Items(Source, field).Select(k => k is AktStruct s ? AnimKey.Wrap(Model, s) : null).ToArray();

    /// <summary>
    /// Per entry of <paramref name="field"/> (<c>KeyList</c>, <c>TInList</c>, <c>TOutList</c>): the
    /// TRS of a transform key, or null. A packed list is read from its floats, without building
    /// per-key objects (which a packed list would keep for as long as the scene lives).
    /// </summary>
    public Transform?[] ReadTransforms(string field)
    {
        if (TransformFloats.Of(SceneValues.Items(Source, field), allowIK: true) is { } packed)
        {
            var result = new Transform?[packed.Column.Count];
            for (int i = 0; i < result.Length; i++) result[i] = packed.Layout.Read(packed.Column.ElementFloats(i));
            return result;
        }
        return KeyList(field).Select(k => k is TransformKey t ? t.Value : (Transform?)null).ToArray();
    }

    /// <summary>Per value key: its space object (null = the target's default parent, or not a transform key).</summary>
    public Object3D?[] ReadSpaces()
    {
        if (TransformFloats.Of(SceneValues.Items(Source, "KeyList"), allowIK: true) is { } packed)
        {
            var result = new Object3D?[packed.Column.Count];
            for (int i = 0; i < result.Length; i++)
                result[i] = packed.Column.RefTarget(i, "Space") is { } space ? Model.Wrap(space) as Object3D : null;
            return result;
        }
        return Keys.Select(k => (k as TransformKey)?.Space).ToArray();
    }

    /// <summary>Whether the value keys are a packed list of plain transform keys (so no IK, camera or blendshape keys).</summary>
    internal bool HasPackedTransformKeys => TransformFloats.Of(SceneValues.Items(Source, "KeyList")) != null;
}

/// <summary>Where a plain transform key's TRS floats sit inside an element of a packed key list.</summary>
internal readonly record struct TransformFloats(int Tx, int Ty, int Tz, int Rotation, int Sx, int Sy, int Sz)
{
    /// <summary>
    /// The packed list and its layout when <paramref name="items"/> is a packed list of plain
    /// transform keys (with <paramref name="allowIK"/>, also IK and reverse-foot keys, whose
    /// <c>Key</c> is read the same way); else null.
    /// </summary>
    internal static (AktMrsKeyColumn Column, TransformFloats Layout)? Of(IList<object?> items, bool allowIK = false)
    {
        if (items is not AktMrsKeyColumn { IsPacked: true } c || c.ElementTypeName is not { } type || c.ValueMemberName != "Key")
            return null;
        // The same classification as AnimKey.Wrap: only keys that wrap as a TransformKey without extra data.
        var chain = AktNames.PolymorphicChain(AktNames.Key(type));
        if (!chain.Contains("CKeyMRS") || (!allowIK && chain.Contains("CKeyIK"))
            || chain.Contains("CKeyCamera") || chain.Contains("CKeyBlendshape"))
            return null;
        var l = new TransformFloats(c.FloatOffset("TranslationX", 0), c.FloatOffset("TranslationY", 0), c.FloatOffset("TranslationZ", 0),
            c.FloatOffset("Rotation", 4), c.FloatOffset("ScaleX", 0), c.FloatOffset("ScaleY", 0), c.FloatOffset("ScaleZ", 0));
        return l.Tx < 0 || l.Ty < 0 || l.Tz < 0 || l.Rotation < 0 || l.Sx < 0 || l.Sy < 0 || l.Sz < 0 ? null : (c, l);
    }

    internal Transform Read(ReadOnlySpan<float> f) => new(
        new Vector3(f[Tx], f[Ty], f[Tz]),
        new Quaternion(f[Rotation], f[Rotation + 1], f[Rotation + 2], f[Rotation + 3]),
        new Vector3(f[Sx], f[Sy], f[Sz]));
}

/// <summary>A key value (<c>CKeyBase</c> and subclasses). Reads and writes its struct in place.</summary>
public class AnimKey
{
    public AktStruct Source { get; }
    protected SceneModel Model { get; }

    protected AnimKey(SceneModel model, AktStruct source)
    {
        Model = model;
        Source = source;
    }

    /// <summary>Concrete key struct, e.g. <c>CKeyMRS</c>.</summary>
    public string KeyType => AktNames.Key(Source.TypeName);

    internal static AnimKey Wrap(SceneModel model, AktStruct source)
    {
        var chain = AktNames.PolymorphicChain(AktNames.Key(source.TypeName));
        if (chain.Contains("CKeyReverseFoot")) return new ReverseFootKey(model, source);
        if (chain.Contains("CKeyIK")) return new IKKey(model, source);
        if (chain.Contains("CKeyCamera")) return new CameraKey(model, source);
        if (chain.Contains("CKeyBlendshape")) return new BlendshapeKey(model, source);
        if (chain.Contains("CKeyMRS")) return new TransformKey(model, source);
        return new AnimKey(model, source);
    }

    protected static Transform ReadTransform(AktStruct key) => new(
        new Vector3(key.Get<float>("TranslationX"), key.Get<float>("TranslationY"), key.Get<float>("TranslationZ")),
        AktMath.ToQuaternion(key.Get<float[]>("Rotation")),
        new Vector3(key.Get<float>("ScaleX"), key.Get<float>("ScaleY"), key.Get<float>("ScaleZ")));

    protected static void WriteTransform(AktStruct key, Transform t)
    {
        key.Set("TranslationX", t.Translation.X);
        key.Set("TranslationY", t.Translation.Y);
        key.Set("TranslationZ", t.Translation.Z);
        key.Set("Rotation", AktMath.ToArray(t.Rotation));
        key.Set("ScaleX", t.Scale.X);
        key.Set("ScaleY", t.Scale.Y);
        key.Set("ScaleZ", t.Scale.Z);
    }

    public override string ToString() => KeyType;
}

/// <summary><c>CKeyMRS</c>: a translation/rotation/scale key.</summary>
public class TransformKey : AnimKey
{
    internal TransformKey(SceneModel model, AktStruct source) : base(model, source) { }

    /// <summary>
    /// The key's TRS. For tangent keys these are per-channel tangent values, not a transform
    /// (rotation then has w = 0).
    /// </summary>
    public Transform Value
    {
        get => ReadTransform(Source.Get<AktStruct>("Key"));
        set => WriteTransform(Source.Get<AktStruct>("Key"), value);
    }

    /// <summary>Space-switching parent; null = the target's default parent.</summary>
    public Object3D? Space
    {
        get => Model.Wrap(Source["Space"] as AktReference) as Object3D;
        set => Source.Set("Space", new AktReference(value?.Source));
    }

    /// <summary>The owning track (null in tangent keys).</summary>
    public AnimTrack? Track => Model.Wrap(Source["TrackMRS"] as AktReference) as AnimTrack;
}

/// <summary>
/// <c>CKeyCamera</c>: an MRS key plus the camera's vertical field of view in degrees, channel 15
/// </summary>
public sealed class CameraKey : TransformKey
{
    internal CameraKey(SceneModel model, AktStruct source) : base(model, source) { }

    /// <summary>
    /// Vertical FOV in degrees; a value key without one reads as 30, a tangent key as 0 (its FOV
    /// is a slope). Value keys name their track in <c>TrackMRS</c>, tangent keys do not.
    /// </summary>
    public float Fov
    {
        get => Source.GetOr("FOV", Source["TrackMRS"] is AktReference { Target: not null } ? DefaultFov : 0f);
        set => Source.Set("FOV", value);
    }

    /// <summary>Per-key camera flags (bit 0 is cleared by a paste whose source key had no FOV).</summary>
    public uint Flags
    {
        get => Source.GetOr("Flags", 0u);
        set => Source.Set("Flags", value);
    }

    /// <summary>30 degrees, the FOV of a new camera key and of a camera without an FOV curve.</summary>
    public const float DefaultFov = 30f;
}

/// <summary><c>CKeyIK</c>: an IK effector key with the solved chain stored alongside.</summary>
public class IKKey : TransformKey
{
    internal IKKey(SceneModel model, AktStruct source) : base(model, source) { }

    /// <summary>
    /// <c>RefArray</c>: the solved local transform of each joint in
    /// <see cref="ControllerIK.Chain"/> at this key (empty in tangent keys).
    /// </summary>
    public IReadOnlyList<Transform> ChainTransforms =>
        SceneValues.Items(Source, "RefArray")
            .Select(r => (AktStruct)r!)
            .Select(r => new Transform(
                AktMath.ToVector3(r.Get<float[]>("Translation")),
                AktMath.ToQuaternion(r.Get<float[]>("Rotation")),
                AktMath.ToVector3(r.Get<float[]>("Scale"))))
            .ToArray();

    /// <summary>
    /// Overwrites the entries of <see cref="ChainTransforms"/> in place; <paramref name="chain"/>
    /// must have as many transforms as the key stores.
    /// </summary>
    public void SetChainTransforms(IReadOnlyList<Transform> chain)
    {
        var items = SceneValues.Items(Source, "RefArray");
        if (items.Count != chain.Count)
            throw new ArgumentException($"The key stores {items.Count} chain transforms, not {chain.Count}.", nameof(chain));
        for (int i = 0; i < chain.Count; i++)
        {
            var r = (AktStruct)items[i]!;
            r.Set("Translation", Resized(r.Get<float[]>("Translation"), AktMath.ToArray(chain[i].Translation)));
            r.Set("Rotation", AktMath.ToArray(chain[i].Rotation));
            r.Set("Scale", Resized(r.Get<float[]>("Scale"), AktMath.ToArray(chain[i].Scale)));
        }
    }

    /// <summary>
    /// Replaces <c>RefArray</c> with one new entry per transform
    /// Used by the hammer's add, where a new key has no chain yet.
    /// </summary>
    public void InitChainTransforms(IReadOnlyList<Transform> chain)
    {
        var schema = Model.Document.Schema;
        var items = SceneValues.Items(Source, "RefArray");
        items.Clear();
        foreach (var transform in chain)
        {
            var r = schema.NewStruct("IKBaseRefInfo");
            r.Set("Translation", Fill(r.Get<float[]>("Translation"), AktMath.ToArray(transform.Translation), 1f));
            r.Set("Rotation", AktMath.ToArray(transform.Rotation));
            r.Set("Scale", Fill(r.Get<float[]>("Scale"), AktMath.ToArray(transform.Scale), 1f));
            items.Add(r);
        }
    }

    /// <summary><paramref name="xyz"/> in the stored array's length; a fourth component is <paramref name="w"/>.</summary>
    private static float[] Fill(float[] stored, float[] xyz, float w)
    {
        var result = new float[Math.Max(stored.Length, 3)];
        Array.Copy(xyz, result, 3);
        for (int i = 3; i < result.Length; i++) result[i] = w;
        return result;
    }

    /// <summary>
    /// <paramref name="value"/> in the stored array's length: translation and scale are stored as
    /// four floats, and the fourth is kept.
    /// </summary>
    private static float[] Resized(float[] stored, float[] value)
    {
        var result = (float[])stored.Clone();
        Array.Copy(value, result, Math.Min(value.Length, result.Length));
        return result;
    }

    public uint Flags => Source.GetOr("Flags", 0u);

    /// <summary>Overwrites <c>Flags</c>.</summary>
    public void SetFlags(uint flags) => Source.Set("Flags", flags);

    /// <summary>Sets or clears bit 0, which makes the controller authoritative at the key.</summary>
    public void SetIkKey(bool ik)
    {
        uint flags = Flags;
        SetFlags(ik ? flags | 1u : flags & ~1u);
    }

    public float PoleAngle
    {
        get => Source.GetOr("PoleAngle", 0f);
        set => Source.Set("PoleAngle", value);
    }
    public float RefPoleAngle => Source.GetOr("RefPoleAngle", 0f);
    public Transform RenderTransform
    {
        get => ReadTransform(Source.Get<AktStruct>("RenderKey"));
        set => WriteTransform(Source.Get<AktStruct>("RenderKey"), value);
    }
    public ControllerIK? Controller => Model.Wrap(Source["TargetControllerIK"] as AktReference) as ControllerIK;
}

/// <summary><c>CKeyReverseFoot</c>: an IK key plus foot roll, twist and bank (radians).</summary>
public sealed class ReverseFootKey : IKKey
{
    internal ReverseFootKey(SceneModel model, AktStruct source) : base(model, source) { }

    public float RollAngle
    {
        get => Source.GetOr("RollAngle", 0f);
        set => Source.Set("RollAngle", value);
    }
    public float TwistAngle
    {
        get => Source.GetOr("TwistAngle", 0f);
        set => Source.Set("TwistAngle", value);
    }
    public float BankAngle
    {
        get => Source.GetOr("BankAngle", 0f);
        set => Source.Set("BankAngle", value);
    }
    public float LocalDrivingRefPoleAngle => Source.GetOr("LocalDrivingRefPoleAngle", 0f);
}
