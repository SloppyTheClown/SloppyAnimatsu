using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// Converts a key value from one space-switching parent to another at a frame
/// the key's world is
/// (rest · delta) under the world of its space at that frame, re-expressed under the world of
/// the new space and made relative to rest again. Worlds come from <see cref="Evaluator"/> and
/// are cached per frame, so one converter serves many tracks of an unchanged scene.
/// </summary>
public sealed class KeySpaceConverter(PoseEvaluator evaluator, Animation animation)
{
    private readonly Dictionary<float, (Matrix4x4[] World, Transform[] Local)> _frames = [];
    private readonly Dictionary<(Object3D, float), Matrix4x4> _others = new(new ObjectFrameComparer());

    public PoseEvaluator Evaluator { get; } = evaluator;
    public Animation Animation { get; } = animation;

    /// <summary>
    /// <paramref name="delta"/>, a key of <paramref name="target"/> stored in space
    /// <paramref name="from"/> (null = its parent), converted into space <paramref name="to"/> at
    /// <paramref name="time"/>. Returns the delta unchanged when the spaces agree or the
    /// conversion is not possible.
    /// </summary>
    public Transform Convert(Object3D target, Transform delta, Object3D? from, Object3D? to, float time)
    {
        if (ReferenceEquals(from, to)) return delta;
        int ci = Evaluator.IndexOf(target);
        if (ci < 0) return delta;
        var rest = Evaluator.RestTransforms[ci];
        bool compensate = Evaluator.Compensates(target, Animation, time);
        var local = PoseEvaluator.Compose(rest, delta).ToMatrix();

        var fromParent = from ?? target.Parent3D;
        var world = fromParent is { } fp ? WorldComposition.Compose(local, WorldOf(fp, time), ScaleOf(fp, time), compensate) : local;
        var toParent = to ?? target.Parent3D;
        Matrix4x4 m = world;
        if (toParent is { } tp && !WorldComposition.TryDecompose(world, WorldOf(tp, time), ScaleOf(tp, time), compensate, out m))
            return delta;
        if (!Transform.TryFromMatrix(m, out var t)) return delta;
        var converted = PoseEvaluator.Decompose(rest, t);
        // Keep the stored quaternion's hemisphere.
        return Quaternion.Dot(converted.Rotation, delta.Rotation) < 0
            ? new Transform(converted.Translation, -converted.Rotation, converted.Scale)
            : converted;
    }

    /// <summary>
    /// <see cref="Convert"/> for a key of an additive stack when <paramref name="additive"/> is set
    /// rotation
    /// and scale are kept, and only the translation is carried from the old space's world axes
    /// and scale into the new space's.
    /// </summary>
    public Transform Convert(Object3D target, Transform delta, Object3D? from, Object3D? to, float time, bool additive)
    {
        if (!additive) return Convert(target, delta, from, to, time);
        var fromSpace = from ?? target.Parent3D;
        var toSpace = to ?? target.Parent3D;
        if (ReferenceEquals(fromSpace, toSpace)) return delta;
        var (qOld, sOld) = AxesOf(fromSpace, time);
        var (qNew, sNew) = AxesOf(toSpace, time);
        var world = Vector3.Transform(delta.Translation * sOld, qOld);
        var t = Vector3.Transform(world, Quaternion.Conjugate(qNew));
        if (sNew.X == 0 || sNew.Y == 0 || sNew.Z == 0) return delta;
        return new Transform(t / sNew, delta.Rotation, delta.Scale);
    }

    /// <summary>The world rotation and scale of a space at <paramref name="time"/>; null is the world itself.</summary>
    private (Quaternion Rotation, Vector3 Scale) AxesOf(Object3D? space, float time)
    {
        if (space == null || !Matrix4x4.Decompose(WorldOf(space, time), out var scale, out var rotation, out _))
            return (Quaternion.Identity, Vector3.One);
        return (rotation, scale);
    }

    private (Matrix4x4[] World, Transform[] Local) Frame(float time)
    {
        if (!_frames.TryGetValue(time, out var f))
            _frames[time] = f = (Evaluator.WorldMatrices(Animation, time), Evaluator.LocalTransforms(Animation, time));
        return f;
    }

    /// <summary>World of <paramref name="o"/> as the evaluator places it; objects that are not controllers use their stored matrix.</summary>
    private Matrix4x4 WorldOf(Object3D o, float time)
    {
        int i = Evaluator.IndexOf(o);
        if (i >= 0) return Frame(time).World[i];
        if (_others.TryGetValue((o, time), out var cached)) return cached;
        var m = o.Parent3D is { } p
            ? WorldComposition.Compose(o.LocalMatrix, WorldOf(p, time), ScaleOf(p, time), Evaluator.Compensates(o, Animation, time))
            : o.LocalMatrix;
        return _others[(o, time)] = m;
    }

    private Vector3 ScaleOf(Object3D o, float time) =>
        Evaluator.IndexOf(o) is var i and >= 0 ? Frame(time).Local[i].Scale : Object3D.LocalScale(o.LocalMatrix);

    private sealed class ObjectFrameComparer : IEqualityComparer<(Object3D, float)>
    {
        public bool Equals((Object3D, float) a, (Object3D, float) b) => ReferenceEquals(a.Item1, b.Item1) && a.Item2 == b.Item2;
        public int GetHashCode((Object3D, float) k) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Item1), k.Item2);
    }
}
