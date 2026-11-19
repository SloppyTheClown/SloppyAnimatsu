using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// Computes a track's value between keys. <see cref="HermiteKeyInterpolator"/> is the curve;
/// <see cref="LinearKeyInterpolator"/> is a simpler alternative.
/// </summary>
public interface IKeyInterpolator
{
    /// <summary>
    /// Value of <paramref name="track"/> at <paramref name="time"/>, which is not an exact key time,
    /// from the stack keys at which the track has keys.
    /// </summary>
    Transform Interpolate(AnimTrack track, TrackKeys keys, float time);
}

/// <summary>
/// A track's keys: the stack keys where it has one, with the values and tangents there. Tangent
/// entries hold per-channel tangent values (rotation with w = 0), not transforms.
/// <see cref="Steps"/> marks keys whose tangent-out type is 5: the segment after them holds the
/// key.
/// </summary>
public sealed record TrackKeys(
    IReadOnlyList<StackKey> Keys, IReadOnlyList<Transform> Values,
    IReadOnlyList<Transform> TangentsIn, IReadOnlyList<Transform> TangentsOut,
    IReadOnlyList<bool>? Steps = null)
{
    /// <summary>Whether the segment after key <paramref name="i"/> holds that key.</summary>
    public bool IsStep(int i) => Steps is { } s && i < s.Count && s[i];

    /// <summary>
    /// The segment containing <paramref name="time"/>: previous key index and the 0..1 parameter;
    /// false before the first key or after the last (with <paramref name="hold"/> the key to hold).
    /// </summary>
    public bool Segment(float time, out int prev, out float u, out int hold)
    {
        int next = 0;
        while (next < Keys.Count && Keys[next].Time <= time) next++;
        prev = next - 1;
        u = 0;
        hold = next == 0 ? 0 : Keys.Count - 1;
        if (next == 0 || next == Keys.Count) return false;
        float span = Keys[next].Time - Keys[prev].Time;
        u = span > 0 ? (time - Keys[prev].Time) / span : 1f;
        return true;
    }
}

/// <summary>
/// Lerps translation and scale and slerps rotation between the two surrounding keys, ignoring
/// tangents. Before the first key and after the last one the nearest key is held.
/// </summary>
public sealed class LinearKeyInterpolator : IKeyInterpolator
{
    public static readonly LinearKeyInterpolator Instance = new();

    public Transform Interpolate(AnimTrack track, TrackKeys keys, float time)
    {
        if (!keys.Segment(time, out int prev, out float u, out int hold)) return keys.Values[hold];
        if (keys.IsStep(prev)) return keys.Values[prev];
        var a = keys.Values[prev];
        var b = keys.Values[prev + 1];
        return new Transform(
            Vector3.Lerp(a.Translation, b.Translation, u),
            Quaternion.Slerp(a.Rotation, b.Rotation, u),
            Vector3.Lerp(a.Scale, b.Scale, u));
    }
}

/// <summary>
/// The curve: cubic Hermite
/// between the two surrounding keys with the segment parameter u in 0..1, the previous key's out
/// tangent and the next key's in tangent. Translation and scale are interpolated per component,
/// rotation by SQUAD with tangent-derived control points (<see cref="Squad"/>). Before the first key and
/// after the last one the nearest key is held.
/// </summary>
public sealed class HermiteKeyInterpolator : IKeyInterpolator
{
    public static readonly HermiteKeyInterpolator Instance = new();

    public Transform Interpolate(AnimTrack track, TrackKeys keys, float time)
    {
        if (!keys.Segment(time, out int prev, out float u, out int hold)) return keys.Values[hold];
        if (keys.IsStep(prev)) return keys.Values[prev];
        var a = keys.Values[prev];
        var b = keys.Values[prev + 1];
        var m0 = keys.TangentsOut[prev];
        var m1 = keys.TangentsIn[prev + 1];
        float u2 = u * u, u3 = u2 * u;
        float h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
        return new Transform(
            h00 * a.Translation + h10 * m0.Translation + h01 * b.Translation + h11 * m1.Translation,
            Squad(a.Rotation, b.Rotation, m0.Rotation, m1.Rotation, u),
            Vector3.Max(h00 * a.Scale + h10 * m0.Scale + h01 * b.Scale + h11 * m1.Scale, new Vector3(MinScale)));
    }

    /// <summary>
    /// Per-component floor on an interpolated scale:
    /// a Hermite overshoot below it is held there instead of going through zero.
    /// </summary>
    public const float MinScale = 0.001f;

    /// <summary>
    /// Rotation between keys a and b: SQUAD whose
    /// inner control points come from the tangents, sa = a·exp((out − L)/2) and
    /// sb = b·exp((L − in)/2) with L = log(a⁻¹b) and b taken on a's hemisphere. Tangents are log
    /// vectors (axis × half angle) in the key's frame. The slerps are plain 4D slerps without a
    /// hemisphere flip (<see cref="Slerp4"/>).
    /// </summary>
    public static Quaternion Squad(Quaternion a, Quaternion b, Quaternion tangentOut, Quaternion tangentIn, float u)
    {
        if (Quaternion.Dot(a, b) <= 0) b = -b;
        static Vector3 V(Quaternion q) => new(q.X, q.Y, q.Z);
        var l = Log(Quaternion.Conjugate(a) * b);
        var sa = a * Exp((V(tangentOut) - l) * 0.5f);
        var sb = b * Exp((l - V(tangentIn)) * 0.5f);
        return Slerp4(Slerp4(a, b, u), Slerp4(sa, sb, u), 2 * u * (1 - u));
    }

    /// <summary>
    /// Plain 4D slerp (p·sin((1−t)ω) + q·sin(tω)) / sin ω, without flipping q onto p's hemisphere;
    /// a component-wise lerp when dot(p, q) &gt; 0.9999999.
    /// </summary>
    public static Quaternion Slerp4(Quaternion p, Quaternion q, float t)
    {
        float cos = Quaternion.Dot(p, q);
        if (cos > 0.9999999f) return p + (q - p) * t;
        float omega = MathF.Acos(cos), sin = MathF.Sin(omega);
        return (p * MathF.Sin((1 - t) * omega) + q * MathF.Sin(t * omega)) * (1 / sin);
    }

    /// <summary>Shortest-path slerp: p·exp(u·log(p⁻¹·q)) with the log on the w ≥ 0 hemisphere.</summary>
    public static Quaternion Slerp(Quaternion p, Quaternion q, float u) =>
        p * Exp(u * Log(Quaternion.Inverse(p) * q));

    /// <summary>Quaternion logarithm (axis × half angle), on the w ≥ 0 hemisphere.</summary>
    public static Vector3 Log(Quaternion q)
    {
        if (q.W < 0) q = -q;
        var v = new Vector3(q.X, q.Y, q.Z);
        float sin = v.Length();
        return sin < 1e-7f ? v : v * (MathF.Atan2(sin, q.W) / sin);
    }

    /// <summary>Inverse of <see cref="Log"/>.</summary>
    public static Quaternion Exp(Vector3 v)
    {
        float angle = v.Length();
        if (angle < 1e-7f) return Quaternion.Normalize(new Quaternion(v, 1));
        return new Quaternion(v * (MathF.Sin(angle) / angle), MathF.Cos(angle));
    }
}

/// <summary>
/// Evaluates a character's controllers at a time, following:
/// each controller's rest transform (from the reference pose) composed with the delta mixed
/// from the animation's stacks. IK chains play their joints' own keys, except inside IK
/// override segments, where they are re-solved toward the controller (<see cref="IKSolver"/>).
/// </summary>
/// <remarks>
/// Stack data is cached on first use. Call <see cref="Invalidate"/> after editing keys, and create a
/// new evaluator after editing poses.
/// </remarks>
public sealed class PoseEvaluator
{
    private const uint StackCycle = 0x1;
    private const uint StackEnabled = 0x2;
    private const uint StackAdditive = 0x8;

    private readonly Dictionary<AnimStack, StackData> _stacks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Object3D, int> _controllerIndex = new(ReferenceEqualityComparer.Instance);

    /// <param name="restPose">
    /// Overrides the pose the animation is relative to, e.g. a mesh's bind pose to get the joints'
    /// bind-time world matrices.
    /// </param>
    public PoseEvaluator(Character character, IKeyInterpolator? interpolator = null, bool riggingContext = false,
                         CharacterPose? restPose = null)
    {
        Character = character;
        Interpolator = interpolator ?? HermiteKeyInterpolator.Instance;
        _controllers = character.Controllers;
        for (int i = 0; i < _controllers.Count; i++)
            _controllerIndex.TryAdd(_controllers[i], i);

        _restPose = restPose;
        _riggingContext = riggingContext;
        RestTransforms = new Transform[_controllers.Count];
        LoadRestTransforms();
    }

    private readonly CharacterPose? _restPose;

    /// <summary>
    /// Whether the base is the rigging context's pose (<see cref="RestPose"/>); setting it reloads
    /// <see cref="RestTransforms"/>. Ignored when the constructor was given a pose.
    /// </summary>
    public bool RiggingContext
    {
        get => _riggingContext;
        set
        {
            if (_riggingContext == value) return;
            _riggingContext = value;
            ReloadRestPose();
        }
    }
    private bool _riggingContext;

    private Transform?[]? _checkPose;

    /// <summary>
    /// Paint &amp; Check's runtime pose: per-controller local transforms that replace the rest pose
    /// when no animation is evaluated, so the bound skin can be posed to check its weights. The scene
    /// keeps nothing of it, and neither does this: it lives only
    /// in this evaluator, is never written to the scene and is cleared by <see cref="ClearCheckPose"/>.
    /// </summary>
    public bool HasCheckPose => _checkPose != null;

    /// <summary>Controller <paramref name="index"/>'s local transform in the check pose (its rest transform when unposed).</summary>
    public Transform CheckLocal(int index) => _checkPose?[index] ?? RestTransforms[index];

    /// <summary>Whether controller <paramref name="index"/> carries a check-pose value.</summary>
    public Transform? CheckValue(int index) => _checkPose?[index];

    /// <summary>Sets (or, with null, drops) controller <paramref name="index"/>'s check-pose value.</summary>
    public void SetCheckLocal(int index, Transform? local)
    {
        if (local == null && _checkPose == null) return;
        _checkPose ??= new Transform?[Controllers.Count];
        _checkPose[index] = local;
    }

    /// <summary>Drops the whole check pose; the rest pose shows again.</summary>
    public void ClearCheckPose()
    {
        if (_checkPose == null) return;
        _checkPose = null;
    }

    /// <summary>Reads <see cref="RestTransforms"/> again, after the rest pose was edited.</summary>
    public void ReloadRestPose()
    {
        if (_checkPose != null && _checkPose.Length != Controllers.Count) _checkPose = null;
        SyncControllers();
        LoadRestTransforms();
        Invalidate();
    }

    private void LoadRestTransforms()
    {
        var matrices = (_restPose ?? RestPose(Character, RiggingContext))?.Matrices ?? [];
        if (RestTransforms.Length != _controllers.Count) RestTransforms = new Transform[_controllers.Count];
        for (int i = 0; i < _controllers.Count; i++)
        {
            var m = i < matrices.Length ? matrices[i] : _controllers[i].LocalMatrix;
            RestTransforms[i] = Transform.TryFromMatrix(m, out var t) ? t : Transform.Identity;
        }
    }

    /// <summary>
    /// Picks up controllers added or removed since construction (an animated camera is appended to
    /// <c>Character.Controllers</c>, and its pose slots to the character's poses).
    /// </summary>
    private void SyncControllers()
    {
        // Called on every IndexOf and Controllers read, hundreds of times a frame: compare the raw
        // references, and build the wrapper array only when the list really changed.
        if (Character.ControllersMatch(_controllers)) return;
        var list = Character.Controllers;
        _controllers = list;
        _controllerIndex.Clear();
        for (int i = 0; i < list.Count; i++) _controllerIndex.TryAdd(list[i], i);
        RestTransforms = new Transform[list.Count];
        _checkPose = null;
        LoadRestTransforms();
        _ikSolveOrder = null;
        Invalidate();
    }

    public Character Character { get; }
    public IKeyInterpolator Interpolator { get; }

    private IReadOnlyList<Object3D> _controllers = [];

    /// <summary>Everything that can be animated, in the order of the character's pose matrices.</summary>
    public IReadOnlyList<Object3D> Controllers
    {
        get
        {
            SyncControllers();
            return _controllers;
        }
    }

    /// <summary>
    /// Whether objects with <see cref="Object3D.CompensatesParentScale"/> use scale-compensated
    /// composition (the behaviour, the default); false chains every matrix plainly.
    /// </summary>
    public bool ScaleCompensation { get; init; } = true;

    /// <summary>Base transform of each controller (indexed like <see cref="Controllers"/>).</summary>
    public Transform[] RestTransforms { get; private set; }

    /// <summary>
    /// The pose animation is relative to: <c>RefPose</c>, or in the rigging context
    /// <c>CurrentPose</c>, then a bind pose, then <c>RefPose</c>.
    /// </summary>
    public static CharacterPose? RestPose(Character character, bool riggingContext) =>
        riggingContext
            ? character.CurrentPose ?? character.BindPoses.FirstOrDefault() ?? character.RefPose
            : character.RefPose;

    /// <summary>Forgets the cached stack data, so that edited keys are read again.</summary>
    public void Invalidate()
    {
        _stacks.Clear();
        _compensateKeys.Clear();
    }

    // Compensate Scale keys per animation, read once.
    private readonly Dictionary<Animation, Dictionary<Akt.AktObject, CompensateScaleKeys.Key[]>?> _compensateKeys =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Whether <paramref name="o"/> composes with its parent's scale compensated at
    /// <paramref name="time"/> of <paramref name="animation"/>: its keyed Compensate Scale there, else
    /// the rigging value; always false with <see cref="ScaleCompensation"/> off.
    /// </summary>
    public bool Compensates(Object3D o, Animation? animation, float time)
    {
        if (!ScaleCompensation) return false;
        if (animation == null) return o.CompensatesParentScale;
        if (!_compensateKeys.TryGetValue(animation, out var table))
            _compensateKeys[animation] = table = CompensateScaleKeys.Table(animation);
        return table != null && table.TryGetValue(o.Source, out var keys) && CompensateScaleKeys.KeyedValue(keys, time) is { } keyed
            ? keyed
            : o.CompensatesParentScale;
    }

    /// <summary>Index of <paramref name="controller"/> in <see cref="Controllers"/>, or -1.</summary>
    public int IndexOf(Object3D controller)
    {
        SyncControllers();
        return _controllerIndex.GetValueOrDefault(controller, -1);
    }

    /// <summary>
    /// Local transform of every controller (relative to its parent); with no animation, the rest
    /// pose. A controller in a space-switching parent is converted back to its own parent.
    /// </summary>
    public Transform[] LocalTransforms(Animation? animation, float time) => Evaluate(animation, time, out _);

    /// <summary><see cref="LocalTransforms(Animation?, float)"/> and <see cref="WorldMatrices"/> from one evaluation.</summary>
    public Transform[] LocalTransforms(Animation? animation, float time, out Matrix4x4[] worldMatrices) =>
        Evaluate(animation, time, out worldMatrices);

    /// <summary>
    /// World matrix of every controller (<see cref="WorldComposition"/>). Ancestors that are not
    /// controllers (the character, folders) contribute their stored <c>LTM</c>.
    /// </summary>
    public Matrix4x4[] WorldMatrices(Animation? animation, float time)
    {
        Evaluate(animation, time, out var world);
        return world;
    }

    /// <summary>
    /// Rest composed with the mixed delta for every controller, placed under its space-switching
    /// parent where its keys name one, else under its parent.
    /// </summary>
    private Transform[] Evaluate(Animation? animation, float time, out Matrix4x4[] worldMatrices) =>
        Evaluate(animation, time, out worldMatrices, out _);

    private Transform[] Evaluate(Animation? animation, float time, out Matrix4x4[] worldMatrices,
        out Dictionary<StackData, Dictionary<Object3D, Transform>>? overrides)
    {
        int n = Controllers.Count;
        var local = new Transform[n];
        var space = new Object3D?[n];
        bool anySpace = false;
        overrides = animation != null && SolveIK ? IKOverrides(animation, time) : null;
        // Paint & Check's runtime pose shows only while that row is current (CurrentPose null).
        var check = animation == null && _riggingContext && Character.CurrentPose == null ? _checkPose : null;
        for (int i = 0; i < n; i++)
        {
            if (animation == null) { local[i] = check?[i] ?? RestTransforms[i]; continue; }
            local[i] = Compose(RestTransforms[i], MixDelta(animation, Controllers[i], time, overrides));
            space[i] = SpaceOf(animation, Controllers[i], time);
            anySpace |= space[i] != null;
        }
        return ComposeWorlds(local, space, anySpace, animation, time, out worldMatrices);
    }

    /// <summary>
    /// The local transforms (relative to each parent) that <paramref name="stack"/> alone produces
    /// at <paramref name="time"/>, and the world matrices they give: the per-stack values the FBX
    /// export writes. A controller without a track in the
    /// stack keeps the rest pose; a **disabled** stack still contributes its own keys, because
    /// The app exports disabled stacks too. IK chains are not re-solved: the stack's keys already
    /// hold the solved chain.
    /// </summary>
    public Transform[] StackLocalTransforms(AnimStack stack, float time, out Matrix4x4[] worldMatrices)
    {
        int n = Controllers.Count;
        var data = Data(stack);
        var local = new Transform[n];
        var space = new Object3D?[n];
        bool anySpace = false;
        for (int i = 0; i < n; i++)
        {
            local[i] = RestTransforms[i];
            if (!data.Tracks.TryGetValue(Controllers[i], out var track)) continue;
            local[i] = Compose(RestTransforms[i], LayerValue(data, track, time, out _));
            space[i] = track.SpaceAt(data.Keys, LocalTime(data.Keys, data.Flags, time));
            anySpace |= space[i] != null;
        }
        return ComposeWorlds(local, space, anySpace, stack.Animation, time, out worldMatrices);
    }

    /// <summary>
    /// Composes the world matrices of <paramref name="local"/> (each placed under its
    /// space-switching parent where <paramref name="space"/> names one) and converts the
    /// space-switched controllers back to their real parent. Returns the adjusted locals.
    /// </summary>
    private Transform[] ComposeWorlds(Transform[] local, Object3D?[] space, bool anySpace, Animation? animation, float time,
        out Matrix4x4[] worldMatrices)
    {
        int n = local.Length;
        var matrices = local.Select(t => t.ToMatrix()).ToArray();
        var world = new Matrix4x4?[n];
        var others = new Dictionary<Object3D, Matrix4x4>(ReferenceEqualityComparer.Instance);

        Matrix4x4 WorldOf(Object3D o)
        {
            int i = IndexOf(o);
            if (i >= 0 && world[i] is { } cached) return cached;
            if (i < 0 && others.TryGetValue(o, out var other)) return other;
            var parentObject = i >= 0 && space[i] is { } s ? s : o.Parent3D;
            var localMatrix = i >= 0 ? matrices[i] : o.LocalMatrix;
            var m = parentObject is { } p
                ? WorldComposition.Compose(localMatrix, WorldOf(p), ScaleOf(p), Compensates(o, animation, time))
                : localMatrix;
            if (i >= 0) world[i] = m; else others[o] = m;
            return m;
        }

        Vector3 ScaleOf(Object3D o) => IndexOf(o) is var i and >= 0 ? local[i].Scale : Object3D.LocalScale(o.LocalMatrix);

        worldMatrices = Controllers.Select(WorldOf).ToArray();
        if (anySpace)
            for (int i = 0; i < n; i++)
            {
                if (space[i] == null) continue;
                var c = Controllers[i];
                var (parent, parentScale) = c.Parent3D is { } p ? (WorldOf(p), ScaleOf(p)) : (Matrix4x4.Identity, Vector3.One);
                if (WorldComposition.TryDecompose(worldMatrices[i], parent, parentScale, Compensates(c, animation, time), out var m)
                    && Transform.TryFromMatrix(m, out var t))
                    local[i] = t;
            }
        return local;
    }

    /// <summary>
    /// Re-solves IK chains inside override segments.
    /// When false, chain joints always play their own keys.
    /// </summary>
    public bool SolveIK { get; init; } = true;

    /// <summary>
    /// IK and reverse-foot solves land on their target even when the chain or its ancestors carry
    /// non-uniform scale (<see cref="IKSolver.Solve(Matrix4x4, Vector3, IReadOnlyList{Transform}, Matrix4x4, IReadOnlyList{bool}, bool, float)"/>).
    /// False keeps the plain per-key solve, which misses there.
    /// </summary>
    public bool ExactScaleIK { get; init; } = true;

    /// <summary>
    /// For every IK controller whose track is inside an override segment at <paramref name="time"/>,
    /// the solved chain as values that replace the chain joints' own values in the IK track's
    /// stack, keyed by stack and joint. Null when no chain is solved.
    /// </summary>
    private Dictionary<StackData, Dictionary<Object3D, Transform>>? IKOverrides(Animation animation, float time)
    {
        Dictionary<StackData, Dictionary<Object3D, Transform>>? overrides = null;
        foreach (var controller in IKSolveOrder)
        {
            if (OverrideString(animation, controller, time, out var stack, out var segment) is not { } chainLocal) continue;
            var chain = controller.Chain;
            var indices = chain.Select(IndexOf).ToArray();
            if (indices.Any(i => i < 0)) continue;

            overrides ??= new(ReferenceEqualityComparer.Instance);
            if (!overrides.TryGetValue(stack!, out var values))
                overrides[stack!] = values = new(ReferenceEqualityComparer.Instance);

            Transform[]? result;
            if (controller is ControllerReverseFoot rf)
                result = ReverseFootOverride(animation, rf, stack!, segment, chainLocal, indices, values, time);
            else
            {
                var cache = new Dictionary<Object3D, (Matrix4x4 World, Vector3 Scale)>(ReferenceEqualityComparer.Instance);
                var (@base, baseScale) = chain[0].Parent3D is { } parent
                    ? StackWorld(animation, stack!, parent, time, cache, values) : (Matrix4x4.Identity, Vector3.One);
                var target = StackWorld(animation, stack!, controller, time, cache, values).World;
                result = IKSolver.Solve(@base, baseScale, chainLocal, target,
                    chain.Select(j => Compensates(j, animation, time)).ToArray(), ExactScaleIK);
            }
            if (result == null) continue;
            for (int j = 0; j < chain.Count; j++) values[chain[j]] = Decompose(RestTransforms[indices[j]], result[j]);
        }
        return overrides;
    }

    /// <summary>
    /// The IK controllers in solve order:
    /// a controller whose base or target lies below another controller's chain joints is solved
    /// after it, so its single-stack world sees that chain already solved. Otherwise file order.
    /// </summary>
    private IReadOnlyList<ControllerIK> IKSolveOrder => _ikSolveOrder ??= OrderIKControllers(Character.IKControllers);

    /// <summary>The character's IK controllers in solve order (see <see cref="IKSolveOrder"/>).</summary>
    public IReadOnlyList<ControllerIK> IKControllersInSolveOrder => IKSolveOrder;

    private IReadOnlyList<ControllerIK>? _ikSolveOrder;

    private static List<ControllerIK> OrderIKControllers(IEnumerable<ControllerIK> all)
    {
        static IEnumerable<Object3D> Ancestors(Object3D? o)
        {
            for (; o != null; o = o.Parent3D) yield return o;
        }
        // Objects whose single-stack world each controller reads: its base (above the first
        // joint) and its target (the controller itself).
        bool DependsOn(ControllerIK c, ControllerIK other) =>
            !ReferenceEquals(c, other) && other.Chain.Any(j =>
                Ancestors(c.Chain.Count > 0 ? c.Chain[0].Parent3D : null).Concat(Ancestors(c)).Contains(j, ReferenceEqualityComparer.Instance));

        var controllers = all.ToList();
        var ordered = new List<ControllerIK>();
        var state = new Dictionary<ControllerIK, int>(ReferenceEqualityComparer.Instance);
        void Visit(ControllerIK c)
        {
            if (state.GetValueOrDefault(c) != 0) return; // done, or a cycle: keep file order
            state[c] = 1;
            foreach (var other in controllers)
                if (DependsOn(c, other)) Visit(other);
            state[c] = 2;
            ordered.Add(c);
        }
        foreach (var c in controllers) Visit(c);
        return ordered;
    }

    /// <summary>
    /// A reverse-foot controller inside an override segment
    /// the shoe locators' values from roll, twist and bank go into
    /// <paramref name="values"/>, and the leg is solved toward the shoe. Null if it cannot be solved.
    /// </summary>
    private Transform[]? ReverseFootOverride(Animation animation, ControllerReverseFoot rf, StackData stack,
        OverrideSegment segment, Transform[] chainLocal, int[] indices, Dictionary<Object3D, Transform> values, float time)
    {
        var shoe = rf.Shoe;
        if (shoe.Count < 7 || chainLocal.Length != 4) return null;
        var shoeIndices = shoe.Take(7).Select(IndexOf).ToArray();
        if (shoeIndices.Any(i => i < 0)) return null;
        var track = stack.Tracks[rf].Track;

        bool hold = IsStepMode(track.TangentModes, segment.A);

        float Angle(Func<ReverseFootKey, float> channel)
        {
            float Value(IReadOnlyList<AnimKey?> list, int i) => i < list.Count && list[i] is ReverseFootKey k ? channel(k) : 0f;
            if (hold) return Value(track.Keys, segment.A);
            return Hermite(Value(track.Keys, segment.A), Value(track.Keys, segment.B),
                Value(track.TangentsOut, segment.A), Value(track.TangentsIn, segment.Next), segment.U);
        }

        var rest = shoeIndices.Select(i => RestTransforms[i]).ToArray();
        var axis = rf.RollAxisPlant;
        var locals = ReverseFootSolver.ShoeLocals(rest, Angle(k => k.RollAngle), Angle(k => k.TwistAngle), Angle(k => k.BankAngle),
            ReverseFootLimits.FromScene(Character.Model.Scene), new Vector3(axis.X, axis.Y, axis.Z));
        for (int j = 0; j < 7; j++) values[shoe[j]] = Decompose(rest[j], locals[j]);

        var cache = new Dictionary<Object3D, (Matrix4x4 World, Vector3 Scale)>(ReferenceEqualityComparer.Instance);
        var chain = rf.Chain;
        var (@base, baseScale) = chain[0].Parent3D is { } parent
            ? StackWorld(animation, stack, parent, time, cache, values) : (Matrix4x4.Identity, Vector3.One);
        Matrix4x4 World(int locator) => StackWorld(animation, stack, shoe[locator], time, cache, values).World;
        return ReverseFootSolver.Solve(@base, baseScale, chainLocal,
            chain.Select(j => Compensates(j, animation, time)).ToArray(),
            RestTransforms[indices[3]], rest[ReverseFootSolver.SkeletonPlant],
            World(ReverseFootSolver.SkeletonAnkle), World(ReverseFootSolver.SkeletonPlant), World(ReverseFootSolver.SkeletonToes),
            ExactScaleIK);
    }

    /// <summary>
    /// Whether key <paramref name="i"/> has tangent-out type 5 on channel 0, which holds the key
    /// until the next one. Observed modes carry the same
    /// type in both nibbles, so which nibble is "out" does not matter here.
    /// </summary>
    private static bool IsStepMode(IReadOnlyList<TangentMode[]> modes, int i) =>
        i < modes.Count && modes[i] is { Length: > 0 } m && m[0].LowMode == 5;

    /// <summary>Cubic Hermite from a to b with the tangents used as stored.</summary>
    private static float Hermite(float a, float b, float outA, float inB, float u)
    {
        float u2 = u * u, u3 = u2 * u;
        return (2 * u3 - 3 * u2 + 1) * a + (-2 * u3 + 3 * u2) * b + (u3 - 2 * u2 + u) * outA + (u3 - u2) * inB;
    }

    /// <summary>
    /// An IK track's override segment: stack key indices of its start key, its end key (key 0 when
    /// the segment ends at the loop key) and the next present key, and the parameter u.
    /// </summary>
    private readonly record struct OverrideSegment(int A, int B, int Next, float U);

    /// <summary>
    /// The world transform of <paramref name="o"/> and its local scale, evaluated from one stack
    /// only. A character is the
    /// identity.
    /// </summary>
    private (Matrix4x4 World, Vector3 Scale) StackWorld(Animation animation, StackData data, Object3D o, float time,
        Dictionary<Object3D, (Matrix4x4 World, Vector3 Scale)> cache, Dictionary<Object3D, Transform>? values = null)
    {
        if (o is Character) return (Matrix4x4.Identity, Vector3.One);
        if (cache.TryGetValue(o, out var cached)) return cached;
        int i = IndexOf(o);
        Matrix4x4 local;
        Vector3 scale;
        if (i >= 0)
        {
            var t = values != null && values.TryGetValue(o, out var solved) ? Compose(RestTransforms[i], solved)
                : data.Tracks.TryGetValue(o, out var track) ? Compose(RestTransforms[i], LayerValue(data, track, time, out _))
                : RestTransforms[i];
            (local, scale) = (t.ToMatrix(), t.Scale);
        }
        else (local, scale) = (o.LocalMatrix, Object3D.LocalScale(o.LocalMatrix));

        var parent = (i >= 0 ? SpaceOf(animation, o, time) : null) ?? o.Parent3D;
        var world = local;
        if (parent != null)
        {
            var (parentWorld, parentScale) = StackWorld(animation, data, parent, time, cache, values);
            world = WorldComposition.Compose(local, parentWorld, parentScale, Compensates(o, animation, time));
        }
        return cache[o] = (world, scale);
    }

    /// <summary>
    /// The world positions of <paramref name="objects"/> in the single-stack world of the stack
    /// that holds <paramref name="controller"/>'s track (the first enabled, non-additive one), with
    /// every chain solved at <paramref name="time"/> in place: the world an IK solve sees, without
    /// the motion other stacks (a root-motion layer) add.
    /// Null when no such stack has a track for the controller.
    /// </summary>
    public Vector3[]? OwnStackPositions(Animation animation, ControllerIK controller, float time, params Object3D[] objects)
    {
        StackData? own = null;
        foreach (var stack in animation.Stacks)
        {
            var data = Data(stack);
            if ((data.Flags & StackEnabled) == 0 || (data.Flags & StackAdditive) != 0) continue;
            if (data.Tracks.ContainsKey(controller)) { own = data; break; }
        }
        if (own == null) return null;
        Dictionary<Object3D, Transform>? values = null;
        if (SolveIK) IKOverrides(animation, time)?.TryGetValue(own, out values);
        var cache = new Dictionary<Object3D, (Matrix4x4 World, Vector3 Scale)>(ReferenceEqualityComparer.Instance);
        return objects.Select(o => StackWorld(animation, own, o, time, cache, values).World.Translation).ToArray();
    }

    /// <summary>
    /// <see cref="WorldMatrices"/> for drawing and picking: the same matrices, except that an IK or
    /// reverse-foot controller (and the non-joint objects under it: shoe locators, props) whose
    /// track lives in an enabled non-additive stack is carried along by its chain's leaf joint. Its keys place it in
    /// that stack's single-stack world, where the chain is solved; the mixed pose adds what the other
    /// stacks key on the joints above it (a TransN root-motion layer) but not on the controller,
    /// which sits under the character. So the controller is drawn at its offset from the leaf joint
    /// in its own stack, applied to the leaf joint's mixed world
    /// With one enabled stack the two worlds agree. Where the IK is not active
    /// (<see cref="IsIKActive"/>: FK keys, or no track at all) the controller drives nothing, and it
    /// follows the leaf joint's mixed world at its rest-pose offset instead.
    /// </summary>
    public Matrix4x4[] DisplayWorldMatrices(Animation? animation, float time)
    {
        Evaluate(animation, time, out var world, out var overrides);
        if (animation == null) return world;
        var enabled = animation.Stacks.Select(Data).Where(d => (d.Flags & StackEnabled) != 0).ToList();

        var restCache = new Dictionary<Object3D, (Matrix4x4 World, Vector3 Scale)>(ReferenceEqualityComparer.Instance);
        (Matrix4x4 World, Vector3 Scale) RestWorld(Object3D o)
        {
            if (o is Character) return (Matrix4x4.Identity, Vector3.One);
            if (restCache.TryGetValue(o, out var cached)) return cached;
            int i = IndexOf(o);
            var (local, scale) = i >= 0 ? (RestTransforms[i].ToMatrix(), RestTransforms[i].Scale)
                : (o.LocalMatrix, Object3D.LocalScale(o.LocalMatrix));
            var w = local;
            if (o.Parent3D is { } parent)
            {
                var (pw, ps) = RestWorld(parent);
                w = WorldComposition.Compose(local, pw, ps, Compensates(o, null, 0));
            }
            return restCache[o] = (w, scale);
        }

        var joints = new HashSet<Object3D>(Character.Joints, ReferenceEqualityComparer.Instance);
        ControllerIK? OwnerOf(Object3D o)
        {
            for (var x = o; x != null; x = x.Parent3D)
            {
                if (x is Character) return null;
                if (x is ControllerIK ik) return ik;
                if (joints.Contains(x)) return null;
            }
            return null;
        }

        Matrix4x4[]? display = null;
        var owned = new Dictionary<ControllerIK, List<int>>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < Controllers.Count; i++)
            if (OwnerOf(Controllers[i]) is { } ik)
            {
                if (!owned.TryGetValue(ik, out var list)) owned[ik] = list = new List<int>();
                list.Add(i);
            }

        foreach (var (controller, members) in owned)
        {
            var chain = controller.Chain;
            int leaf = chain.Count > 0 ? IndexOf(chain[^1]) : -1;
            if (leaf < 0) continue;
            // An IK that is not pinning its chain here (FK keys, or no track at all: a retargeted
            // animation whose source had no such handle) does not drive anything, so its handle
            // follows the leaf joint at its rigged offset, like a bone's child.
            if (!IsIKActive(animation, controller, time))
            {
                if (!Matrix4x4.Invert(RestWorld(chain[^1]).World, out var restToLeaf)) continue;
                display ??= (Matrix4x4[])world.Clone();
                foreach (int i in members)
                    display[i] = RestWorld(Controllers[i]).World * restToLeaf * world[leaf];
                continue;
            }
            // The stack whose single-stack world the chain is solved in: the first enabled,
            // non-additive stack keying the controller (as OwnStackPositions). Other stacks may key
            // it too (an "All" stack plus layers, a root-motion layer keying every controller);
            // the leaf joint's mixed world still carries what they add.
            StackData? own = null;
            foreach (var d in enabled)
                if ((d.Flags & StackAdditive) == 0 && d.Tracks.ContainsKey(controller)) { own = d; break; }
            if (own == null) continue;

            Dictionary<Object3D, Transform>? values = null;
            overrides?.TryGetValue(own, out values);
            var cache = new Dictionary<Object3D, (Matrix4x4 World, Vector3 Scale)>(ReferenceEqualityComparer.Instance);
            var leafOwn = StackWorld(animation, own, chain[^1], time, cache, values).World;
            if (!Matrix4x4.Invert(leafOwn, out var toLeaf)) continue;
            var toMixed = toLeaf * world[leaf];
            display ??= (Matrix4x4[])world.Clone();
            foreach (int i in members)
                display[i] = StackWorld(animation, own, Controllers[i], time, cache, values).World * toMixed;
        }
        return display ?? world;
    }

    /// <summary>
    /// Every controller's local transform (rest composed with <paramref name="stack"/>'s own value,
    /// or rest where the stack has no track) and world matrix in the single-stack world of
    /// <paramref name="stack"/>, with the chains solved at <paramref name="time"/> in place: the
    /// world an IK solve in that stack sees, without what the other stacks add
    /// Indexed like <see cref="Controllers"/>.
    /// </summary>
    public Transform[] StackPose(Animation animation, AnimStack stack, float time, out Matrix4x4[] world)
    {
        var data = Data(stack);
        Dictionary<Object3D, Transform>? values = null;
        if (SolveIK) IKOverrides(animation, time)?.TryGetValue(data, out values);
        var cache = new Dictionary<Object3D, (Matrix4x4 World, Vector3 Scale)>(ReferenceEqualityComparer.Instance);
        var objects = Controllers;
        var local = new Transform[objects.Count];
        world = new Matrix4x4[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            world[i] = StackWorld(animation, data, o, time, cache, values).World;
            local[i] = values != null && values.TryGetValue(o, out var solved) ? Compose(RestTransforms[i], solved)
                : data.Tracks.TryGetValue(o, out var track) ? Compose(RestTransforms[i], LayerValue(data, track, time, out _))
                : RestTransforms[i];
        }
        return local;
    }

    /// <summary>
    /// The chain joints' interpolated local transforms when <paramref name="controller"/>'s track
    /// is strictly inside an override segment (both keys with flag bit 0) at <paramref name="time"/>,
    /// else null; at a key's own time the chain plays its keys. The track is the one in the first
    /// enabled override stack that has one.
    /// </summary>
    public Transform[]? OverrideString(Animation animation, ControllerIK controller, float time) =>
        OverrideString(animation, controller, time, out _, out _);

    private Transform[]? OverrideString(Animation animation, ControllerIK controller, float time, out StackData? stackData,
        out OverrideSegment segment)
    {
        stackData = null;
        segment = default;
        foreach (var stack in animation.Stacks)
        {
            var data = Data(stack);
            if ((data.Flags & StackEnabled) == 0 || (data.Flags & StackAdditive) != 0) continue;
            if (!data.Tracks.TryGetValue(controller, out var track)) continue;

            float t = LocalTime(data.Keys, data.Flags, time);
            var keys = track.PresentIndices(data.Keys.Count);
            int m = keys.Length;
            if (m < 2) return null;
            int seg = -1;
            for (int j = 0; j < m - 1; j++)
                if (data.Keys[keys[j]].Time < t && t < data.Keys[keys[j + 1]].Time) { seg = j; break; }
            if (seg < 0) return null;
            stackData = data;

            // The last key loops back: its flags and joint values are key 0's.
            int a = keys[seg], b = seg + 1 == m - 1 ? keys[0] : keys[seg + 1];
            if ((track.IKFlags(a) & 1) == 0 || (track.IKFlags(b) & 1) == 0) return null;
            float ta = data.Keys[a].Time, tb = data.Keys[keys[seg + 1]].Time;
            float u = (t - ta) / (tb - ta);
            segment = new OverrideSegment(a, b, keys[seg + 1], u);

            // A stepped key on the IK track holds the string at key a.
            bool step = IsStepMode(track.Track.TangentModes, a);
            var chain = controller.Chain;
            var result = new Transform[chain.Count];
            for (int j = 0; j < chain.Count; j++)
            {
                int ci = IndexOf(chain[j]);
                var rest = ci >= 0 ? RestTransforms[ci] : Transform.Identity;
                var ka = Compose(rest, JointKey(data, chain[j], a));
                var kb = Compose(rest, JointKey(data, chain[j], b));
                result[j] = step ? ka : Lerp(ka, kb, u);
            }
            return result;
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="controller"/>'s IK is active at <paramref name="time"/>: at a key,
    /// that key's IK flag bit 0 (the last key counts as key 0); between keys, inside an override
    /// segment.
    /// </summary>
    public bool IsIKActive(Animation animation, ControllerIK controller, float time)
    {
        foreach (var stack in animation.Stacks)
        {
            var data = Data(stack);
            if ((data.Flags & StackEnabled) == 0 || (data.Flags & StackAdditive) != 0) continue;
            if (!data.Tracks.TryGetValue(controller, out var track)) continue;

            float t = LocalTime(data.Keys, data.Flags, time);
            var keys = track.PresentIndices(data.Keys.Count);
            for (int j = 0; j < keys.Length; j++)
                if (data.Keys[keys[j]].Time == t)
                    return (track.IKFlags(j == keys.Length - 1 ? keys[0] : keys[j]) & 1) != 0;
            return OverrideString(animation, controller, time) != null;
        }
        return false;
    }

    /// <summary>
    /// Where an edit of <paramref name="controller"/> starts when its IK is not active at
    /// <paramref name="time"/> (an FK key): the local transform that puts it at its rigged offset from
    /// its chain's leaf joint, which is where <see cref="DisplayWorldMatrices"/> draws it. That is
    /// solved in the controller's own stack's single-stack world, the world an IK edit is solved in
    /// Null when the IK is
    /// active, or when there is no chain or own stack: the stored key is then where it is drawn.
    /// The key at an FK key is a leftover (a retarget writes whatever the source had) and can be
    /// far from the hand; a drag from it threw the chain there.
    /// </summary>
    public Transform? FollowingLocal(Animation animation, ControllerIK controller, float time)
    {
        if (controller.Chain.Count == 0 || IsIKActive(animation, controller, time)) return null;
        var own = animation.Stacks.FirstOrDefault(s => s.IsEnabled && !s.IsAdditive && s.TrackFor(controller) != null);
        if (own == null) return null;
        int c = IndexOf(controller), leaf = IndexOf(controller.Chain[^1]);
        if (c < 0 || leaf < 0) return null;
        var locals = StackPose(animation, own, time, out var world);
        if (!Matrix4x4.Invert(RestWorld(controller.Chain[^1]), out var restToLeaf)) return null;
        var target = RestWorld(controller) * restToLeaf * world[leaf];
        var parentWorld = Matrix4x4.Identity;
        var parentScale = Vector3.One;
        if (controller.Parent3D is { } parent and not SloppyAnimatsu.Core.Model.Character)
        {
            int p = IndexOf(parent);
            if (p < 0) return null;
            parentWorld = world[p];
            parentScale = locals[p].Scale;
        }
        if (!WorldComposition.TryDecompose(target, parentWorld, parentScale, Compensates(controller, animation, time), out var local))
            return null;
        // Keep the stored scale: only where the handle sits and how it is turned follow the leaf.
        return Transform.FromMatrixLoose(local) with { Scale = locals[c].Scale };
    }

    /// <summary>An object's world matrix in the rest pose (rest transforms composed up to the character).</summary>
    private Matrix4x4 RestWorld(Object3D o)
    {
        if (o is SloppyAnimatsu.Core.Model.Character) return Matrix4x4.Identity;
        int i = IndexOf(o);
        var local = i >= 0 ? RestTransforms[i].ToMatrix() : o.LocalMatrix;
        if (o.Parent3D is not { } parent) return local;
        var scale = parent is SloppyAnimatsu.Core.Model.Character ? Vector3.One
            : IndexOf(parent) is var pi and >= 0 ? RestTransforms[pi].Scale : Object3D.LocalScale(parent.LocalMatrix);
        return WorldComposition.Compose(local, RestWorld(parent), scale, Compensates(o, null, 0));
    }

    /// <summary>A chain joint's own key value at stack key <paramref name="index"/>, interpolated if the joint has no key there.</summary>
    private Transform JointKey(StackData data, Object3D joint, int index)
    {
        if (!data.Tracks.TryGetValue(joint, out var track)) return Transform.Identity;
        if (track.HasKey(index)) return track.ValueAt(index);
        var keys = track.Present(data.Keys);
        return keys.Keys.Count == 0 ? Transform.Identity : Interpolator.Interpolate(track.Track, keys, data.Keys[index].Time);
    }

    /// <summary>Linear translation and scale, slerp rotation (b moved onto a's hemisphere).</summary>
    private static Transform Lerp(Transform a, Transform b, float u)
    {
        var rb = Quaternion.Dot(a.Rotation, b.Rotation) < 0 ? -b.Rotation : b.Rotation;
        return new Transform(
            Vector3.Lerp(a.Translation, b.Translation, u),
            Quaternion.Slerp(a.Rotation, rb, u),
            Vector3.Lerp(a.Scale, b.Scale, u));
    }

    /// <summary>
    /// The space-switching parent of <paramref name="controller"/> at <paramref name="time"/>, or
    /// null for its own parent: the <c>Space</c> of the key that starts the current segment, in
    /// the first enabled override stack with a track for it.
    /// </summary>
    public Object3D? SpaceOf(Animation animation, Object3D controller, float time)
    {
        foreach (var stack in animation.Stacks)
        {
            var data = Data(stack);
            if ((data.Flags & StackEnabled) == 0 || (data.Flags & StackAdditive) != 0) continue;
            if (!data.Tracks.TryGetValue(controller, out var track)) continue;
            return track.SpaceAt(data.Keys, LocalTime(data.Keys, data.Flags, time));
        }
        return null;
    }

    /// <summary>
    /// The delta of <paramref name="controller"/>: stacks mixed from the last to the first,
    /// skipping disabled stacks and stacks without a track for it.
    /// </summary>
    public Transform MixDelta(Animation animation, Object3D controller, float time) =>
        MixDelta(animation, controller, time, null);

    /// <summary>
    /// The delta mixed from the stacks below <paramref name="stack"/> only (those after it in
    /// <see cref="Animation.Stacks"/>), as editing that stack sees them
    /// IK chains are not re-solved.
    /// </summary>
    public Transform MixDeltaBelow(Animation animation, AnimStack stack, Object3D controller, float time)
    {
        int index = -1;
        var stacks = animation.Stacks;
        for (int s = 0; s < stacks.Count; s++)
            if (ReferenceEquals(stacks[s], stack)) index = s;
        if (index < 0) throw new ArgumentException($"{stack} is not a stack of {animation}.", nameof(stack));
        return MixDelta(animation, controller, time, null, index + 1);
    }

    private Transform MixDelta(Animation animation, Object3D controller, float time,
        Dictionary<StackData, Dictionary<Object3D, Transform>>? overrides, int first = 0)
    {
        var delta = Transform.Identity;
        var stacks = animation.Stacks;
        for (int s = stacks.Count - 1; s >= first; s--)
        {
            var data = Data(stacks[s]);
            if ((data.Flags & StackEnabled) == 0 || !data.Tracks.TryGetValue(controller, out var track)) continue;
            var value = LayerValue(data, track, time, out float weight);
            if (overrides != null && overrides.TryGetValue(data, out var values) && values.TryGetValue(controller, out var solved))
                value = solved;
            delta = (data.Flags & StackAdditive) != 0
                ? MixAdditive(delta, value, weight, track.Channels)
                : MixOverride(delta, value, weight, track.Channels);
        }
        return delta;
    }

    /// <summary>The <c>Space</c> of one stack's track at <paramref name="time"/> (null = the target's parent).</summary>
    public Object3D? SpaceOf(AnimStack stack, AnimTrack track, float time)
    {
        var data = Data(stack);
        return new TrackData(track).SpaceAt(data.Keys, LocalTime(data.Keys, data.Flags, time));
    }

    /// <summary>The value of one stack's track at <paramref name="time"/> and its layer weight.</summary>
    public Transform LayerValue(AnimStack stack, AnimTrack track, float time, out float weight) =>
        LayerValue(Data(stack), new TrackData(track), time, out weight);

    private Transform LayerValue(StackData data, TrackData track, float time, out float weight)
    {
        time = LocalTime(data.Keys, data.Flags, time);
        float fade = Timing(data.Keys, time, out int exactKey);
        weight = data.Opacity * fade;
        if (exactKey >= 0 && track.HasKey(exactKey)) return track.ValueAt(exactKey);
        var keys = track.Present(data.Keys);
        if (keys.Keys.Count == 0) return Transform.Identity;
        return Interpolator.Interpolate(track.Track, keys, time);
    }

    /// <summary>
    /// The stack's own time for animation time <paramref name="time"/>
    /// A cycle stack (bit 0) repeats with the period from its first to its last key. Any
    /// other stack stops at its penultimate key, since the last key is only an end marker.
    /// </summary>
    public static float LocalTime(IReadOnlyList<StackKey> keys, uint flags, float time)
    {
        int n = keys.Count;
        if (n < 2) return time;
        if ((flags & StackCycle) != 0)
        {
            float first = keys[0].Time, period = keys[n - 1].Time - first;
            if (period <= 0) return time;
            float r = (time - first) % period;
            return first + (r < 0 ? r + period : r);
        }
        return MathF.Min(time, keys[n - 2].Time);
    }

    /// <summary>The FOV of a camera that no stack animates, and of a new camera key (30 degrees).</summary>
    public const float DefaultCameraFov = 30f;

    /// <summary>
    /// A blendshape controller's two values at <paramref name="time"/>
    /// each enabled stack with a <c>CAnimTrackBlendshape</c> for the
    /// controller mixes toward its key (override) or adds it (additive), with opacity and fades,
    /// from the bottom stack up; the value is clamped after every stack.
    /// </summary>
    public float[] BlendshapeValues(Animation? animation, BlendshapeController controller, float time)
    {
        var values = new float[2];
        ClampBlendshape(controller, values);
        if (animation == null) return values;
        var stacks = animation.Stacks;
        for (int s = stacks.Count - 1; s >= 0; s--)
        {
            var data = Data(stacks[s]);
            if ((data.Flags & StackEnabled) == 0) continue;
            if (!data.Tracks.TryGetValue(controller, out var track)) continue;
            if (track.Track is not BlendshapeTrack blend) continue;
            var layer = LayerBlendshapeValues(data, blend, time, out float weight);
            bool additive = (data.Flags & StackAdditive) != 0;
            for (int i = 0; i < 2; i++)
                values[i] = additive ? values[i] + weight * layer[i] : (1 - weight) * values[i] + weight * layer[i];
            ClampBlendshape(controller, values);
        }
        return values;
    }

    /// <summary>One stack's two blendshape values at <paramref name="time"/> and its layer weight.</summary>
    public float[] LayerBlendshapeValues(AnimStack stack, BlendshapeTrack track, float time, out float weight) =>
        LayerBlendshapeValues(Data(stack), track, time, out weight);

    private float[] LayerBlendshapeValues(StackData data, BlendshapeTrack track, float time, out float weight)
    {
        time = LocalTime(data.Keys, data.Flags, time);
        float fade = Timing(data.Keys, time, out int exactKey);
        weight = data.Opacity * fade;
        var keys = track.Keys;
        if (exactKey >= 0 && exactKey < keys.Count && keys[exactKey] is { } exact) return exact.Values;
        var indices = Enumerable.Range(0, Math.Min(data.Keys.Count, keys.Count)).Where(i => keys[i] != null).ToArray();
        if (indices.Length == 0) return [0f, 0f];
        var values = indices.Select(i => keys[i]!.Values).ToArray();
        // The last key is the loop key: it replays key 0's value.
        if (values.Length > 1) values[^1] = (float[])values[0].Clone();
        var segment = new TrackKeys(indices.Select(i => data.Keys[i]).ToArray(), [], [], []);
        if (!segment.Segment(time, out int prev, out float u, out int hold))
            return (float[])values[Math.Clamp(hold, 0, values.Length - 1)].Clone();
        var result = new float[2];
        for (int c = 0; c < 2; c++)
        {
            if (track.IsStep(indices[prev], c)) { result[c] = values[prev][c]; continue; }
            float a = values[prev][c], b = values[prev + 1][c];
            float m0 = track.TangentOut(indices[prev], c), m1 = track.TangentIn(indices[prev + 1], c);
            float u2 = u * u, u3 = u2 * u;
            result[c] = (2 * u3 - 3 * u2 + 1) * a + (u3 - 2 * u2 + u) * m0
                      + (-2 * u3 + 3 * u2) * b + (u3 - u2) * m1;
        }
        return result;
    }

    /// <summary>
    /// The blendshape key clamp: a group with no channel is 0; a group with
    /// only positive directions (0, 3) is clamped to [0, 1], only negative ones (1, 2) to [-1, 0],
    /// both to [-1, 1].
    /// </summary>
    private static void ClampBlendshape(BlendshapeController controller, float[] values)
    {
        Span<bool> used = stackalloc bool[2];
        Span<float> low = stackalloc float[2];
        Span<float> high = stackalloc float[2];
        foreach (var channel in controller.Channels)
        {
            int group = Math.Clamp(channel.Group, 0, 1);
            used[group] = true;
            if (channel.Direction is 0 or 3) high[group] = 1f;
            else low[group] = -1f;
        }
        for (int i = 0; i < 2; i++)
            values[i] = used[i] ? Math.Clamp(values[i], low[i], high[i]) : 0f;
    }

    /// <summary>
    /// The camera's field of view at <paramref name="time"/>:
    /// the value mixed from the stacks, where FOV is absolute (no rest FOV) and each enabled
    /// override stack interpolates toward its own value with its opacity and fade. Additive stacks
    /// never animate FOV. With no FOV from any stack the result is <see cref="DefaultCameraFov"/>.
    /// </summary>
    public float CameraFov(Animation? animation, CameraAnimated camera, float time)
    {
        if (animation == null) return DefaultCameraFov;
        float acc = 0;
        // The stacks mix from the last (bottom) to the first (top), as the transforms do.
        var stacks = animation.Stacks;
        for (int s = stacks.Count - 1; s >= 0; s--)
        {
            var data = Data(stacks[s]);
            if ((data.Flags & StackEnabled) == 0 || (data.Flags & StackAdditive) != 0) continue;
            if (!data.Tracks.TryGetValue(camera, out var track) || !track.HasFov) continue;
            float value = LayerFov(data, track, time, out float weight);
            acc = acc * (1 - weight) + value * weight;
        }
        return acc == 0 ? DefaultCameraFov : acc;
    }

    /// <summary>
    /// The FOV mixed from the stacks below <paramref name="stack"/> only, as editing that stack
    /// sees it (the FOV counterpart of <see cref="MixDeltaBelow"/>).
    /// </summary>
    public float CameraFovBelow(Animation animation, AnimStack stack, CameraAnimated camera, float time)
    {
        int index = -1;
        var stacks = animation.Stacks;
        for (int s = 0; s < stacks.Count; s++)
            if (ReferenceEquals(stacks[s], stack)) index = s;
        if (index < 0) throw new ArgumentException($"{stack} is not a stack of {animation}.", nameof(stack));
        float acc = 0;
        for (int s = stacks.Count - 1; s > index; s--)
        {
            var data = Data(stacks[s]);
            if ((data.Flags & StackEnabled) == 0 || (data.Flags & StackAdditive) != 0) continue;
            if (!data.Tracks.TryGetValue(camera, out var track) || !track.HasFov) continue;
            float value = LayerFov(data, track, time, out float weight);
            acc = acc * (1 - weight) + value * weight;
        }
        return acc;
    }

    /// <summary>One stack's own FOV at animation time <paramref name="time"/>, without the stacks below.</summary>
    public float LayerFov(AnimStack stack, AnimTrack track, float time) =>
        LayerFov(Data(stack), new TrackData(track), time, out _);

    /// <summary>One stack's FOV at <paramref name="time"/> and its layer weight (opacity times fade).</summary>
    private float LayerFov(StackData data, TrackData track, float time, out float weight)
    {
        time = LocalTime(data.Keys, data.Flags, time);
        float fade = Timing(data.Keys, time, out int exactKey);
        weight = data.Opacity * fade;
        if (exactKey >= 0 && track.FovAt(exactKey) is { } exact) return exact;
        var indices = track.PresentFovIndices(data.Keys.Count);
        if (indices.Length == 0) return DefaultCameraFov;
        var values = indices.Select(i => track.FovAt(i) ?? DefaultCameraFov).ToArray();
        // The last key is the loop key: it replays key 0's value.
        if (values.Length > 1) values[^1] = values[0];
        var keys = indices.Select(i => data.Keys[i]).ToArray();
        var segment = new TrackKeys(keys, [], [], []);
        if (!segment.Segment(time, out int prev, out float u, out int hold))
            return values[Math.Clamp(hold, 0, values.Length - 1)];
        if (track.FovStep(indices[prev])) return values[prev];
        float u2 = u * u, u3 = u2 * u;
        return (2 * u3 - 3 * u2 + 1) * values[prev] + (u3 - 2 * u2 + u) * track.FovOut(indices[prev])
             + (-2 * u3 + 3 * u2) * values[prev + 1] + (u3 - u2) * track.FovIn(indices[prev + 1]);
    }

    /// <summary>
    /// Where <paramref name="time"/> falls among a stack's keys. Returns the fade factor (1 at an
    /// exact key) and sets <paramref name="exactKey"/> to the first of the first n-1 keys at exactly
    /// that time, or -1; the last key is never an exact match.
    /// </summary>
    public static float Timing(IReadOnlyList<StackKey> keys, float time, out int exactKey)
    {
        int n = keys.Count;
        // The first of the first n-1 keys at or after the time.
        int i = 0;
        while (i < n - 1 && keys[i].Time < time) i++;
        if (i < n - 1 && keys[i].Time == time)
        {
            exactKey = i;
            return 1f;
        }
        exactKey = -1;

        float fadeIn = 1f, fadeOut = 1f;
        if (i < n - 1 && keys[i].FadeIn != 0)
            fadeIn = MathF.Max(0f, 1f - (keys[i].Time - time) / keys[i].FadeIn);
        if (i > 0 && keys[i - 1].FadeOut != 0)
            fadeOut = MathF.Max(0f, 1f - (time - keys[i - 1].Time) / keys[i - 1].FadeOut);
        return fadeIn < 1f && fadeOut < 1f ? MathF.Max(fadeIn, fadeOut) : MathF.Min(fadeIn, fadeOut);
    }

    /// <summary>The delta that <see cref="Compose"/> turns into <paramref name="local"/> on <paramref name="base"/>.</summary>
    public static Transform Decompose(Transform @base, Transform local) => new(
        local.Translation - @base.Translation,
        Quaternion.Inverse(@base.Rotation) * local.Rotation,
        local.Scale / @base.Scale);

    /// <summary>Base composed with a delta: translations add, rotations multiply (base first), scales multiply.</summary>
    public static Transform Compose(Transform @base, Transform delta) =>
        new(@base.Translation + delta.Translation, @base.Rotation * delta.Rotation, @base.Scale * delta.Scale);

    /// <summary>Override layer: lerp translation and scale, slerp rotation, per masked channel.</summary>
    public static Transform MixOverride(Transform acc, Transform value, float weight, AnimChannels channels)
    {
        var lerpT = Vector3.Lerp(acc.Translation, value.Translation, weight);
        var lerpS = Vector3.Lerp(acc.Scale, value.Scale, weight);
        return new Transform(
            Select(channels, AnimChannels.TranslateX, acc.Translation, lerpT),
            (channels & AnimChannels.Rotate) != 0 ? Quaternion.Slerp(acc.Rotation, value.Rotation, weight) : acc.Rotation,
            Select(channels, AnimChannels.ScaleX, acc.Scale, lerpS));
    }

    /// <summary>Additive layer: weight the value (scale T, power S and R), then compose it onto the accumulator.</summary>
    public static Transform MixAdditive(Transform acc, Transform value, float weight, AnimChannels channels)
    {
        var addT = acc.Translation + weight * value.Translation;
        var mulS = acc.Scale * new Vector3(MathF.Pow(value.Scale.X, weight), MathF.Pow(value.Scale.Y, weight), MathF.Pow(value.Scale.Z, weight));
        return new Transform(
            Select(channels, AnimChannels.TranslateX, acc.Translation, addT),
            (channels & AnimChannels.Rotate) != 0 ? acc.Rotation * Power(value.Rotation, weight) : acc.Rotation,
            Select(channels, AnimChannels.ScaleX, acc.Scale, mulS));
    }

    /// <summary>q raised to <paramref name="exponent"/> via log/exp, on the w ≥ 0 hemisphere; near-zero q gives identity.</summary>
    public static Quaternion Power(Quaternion q, float exponent)
    {
        float length = q.Length();
        if (length < 1e-5f) return Quaternion.Identity;
        if (q.W < 0) q = -q;
        q = Quaternion.Multiply(q, 1f / length);
        var axis = new Vector3(q.X, q.Y, q.Z);
        float sin = axis.Length();
        if (sin < 1e-7f) return Quaternion.Identity;
        float angle = MathF.Atan2(sin, q.W) * exponent;
        return new Quaternion(axis / sin * MathF.Sin(angle), MathF.Cos(angle));
    }

    /// <summary>Per component: <paramref name="mixed"/> where the X/Y/Z channel bit is set, else <paramref name="kept"/>.</summary>
    private static Vector3 Select(AnimChannels channels, AnimChannels x, Vector3 kept, Vector3 mixed)
    {
        uint bits = (uint)channels;
        int shift = System.Numerics.BitOperations.TrailingZeroCount((uint)x);
        return new Vector3(
            (bits >> shift & 1) != 0 ? mixed.X : kept.X,
            (bits >> (shift + 1) & 1) != 0 ? mixed.Y : kept.Y,
            (bits >> (shift + 2) & 1) != 0 ? mixed.Z : kept.Z);
    }

    private StackData Data(AnimStack stack)
    {
        if (!_stacks.TryGetValue(stack, out var data))
        {
            var tracks = new Dictionary<Object3D, TrackData>(ReferenceEqualityComparer.Instance);
            foreach (var track in stack.Tracks)
                if (track.Target is { } target)
                    tracks.TryAdd(target, new TrackData(track));
            _stacks[stack] = data = new StackData(stack.Keys, stack.Flags, stack.Opacity, tracks);
        }
        return data;
    }

    private sealed record StackData(IReadOnlyList<StackKey> Keys, uint Flags, float Opacity, Dictionary<Object3D, TrackData> Tracks);

    /// <summary>
    /// A track with its key values read once. A null key pointer means the track has no key at
    /// that stack key; such gaps are interpolated from the track's own keys.
    /// </summary>
    private sealed class TrackData(AnimTrack track)
    {
        private readonly Transform?[] _values = Read(track.Keys);
        private readonly Object3D?[] _spaces = track.Keys.Select(k => (k as TransformKey)?.Space).ToArray();
        private readonly float?[] _fovs = ReadFov(track.Keys);
        private readonly float[] _fovIn = ReadFovTangents(track.TangentsIn);
        private readonly float[] _fovOut = ReadFovTangents(track.TangentsOut);
        private TrackKeys? _present;

        public AnimTrack Track => track;
        public AnimChannels Channels { get; } = track.Channels;

        /// <summary>Whether this track keys the camera FOV (a camera track with channel 15).</summary>
        public bool HasFov => track.HasFov && _fovs.Any(f => f != null);

        /// <summary>The FOV key value at stack key <paramref name="i"/>, or null when it has none.</summary>
        public float? FovAt(int i) => i < _fovs.Length ? _fovs[i] : null;

        public float FovIn(int i) => i < _fovIn.Length ? _fovIn[i] : 0f;
        public float FovOut(int i) => i < _fovOut.Length ? _fovOut[i] : 0f;

        /// <summary>The stack keys at which this track stores an FOV.</summary>
        public int[] PresentFovIndices(int stackKeyCount) =>
            Enumerable.Range(0, Math.Min(stackKeyCount, _fovs.Length)).Where(i => _fovs[i] != null).ToArray();

        /// <summary>Whether the segment after key <paramref name="i"/> holds: the FOV channel's out type is 5 (step).</summary>
        public bool FovStep(int i) =>
            i < track.TangentModes.Count && track.TangentModes[i] is { Length: > 9 } m && m[9].LowMode == 5;

        public bool HasKey(int i) => i < _values.Length && _values[i] != null;

        public Transform ValueAt(int i) => i < _values.Length ? _values[i] ?? Transform.Identity : Transform.Identity;

        public int[] PresentIndices(int stackKeyCount) =>
            Enumerable.Range(0, Math.Min(stackKeyCount, _values.Length)).Where(HasKey).ToArray();

        /// <summary><c>CKeyIK.Flags</c> of key <paramref name="i"/>, or 0 for other key types.</summary>
        public uint IKFlags(int i) => (_ikFlags ??= track.Keys.Select(k => (k as IKKey)?.Flags ?? 0u).ToArray()) is var f && i < f.Length ? f[i] : 0u;

        private uint[]? _ikFlags;

        /// <summary>
        /// The <c>Space</c> of the last key at or before <paramref name="time"/> (the first key
        /// before it). The last stack key is only an end marker and is skipped.
        /// </summary>
        public Object3D? SpaceAt(IReadOnlyList<StackKey> stackKeys, float time)
        {
            Object3D? space = null;
            bool found = false;
            for (int i = 0; i < Math.Min(stackKeys.Count - 1, _values.Length); i++)
            {
                if (!HasKey(i)) continue;
                if (found && stackKeys[i].Time > time) break;
                space = i < _spaces.Length ? _spaces[i] : null;
                found = true;
            }
            return space;
        }

        /// <summary>The stack keys at which this track has a key, with its values and tangents there.</summary>
        public TrackKeys Present(IReadOnlyList<StackKey> stackKeys)
        {
            if (_present is null)
            {
                var idx = Enumerable.Range(0, Math.Min(stackKeys.Count, _values.Length)).Where(HasKey).ToArray();
                var values = idx.Select(i => _values[i]!.Value).ToArray();
                // The last key is the loop key: its stored value is ignored and the first key's
                // value is used.
                if (values.Length > 1) values[^1] = values[0];
                var tIn = Read(track.TangentsIn);
                var tOut = Read(track.TangentsOut);
                Transform Tangent(Transform?[] list, int i) => i < list.Length && list[i] is { } t ? t : default;
                var modes = track.TangentModes;
                _present = new TrackKeys(
                    idx.Select(i => stackKeys[i]).ToArray(), values,
                    idx.Select(i => Tangent(tIn, i)).ToArray(), idx.Select(i => Tangent(tOut, i)).ToArray(),
                    idx.Select(i => IsStepMode(modes, i)).ToArray());
            }
            return _present;
        }

        private static Transform?[] Read(IReadOnlyList<AnimKey?> keys) =>
            keys.Select(k => k is TransformKey t ? t.Value : (Transform?)null).ToArray();

        private static float?[] ReadFov(IReadOnlyList<AnimKey?> keys) =>
            keys.Select(k => k is CameraKey c ? c.Fov : (float?)null).ToArray();

        private static float[] ReadFovTangents(IReadOnlyList<AnimKey?> keys) =>
            keys.Select(k => k is CameraKey c ? c.Fov : 0f).ToArray();
    }
}
