using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>Which parts of a local transform <see cref="SceneReframe"/> resets.</summary>
[Flags]
public enum ReframeParts
{
    /// <summary>Move the object's offset from its parent into its children and geometry.</summary>
    Translation = 1,
    /// <summary>Move the object's rotation into its children and geometry.</summary>
    Rotation = 2,
}

/// <summary>
/// Resets the local translation and/or rotation of every non-animated character, mesh and group
/// to zero without moving anything on screen: what is removed from an object's transform is
/// pushed into what lives in its frame - its children's transforms, the rest and bind poses of
/// controllers parented to it, the translation part of their animation keys, and its mesh
/// vertices and normals. The companion of <see cref="SceneRescale.Normalize"/> (scale).
/// <para>
/// Joints and controllers keep their transforms (they are what animation edits). An object is
/// skipped, with a reason, when it is animated itself, is used as the space of a key, is the
/// parent of an IK chain joint, or (for rotation) has a non-uniform scale, which a rotation
/// cannot be separated from.
/// </para>
/// Like <see cref="SceneRescale"/>, it writes copies of the values it changes, never the shared
/// arrays in place (undo snapshots may hold them), and records each replaced value for <see cref="Undo"/>.
/// </summary>
public sealed class SceneReframe
{
    private readonly List<(AktFieldContainer Container, string Field, object? Old)> _changes = [];

    /// <summary>The objects whose translation and/or rotation was reset.</summary>
    public IReadOnlyList<Object3D> Normalized { get; private set; } = [];

    /// <summary>Objects that had something to reset but were left alone, with the reason.</summary>
    public IReadOnlyList<(Object3D Object, string Reason)> Skipped { get; private set; } = [];

    /// <summary>Whether anything was changed.</summary>
    public bool Changed => _changes.Count > 0;

    /// <summary>Restores every value this reframe replaced, newest first.</summary>
    public void Undo()
    {
        for (int i = _changes.Count - 1; i >= 0; i--)
            _changes[i].Container.Set(_changes[i].Field, _changes[i].Old);
        _changes.Clear();
    }

    /// <summary>Under this length (scene units) a translation already counts as zero.</summary>
    private const float TranslationTolerance = 1e-4f;

    /// <summary>
    /// Under this angle a rotation already counts as none: 1 - |w| of the unit quaternion, about
    /// 0.05 degrees.
    /// </summary>
    private const float RotationTolerance = 1e-7f;

    /// <summary>Relative tolerance under which three axis scales count as one uniform scale.</summary>
    private const float UniformTolerance = 1e-3f;

    /// <summary>Whether <paramref name="o"/>'s own transform has a part of <paramref name="parts"/> to reset.</summary>
    public static bool HasPartsToNormalize(Object3D o, ReframeParts parts)
    {
        if (!IsCandidateType(o)) return false;
        var m = o.LocalMatrix;
        if (parts.HasFlag(ReframeParts.Translation) && m.Translation.Length() > TranslationTolerance) return true;
        if (parts.HasFlag(ReframeParts.Rotation) && Matrix4x4.Decompose(m, out _, out var q, out _)
            && 1f - MathF.Abs(Quaternion.Normalize(q).W) > RotationTolerance) return true;
        return false;
    }

    /// <summary>The characters, meshes and groups whose <paramref name="parts"/> would be reset (before the skip checks).</summary>
    public static IReadOnlyList<Object3D> Candidates(SceneModel model, ReframeParts parts)
    {
        var controllers = ControllerSet(model);
        return model.Scene.Descendants<Object3D>()
            .Where(o => !controllers.Contains(o.Source) && HasPartsToNormalize(o, parts)).ToList();
    }

    /// <summary>A character, a mesh or a plain group: the objects placed by their own transform.</summary>
    private static bool IsCandidateType(Object3D o) =>
        o is Character or Mesh || o.GetType() == typeof(Object3D);

    private static HashSet<AktObject> ControllerSet(SceneModel model) =>
        model.Scene.Descendants<Character>().SelectMany(c => c.Controllers).Select(o => o.Source).ToHashSet();

    /// <summary>Resets <paramref name="parts"/> of every eligible character, mesh and group, keeping every world position.</summary>
    public static SceneReframe Normalize(SceneModel model, ReframeParts parts)
    {
        var result = new SceneReframe();
        var objects = model.Scene.Descendants<Object3D>().ToList();   // pre-order: parents first
        var controllers = ControllerSet(model);

        // Objects whose frame other data depends on in ways a frame change would break.
        var animated = new HashSet<AktObject>();
        foreach (var track in model.Scene.Descendants<AnimTrack>())
            if (track.Target is { } t) animated.Add(t.Source);
        var spaces = new HashSet<AktObject>();
        foreach (var track in model.Scene.Descendants<AnimTrack>())
            foreach (var field in new[] { "KeyList", "TInList", "TOutList" })
            {
                var items = SceneValues.Items(track.Source, field);
                for (int i = 0; i < items.Count; i++)
                    if (SceneRescale.SpaceOf(items, i) is { } target
                        && model.Wrap(target) is Object3D space)
                        spaces.Add(space.Source);
            }
        var ikParents = new HashSet<AktObject>();
        foreach (var ik in model.Scene.Descendants<ControllerIK>())
            foreach (var joint in ik.Chain)
                if (joint.Parent3D is { } p) ikParents.Add(p.Source);

        // The correction of each object whose frame changed: old world = C * new world, so data
        // expressed in that frame is multiplied by C (row vectors) to stay where it was.
        var correction = new Dictionary<AktObject, Matrix4x4>();
        var normalized = new List<Object3D>();
        var skipped = new List<(Object3D, string)>();
        foreach (var o in objects)
        {
            var local = o.LocalMatrix;
            if (o.Parent3D is { } parent && correction.TryGetValue(parent.Source, out var parentC))
                local *= parentC;   // the same world placement, relative to the parent's new frame

            var stripped = local;
            if (!controllers.Contains(o.Source) && HasPartsToNormalize(o, parts))
            {
                string? reason = SkipReason(o, parts, animated, spaces, ikParents);
                if (reason != null) skipped.Add((o, reason));
                else if (Strip(local, parts) is { } s && Matrix4x4.Invert(s, out var inverse))
                {
                    stripped = s;
                    correction[o.Source] = local * inverse;
                    normalized.Add(o);
                }
                else skipped.Add((o, "its transform could not be separated"));
            }
            if (!Same(stripped, o.LocalMatrix))
                result.Replace(o.Source, "LTM", AktMath.ToArray(stripped));
        }
        result.Normalized = normalized;
        result.Skipped = skipped;
        if (correction.Count == 0) return result;

        Matrix4x4? ParentCorrection(Object3D? o) =>
            o?.Parent3D is { } p && correction.TryGetValue(p.Source, out var c) ? c : null;

        // Character poses: slot i is the local matrix of Controllers[i] in its parent's frame.
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
                if (i < slots.Count && ParentCorrection(slots[i]) is { } c && items[i] is float[] a && a.Length >= 16)
                {
                    copy.Add(AktMath.ToArray(AktMath.ToMatrix(a) * c));
                    any = true;
                }
                else copy.Add(items[i]);
            }
            if (any) result.Replace(pose.Source, "Matrices", copy);
        }

        // Animation keys: a key's translation is an offset in the target's parent frame, so it
        // turns with that frame; its rotation multiplies the pose's rotation on the right and its
        // scale is per axis of the object itself, so neither changes. A key with a space object is
        // expressed in that space, whose frame was left alone (spaces are never normalized).
        foreach (var track in model.Scene.Descendants<AnimTrack>())
        {
            if (ParentCorrection(track.Target) is not { } c) continue;
            var values = SceneValues.Items(track.Source, "KeyList");
            foreach (var field in new[] { "KeyList", "TInList", "TOutList" })
            {
                var items = SceneValues.Items(track.Source, field);
                if (items.Count == 0) continue;
                if (TransformFloats.Of(items, allowIK: true) is { } packed)
                {
                    // Turn the translation floats of a packed copy (an IK key's render key too),
                    // so the list stays packed.
                    var render = SceneRescale.RenderTranslation(packed.Column);
                    AktMrsKeyColumn? turnedColumn = null;
                    for (int i = 0; i < packed.Column.Count; i++)
                    {
                        var space = SceneRescale.HasValueKey(values, i) ? SceneRescale.SpaceOf(values, i) : packed.Column.RefTarget(i, "Space");
                        if (space != null) continue;
                        turnedColumn ??= packed.Column.CopyPacked();
                        var floats = turnedColumn.ElementFloats(i);
                        var t = Vector3.TransformNormal(new Vector3(floats[packed.Layout.Tx], floats[packed.Layout.Ty], floats[packed.Layout.Tz]), c);
                        floats[packed.Layout.Tx] = t.X;
                        floats[packed.Layout.Ty] = t.Y;
                        floats[packed.Layout.Tz] = t.Z;
                        if (render is var (rx, ry, rz))
                        {
                            var r = Vector3.TransformNormal(new Vector3(floats[rx], floats[ry], floats[rz]), c);
                            floats[rx] = r.X;
                            floats[ry] = r.Y;
                            floats[rz] = r.Z;
                        }
                    }
                    if (turnedColumn != null) result.Replace(track.Source, field, turnedColumn);
                    continue;
                }
                var copy = new List<object?>(items.Count);
                bool any = false;
                for (int i = 0; i < items.Count; i++)
                {
                    var spaceSource = (i < values.Count ? values[i] as AktStruct : null) ?? items[i] as AktStruct;
                    if (items[i] is not AktStruct key || !key.Has("Key")
                        || spaceSource?["Space"] is AktReference { Target: not null })
                    {
                        copy.Add(items[i]);
                        continue;
                    }
                    var turned = key.Clone();
                    TurnTranslation(turned.Get<AktStruct>("Key"), c);
                    if (turned["RenderKey"] is AktStruct render) TurnTranslation(render, c);
                    copy.Add(turned);
                    any = true;
                }
                if (any) result.Replace(track.Source, field, copy);
            }
        }

        // Geometry of a normalized mesh: vertices are in its own frame, blendshape targets store
        // offsets from them, and normals, tangents and binormals are directions, so those two take only the linear part.
        foreach (var mesh in model.Scene.Descendants<Mesh>())
        {
            if (!correction.TryGetValue(mesh.Source, out var c)) continue;
            result.TransformPoints(mesh.Source, "VertexArray", c, direction: false);
            foreach (var field in new[] { "NormalArray", "TangentArray", "BinormalArray" })
                result.TransformPoints(mesh.Source, field, c, direction: true);
            foreach (var target in mesh.BlendshapeTargets)
            {
                result.TransformPoints(target.Source, "VertexArray", c, direction: true);
                result.TransformPoints(target.Source, "NormalArray", c, direction: true);
            }
        }
        return result;
    }

    private static string? SkipReason(Object3D o, ReframeParts parts, HashSet<AktObject> animated,
        HashSet<AktObject> spaces, HashSet<AktObject> ikParents)
    {
        if (animated.Contains(o.Source)) return "it is animated";
        if (spaces.Contains(o.Source)) return "animation keys use it as their space";
        if (ikParents.Contains(o.Source)) return "it is the parent of an IK chain";
        if (o.Children.OfType<Object3D>().Any(c => c.CompensatesParentScale) && !IsUnitScale(o.LocalMatrix))
            return "a child compensates its scale";
        if (parts.HasFlag(ReframeParts.Rotation) && !IsUniformScale(o.LocalMatrix))
            return "its scale is not uniform (normalize the scale first)";
        return null;
    }

    /// <summary>
    /// <paramref name="local"/> without the requested parts: a zero translation row and/or the
    /// rotation replaced by the axis scales alone. Null when the matrix does not decompose.
    /// </summary>
    private static Matrix4x4? Strip(Matrix4x4 local, ReframeParts parts)
    {
        var m = local;
        if (parts.HasFlag(ReframeParts.Rotation))
        {
            if (!Matrix4x4.Decompose(local, out var scale, out _, out _)) return null;
            m = Matrix4x4.CreateScale(scale);
            m.Translation = local.Translation;
        }
        if (parts.HasFlag(ReframeParts.Translation)) m.Translation = Vector3.Zero;
        return m;
    }

    private static bool IsUniformScale(Matrix4x4 m)
    {
        var s = Object3D.LocalScale(m);
        float max = MathF.Max(s.X, MathF.Max(s.Y, s.Z)), min = MathF.Min(s.X, MathF.Min(s.Y, s.Z));
        return min > 0f && (max - min) <= UniformTolerance * max;
    }

    private static bool IsUnitScale(Matrix4x4 m)
    {
        var s = Object3D.LocalScale(m);
        return MathF.Abs(s.X - 1f) < UniformTolerance && MathF.Abs(s.Y - 1f) < UniformTolerance && MathF.Abs(s.Z - 1f) < UniformTolerance;
    }

    private static bool Same(Matrix4x4 a, Matrix4x4 b)
    {
        var d = a - b;
        return MathF.Abs(d.M11) + MathF.Abs(d.M12) + MathF.Abs(d.M13) + MathF.Abs(d.M14)
             + MathF.Abs(d.M21) + MathF.Abs(d.M22) + MathF.Abs(d.M23) + MathF.Abs(d.M24)
             + MathF.Abs(d.M31) + MathF.Abs(d.M32) + MathF.Abs(d.M33) + MathF.Abs(d.M34)
             + MathF.Abs(d.M41) + MathF.Abs(d.M42) + MathF.Abs(d.M43) + MathF.Abs(d.M44) == 0f;
    }

    private static void TurnTranslation(AktStruct trs, Matrix4x4 c)
    {
        if (trs["TranslationX"] is not float x || trs["TranslationY"] is not float y || trs["TranslationZ"] is not float z) return;
        var t = Vector3.TransformNormal(new Vector3(x, y, z), c);
        trs.Set("TranslationX", t.X);
        trs.Set("TranslationY", t.Y);
        trs.Set("TranslationZ", t.Z);
    }

    private void TransformPoints(AktFieldContainer source, string field, Matrix4x4 c, bool direction)
    {
        var items = SceneValues.Items(source, field);
        if (items.Count == 0) return;
        var copy = new List<object?>(items.Count);
        foreach (var item in items)
        {
            if (item is float[] p && p.Length >= 3)
            {
                var v = new Vector3(p[0], p[1], p[2]);
                v = direction ? Vector3.TransformNormal(v, c) : Vector3.Transform(v, c);
                var q = (float[])p.Clone();   // a fourth component (w) is kept
                q[0] = v.X; q[1] = v.Y; q[2] = v.Z;
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
