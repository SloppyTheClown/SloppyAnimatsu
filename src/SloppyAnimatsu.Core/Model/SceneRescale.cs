using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// Rescales a scene without changing what it looks like, or scales all of it uniformly.
/// <para>
/// <see cref="Normalize"/> sets the uniform local scale of every non-animated object (characters,
/// meshes, groups) to 1 and moves that scale into the data below it: child translations, pose
/// and key translations, and mesh vertices. Every world position stays where it was, so a rig
/// imported with a large root scale and counter-scaled meshes reads as a 1x rig of the same size.
/// It also sets joint and controller scales that drifted a hair off 1 (float round-off piled up
/// by earlier imports and edits, like 1.00002) to exactly 1, in their matrices, poses and scale
/// tracks; a track is only touched when every key is that close to 1, so real scale animation stays.
/// </para>
/// <para>
/// <see cref="ScaleUniformly"/> multiplies every translation and vertex by one factor, which
/// scales every world position (skinned geometry included) by exactly that factor. Export uses it.
/// </para>
/// Both write copies of the values they change (never the shared arrays in place, which undo
/// snapshots may hold) and record each replaced value, so <see cref="Undo"/> restores the scene.
/// </summary>
public sealed class SceneRescale
{
    private readonly List<(AktFieldContainer Container, string Field, object? Old)> _changes = [];

    /// <summary>The objects whose local scale was reset to 1.</summary>
    public IReadOnlyList<Object3D> Normalized { get; private set; } = [];

    /// <summary>Objects that were skipped because their local scale is not uniform.</summary>
    public IReadOnlyList<Object3D> SkippedNonUniform { get; private set; } = [];

    /// <summary>Whether anything was changed.</summary>
    public bool Changed => _changes.Count > 0;

    /// <summary>Restores every value this rescale replaced, newest first.</summary>
    public void Undo()
    {
        for (int i = _changes.Count - 1; i >= 0; i--)
            _changes[i].Container.Set(_changes[i].Field, _changes[i].Old);
        _changes.Clear();
    }

    /// <summary>Relative tolerance under which three axis scales count as one uniform scale.</summary>
    private const float UniformTolerance = 1e-3f;

    /// <summary>Under this relative distance from 1 a scale already counts as 1.</summary>
    private const float UnitTolerance = 1e-5f;

    /// <summary>Whether <paramref name="o"/> holds a uniform scale that differs from 1.</summary>
    public static bool HasScaleToNormalize(Object3D o) =>
        IsNormalizable(o, out float u) && MathF.Abs(u - 1f) > UnitTolerance;

    /// <summary>Whether the scene has any object <see cref="Normalize"/> would change.</summary>
    public static bool SceneNeedsNormalizing(SceneModel model)
    {
        var controllers = ControllerSet(model);
        return model.Scene.Descendants<Object3D>().Any(o => !controllers.Contains(o.Source) && HasScaleToNormalize(o))
            || ControllerScaleDrift(model) > 0;
    }

    /// <summary>
    /// Sets the uniform scale of every character, mesh and group to 1, keeping every world
    /// position. Joints and controllers keep a real scale (it is part of their animation), but
    /// their float drift around 1 is set to exactly 1 (<see cref="ControllerDriftTolerance"/>).
    /// </summary>
    public static SceneRescale Normalize(SceneModel model) => Run(model, rootFactor: 1f, normalize: true);

    /// <summary>Multiplies every world position in the scene by <paramref name="factor"/>.</summary>
    public static SceneRescale ScaleUniformly(SceneModel model, float factor)
    {
        if (!(factor > 0f) || float.IsInfinity(factor))
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The scale factor must be a positive number.");
        return Run(model, rootFactor: factor, normalize: false);
    }

    private static HashSet<AktObject> ControllerSet(SceneModel model) =>
        model.Scene.Descendants<Character>().SelectMany(c => c.Controllers).Select(o => o.Source).ToHashSet();

    private static bool IsNormalizable(Object3D o, out float uniform)
    {
        uniform = 1f;
        if (o is Blendshape) return false;
        var s = Object3D.LocalScale(o.LocalMatrix);
        float max = MathF.Max(s.X, MathF.Max(s.Y, s.Z)), min = MathF.Min(s.X, MathF.Min(s.Y, s.Z));
        if (!(min > 0f)) return false;
        uniform = (s.X + s.Y + s.Z) / 3f;
        return (max - min) <= UniformTolerance * max;
    }

    private static SceneRescale Run(SceneModel model, float rootFactor, bool normalize)
    {
        var result = new SceneRescale();
        var objects = model.Scene.Descendants<Object3D>().ToList();   // pre-order: parents first
        var controllers = ControllerSet(model);

        // How much each object's frame shrinks (old linear part / new linear part); everything
        // expressed in that frame is multiplied by it to keep its world position. Computed for
        // every object before anything is changed.
        var factor = new Dictionary<AktObject, float>();
        var ownScale = new Dictionary<AktObject, float>();          // the scale moved out of a normalized object
        var normalized = new List<Object3D>();
        var skipped = new List<Object3D>();
        foreach (var o in objects)
        {
            var parent = o.Parent3D;
            float parentFactor = parent != null ? factor[parent.Source] : rootFactor;
            // A child that compensates its parent's scale divides that scale out of its basis; the
            // parent's removed scale therefore did not reach the child's basis.
            float basis = parent != null && o.CompensatesParentScale && ownScale.TryGetValue(parent.Source, out float up)
                ? parentFactor / up
                : parentFactor;
            float f = basis;
            if (normalize && !controllers.Contains(o.Source))
            {
                if (IsNormalizable(o, out float u))
                {
                    if (MathF.Abs(u - 1f) > UnitTolerance)
                    {
                        ownScale[o.Source] = u;
                        f = basis * u;
                        normalized.Add(o);
                    }
                }
                else if (o is not Blendshape)
                    skipped.Add(o);
            }
            factor[o.Source] = f;
        }
        result.Normalized = normalized;
        result.SkippedNonUniform = skipped;

        float ParentFactor(Object3D? o) => o?.Parent3D is { } p ? factor[p.Source] : rootFactor;
        float FrameFactor(Object3D? frame) => frame != null && factor.TryGetValue(frame.Source, out float f) ? f : rootFactor;

        // 1. Local matrices: translation into the parent's new frame, own scale removed.
        foreach (var o in objects)
        {
            float t = ParentFactor(o);
            bool removeScale = ownScale.TryGetValue(o.Source, out float u);
            if (t == 1f && !removeScale) continue;
            var m = o.LocalMatrix;
            if (removeScale)
            {
                float inv = 1f / u;
                m.M11 *= inv; m.M12 *= inv; m.M13 *= inv;
                m.M21 *= inv; m.M22 *= inv; m.M23 *= inv;
                m.M31 *= inv; m.M32 *= inv; m.M33 *= inv;
            }
            m.M41 *= t; m.M42 *= t; m.M43 *= t;
            result.Replace(o.Source, "LTM", AktMath.ToArray(m));
        }

        // 2. Character poses: slot i is the local matrix of Controllers[i].
        foreach (var pose in model.Scene.Descendants<CharacterPose>())
        {
            var slots = pose.Character?.Controllers;
            if (slots == null) continue;
            var items = SceneValues.Items(pose.Source, "Matrices");
            if (items.Count == 0) continue;
            var copy = new List<object?>(items.Count);
            bool any = false;
            for (int i = 0; i < items.Count; i++)
            {
                float t = i < slots.Count ? ParentFactor(slots[i]) : rootFactor;
                if (items[i] is float[] a && a.Length >= 16 && t != 1f)
                {
                    var b = (float[])a.Clone();
                    b[12] *= t; b[13] *= t; b[14] *= t;   // row 4 of a row-major 4x4: the translation
                    copy.Add(b);
                    any = true;
                }
                else copy.Add(items[i]);
            }
            if (any) result.Replace(pose.Source, "Matrices", copy);
        }

        // 3. Animation keys: translations are offsets in the target's parent frame (or in the
        //    key's space object), and IK keys store their chain's solved local transforms.
        foreach (var track in model.Scene.Descendants<AnimTrack>())
        {
            var target = track.Target;
            float targetFactor = ParentFactor(target);
            var chain = (target as ControllerIK)?.Chain;
            var values = SceneValues.Items(track.Source, "KeyList");
            foreach (var field in new[] { "KeyList", "TInList", "TOutList" })
            {
                var items = SceneValues.Items(track.Source, field);
                if (items.Count == 0) continue;
                if (TransformFloats.Of(items, allowIK: true) is { } packed)
                {
                    // Scale the floats of a packed copy, so the list stays packed: the key's
                    // translation, an IK key's render key, and each chain record's translation.
                    var column = packed.Column;
                    var render = RenderTranslation(column);
                    var (recordSlot, recordStride, recordT, recordWidth) = ChainTranslation(column);
                    AktMrsKeyColumn? scaledColumn = null;
                    for (int i = 0; i < column.Count; i++)
                    {
                        var spaceObject = HasValueKey(values, i) ? SpaceOf(values, i) : column.RefTarget(i, "Space");
                        var space = spaceObject != null ? model.Wrap(spaceObject) as Object3D : null;
                        float f = space != null ? FrameFactor(space) : targetFactor;
                        if (f != 1f)
                        {
                            scaledColumn ??= column.CopyPacked();
                            var floats = scaledColumn.ElementFloats(i);
                            floats[packed.Layout.Tx] *= f;
                            floats[packed.Layout.Ty] *= f;
                            floats[packed.Layout.Tz] *= f;
                            if (render is var (rx, ry, rz)) { floats[rx] *= f; floats[ry] *= f; floats[rz] *= f; }
                        }
                        if (recordSlot < 0) continue;
                        // The stored record arrays are shared with copies: write a new one.
                        var records = column.Records(recordSlot, i);
                        float[]? scaledRecords = null;
                        for (int j = 0; j < records.Length / recordStride; j++)
                        {
                            float jf = chain != null && j < chain.Count ? ParentFactor(chain[j]) : f;
                            if (jf == 1f) continue;
                            scaledRecords ??= (float[])records.Clone();
                            for (int c = 0; c < recordWidth; c++) scaledRecords[j * recordStride + recordT + c] *= jf;
                        }
                        if (scaledRecords == null) continue;
                        scaledColumn ??= column.CopyPacked();
                        scaledColumn.SetRecords(recordSlot, i, scaledRecords);
                    }
                    if (scaledColumn != null) result.Replace(track.Source, field, scaledColumn);
                    continue;
                }
                var copy = new List<object?>(items.Count);
                bool any = false;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] is not AktStruct key || !key.Has("Key"))
                    {
                        copy.Add(items[i]);
                        continue;
                    }
                    // A tangent follows the space of the value key at the same index.
                    var spaceSource = (i < values.Count ? values[i] as AktStruct : null) ?? key;
                    var space = spaceSource["Space"] is AktReference { Target: not null } r ? model.Wrap(r) as Object3D : null;
                    float f = space != null ? FrameFactor(space) : targetFactor;
                    var scaled = ScaleKey(key, f, chain, ParentFactor);
                    any |= !ReferenceEquals(scaled, key);
                    copy.Add(scaled);
                }
                if (any) result.Replace(track.Source, field, copy);
            }
        }

        // 4. Geometry: vertices are in the mesh's own frame; blendshape targets store offsets
        //    from those vertices in the same frame. Normals keep their direction.
        foreach (var mesh in model.Scene.Descendants<Mesh>())
        {
            float f = factor[mesh.Source];
            if (f == 1f) continue;
            result.ScalePoints(mesh.Source, "VertexArray", f);
            foreach (var target in mesh.BlendshapeTargets)
                result.ScalePoints(target.Source, "VertexArray", f);
        }

        // 5. Joints and controllers: float drift around a scale of 1 becomes exactly 1.
        if (normalize) result.CleanedControllerScales = result.CleanControllerScales(model, apply: true);

        return result;
    }

    /// <summary>
    /// How far a joint or controller scale axis may sit from 1 and still count as drift, which
    /// <see cref="Normalize"/> sets to exactly 1. Drift is what imports, re-derived rest poses
    /// and rounding leave behind (1.000002, 0.99998, up to ~2e-4 seen in real rigs). Scale
    /// animation moves further than this and is kept. Used for rest matrices; key values use
    /// <see cref="KeyDriftTolerance"/>.
    /// </summary>
    private const float ControllerDriftTolerance = 1e-3f;

    /// <summary>
    /// How far a joint or controller scale <em>key value</em> may sit from 1 and still count as
    /// drift. Rebased and re-solved animation leaves more than rest matrices do: leg chains and
    /// reverse-foot keys of a real rig carried up to ~2e-3 (1.00166, 0.99872, max 1.95e-3 over
    /// 44,849 tracks), with no cluster of values above 2.5e-3 - beyond that the values spread
    /// evenly, as real scale animation does. Each value is judged on its own, so the drifted rest
    /// keys of a track that also animates its scale (1 -> 1.5 -> 1) are cleaned too.
    /// </summary>
    internal const float KeyDriftTolerance = 2.5e-3f;

    /// <summary>Whether a scale key value is drift to set to 1: not 1, within <see cref="KeyDriftTolerance"/> (NaN is not).</summary>
    private static bool IsKeyDrift(float v) => v != 1f && MathF.Abs(v - 1f) <= KeyDriftTolerance;

    /// <summary>How many joint/controller rest matrices and key tracks had their scale drift set to exactly 1.</summary>
    public int CleanedControllerScales { get; private set; }

    /// <summary>How many joint/controller rest matrices and key tracks <see cref="Normalize"/> would clean to a scale of exactly 1.</summary>
    public static int ControllerScaleDrift(SceneModel model) => new SceneRescale().CleanControllerScales(model, apply: false);

    /// <summary>
    /// Sets near-1 scales of joints and controllers to exactly 1: their stored local matrices
    /// (<c>LTM</c>), their slots in every character pose, and every scale key value within
    /// <see cref="KeyDriftTolerance"/> of 1 (scale tangents go to 0, since tangents are slopes,
    /// when the whole track becomes a constant 1). Rotation and translation are kept, so a child moves by at
    /// most the removed drift times its offset. Returns how many matrices and tracks change.
    /// </summary>
    private int CleanControllerScales(SceneModel model, bool apply)
    {
        int count = 0;
        var controllers = new HashSet<AktObject>();
        foreach (var c in model.Scene.Descendants<Character>().SelectMany(c => c.Controllers))
        {
            if (c is Blendshape || !controllers.Add(c.Source)) continue;
            if (UnitScaleMatrix(c.Source.Get<float[]>("LTM")) is not { } clean) continue;
            count++;
            if (apply) Replace(c.Source, "LTM", clean);
        }

        foreach (var pose in model.Scene.Descendants<CharacterPose>())
        {
            var items = SceneValues.Items(pose.Source, "Matrices");
            List<object?>? copy = null;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is not float[] a || UnitScaleMatrix(a) is not { } clean) continue;
                copy ??= new List<object?>(items);
                copy[i] = clean;
                count++;
            }
            if (copy != null && apply) Replace(pose.Source, "Matrices", copy);
        }

        foreach (var track in model.Scene.Descendants<AnimTrack>())
        {
            if (track.Target is not { } target || !controllers.Contains(target.Source)) continue;
            var values = SceneValues.Items(track.Source, "KeyList");
            if (values.Count == 0) continue;
            if (MapScales(values, v => IsKeyDrift(v) ? 1f : v) is not IList<object?> cleanValues) continue;
            count++;
            if (!apply) continue;
            Replace(track.Source, "KeyList", cleanValues);
            // Scale tangents are flattened only when the whole track is now a constant 1; a track
            // that still animates its scale keeps its tangents. NaN floats (seen in cached render
            // copies of keys) are left as they are and do not count.
            bool allOne = true;
            MapScales(cleanValues, v => { if (!float.IsNaN(v) && v != 1f) allOne = false; return v; });
            if (!allOne) continue;
            foreach (var field in new[] { "TInList", "TOutList" })
            {
                var tangents = SceneValues.Items(track.Source, field);
                if (tangents.Count > 0 && MapScales(tangents, _ => 0f) is { } flat) Replace(track.Source, field, flat);
            }
        }
        return count;
    }

    /// <summary>
    /// A copy of row-major matrix <paramref name="a"/> with its 3x3 part replaced by its pure
    /// rotation, when every row length is within <see cref="ControllerDriftTolerance"/> of 1 but
    /// not already 1 (within <see cref="Transform.ScaleRoundOff"/>); else null. Mirrored matrices
    /// are left alone.
    /// </summary>
    private static float[]? UnitScaleMatrix(float[] a)
    {
        if (a.Length < 16) return null;
        float off = 0;
        for (int r = 0; r < 3; r++)
        {
            float d = MathF.Abs(MathF.Sqrt(a[r * 4] * a[r * 4] + a[r * 4 + 1] * a[r * 4 + 1] + a[r * 4 + 2] * a[r * 4 + 2]) - 1f);
            if (!(d <= ControllerDriftTolerance)) return null;
            off = MathF.Max(off, d);
        }
        if (off <= Transform.ScaleRoundOff) return null;
        var m = AktMath.ToMatrix(a);
        if (m.GetDeterminant() <= 0) return null;
        var rotation = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Transform.FromMatrixLoose(m).Rotation));
        var b = (float[])a.Clone();
        b[0] = rotation.M11; b[1] = rotation.M12; b[2] = rotation.M13;
        b[4] = rotation.M21; b[5] = rotation.M22; b[6] = rotation.M23;
        b[8] = rotation.M31; b[9] = rotation.M32; b[10] = rotation.M33;
        return b;
    }

    /// <summary>
    /// Passes every scale float of a key list (a transform key's <c>Key</c> and <c>RenderKey</c>
    /// scale, an IK key's chain record scales) through <paramref name="map"/>; returns a changed
    /// copy of the list (packed stays packed), or null when no value changed. Values are compared
    /// with <c>Equals</c>, so a NaN mapped to itself counts as unchanged.
    /// </summary>
    internal static object? MapScales(IList<object?> items, Func<float, float> map)
    {
        if (TransformFloats.Of(items, allowIK: true) is { } packed)
        {
            var column = packed.Column;
            var offsets = new List<int> { packed.Layout.Sx, packed.Layout.Sy, packed.Layout.Sz };
            foreach (var axis in new[] { "ScaleX", "ScaleY", "ScaleZ" })
                if (column.FloatOffset(axis, 0, "RenderKey") is >= 0 and var o) offsets.Add(o);
            var (slot, stride, first, width) = ChainMember(column, "Scale");
            AktMrsKeyColumn? copy = null;
            for (int i = 0; i < column.Count; i++)
            {
                foreach (int o in offsets)
                {
                    float v = column.ElementFloats(i)[o], w = map(v);
                    if (w.Equals(v)) continue;
                    copy ??= column.CopyPacked();
                    copy.ElementFloats(i)[o] = w;
                }
                if (slot < 0) continue;
                var records = column.Records(slot, i);
                float[]? changed = null;
                for (int j = 0; j < records.Length / stride; j++)
                    for (int c = 0; c < width; c++)
                    {
                        int k = j * stride + first + c;
                        float w = map(records[k]);
                        if (w.Equals(records[k])) continue;
                        changed ??= (float[])records.Clone();   // record arrays are shared with copies
                        changed[k] = w;
                    }
                if (changed == null) continue;
                copy ??= column.CopyPacked();
                copy.SetRecords(slot, i, changed);
            }
            return copy;
        }

        List<object?>? list = null;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not AktStruct key) continue;
            AktStruct? clone = null;
            foreach (var part in new[] { "Key", "RenderKey" })
            {
                if (key[part] is not AktStruct trs) continue;
                foreach (var axis in new[] { "ScaleX", "ScaleY", "ScaleZ" })
                {
                    if (trs[axis] is not float v) continue;
                    float w = map(v);
                    if (w.Equals(v)) continue;
                    clone ??= key.Clone();
                    clone.Get<AktStruct>(part).Set(axis, w);
                }
            }
            var refs = SceneValues.Items(key, "RefArray");
            for (int j = 0; j < refs.Count; j++)
            {
                if (refs[j] is not AktStruct entry || entry["Scale"] is not float[] s) continue;
                float[]? changed = null;
                for (int c = 0; c < Math.Min(3, s.Length); c++)
                {
                    float w = map(s[c]);
                    if (w.Equals(s[c])) continue;
                    changed ??= (float[])s.Clone();
                    changed[c] = w;
                }
                if (changed == null) continue;
                clone ??= key.Clone();
                ((AktStruct)SceneValues.Items(clone, "RefArray")[j]!).Set("Scale", changed);
            }
            if (clone == null) continue;
            list ??= new List<object?>(items);
            list[i] = clone;
        }
        return list;
    }

    /// <summary>
    /// The records slot of an IK key's <c>RefArray</c> in a packed list, with the record stride and
    /// the first float and count (at most 3) of record member <paramref name="name"/>; slot -1
    /// when the keys have no chain records or the records lack the member.
    /// </summary>
    private static (int Slot, int Stride, int First, int Width) ChainMember(AktMrsKeyColumn column, string name)
    {
        int m = column.Shape.MemberIndex("RefArray");
        if (m < 0 || column.Shape.Part[m] != MrsPart.Records) return (-1, 0, 0, 0);
        var record = column.Shape.Struct[m]!;
        int k = record.IndexOf(name);
        if (k < 0 || record.Width[k] == 0) return (-1, 0, 0, 0);
        return (column.Shape.At[m], record.Stride, record.Offset[k], Math.Min(3, record.Width[k]));
    }

    /// <summary>
    /// The space object of value key <paramref name="i"/> (null when it has none or there is no
    /// such key), read without building a per-key object when <paramref name="values"/> is packed.
    /// </summary>
    internal static bool HasValueKey(IList<object?> values, int i) =>
        i < values.Count && (values is AktMrsKeyColumn { IsPacked: true } || values[i] is AktStruct);

    internal static AktObject? SpaceOf(IList<object?> values, int i)
    {
        if (i >= values.Count) return null;
        if (values is AktMrsKeyColumn { IsPacked: true } column) return column.RefTarget(i, "Space");
        return values[i] is AktStruct key && key["Space"] is AktReference { Target: { } target } ? target : null;
    }

    /// <summary>Where an IK key's <c>RenderKey</c> translation sits in a packed element's floats, or null when the keys have none.</summary>
    internal static (int X, int Y, int Z)? RenderTranslation(AktMrsKeyColumn column)
    {
        int x = column.FloatOffset("TranslationX", 0, "RenderKey");
        int y = column.FloatOffset("TranslationY", 0, "RenderKey");
        int z = column.FloatOffset("TranslationZ", 0, "RenderKey");
        return x < 0 || y < 0 || z < 0 ? null : (x, y, z);
    }

    /// <summary>
    /// The records slot of an IK key's <c>RefArray</c> in a packed list, with the record stride and
    /// the first float and count (at most 3; a fourth component is kept) of its
    /// <c>Translation</c>; slot -1 when the keys have no chain records.
    /// </summary>
    private static (int Slot, int Stride, int Translation, int Width) ChainTranslation(AktMrsKeyColumn column)
    {
        int m = column.Shape.MemberIndex("RefArray");
        if (m < 0 || column.Shape.Part[m] != MrsPart.Records) return (-1, 0, 0, 0);
        var record = column.Shape.Struct[m]!;
        int k = record.IndexOf("Translation");
        if (k < 0 || record.Width[k] == 0) return (-1, 0, 0, 0);
        return (column.Shape.At[m], record.Stride, record.Offset[k], Math.Min(3, record.Width[k]));
    }

    /// <summary>A copy of <paramref name="key"/> with its translations scaled, or the key itself when nothing changes.</summary>
    private static AktStruct ScaleKey(AktStruct key, float f, IReadOnlyList<Object3D>? chain, Func<Object3D?, float> parentFactor)
    {
        bool hasChain = SceneValues.Items(key, "RefArray").Count > 0;
        if (f == 1f && !hasChain) return key;
        var copy = key.Clone();
        if (f != 1f)
        {
            ScaleTranslation(copy.Get<AktStruct>("Key"), f);
            if (copy["RenderKey"] is AktStruct render) ScaleTranslation(render, f);
        }
        var refs = SceneValues.Items(copy, "RefArray");
        for (int j = 0; j < refs.Count; j++)
        {
            if (refs[j] is not AktStruct entry || entry["Translation"] is not float[] t) continue;
            float jf = chain != null && j < chain.Count ? parentFactor(chain[j]) : f;
            if (jf == 1f) continue;
            var scaled = (float[])t.Clone();
            for (int c = 0; c < Math.Min(3, scaled.Length); c++) scaled[c] *= jf;
            entry.Set("Translation", scaled);
        }
        return copy;
    }

    private static void ScaleTranslation(AktStruct trs, float f)
    {
        foreach (var axis in new[] { "TranslationX", "TranslationY", "TranslationZ" })
            if (trs[axis] is float v) trs.Set(axis, v * f);
    }

    private void ScalePoints(AktFieldContainer source, string field, float f)
    {
        var items = SceneValues.Items(source, field);
        if (items.Count == 0) return;
        var copy = new List<object?>(items.Count);
        foreach (var item in items)
        {
            if (item is float[] p)
            {
                var q = (float[])p.Clone();
                for (int c = 0; c < Math.Min(3, q.Length); c++) q[c] *= f;   // a fourth component (w) is kept
                copy.Add(q);
            }
            else copy.Add(item);
        }
        Replace(source, field, copy);
    }

    private void Replace(AktFieldContainer container, string field, object? value)
    {
        _changes.Add((container, field, container[field]));
        container.Set(field, value);
    }
}
