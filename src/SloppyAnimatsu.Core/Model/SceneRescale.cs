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
        return model.Scene.Descendants<Object3D>().Any(o => !controllers.Contains(o.Source) && HasScaleToNormalize(o));
    }

    /// <summary>
    /// Sets the uniform scale of every character, mesh and group to 1, keeping every world
    /// position. Joints and controllers keep their scale (it is part of their animation).
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

        return result;
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
