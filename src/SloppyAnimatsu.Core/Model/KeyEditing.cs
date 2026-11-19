using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// Recomputes a track's stored tangents after its keys change
/// Catmull-Rom auto tangents, flat tangents next to a close neighbour for
/// type 3 (Clamped), and stored tangents kept for type 0xF (Custom). The type names are in
/// <see cref="TangentTypes"/>.
/// </summary>
public static class TangentRecompute
{
    /// <summary>Type 3: flat when the key is this close to the neighbour on that side.</summary>
    public const uint FlatWhenClose = 3;
    /// <summary>Type 1: linear, the whole difference to the neighbour on that side.</summary>
    public const uint Linear = 1;
    /// <summary>Type 4 (IK): the app resolves it per key on IK targets; computed like Spline here.</summary>
    public const uint TargetDefault = 4;
    /// <summary>Type 0xF: the side is left as stored.</summary>
    public const uint Keep = 0xF;

    public const float CloseLimit = 0.1f;

    /// <summary>
    /// The default tangent type of a new key on <paramref name="target"/> for <paramref name="channel"/>:
    /// 4 on the nine MRS channels of an IK or reverse-foot controller and 3 (Clamped) on its scalar
    /// channels (pole, roll, twist, bank); 3 for an animated camera and for a reverse-foot shoe
    /// locator; else 0.
    /// The flags K writes:
    /// 0x144 + 0x133 on IK and reverse-foot tracks, 0x133 on the shoe locators
    /// and on camera tracks.
    /// </summary>
    public static uint DefaultType(Object3D? target, int channel = 0) => target switch
    {
        ControllerIK => channel >= 9 ? FlatWhenClose : TargetDefault,
        ReverseFootLocator => FlatWhenClose,
        CameraAnimated => FlatWhenClose,
        _ => 0u,
    };

    /// <summary>
    /// How rotations count as close for type 3. The original measure is not recorded; this one
    /// compares the length of log(q⁻¹·qₙ) (half the angle between them, in radians).
    /// </summary>
    public static bool RotationsClose(Vector3 logToNeighbour) => logToNeighbour.Length() < CloseLimit;

    /// <summary>The IK and reverse-foot scalar channels (bits 9-12) and their key fields.</summary>
    private static readonly (int Channel, Func<IKKey, float> Get, Action<IKKey, float> Set)[] Scalars =
    [
        (9, k => k.PoleAngle, (k, v) => k.PoleAngle = v),
        (10, k => ((ReverseFootKey)k).RollAngle, (k, v) => ((ReverseFootKey)k).RollAngle = v),
        (11, k => ((ReverseFootKey)k).TwistAngle, (k, v) => ((ReverseFootKey)k).TwistAngle = v),
        (12, k => ((ReverseFootKey)k).BankAngle, (k, v) => ((ReverseFootKey)k).BankAngle = v),
    ];

    /// <summary>
    /// Recomputes the tangents of keys 0 … n−2 of <paramref name="track"/> and copies key 0's onto
    /// the last (loop) key. Only the keys the track has are used (stack keys where it stores a null
    /// key are skipped), which is an assumption: the samples have no such gaps. Neighbours in
    /// another space are not converted; see the overloads that take an evaluator.
    /// </summary>
    public static void Recompute(AnimTrack track) => Recompute(track, (KeySpaceConverter?)null);

    /// <summary>
    /// <see cref="Recompute(AnimTrack)"/> with the space-aware neighbour lookup
    /// converting keys between spaces
    /// with <paramref name="evaluator"/>'s current animation state.
    /// </summary>
    public static void Recompute(AnimTrack track, PoseEvaluator? evaluator) =>
        Recompute(track, evaluator != null && track.Stack?.Animation is { } animation ? new KeySpaceConverter(evaluator, animation) : null);

    /// <summary>
    /// <see cref="Recompute(AnimTrack, PoseEvaluator)"/> with a shared converter (its world cache
    /// stays valid while the scene is unchanged). With a null converter this is the plain lookup
    /// of <see cref="Recompute(AnimTrack)"/>.
    /// </summary>
    public static void Recompute(AnimTrack track, KeySpaceConverter? converter)
    {
        var stack = track.Stack ?? throw new InvalidOperationException($"{track} is not in a stack.");
        var stackKeys = stack.Keys;
        var keys = track.Keys;
        var tIn = track.TangentsIn;
        var tOut = track.TangentsOut;
        var modes = track.TangentModes;
        var idx = Enumerable.Range(0, Math.Min(stackKeys.Count, keys.Count))
            .Where(i => keys[i] is TransformKey && i < tIn.Count && tIn[i] is TransformKey && i < tOut.Count && tOut[i] is TransformKey)
            .ToArray();
        int n = idx.Length;
        if (n < 2) return;

        var key = idx.Select(i => (TransformKey)keys[i]!).ToArray();
        var space = key.Select(k => k.Space).ToArray();
        var times = idx.Select(i => (float)stackKeys[i].Time).ToArray();
        var neighbours = new Neighbours(times, stack.IsCycle);
        uint defaultType = DefaultType(track.Target);
        bool spaceAware = converter != null && space.Any(s => s != null);
        var target = track.Target;

        (uint In, uint Out) Types(int k, int channel)
        {
            uint raw = idx[k] < modes.Count && channel < modes[idx[k]].Length ? modes[idx[k]][channel].Raw : defaultType * 0x11;
            uint Resolve(uint t) => t == TargetDefault ? defaultType : t;
            return (Resolve(raw & 0xF), Resolve((raw >> 4) & 0xF));
        }

        // Key j's value in space s, at key j's own frame.
        Transform In(int j, Object3D? s) =>
            target == null || ReferenceEquals(space[j], s) ? key[j].Value
                : converter!.Convert(target, key[j].Value, space[j], s, times[j]);

        for (int k = 0; k < n - 1; k++)
        {
            var (prev, tPrev) = neighbours.At(k, -1);
            var (next, tNext) = neighbours.At(k, +1);
            float t = times[k];
            var inKey = (TransformKey)tIn[idx[k]]!;
            var outKey = (TransformKey)tOut[idx[k]]!;

            // The plain lookup: one triple for both sides.
            var plain = new Triple(key[prev].Value, tPrev, key[k].Value, t, key[next].Value, tNext, false, false, prev, k, next);
            Triple inSide = plain, outSide = plain;
            if (spaceAware)
            {
                bool prevOther = !ReferenceEquals(space[prev], space[k]);
                var nextInKeySpace = In(next, space[k]);
                // Out side: the segment starting at the key, in the key's space.
                outSide = prevOther
                    ? new Triple(key[k].Value, t, key[k].Value, t, nextInKeySpace, tNext, true, false, k, k, next)
                    : plain with { B = nextInKeySpace };
                // In side: the segment ending at the key, in the previous key's space.
                if (prevOther)
                {
                    var converted = In(k, space[prev]);
                    inSide = new Triple(key[prev].Value, tPrev, converted, t, converted, t, false, true, prev, k, k);
                }
                else inSide = plain with { B = nextInKeySpace };
            }

            var newIn = Sides(inSide, inKey.Value, outKey.Value, k, Types).In;
            var newOut = Sides(outSide, inKey.Value, outKey.Value, k, Types).Out;
            inKey.Value = newIn;
            outKey.Value = newOut;

            if (key[k] is IKKey ik && inKey is IKKey ikIn && outKey is IKKey ikOut)
                foreach (var (channel, get, set) in Scalars)
                {
                    if (channel >= 10 && ik is not ReverseFootKey) continue;
                    // A replaced neighbour takes the key's scalars, without conversion (hyp.).
                    (float In, float Out) Scalar(Triple s) =>
                        Side(get((IKKey)key[s.PrevKey]), get(ik), get((IKKey)key[s.NextKey]), s.TA, s.T, s.TB,
                            Types(k, channel), get(ikIn), get(ikOut), (x, y) => MathF.Abs(x - y) < CloseLimit, s.PrevReplaced, s.NextReplaced);
                    float sIn = Scalar(inSide).In, sOut = Scalar(outSide).Out;
                    set(ikIn, sIn);
                    set(ikOut, sOut);
                }
        }

        // The camera FOV (channel 15, tangent-info index 9): the same side computation on the
        // scalar FOV values, when the track keys it.
        if (track.HasFov && key.All(k => k is CameraKey))
        {
            for (int k = 0; k < n - 1; k++)
            {
                var (prev, tPrev) = neighbours.At(k, -1);
                var (next, tNext) = neighbours.At(k, +1);
                var fovIn = (CameraKey)tIn[idx[k]]!;
                var fovOut = (CameraKey)tOut[idx[k]]!;
                float fov = ((CameraKey)key[k]).Fov;
                float fovPrev = ((CameraKey)key[prev]).Fov, fovNext = ((CameraKey)key[next]).Fov;
                var (sIn, sOut) = Side(fovPrev, fov, fovNext, tPrev, times[k], tNext, Types(k, 9),
                    fovIn.Fov, fovOut.Fov, (x, y) => MathF.Abs(x - y) < CloseLimit);
                fovIn.Fov = sIn;
                fovOut.Fov = sOut;
            }
            if (n > 1) ((CameraKey)key[n - 1]).Fov = ((CameraKey)key[0]).Fov;
        }

        // The loop key takes key 0's tangents.
        CopyTangent((TransformKey)tIn[idx[0]]!, (TransformKey)tIn[idx[n - 1]]!);
        CopyTangent((TransformKey)tOut[idx[0]]!, (TransformKey)tOut[idx[n - 1]]!);
    }

    /// <summary>
    /// A (previous, key, next) triple for one side. A neighbour marked replaced is the key itself
    /// and is left out of type 3's test against the other side. The key indices pick the IK scalars.
    /// </summary>
    private readonly record struct Triple(Transform A, float TA, Transform P, float T, Transform B, float TB,
        bool PrevReplaced, bool NextReplaced, int PrevKey, int Key, int NextKey);

    /// <summary>Translation, rotation and scale tangents of both sides from one triple.</summary>
    private static (Transform In, Transform Out) Sides(Triple s, Transform storedIn, Transform storedOut, int k,
        Func<int, int, (uint In, uint Out)> types)
    {
        var (a, p, b) = (s.A, s.P, s.B);
        Vector3 inT = default, outT = default, inS = default, outS = default;
        for (int c = 0; c < 3; c++)
        {
            (inT[c], outT[c]) = Side(a.Translation[c], p.Translation[c], b.Translation[c], s.TA, s.T, s.TB, types(k, c),
                storedIn.Translation[c], storedOut.Translation[c], (x, y) => MathF.Abs(x - y) < CloseLimit, s.PrevReplaced, s.NextReplaced);
            (inS[c], outS[c]) = Side(a.Scale[c], p.Scale[c], b.Scale[c], s.TA, s.T, s.TB, types(k, 6 + c),
                storedIn.Scale[c], storedOut.Scale[c], (x, y) => y != 0 && MathF.Abs(x / y - 1) < CloseLimit, s.PrevReplaced, s.NextReplaced);
        }

        // Rotation: logs in the key's frame, where the key itself is the origin.
        var inverse = Quaternion.Inverse(p.Rotation);
        var la = HermiteKeyInterpolator.Log(inverse * a.Rotation);
        var lb = HermiteKeyInterpolator.Log(inverse * b.Rotation);
        var rotationTypes = types(k, 3);
        var (rIn, rOut) = SideVector(la, lb, s.TA, s.T, s.TB, rotationTypes, s.PrevReplaced, s.NextReplaced);
        var inR = rotationTypes.In == Keep ? storedIn.Rotation : Tangent(rIn);
        var outR = rotationTypes.Out == Keep ? storedOut.Rotation : Tangent(rOut);
        return (new Transform(inT, inR, inS), new Transform(outT, outR, outS));
    }

    /// <summary>A rotation tangent: (v, 0), or the identity for a zero tangent.</summary>
    private static Quaternion Tangent(Vector3 v) => v == Vector3.Zero ? Quaternion.Identity : new Quaternion(v, 0);

    private static void CopyTangent(TransformKey from, TransformKey to)
    {
        to.Value = from.Value;
        if (from is CameraKey ca && to is CameraKey cb) cb.Fov = ca.Fov;
        if (from is IKKey a && to is IKKey b)
            foreach (var (channel, get, set) in Scalars)
                if (channel < 10 || (a is ReverseFootKey && b is ReverseFootKey))
                    set(b, get(a));
    }

    /// <summary>One scalar channel's in and out tangents.</summary>
    private static (float In, float Out) Side(float prev, float value, float next, float tPrev, float t, float tNext,
        (uint In, uint Out) types, float storedIn, float storedOut, Func<float, float, bool> close,
        bool prevReplaced = false, bool nextReplaced = false)
    {
        float span = tNext - tPrev;
        float slope = MathF.Abs(span) < 1e-5f ? 0 : (next - prev) / span;
        var (flatIn, flatOut) = Flat(types, close(value, prev), close(value, next), prevReplaced, nextReplaced);
        return (
            types.In == Keep ? storedIn : flatIn ? 0 : types.In == Linear ? value - prev : slope * (t - tPrev),
            types.Out == Keep ? storedOut : flatOut ? 0 : types.Out == Linear ? next - value : slope * (tNext - t));
    }

    /// <summary>Rotation tangents from the neighbours' logs relative to the key.</summary>
    private static (Vector3 In, Vector3 Out) SideVector(Vector3 prev, Vector3 next, float tPrev, float t, float tNext, (uint In, uint Out) types,
        bool prevReplaced = false, bool nextReplaced = false)
    {
        float span = tNext - tPrev;
        var slope = MathF.Abs(span) < 1e-5f ? Vector3.Zero : (next - prev) / span;
        var (flatIn, flatOut) = Flat(types, RotationsClose(prev), RotationsClose(next), prevReplaced, nextReplaced);
        return (
            flatIn ? Vector3.Zero : types.In == Linear ? -prev : slope * (t - tPrev),
            flatOut ? Vector3.Zero : types.Out == Linear ? next : slope * (tNext - t));
    }

    /// <summary>
    /// Type 3 flattens a side whose neighbour is close; when both sides are type 3, a close
    /// neighbour on either side flattens both, unless that other neighbour was replaced by the key
    /// (space-aware lookup).
    /// </summary>
    private static (bool In, bool Out) Flat((uint In, uint Out) types, bool closeToPrev, bool closeToNext,
        bool prevReplaced = false, bool nextReplaced = false)
    {
        bool both = types.In == FlatWhenClose && types.Out == FlatWhenClose;
        return (
            types.In == FlatWhenClose && (closeToPrev || both && !nextReplaced && closeToNext),
            types.Out == FlatWhenClose && (closeToNext || both && !prevReplaced && closeToPrev));
    }

    /// <summary>
    /// Neighbour lookup: clamped to keys 0 … n−2 on a plain stack (the end key is never a
    /// neighbour), wrapped modulo n−1 with shifted times on a cycle stack.
    /// </summary>
    private readonly struct Neighbours(float[] times, bool cycle)
    {
        public (int Index, float Time) At(int k, int offset)
        {
            int n = times.Length, m = n - 1, j = k + offset;
            if (!cycle)
            {
                j = Math.Clamp(j, 0, m - 1);
                return (j, times[j]);
            }
            if (j < 0) return (m + j, times[m + j] - times[m] + times[0]);
            if (j >= m) return (j - m, times[j - m] - times[0] + times[m]);
            return (j, times[j]);
        }
    }
}

/// <summary>
/// Turning a posed controller into keys, and creating keys. Edits
/// change the scene document in place; tangents are recomputed right away.
/// </summary>
/// <remarks>
/// Not covered yet: keeping children fixed and mirroring. Space edits and the space fix-ups
/// after retiming, deleting or adding keys are in SpaceEditing.cs.
/// </remarks>
public sealed partial class KeyEditor
{
    public KeyEditor(Character character, PoseEvaluator? evaluator = null)
    {
        Character = character;
        Evaluator = evaluator ?? new PoseEvaluator(character);
    }

    public Character Character { get; }

    /// <summary>The evaluator used for the base pose and the stacks below; invalidated after each edit.</summary>
    public PoseEvaluator Evaluator { get; }

    /// <summary>
    /// The rule: a dragged IK or reverse-foot handle that its chain cannot reach is put back
    /// on the chain's end. Off by default: at a locked (IK) key SloppyAnimatsu leaves it where it
    /// was dragged, as a goal the chain stretches toward, until the Spinner's Snap
    /// (<see cref="SnapToChain"/>) or an FK edit puts it on the chain
    /// Parity tests turn it on.
    /// </summary>
    public bool SnapUnreachedHandles { get; init; }

    /// <summary>
    /// Writes key <paramref name="keyIndex"/> of <paramref name="stack"/>'s track for
    /// <paramref name="controller"/> so that the mixed local transform at that key's time becomes
    /// <paramref name="posedLocal"/>, ignoring the stacks above <paramref name="stack"/>. Only
    /// <paramref name="channels"/> that the track animates are written; rotation is always
    /// written whole. Editing the end key edits key 0, and key 0's value is mirrored into the end key
    /// unless <paramref name="mirrorEndKey"/> is false (the looker-target edit leaves the loop
    /// end key alone). A camera key also derives its rig unless <paramref name="deriveRig"/> is
    /// false (the looker-target edit writes the rig itself, keeping its chain).
    /// </summary>
    public void SetKey(AnimStack stack, int keyIndex, Object3D controller, Transform posedLocal,
        AnimChannels channels = AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale,
        bool mirrorEndKey = true, bool deriveRig = true, bool ownValue = false)
    {
        var animation = stack.Animation ?? throw new InvalidOperationException($"{stack} is not in an animation.");
        var track = stack.TrackFor(controller) ?? throw new ArgumentException($"{stack} has no track for {controller}.", nameof(controller));
        var stackKeys = stack.Keys;
        int n = stackKeys.Count;
        if (keyIndex < 0 || keyIndex >= n) throw new ArgumentOutOfRangeException(nameof(keyIndex));
        if (keyIndex == n - 1 && n > 1) keyIndex = 0;
        var keys = track.Keys;
        if (keyIndex >= keys.Count || keys[keyIndex] is not TransformKey key)
            throw new InvalidOperationException($"{track} has no key {keyIndex}.");

        int ci = Evaluator.IndexOf(controller);
        var rest = ci >= 0 ? Evaluator.RestTransforms[ci] : Transform.Identity;
        float time = stackKeys[keyIndex].Time;
        var posed = PoseEvaluator.Decompose(rest, posedLocal);
        // At its own key the stack's fade is 1, so its weight is the opacity. An IK edit in the
        // stack's single-stack world writes the stack's own value as it is (ownValue).
        var solved = ownValue ? posed
            : InverseMix(Evaluator.MixDeltaBelow(animation, stack, controller, time), posed, stack.Opacity, stack.IsAdditive);

        var mask = channels & track.Channels;
        if ((mask & AnimChannels.Rotate) != 0) mask |= AnimChannels.Rotate;
        key.Value = Merge(key.Value, solved, mask);
        if (mirrorEndKey && keyIndex == 0 && n > 1 && n - 1 < keys.Count && keys[n - 1] is TransformKey loop)
            loop.Value = Merge(loop.Value, solved, mask);

        // A camera's key also carries its editor rig: the hidden joint's MRS key and the looker's IK
        // key follow it, and a looker target's key turns the camera to look at it
        AnimTrack[] rigTracks = !deriveRig ? [] : controller switch
        {
            CameraAnimated { Target: { } locked } camera when CameraEditor.IsTargetKeyLocked(locked, stack, keyIndex)
                && LockedLookPoint(stack, keyIndex, locked) is { } point => AimRig(stack, keyIndex, camera, point),
            CameraAnimated camera => CameraRigKeys.Derive(stack, keyIndex, Evaluator, camera, mirrorEndKey: mirrorEndKey),
            CameraTarget { Camera: { } looker } => AimRig(stack, keyIndex, looker, posedLocal.Translation),
            _ => [],
        };
        Evaluator.Invalidate();
        TangentRecompute.Recompute(track, Evaluator);
        foreach (var rigTrack in rigTracks) TangentRecompute.Recompute(rigTrack, Evaluator);
        Evaluator.Invalidate();
    }

    /// <summary>
    /// A looker target's key was posed: the target is the chain's IK goal, so the camera turns about
    /// its own eye to keep looking at it and the hidden joint's effector moves onto the point
    /// (the looker solve plus the chain solve, the editor's orbit rig of
    ///). The camera's and the joint's
    /// keys are written from the aim, and the target's own key is re-derived from them, so it lands
    /// on <paramref name="lookPoint"/>; a drag onto the eye leaves the rig as it was. Returns the
    /// tracks written. The write follows a live edit: the joint's key carries the camera
    /// key's inverse rotation, the looker key keeps its rotation and <c>RefArray</c>, and only the
    /// key at the playhead is touched (the loop end key stays as it was).
    /// </summary>
    /// <summary>
    /// The locked target's look point (its local translation at the key's time) before the camera's
    /// new key re-derives it: a camera moved under an IK-locked target re-aims at this point instead
    /// of carrying the target along.
    /// </summary>
    private Vector3? LockedLookPoint(AnimStack stack, int keyIndex, CameraTarget target)
    {
        if (stack.Animation is not { } animation) return null;
        int index = Evaluator.IndexOf(target);
        if (index < 0) return null;
        Evaluator.Invalidate();
        return Evaluator.LocalTransforms(animation, stack.Keys[keyIndex].Time)[index].Translation;
    }

    private AnimTrack[] AimRig(AnimStack stack, int keyIndex, CameraAnimated camera, Vector3 lookPoint)
    {
        if (CameraEditor.Rig(camera) is not { } rig || stack.Animation is not { } animation) return [];
        int index = Evaluator.IndexOf(camera), hideIndex = Evaluator.IndexOf(rig.HideJoint);
        if (index < 0) return [];
        var eye = Evaluator.LocalTransforms(animation, stack.Keys[keyIndex].Time)[index].ToMatrix();
        if (!CameraRigKeys.Aim(eye, lookPoint, out var aimed, out float distance))
            return CameraRigKeys.Derive(stack, keyIndex, Evaluator, camera,
                keepLookerChain: true, mirrorEndKey: false);
        // The app writes the camera key's inverse rotation into the hidden joint's key as well
        // so the chain's effector carries the aimed frame.
        var cameraKey = PoseEvaluator.Decompose(Evaluator.RestTransforms[index], aimed);
        SetKey(stack, keyIndex, camera, aimed, AnimChannels.Translate | AnimChannels.Rotate,
            mirrorEndKey: false, deriveRig: false);
        if (hideIndex >= 0)
        {
            var jointPose = new Transform(new Vector3(0f, 0f, -distance),
                Evaluator.RestTransforms[hideIndex].Rotation * Quaternion.Inverse(cameraKey.Rotation), Vector3.One);
            SetKey(stack, keyIndex, rig.HideJoint, jointPose,
                AnimChannels.Translate | AnimChannels.Rotate, mirrorEndKey: false);
        }
        return CameraRigKeys.Derive(stack, keyIndex, Evaluator, camera,
            keepLookerChain: true, mirrorEndKey: false);
    }

    /// <summary>
    /// Writes the camera's FOV key <paramref name="keyIndex"/> of <paramref name="stack"/> so that
    /// the mixed FOV at that key's time becomes <paramref name="fov"/>, inverse-mixing through the
    /// stacks below (override only: the app disables the FOV channel on additive stacks). Editing
    /// the end key edits key 0, and key 0's FOV is mirrored into the end key
    /// </summary>
    public void SetFov(AnimStack stack, int keyIndex, CameraAnimated camera, float fov)
    {
        var animation = stack.Animation ?? throw new InvalidOperationException($"{stack} is not in an animation.");
        if (stack.IsAdditive) throw new InvalidOperationException($"{stack.Name} is additive; an additive stack cannot animate the camera FOV.");
        var track = stack.TrackFor(camera) ?? throw new ArgumentException($"{stack} has no track for {camera}.", nameof(camera));
        if (!track.HasFov) throw new InvalidOperationException($"{track} does not animate the FOV.");
        var stackKeys = stack.Keys;
        int n = stackKeys.Count;
        if (keyIndex < 0 || keyIndex >= n) throw new ArgumentOutOfRangeException(nameof(keyIndex));
        if (keyIndex == n - 1 && n > 1) keyIndex = 0;
        var keys = track.Keys;
        if (keyIndex >= keys.Count || keys[keyIndex] is not CameraKey key)
            throw new InvalidOperationException($"{track} has no camera key {keyIndex}.");

        float time = stackKeys[keyIndex].Time;
        float below = Evaluator.CameraFovBelow(animation, stack, camera, time);
        float weight = stack.Opacity;
        float solved = weight < 1e-6f ? fov : (fov - below * (1 - weight)) / weight;
        key.Fov = solved;
        if (keyIndex == 0 && n > 1 && n - 1 < keys.Count && keys[n - 1] is CameraKey loop) loop.Fov = solved;

        Evaluator.Invalidate();
        TangentRecompute.Recompute(track, Evaluator);
        Evaluator.Invalidate();
    }

    /// <summary>
    /// Sets one of the TRANSFORM window's extra channels on key <paramref name="keyIndex"/> of
    /// <paramref name="controller"/>'s track: 9 the IK pole angle, 10-12 reverse-foot roll, twist and
    /// bank (radians), 13-14 blendshape X and Y. The value is the layer's own (no inverse mixing
    /// through the stacks below, a SloppyAnimatsu choice). The pole angle goes through
    /// <see cref="SwivelChain"/> by the difference, so the chain's joint keys follow; where the chain
    /// cannot be swung, <c>PoleAngle</c> and <c>RefPoleAngle</c> move alone. Editing key 0 of a looped
    /// track also writes the loop key. Returns false when the track has no such key or channel.
    /// </summary>
    public bool SetScalarChannel(AnimStack stack, int keyIndex, Object3D controller, int channel, float value)
    {
        if (stack.IsAdditive || stack.TrackFor(controller) is not { } track) return false;
        if ((track.Channels & (AnimChannels)(1u << channel)) == 0) return false;
        int n = stack.Keys.Count;
        if (keyIndex < 0 || keyIndex >= n) return false;
        if (keyIndex == n - 1 && n > 1) keyIndex = 0;
        var targets = keyIndex == 0 && n > 1 ? new[] { 0, n - 1 } : new[] { keyIndex };

        if (channel is 13 or 14 && track is BlendshapeTrack blend)
        {
            if (keyIndex >= blend.Keys.Count || blend.Keys[keyIndex] == null) return false;
            foreach (int k in targets)
            {
                if (k >= blend.Keys.Count || blend.Keys[k] == null) continue;
                var v = blend.ValuesAt(k);
                blend.SetValues(k, channel == 13 ? value : v[0], channel == 14 ? value : v[1]);
            }
            Evaluator.Invalidate();
            return true;
        }

        var keys = track.Keys;
        if (keyIndex >= keys.Count || Curves.Scalar(keys[keyIndex], channel) is not { } current) return false;
        if (channel == 9 && controller is ControllerIK ik)
        {
            float delta = value - current;
            if (!SwivelChain(stack, keyIndex, ik, delta)) AddPoleAngle(stack, keyIndex, ik, delta);
        }
        else
        {
            foreach (int k in targets)
                if (k < keys.Count && keys[k] is { } key) Curves.SetScalar(key, channel, value);
        }
        Evaluator.Invalidate();
        TangentRecompute.Recompute(track, Evaluator);
        Evaluator.Invalidate();
        // Roll, twist and bank pose the shoe under the controller: the controller's key changed, so
        // the leg follows the shoe at FK and IK keys alike
        // Without it an FK key's leg - which is what is
        // drawn outside IK override segments - ignored the new angle.
        if (channel is >= 10 and <= 12 && controller is ControllerReverseFoot rf && FootChain(stack, keyIndex, rf) is { } foot)
        {
            ShoeToLeg(stack, keyIndex, foot);
            foreach (var other in Evaluator.IKControllersInSolveOrder)
                if (!ReferenceEquals(other, rf)) Follow(stack, keyIndex, other, rf);
            Evaluator.Invalidate();
        }
        return true;
    }

    /// <summary>
    /// Toggles a controller's **IK lock** - <c>CKeyIK.Flags</c> bit 0, the flag that makes a segment
    /// an IK override segment.
    /// A camera acts on its looker target, which is what the app passes
    /// to the IK lock toggle; every other object acts on itself.
    /// <para>
    /// The value is the opposite of the key exactly at <paramref name="frame"/> (the
    /// current key index; the app reads past the key list when no key sits
    /// there, so SloppyAnimatsu refuses) and is written to <paramref name="keyIndices"/> when any are
    /// given, else to every key of the track (the app writes the keys ticked in the timeline, whose
    /// selection is a byte per stack key; SloppyAnimatsu reads an empty
    /// selection as the whole track). Returns the number of keys whose flag changed, 0 with
    /// <paramref name="reason"/> when the toggle cannot run.
    /// </para>
    /// </summary>
    public int ToggleIkLock(AnimStack stack, Object3D controller, int frame,
        IReadOnlyCollection<int>? keyIndices, out bool locked, out string? reason) =>
        ToggleIkLock(stack, controller, [controller], frame, keyIndices, out locked, out reason);

    /// <summary>
    /// The same toggle for a selection: <paramref name="clicked"/> (the object the click landed on)
    /// decides the value and every object of <paramref name="selection"/> that has an IK,
    /// reverse-foot or looker track in <paramref name="stack"/> gets it - the app writes every
    /// selected <c>CControllerIK</c> when the clicked one answers its "apply to the selection"
    /// predicate (not read).
    /// </summary>
    public int ToggleIkLock(AnimStack stack, Object3D clicked, IReadOnlyList<Object3D> selection, int frame,
        IReadOnlyCollection<int>? keyIndices, out bool locked, out string? reason)
    {
        locked = false;
        if (!TryIkLock(stack, clicked, frame, out bool value, out reason)) return 0;
        locked = value;
        int written = 0;
        var done = new List<Object3D>();
        foreach (var controller in selection.Prepend(clicked))
        {
            var target = IkLockTarget(controller);
            if (done.Contains(target)) continue;
            done.Add(target);
            written += SetIkLock(stack, target, value, keyIndices);
        }
        return written;
    }

    /// <summary>
    /// Writes <paramref name="locked"/> into <c>CKeyIK.Flags</c> bit 0 of the keys
    /// <paramref name="keyIndices"/> of <paramref name="controller"/>'s track in
    /// <paramref name="stack"/> (every key of it when none are given). Returns the number of keys
    /// whose flag changed.
    /// </summary>
    public int SetIkLock(AnimStack stack, Object3D controller, bool locked, IReadOnlyCollection<int>? keyIndices = null)
    {
        if (stack.TrackFor(IkLockTarget(controller)) is not { } track) return 0;
        var keys = track.Keys;
        int[] indices = keyIndices is { Count: > 0 }
            ? [.. keyIndices.Where(i => i >= 0 && i < keys.Count).Distinct()]
            : [.. Enumerable.Range(0, keys.Count)];
        int written = 0;
        foreach (int index in indices)
        {
            if (keys[index] is not IKKey key) continue;
            if (((key.Flags & 1u) != 0) == locked) continue;
            key.SetIkKey(locked);
            written++;
        }
        if (written > 0) Evaluator.Invalidate();
        return written;
    }

    /// <summary>The object whose track carries an IK lock: a camera's looker target, else itself.</summary>
    private static Object3D IkLockTarget(Object3D controller) =>
        controller is CameraAnimated { Target: { } target } ? target : controller;

    /// <summary>
    /// The value a toggle would write: the opposite of <c>CKeyIK.Flags</c> bit 0 of the key exactly at
    /// <paramref name="frame"/>. False, with <paramref name="reason"/>, when the track cannot carry an
    /// IK lock or has no key at that frame.
    /// </summary>
    private static bool TryIkLock(AnimStack stack, Object3D controller, int frame, out bool locked, out string? reason)
    {
        locked = false;
        reason = null;
        var target = IkLockTarget(controller);
        if (stack.TrackFor(target) is not { } track)
        {
            reason = $"'{target.Name}' has no track in '{stack.Name}'.";
            return false;
        }
        var keys = track.Keys;
        var times = stack.Keys;
        int current = -1;
        for (int i = 0; i < times.Count && i < keys.Count; i++)
            if (times[i].Time == frame)
            {
                current = i;
                break;
            }
        if (current < 0)
        {
            reason = $"There is no key at frame {frame} to take the IK lock from.";
            return false;
        }
        if (keys[current] is not IKKey key)
        {
            reason = $"'{target.Name}' is not an IK controller, so its keys carry no IK lock.";
            return false;
        }
        locked = (key.Flags & 1u) == 0;
        return true;
    }

    /// <summary>
    /// Poses <paramref name="controller"/> at key <paramref name="keyIndex"/> of
    /// <paramref name="stack"/> like <see cref="SetKey"/>, keeping the IK chains keyed in that
    /// stack consistent at that key. An IK controller is reached
    /// by re-solving its chain from the mixed pose: the chain joints are keyed, the controller is
    /// keyed where the chain ends, and its <c>RefArray</c> is updated. Then every other IK
    /// controller with a key there is brought back in line: at an FK key the controller snaps to
    /// its chain's end; at an IK key its chain is solved from <c>RefArray</c> to the controller.
    /// Reverse-foot controllers follow: a dragged one
    /// re-solves its leg toward its shoe and is placed where the shoe's ankle meets the leg; any
    /// other one re-solves its leg to the shoe at an IK key or when the edit moved it, else (an FK
    /// key) follows its leg.
    /// </summary>
    public void Pose(AnimStack stack, int keyIndex, Object3D controller, Transform posedLocal,
        AnimChannels channels = AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale)
    {
        int n = stack.Keys.Count;
        if (keyIndex == n - 1 && n > 1) keyIndex = 0;
        // The looker-target edit writes only the key at the playhead: the loop end key stays.
        SetKey(stack, keyIndex, controller, posedLocal, channels, mirrorEndKey: controller is not CameraTarget);
        if (controller is ControllerReverseFoot rf && FootChain(stack, keyIndex, rf) is { } foot)
            ReachFoot(stack, keyIndex, foot, posedLocal);
        else if (controller is ControllerIK ik && IKChain(stack, keyIndex, ik) is { } dragged)
            ReachController(stack, keyIndex, dragged, posedLocal);
        foreach (var other in Evaluator.IKControllersInSolveOrder)
            if (!ReferenceEquals(other, controller)) Follow(stack, keyIndex, other, controller);
    }

    /// <summary>
    /// Re-solves, at every key of <paramref name="stack"/>, each IK-locked key's chain toward its
    /// controller (a reverse foot's leg toward its shoe, <em>shoe to leg</em>), in solve order, and
    /// returns how many keys it looked at. A frame exactly at a key plays the chain joints' own keys
    /// so a chain that does not reach its controller there
    /// jumps for one frame; the rule for an IK key is that the leg follows the shoe
    /// Used after a retarget between different rigs, whose
    /// converted joint keys do not land on the converted controllers.
    /// </summary>
    public int SolveLockedKeys(AnimStack stack)
    {
        int n = stack.Keys.Count;
        int count = n > 1 ? n - 1 : n;
        // A new key changes the string the segment interpolates, and so the solve inside it; a
        // few passes settle that.
        for (int pass = 0; pass < 3; pass++)
        for (int k = 0; k < count; k++)
        {
            foreach (var controller in Evaluator.IKControllersInSolveOrder)
            {
                if (controller is ControllerReverseFoot foot)
                {
                    if (FootChain(stack, k, foot) is { } chain && (chain.Key.Flags & 1) != 0)
                    {
                        // A key that already plays without a jump is left alone: rewriting its
                        // leg moves the string the segment interpolates, and on a rig whose
                        // joints do not compensate their parent's scale that does not settle
                        // (Citro_Knight_OLD's own SpecialAirLand went from 0.05 to 25 units).
                        if (JumpAt(stack, k, foot, chain.Joints[3]) is not { } jump) ReachLockedFoot(stack, k, chain);
                        else if (jump <= SettledJump || !MatchOverride(stack, k, foot, chain.Joints)) continue;
                        StoreFoot(stack, k, chain, stack.Keys[k].Time, storeLeg: false);
                    }
                }
                else if (IKChain(stack, k, controller) is { } chain && (chain.Key.Flags & 1) != 0)
                {
                    // A locked key with no override segment beside it plays its joint keys on both
                    // sides, so it cannot jump; but the IK key means the chain reaches its
                    // controller there (an edit re-solves it so), and the source's did. Solve it,
                    // unless it already reaches.
                    if (JumpAt(stack, k, controller, chain.Joints[^1]) is { } jump)
                    {
                        if (jump > SettledJump) MatchOverride(stack, k, controller, chain.Joints);
                    }
                    else if (ReachMiss(stack, k, chain) > SettledJump) ReachLockedKey(stack, k, chain);
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Keys <paramref name="joints"/> at key <paramref name="keyIndex"/> with the pose playback gives
    /// them just inside the override segment that starts there (or, failing that, the one that ends
    /// there), so the frame at the key matches its neighbours. Playback solves a segment in the
    /// single-stack world of its override stack, which a
    /// solve in the full world does not reproduce when other stacks move the character. Returns
    /// whether a segment was found.
    /// </summary>
    private bool MatchOverride(AnimStack stack, int keyIndex, ControllerIK controller, IReadOnlyList<Object3D> joints)
    {
        const float Epsilon = 1e-3f;
        var animation = stack.Animation!;
        float time = stack.Keys[keyIndex].Time;
        float side = Evaluator.OverrideString(animation, controller, time + Epsilon) != null ? time + Epsilon
            : keyIndex > 0 && Evaluator.OverrideString(animation, controller, time - Epsilon) != null ? time - Epsilon
            : float.NaN;
        if (float.IsNaN(side)) return false;
        var inside = Evaluator.LocalTransforms(animation, side);
        var current = Evaluator.LocalTransforms(animation, time);
        foreach (var joint in joints)
        {
            int i = Evaluator.IndexOf(joint);
            if (i >= 0 && stack.TrackFor(joint) != null && !Near(current[i], inside[i]))
                SetKeyMixed(stack, keyIndex, joint, inside[i]);
        }
        return true;
    }

    /// <summary>
    /// A jump (or reach miss) up to this many scene units counts as settled, and
    /// <see cref="SolveLockedKeys"/> leaves the key as it is.
    /// </summary>
    private const double SettledJump = 0.1;

    /// <summary>
    /// How far <paramref name="end"/> moves between key <paramref name="keyIndex"/> and just inside
    /// the override segment beside it (the side <see cref="MatchOverride"/> takes), or null when
    /// no segment starts or ends there. This is the one-frame jump <c>keyjump</c> reports.
    /// </summary>
    private double? JumpAt(AnimStack stack, int keyIndex, ControllerIK controller, Object3D end)
    {
        const float Epsilon = 1e-3f;
        var animation = stack.Animation!;
        float time = stack.Keys[keyIndex].Time;
        float side = Evaluator.OverrideString(animation, controller, time + Epsilon) != null ? time + Epsilon
            : keyIndex > 0 && Evaluator.OverrideString(animation, controller, time - Epsilon) != null ? time - Epsilon
            : float.NaN;
        int e = Evaluator.IndexOf(end);
        if (float.IsNaN(side) || e < 0) return null;
        return Vector3.Distance(Evaluator.WorldMatrices(animation, time)[e].Translation,
            Evaluator.WorldMatrices(animation, side)[e].Translation);
    }

    /// <summary>
    /// How far an IK chain's end is from its controller at key <paramref name="keyIndex"/>, in the
    /// single-stack world of <paramref name="stack"/>: an IK chain answers only to the layers it is
    /// on, so the motion another stack adds (a root-motion layer) is not a miss.
    /// </summary>
    private double ReachMiss(AnimStack stack, int keyIndex, IKKeyChain chain)
    {
        Evaluator.StackPose(stack.Animation!, stack, stack.Keys[keyIndex].Time, out var world);
        return Vector3.Distance(world[Evaluator.IndexOf(chain.Joints[^1])].Translation,
            world[Evaluator.IndexOf(chain.Controller)].Translation);
    }

    /// <summary>
    /// A locked IK key with no override segment beside it: the chain is solved toward the
    /// controller in the single-stack world of <paramref name="stack"/> (the world a segment's solve
    /// sees) and the stack's own values are keyed, and
    /// <c>RefArray</c> takes the result.
    /// </summary>
    private void ReachLockedKey(AnimStack stack, int keyIndex, IKKeyChain chain)
    {
        var animation = stack.Animation!;
        float time = stack.Keys[keyIndex].Time;
        var local = Evaluator.StackPose(animation, stack, time, out var world);
        var solved = Solve(animation, time, chain, world, local,chain.Joints.Select(j => local[Evaluator.IndexOf(j)]).ToArray(),
            world[Evaluator.IndexOf(chain.Controller)]);
        for (int j = 0; j < chain.Joints.Count; j++)
            if (!Near(local[Evaluator.IndexOf(chain.Joints[j])], solved[j]))
                SetStackValue(stack, keyIndex, chain.Joints[j], solved[j]);
        StoreChain(stack, keyIndex, chain, time);
    }

    /// <summary>
    /// A locked reverse-foot key with no override segment beside it: the leg is solved toward the
    /// shoe under the controller in the single-stack world of <paramref name="stack"/>, and the
    /// stack's own values are keyed.
    /// </summary>
    private void ReachLockedFoot(AnimStack stack, int keyIndex, FootKeyChain chain)
    {
        var animation = stack.Animation!;
        float time = stack.Keys[keyIndex].Time;
        var local = Evaluator.StackPose(animation, stack, time, out var world);
        var solved = SolveLeg(animation, time, chain, world, local,chain.Joints.Select(j => local[Evaluator.IndexOf(j)]).ToArray(),
            world[Evaluator.IndexOf(chain.Controller)], out _);
        if (solved == null) return;
        for (int j = 0; j < 4; j++)
            if (!Near(local[Evaluator.IndexOf(chain.Joints[j])], solved[j]))
                SetStackValue(stack, keyIndex, chain.Joints[j], solved[j]);
    }

    /// <summary>
    /// Writes key <paramref name="keyIndex"/> of <paramref name="stack"/>'s track for
    /// <paramref name="joint"/> so that the stack's own value, composed with rest, is
    /// <paramref name="local"/> (the local a <see cref="PoseEvaluator.StackPose"/> solve gives),
    /// mirrored into the loop end key. A joint without a track in the stack is left alone.
    /// </summary>
    private void SetStackValue(AnimStack stack, int keyIndex, Object3D joint, Transform local)
    {
        var track = stack.TrackFor(joint);
        int n = stack.Keys.Count, i = Evaluator.IndexOf(joint);
        if (track == null || i < 0) return;
        if (keyIndex == n - 1 && n > 1) keyIndex = 0;
        var keys = track.Keys;
        if (keyIndex >= keys.Count || keys[keyIndex] is not TransformKey key) return;
        var value = PoseEvaluator.Decompose(Evaluator.RestTransforms[i], local);
        var mask = track.Channels | AnimChannels.Rotate;
        key.Value = Merge(key.Value, value, mask);
        if (keyIndex == 0 && n > 1 && n - 1 < keys.Count && keys[n - 1] is TransformKey loop)
            loop.Value = Merge(loop.Value, value, mask);
        Evaluator.Invalidate();
    }

    /// <summary>
    /// <see cref="SetKey"/> so that the <em>fully mixed</em> local transform at the key becomes
    /// <paramref name="wanted"/>, stacks above <paramref name="stack"/> included. <see cref="SetKey"/>
    /// ignores those stacks, as an interactive edit does; a pose taken from playback already carries
    /// them, so writing it back unchanged would count an additive stack above twice (an additive
    /// <c>TransN</c> stack over <c>All</c> in DupModel_Blackcat's Cartwheel). The value written is
    /// corrected by the difference the stacks above make, a few times while that shrinks.
    /// </summary>
    private void SetKeyMixed(AnimStack stack, int keyIndex, Object3D joint, Transform wanted)
    {
        var animation = stack.Animation!;
        int i = Evaluator.IndexOf(joint);
        float time = stack.Keys[keyIndex].Time;
        var write = wanted;
        SetKey(stack, keyIndex, joint, write);
        var got = Evaluator.LocalTransforms(animation, time)[i];
        float error = Mismatch(got, wanted);
        for (int pass = 0; pass < 4 && !Near(got, wanted); pass++)
        {
            var next = write with
            {
                Translation = write.Translation + wanted.Translation - got.Translation,
                Rotation = Quaternion.Normalize(wanted.Rotation * Quaternion.Inverse(got.Rotation) * write.Rotation),
                Scale = write.Scale * Safe(wanted.Scale, got.Scale),
            };
            var previous = write;
            SetKey(stack, keyIndex, joint, next);
            var nextGot = Evaluator.LocalTransforms(animation, time)[i];
            float nextError = Mismatch(nextGot, wanted);
            if (nextError >= error)
            {
                SetKey(stack, keyIndex, joint, previous);
                break;
            }
            (write, got, error) = (next, nextGot, nextError);
        }

        static Vector3 Safe(Vector3 a, Vector3 b) => new(
            MathF.Abs(b.X) > 1e-6f ? a.X / b.X : 1, MathF.Abs(b.Y) > 1e-6f ? a.Y / b.Y : 1, MathF.Abs(b.Z) > 1e-6f ? a.Z / b.Z : 1);
        static float Mismatch(Transform a, Transform b) =>
            Vector3.Distance(a.Translation, b.Translation) + 100f * (1 - MathF.Abs(Quaternion.Dot(a.Rotation, b.Rotation)))
            + Vector3.Distance(a.Scale, b.Scale);
    }

    /// <summary>
    /// Brings <paramref name="controller"/>'s key at <paramref name="keyIndex"/> up to date after
    /// <paramref name="moved"/> changed: an IK key re-solves the chain (or leg) to the controller
    /// (or shoe); an FK key snaps the controller to the chain's end, or moves the shoe to the leg
    /// unless <paramref name="moved"/> carries the reverse-foot controller itself.
    /// </summary>
    private void Follow(AnimStack stack, int keyIndex, ControllerIK controller, Object3D moved)
    {
        if (controller is ControllerReverseFoot foot)
        {
            if (FootChain(stack, keyIndex, foot) is not { } chain) return;
            if ((chain.Key.Flags & 1) != 0 || IsAncestorOrSelf(moved, foot)) ShoeToLeg(stack, keyIndex, chain);
            else LegToShoe(stack, keyIndex, chain);
        }
        else if (IKChain(stack, keyIndex, controller) is { } chain)
        {
            if ((chain.Key.Flags & 1) != 0) SolveChain(stack, keyIndex, chain);
            else SnapController(stack, keyIndex, chain);
        }
    }

    /// <summary>
    /// Swings the middle joints of <paramref name="controller"/>'s chain at key
    /// <paramref name="keyIndex"/> by <paramref name="angle"/> radians about the line from the
    /// chain's first joint to its end joint (the ankle for a reverse foot), as the Spinner
    /// centre does in rotate mode. The first joint turns about that line, the end joint keeps its
    /// world transform, and the joints in between follow; the joints are keyed and <c>RefArray</c>
    /// takes the new locals, then the other IK controllers are brought back in line as after
    /// <see cref="Pose"/>. The sense of the angle and the end joint's kept orientation are
    /// SloppyAnimatsu's own. Returns
    /// false when the stack does not key the chain there or the chain is folded onto its base.
    /// </summary>
    public bool SwivelChain(AnimStack stack, int keyIndex, ControllerIK controller, float angle)
    {
        int n = stack.Keys.Count;
        if (keyIndex == n - 1 && n > 1) keyIndex = 0;
        IReadOnlyList<Object3D> joints;
        int end;
        FootKeyChain? foot = null;
        IKKeyChain? chain = null;
        if (controller is ControllerReverseFoot rf)
        {
            if ((foot = FootChain(stack, keyIndex, rf)) == null) return false;
            joints = foot.Joints;
            end = 2;
        }
        else
        {
            if ((chain = IKChain(stack, keyIndex, controller)) == null) return false;
            joints = chain.Joints;
            end = joints.Count - 1;
        }
        var animation = stack.Animation!;
        float time = stack.Keys[keyIndex].Time;
        var local = EditPose(stack, controller, time, out var world);
        int root = Evaluator.IndexOf(joints[0]), tip = Evaluator.IndexOf(joints[end]);
        var p0 = world[root].Translation;
        var axis = world[tip].Translation - p0;
        if (axis.LengthSquared() < 1e-12f) return false;
        // Row vectors: a point is moved to the pivot, turned, and moved back.
        var swing = Matrix4x4.CreateTranslation(-p0) * Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), angle)
            * Matrix4x4.CreateTranslation(p0);

        int pi = joints[0].Parent3D is { } p ? Evaluator.IndexOf(p) : -1;
        var parentWorld = pi >= 0 ? world[pi] : Matrix4x4.Identity;
        int ep = joints[end].Parent3D is { } q ? Evaluator.IndexOf(q) : -1;
        var endParentWorld = ep >= 0 ? world[ep] : Matrix4x4.Identity;
        if (!Matrix4x4.Invert(parentWorld, out var inverseParent) || !Matrix4x4.Invert(endParentWorld * swing, out var inverseEndParent))
            return false;
        var rootLocal = Transform.FromMatrixLoose(world[root] * swing * inverseParent) with
        {
            Translation = local[root].Translation,
            Scale = local[root].Scale,
        };
        var endLocal = Transform.FromMatrixLoose(world[tip] * inverseEndParent) with
        {
            Translation = local[tip].Translation,
            Scale = local[tip].Scale,
        };
        SetEditKey(stack, keyIndex, controller, joints[0], rootLocal, AnimChannels.Rotate);
        SetEditKey(stack, keyIndex, controller, joints[end], endLocal, AnimChannels.Rotate);
        if (foot != null) StoreFoot(stack, keyIndex, foot, time, storeLeg: true);
        else StoreChain(stack, keyIndex, chain!, time);
        foreach (var other in Evaluator.IKControllersInSolveOrder)
            if (!ReferenceEquals(other, controller)) Follow(stack, keyIndex, other, joints[0]);
        AddPoleAngle(stack, keyIndex, controller, angle);
        return true;
    }

    /// <summary>
    /// The Spinner swivel adds its angle to the key's <c>PoleAngle</c> (radians;
    ///). SloppyAnimatsu also keys the swung joints and <c>RefArray</c>, so it
    /// moves <c>RefPoleAngle</c> by the same amount: the stored pose is the one at the new angle.
    /// The loop key follows key 0.
    /// </summary>
    private static void AddPoleAngle(AnimStack stack, int keyIndex, ControllerIK controller, float angle)
    {
        if (stack.TrackFor(controller) is not { } track) return;
        int n = track.Keys.Count;
        foreach (int k in keyIndex == 0 && n > 1 ? new[] { 0, n - 1 } : new[] { keyIndex })
        {
            if (k >= n || track.Keys[k] is not IKKey key) continue;
            key.PoleAngle += angle;
            key.Source.Set("RefPoleAngle", key.RefPoleAngle + angle);
        }
    }

    /// <summary>
    /// The IK key of <paramref name="controller"/> at <paramref name="keyIndex"/> and its chain,
    /// when the stack keys the controller and every chain joint there. Null for reverse-foot
    /// controllers (see <see cref="FootChain"/>).
    /// </summary>
    public IKKeyChain? IKChain(AnimStack stack, int keyIndex, ControllerIK controller)
    {
        if (controller is ControllerReverseFoot || Evaluator.IndexOf(controller) < 0) return null;
        if (stack.TrackFor(controller) is not { } track || keyIndex >= track.Keys.Count || track.Keys[keyIndex] is not IKKey key)
            return null;
        var joints = controller.Chain;
        if (joints.Count < 2 || joints.Any(j => Evaluator.IndexOf(j) < 0 || stack.TrackFor(j) == null)) return null;
        return new IKKeyChain(controller, key, joints);
    }

    /// <summary>An IK controller's key and chain joints in one stack (<see cref="IKChain"/>).</summary>
    public sealed record IKKeyChain(ControllerIK Controller, IKKey Key, IReadOnlyList<Object3D> Joints);

    /// <summary>
    /// Whether <paramref name="stack"/> is the stack whose single-stack world
    /// <paramref name="controller"/>'s chain is solved in: the first enabled, non-additive stack
    /// keying it (as <see cref="PoseEvaluator.OwnStackPositions"/>). The IK edit helpers then solve
    /// and write in that world, so the motion other stacks add (a TransN root-motion layer) is not
    /// pulled out of the chain.
    /// </summary>
    private static bool IsOwnStack(AnimStack stack, ControllerIK controller)
    {
        foreach (var s in stack.Animation!.Stacks)
            if (s.IsEnabled && !s.IsAdditive && s.TrackFor(controller) != null) return ReferenceEquals(s, stack);
        return false;
    }

    /// <summary>
    /// The locals and worlds an IK edit of <paramref name="controller"/> in <paramref name="stack"/>
    /// works in: the stack's single-stack world when it is the controller's own stack
    /// (<see cref="IsOwnStack"/>), else the mixed pose.
    /// </summary>
    private Transform[] EditPose(AnimStack stack, ControllerIK controller, float time, out Matrix4x4[] world)
    {
        var animation = stack.Animation!;
        if (IsOwnStack(stack, controller)) return Evaluator.StackPose(animation, stack, time, out world);
        world = Evaluator.WorldMatrices(animation, time);
        return Evaluator.LocalTransforms(animation, time);
    }

    /// <summary><see cref="SetKey"/> for an IK edit of <paramref name="controller"/>: in its own stack the value written is the stack's own (<see cref="EditPose"/>).</summary>
    private void SetEditKey(AnimStack stack, int keyIndex, ControllerIK controller, Object3D target, Transform local,
        AnimChannels channels = AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale) =>
        SetKey(stack, keyIndex, target, local, channels, ownValue: IsOwnStack(stack, controller));

    /// <summary>
    /// The drag of an IK controller:
    /// solve the chain's mixed locals towards the keyed controller, key the joints, key the
    /// controller at the chain's end, then update <c>RefArray</c>.
    /// </summary>
    private void ReachController(AnimStack stack, int keyIndex, IKKeyChain chain, Transform posedLocal)
    {
        var animation = stack.Animation!;
        float time = stack.Keys[keyIndex].Time;
        var local = EditPose(stack, chain.Controller, time, out var world);
        var wanted = world[Evaluator.IndexOf(chain.Controller)];
        var solved = Solve(animation, time, chain, world, local,chain.Joints.Select(j => local[Evaluator.IndexOf(j)]).ToArray(), wanted);
        for (int j = 0; j < chain.Joints.Count; j++) SetEditKey(stack, keyIndex, chain.Controller, chain.Joints[j], solved[j]);

        // The controller's parent frame is unchanged by the joints: local = world × parent⁻¹.
        Matrix4x4.Invert(posedLocal.ToMatrix(), out var inverseLocal);
        var parent = inverseLocal * wanted;
        EditPose(stack, chain.Controller, time, out world);
        double length = ChainLength(chain, world);
        float miss = Vector3.Distance(world[Evaluator.IndexOf(chain.Joints[^1])].Translation, wanted.Translation);
        bool reached = miss * 0.001 / Math.Max(length, 1e-12) < 1e-5;
        bool locked = (chain.Key.Flags & 1) != 0;
        // A locked (IK) key the chain cannot reach keeps the handle where it was dragged: a goal
        // the chain stretches toward.
        // SnapUnreachedHandles puts it on the chain's end instead.
        if (reached || !locked || SnapUnreachedHandles)
            KeyControllerAt(stack, keyIndex, chain.Controller, world[Evaluator.IndexOf(chain.Joints[^1])], parent, posedLocal.Scale);

        // FK keys always store the chain's locals.
        if (!reached || !locked) StoreChain(stack, keyIndex, chain, time);
    }

    /// <summary>
    /// The Spinner's Snap button: keys <paramref name="controller"/> at key
    /// <paramref name="keyIndex"/> on the end of its chain (an IK ring on the leaf joint, a reverse
    /// foot where its shoe's ankle meets the leg's ankle) without moving the chain, stores the
    /// chain in <c>RefArray</c>, then brings the other controllers back in line as after
    /// <see cref="Pose"/>. Returns false when
    /// the stack does not key the controller's chain there.
    /// </summary>
    public bool SnapToChain(AnimStack stack, int keyIndex, ControllerIK controller)
    {
        int n = stack.Keys.Count;
        if (keyIndex == n - 1 && n > 1) keyIndex = 0;
        if (controller is ControllerReverseFoot rf)
        {
            if (FootChain(stack, keyIndex, rf) is not { } foot) return false;
            var animation = stack.Animation!;
            float time = stack.Keys[keyIndex].Time;
            var local = EditPose(stack, rf, time, out var world);
            int ci = Evaluator.IndexOf(rf);
            if (SolveLeg(animation, time, foot, world, local, foot.Joints.Select(j => local[Evaluator.IndexOf(j)]).ToArray(),
                    world[ci], out var shoe) == null) return false;
            Matrix4x4.Invert(local[ci].ToMatrix(), out var inverseLocal);
            var parent = inverseLocal * world[ci];
            var target = ControllerOnAnkle(shoe, world[Evaluator.IndexOf(foot.Joints[2])], world[ci]);
            if (!Near(Transform.FromMatrixLoose(target), Transform.FromMatrixLoose(world[ci])))
                KeyControllerAt(stack, keyIndex, rf, target, parent, local[ci].Scale);
            StoreFoot(stack, keyIndex, foot, time, storeLeg: true);
        }
        else if (IKChain(stack, keyIndex, controller) is { } chain) SnapController(stack, keyIndex, chain);
        else return false;
        foreach (var other in Evaluator.IKControllersInSolveOrder)
            if (!ReferenceEquals(other, controller)) Follow(stack, keyIndex, other, controller);
        return true;
    }

    /// <summary>An FK key: the controller takes its chain end's world transform, and <c>RefArray</c> the chain's locals.</summary>
    private void SnapController(AnimStack stack, int keyIndex, IKKeyChain chain)
    {
        var animation = stack.Animation!;
        float time = stack.Keys[keyIndex].Time;
        var local = EditPose(stack, chain.Controller, time, out var world);
        int ci = Evaluator.IndexOf(chain.Controller);
        Matrix4x4.Invert(local[ci].ToMatrix(), out var inverseLocal);
        var parent = inverseLocal * world[ci];
        var end = world[Evaluator.IndexOf(chain.Joints[^1])];
        if (!Near(Transform.FromMatrixLoose(end), Transform.FromMatrixLoose(world[ci])))
            KeyControllerAt(stack, keyIndex, chain.Controller, end, parent, local[ci].Scale);
        StoreChain(stack, keyIndex, chain, time);
    }

    /// <summary>An IK key: the chain joints are solved from <c>RefArray</c> to the controller and keyed.</summary>
    private void SolveChain(AnimStack stack, int keyIndex, IKKeyChain chain)
    {
        var refs = chain.Key.ChainTransforms;
        if (refs.Count != chain.Joints.Count) return;
        var animation = stack.Animation!;
        float time = stack.Keys[keyIndex].Time;
        var local = EditPose(stack, chain.Controller, time, out var world);
        var solved = Solve(animation, time, chain, world, local,refs, world[Evaluator.IndexOf(chain.Controller)]);
        for (int j = 0; j < chain.Joints.Count; j++)
            if (!Near(local[Evaluator.IndexOf(chain.Joints[j])], solved[j]))
                SetEditKey(stack, keyIndex, chain.Controller, chain.Joints[j], solved[j]);
    }

    /// <summary>FABRIK of <paramref name="string"/> towards <paramref name="target"/> from the chain's base in <paramref name="world"/>.</summary>
    private Transform[] Solve(Animation animation, float time, IKKeyChain chain, Matrix4x4[] world, IReadOnlyList<Transform> local, IReadOnlyList<Transform> @string, Matrix4x4 target)
    {
        int pi = chain.Joints[0].Parent3D is { } p ? Evaluator.IndexOf(p) : -1;
        var @base = pi >= 0 ? world[pi] : Matrix4x4.Identity;
        var baseScale = pi >= 0 ? local[pi].Scale : Vector3.One;
        return IKSolver.Solve(@base, baseScale, @string, target,
            chain.Joints.Select(j => Evaluator.Compensates(j, animation, time)).ToArray(), Evaluator.ExactScaleIK);
    }

    /// <summary>
    /// Keys <paramref name="controller"/> so that its world becomes <paramref name="world"/> under
    /// <paramref name="parent"/> (the frame its keys are in), keeping its scale.
    /// </summary>
    private void KeyControllerAt(AnimStack stack, int keyIndex, ControllerIK controller, Matrix4x4 world, Matrix4x4 parent,
        Vector3 scale)
    {
        if (!Matrix4x4.Invert(parent, out var inverseParent)) return;
        var local = Transform.FromMatrixLoose(world * inverseParent) with { Scale = scale };
        SetEditKey(stack, keyIndex, controller, controller, local, AnimChannels.Translate | AnimChannels.Rotate);
    }

    /// <summary><c>RefArray</c> of the key (and of the end key for key 0) := the chain's current locals.</summary>
    private void StoreChain(AnimStack stack, int keyIndex, IKKeyChain chain, float time)
    {
        var local = EditPose(stack, chain.Controller, time, out _);
        var values = chain.Joints.Select(j => local[Evaluator.IndexOf(j)]).ToArray();
        if (chain.Key.ChainTransforms.Count != values.Length) return;
        chain.Key.SetChainTransforms(values);
        int n = stack.Keys.Count;
        var keys = stack.TrackFor(chain.Controller)!.Keys;
        if (keyIndex == 0 && n > 1 && n - 1 < keys.Count && keys[n - 1] is IKKey loop && loop.ChainTransforms.Count == values.Length)
            loop.SetChainTransforms(values);
    }

    /// <summary>A reverse-foot controller's key, its four leg joints and its seven shoe locators in one stack (<see cref="FootChain"/>).</summary>
    public sealed record FootKeyChain(ControllerReverseFoot Controller, ReverseFootKey Key, IReadOnlyList<Object3D> Joints,
        IReadOnlyList<Object3D> Shoe);

    /// <summary>
    /// The reverse-foot key of <paramref name="controller"/> at <paramref name="keyIndex"/>, when
    /// the stack keys the controller and its four leg joints there, the shoe has seven locators and
    /// the key's <c>RefArray</c> has five entries.
    /// </summary>
    public FootKeyChain? FootChain(AnimStack stack, int keyIndex, ControllerReverseFoot controller)
    {
        if (Evaluator.IndexOf(controller) < 0) return null;
        if (stack.TrackFor(controller) is not { } track || keyIndex >= track.Keys.Count || track.Keys[keyIndex] is not ReverseFootKey key)
            return null;
        var joints = controller.Chain;
        var shoe = controller.Shoe.Take(7).ToArray();
        if (joints.Count != 4 || shoe.Length != 7 || key.ChainTransforms.Count != 5) return null;
        if (joints.Any(j => Evaluator.IndexOf(j) < 0 || stack.TrackFor(j) == null) || shoe.Any(s => Evaluator.IndexOf(s) < 0)) return null;
        return new FootKeyChain(controller, key, joints, shoe);
    }

    /// <summary>
    /// The drag of a reverse-foot controller
    /// the leg's mixed locals are solved toward the shoe under the keyed controller,
    /// the joints are keyed, the controller is keyed where the shoe's ankle meets the leg's ankle,
    /// then <c>RefArray</c> is updated.
    /// </summary>
    private void ReachFoot(AnimStack stack, int keyIndex, FootKeyChain chain, Transform posedLocal)
    {
        var animation = stack.Animation!;
        float time = stack.Keys[keyIndex].Time;
        var local = EditPose(stack, chain.Controller, time, out var world);
        var wanted = world[Evaluator.IndexOf(chain.Controller)];
        var solved = SolveLeg(animation, time, chain, world, local,chain.Joints.Select(j => local[Evaluator.IndexOf(j)]).ToArray(), wanted, out var shoe);
        if (solved == null) return;
        for (int j = 0; j < 4; j++) SetEditKey(stack, keyIndex, chain.Controller, chain.Joints[j], solved[j]);

        // controller = P⁻¹ · ankle (column-vector terms), with P the shoe chain to SkeletonAnkle.
        Matrix4x4.Invert(posedLocal.ToMatrix(), out var inverseLocal);
        var parent = inverseLocal * wanted;
        EditPose(stack, chain.Controller, time, out world);
        var onAnkle = ControllerOnAnkle(shoe, world[Evaluator.IndexOf(chain.Joints[2])], wanted);
        double length = ChainLength(chain.Joints, world);
        float miss = Vector3.Distance(onAnkle.Translation, wanted.Translation);
        bool reached = miss * 0.001 / Math.Max(length, 1e-12) < 1e-5;
        bool locked = (chain.Key.Flags & 1) != 0;
        // As ReachController: a locked key the leg cannot reach keeps the shoe where it was dragged.
        if (reached || !locked || SnapUnreachedHandles)
            KeyControllerAt(stack, keyIndex, chain.Controller, onAnkle, parent, posedLocal.Scale);
        StoreFoot(stack, keyIndex, chain, time, storeLeg: !reached || !locked);
    }

    /// <summary>
    /// The controller world that puts the shoe's SkeletonAnkle (<paramref name="shoe"/>, relative to
    /// the controller) on <paramref name="ankle"/>: the orientation of P⁻¹ · ankle, the scale of
    /// <paramref name="controller"/>, and the position that sets SkeletonAnkle exactly on the ankle.
    /// With unit or matching scales this is P⁻¹ · ankle itself; when the leg's scale differs from
    /// the shoe's (a non-uniformly scaled calf or controller), the full product would carry that
    /// difference into the controller's position
    /// </summary>
    private static Matrix4x4 ControllerOnAnkle(Matrix4x4 shoe, Matrix4x4 ankle, Matrix4x4 controller)
    {
        Matrix4x4.Invert(shoe, out var inverseShoe);
        var rotation = Transform.FromMatrixLoose(inverseShoe * ankle).Rotation;
        var world = Matrix4x4.CreateScale(Transform.FromMatrixLoose(controller).Scale) * Matrix4x4.CreateFromQuaternion(rotation);
        world.Translation = ankle.Translation - Vector3.Transform(shoe.Translation, world);
        return world;
    }

    /// <summary>
    /// <em>Shoe to leg</em>: the leg is solved from <c>RefArray</c> toward the shoe under the
    /// controller and keyed, then <c>RefArray[4]</c> takes the controller relative to the ball.
    /// </summary>
    private void ShoeToLeg(AnimStack stack, int keyIndex, FootKeyChain chain)
    {
        var animation = stack.Animation!;
        float time = stack.Keys[keyIndex].Time;
        var local = EditPose(stack, chain.Controller, time, out var world);
        var refs = chain.Key.ChainTransforms;
        var solved = SolveLeg(animation, time, chain, world, local,refs.Take(4).ToArray(), world[Evaluator.IndexOf(chain.Controller)], out _);
        if (solved == null) return;
        for (int j = 0; j < 4; j++)
            if (!Near(local[Evaluator.IndexOf(chain.Joints[j])], solved[j]))
                SetEditKey(stack, keyIndex, chain.Controller, chain.Joints[j], solved[j]);
        StoreFoot(stack, keyIndex, chain, time, storeLeg: false);
    }

    /// <summary>
    /// <em>Leg to shoe</em> (an FK key): <c>RefArray[0..3]</c> takes the leg's locals and the
    /// controller keeps its offset to the ball, <c>RefArray[4]</c>.
    /// </summary>
    private void LegToShoe(AnimStack stack, int keyIndex, FootKeyChain chain)
    {
        var animation = stack.Animation!;
        float time = stack.Keys[keyIndex].Time;
        var local = EditPose(stack, chain.Controller, time, out var world);
        int ci = Evaluator.IndexOf(chain.Controller);
        Matrix4x4.Invert(local[ci].ToMatrix(), out var inverseLocal);
        var parent = inverseLocal * world[ci];
        var target = chain.Key.ChainTransforms[4].ToMatrix() * world[Evaluator.IndexOf(chain.Joints[3])];
        if (!Near(Transform.FromMatrixLoose(target), Transform.FromMatrixLoose(world[ci])))
            KeyControllerAt(stack, keyIndex, chain.Controller, target, parent, local[ci].Scale);
        StoreFoot(stack, keyIndex, chain, time, storeLeg: true);
    }

    /// <summary>
    /// The reverse-foot leg solve of <paramref name="string"/> toward the shoe that the key's
    /// angles pose under <paramref name="controller"/> (twist 0). <paramref name="shoe"/> is the
    /// shoe chain's product, SkeletonAnkle relative to the controller.
    /// </summary>
    private Transform[]? SolveLeg(Animation animation, float time, FootKeyChain chain, Matrix4x4[] world, Transform[] local, IReadOnlyList<Transform> @string,
        Matrix4x4 controller, out Matrix4x4 shoe)
    {
        var rf = chain.Controller;
        var rest = chain.Shoe.Select(s => Evaluator.RestTransforms[Evaluator.IndexOf(s)]).ToArray();
        var locals = ReverseFootSolver.ShoeLocals(rest, chain.Key.RollAngle, chain.Key.TwistAngle, chain.Key.BankAngle,
            ReverseFootLimits.FromScene(Character.Model.Scene), new Vector3(rf.RollAxisPlant.X, rf.RollAxisPlant.Y, rf.RollAxisPlant.Z));
        shoe = Matrix4x4.Identity;
        var shoeWorld = new Matrix4x4[7];
        for (int j = 0; j < 7; j++)
        {
            shoe = locals[j].ToMatrix() * shoe;
            shoeWorld[j] = shoe * controller;
        }
        int pi = chain.Joints[0].Parent3D is { } p ? Evaluator.IndexOf(p) : -1;
        var @base = pi >= 0 ? world[pi] : Matrix4x4.Identity;
        var baseScale = pi >= 0 ? local[pi].Scale : Vector3.One;
        return ReverseFootSolver.Solve(@base, baseScale, @string,
            chain.Joints.Select(j => Evaluator.Compensates(j, animation, time)).ToArray(),
            Evaluator.RestTransforms[Evaluator.IndexOf(chain.Joints[3])], rest[ReverseFootSolver.SkeletonPlant],
            shoeWorld[ReverseFootSolver.SkeletonAnkle], shoeWorld[ReverseFootSolver.SkeletonPlant], shoeWorld[ReverseFootSolver.SkeletonToes],
            Evaluator.ExactScaleIK);
    }

    /// <summary>
    /// <c>RefArray</c> of the key (and of the end key for key 0): entry 4 := the controller
    /// relative to the ball; with <paramref name="storeLeg"/>, entries 0..3 := the leg's locals.
    /// </summary>
    private void StoreFoot(AnimStack stack, int keyIndex, FootKeyChain chain, float time, bool storeLeg)
    {
        var local = EditPose(stack, chain.Controller, time, out var world);
        var values = chain.Key.ChainTransforms.ToArray();
        if (storeLeg)
            for (int j = 0; j < 4; j++) values[j] = local[Evaluator.IndexOf(chain.Joints[j])];
        Matrix4x4.Invert(world[Evaluator.IndexOf(chain.Joints[3])], out var inverseBall);
        values[4] = Transform.FromMatrixLoose(world[Evaluator.IndexOf(chain.Controller)] * inverseBall);
        chain.Key.SetChainTransforms(values);
        int n = stack.Keys.Count;
        var keys = stack.TrackFor(chain.Controller)!.Keys;
        if (keyIndex == 0 && n > 1 && n - 1 < keys.Count && keys[n - 1] is IKKey loop && loop.ChainTransforms.Count == values.Length)
            loop.SetChainTransforms(values);
    }

    /// <summary>Whether <paramref name="node"/> is <paramref name="descendant"/> or one of its parents.</summary>
    private static bool IsAncestorOrSelf(Object3D node, Object3D descendant)
    {
        for (Object3D? o = descendant; o != null; o = o.Parent3D)
            if (ReferenceEquals(o, node)) return true;
        return false;
    }

    private double ChainLength(IKKeyChain chain, Matrix4x4[] world) => ChainLength(chain.Joints, world);

    private double ChainLength(IReadOnlyList<Object3D> joints, Matrix4x4[] world)
    {
        double length = 0;
        for (int j = 1; j < joints.Count; j++)
            length += Vector3.Distance(world[Evaluator.IndexOf(joints[j])].Translation,
                world[Evaluator.IndexOf(joints[j - 1])].Translation);
        return length;
    }

    private static bool Near(Transform a, Transform b) =>
        Vector3.Distance(a.Translation, b.Translation) <= 1e-5f * MathF.Max(1, a.Translation.Length())
        && MathF.Abs(Quaternion.Dot(a.Rotation, b.Rotation)) >= 1 - 1e-7f;

    /// <summary>
    /// The create-key action at <paramref name="frame"/>: on an existing key (not the end key) it
    /// duplicates that key, otherwise it inserts a key holding each track's current value.
    /// Returns the new key's index.
    /// </summary>
    public int CreateKey(AnimStack stack, int frame)
    {
        var keys = stack.Keys;
        if (keys.Count == 0) throw new InvalidOperationException($"{stack} has no keys.");
        PoseEvaluator.Timing(keys, frame, out int exact);
        int result = exact >= 0 ? DuplicateKey(stack, exact) : InsertKey(stack, frame);
        // A created key re-derives every camera rig's hidden-joint and looker keys in the stack.
        foreach (var camera in stack.Tracks.Select(t => t.Target).OfType<CameraAnimated>().Distinct().ToList())
            CameraRigKeys.Derive(stack, result, Evaluator, camera);
        RecomputeAll(stack);
        return result;
    }

    /// <summary>
    /// Whether <see cref="AddToStack"/> can create tracks for <paramref name="target"/> and its
    /// group. IK controllers, reverse-foot
    /// controllers and shoe locators are supported; an object driven by a controller type
    /// SloppyAnimatsu does not model is refused, because its group is unknown.
    /// </summary>
    public static bool CanAddToStack(Object3D target, out string? reason)
    {
        reason = target.Constraint is { } c and not ControllerIK and not CameraTarget
            ? $"{target.Name} is driven by {c.Name}; adding objects of that kind to a stack is not supported yet."
            : null;
        return reason == null;
    }

    /// <summary>
    /// The objects the hammer's add takes into a stack for <paramref name="target"/>: its
    /// functional group of the add kind
    /// A plain object comes alone (or, when constrained, brings its constraint's
    /// group); an IK controller brings its chain joints and then itself; a reverse-foot
    /// controller brings its shoe locators, its leg and then itself. Order is preserved.
    /// </summary>
    public static IEnumerable<Object3D> AddGroup(Object3D target)
    {
        var seen = new HashSet<Object3D>(ReferenceEqualityComparer.Instance);
        IEnumerable<Object3D> Expand(Object3D o)
        {
            switch (o)
            {
                case ControllerReverseFoot foot:
                    foreach (var locator in foot.Shoe) yield return locator;
                    foreach (var joint in foot.Chain) yield return joint;
                    yield return foot;
                    break;
                case ControllerIK ik:
                    foreach (var joint in ik.Chain) yield return joint;
                    yield return ik;
                    break;
                default:
                    yield return o;
                    if (o.Constraint is ControllerIK owner)
                        foreach (var member in Expand(owner)) yield return member;
                    break;
            }
        }
        foreach (var item in Expand(target))
            if (seen.Add(item)) yield return item;
    }

    /// <summary>
    /// The hammer's add: appends a new track to
    /// <paramref name="stack"/> for <paramref name="targets"/> and their functional groups (see
    /// <see cref="AddGroup"/>) that have none there. The new tracks hold the reference pose
    /// (identity offsets) with the target's defaults; IK and reverse-foot keys get their
    /// <c>RefArray</c> from the evaluated locals at each key ([editing-keys](editing-keys.md)
    /// *IK and reverse-foot keys*). Their tangents are then recomputed.
    /// At each key (but the end key) whose time is also a key time of another stack of the
    /// animation, the key takes the object's space there. Throws when a target cannot be added
    /// (<see cref="CanAddToStack"/>), before changing anything. Returns the tracks created.
    /// </summary>
    public IReadOnlyList<AnimTrack> AddToStack(AnimStack stack, IEnumerable<Object3D> targets)
    {
        var animation = stack.Animation ?? throw new InvalidOperationException($"{stack} is not in an animation.");
        var list = targets.Distinct().ToList();
        foreach (var t in list)
            if (!CanAddToStack(t, out string? reason))
                throw new InvalidOperationException(reason);
        var todo = list.SelectMany(AddGroup).Distinct().Where(t => stack.TrackFor(t) == null).ToList();
        if (todo.Count == 0) return [];

        var stackKeys = stack.Keys;
        int n = stackKeys.Count;
        var created = todo.Select(target => TrackFactory.Create(stack, target)).ToList();
        Evaluator.Invalidate();

        // Spaces at key times shared with another stack
        // converted from the space over all stacks into the other stacks' one.
        var converter = new KeySpaceConverter(Evaluator, animation);
        for (int i = 0; i < n - 1; i++)
        {
            int time = stackKeys[i].Time;
            if (!OtherStackKeysFrame(animation, stack, time)) continue;
            foreach (var track in created)
            {
                if (track.Target is not { } target || track.Keys[i] is not TransformKey key) continue;
                var space = SpaceInStacks(animation, target, time, stack, out bool answered);
                if (!answered) continue;
                SetSpace(converter, stack, key, target, SpaceInStacks(animation, target, time, null, out _), space, time);
                if (i == 0) MirrorLoopKey(track);
            }
        }
        Evaluator.Invalidate();
        foreach (var track in created) FillChainTransforms(stack, track);
        // A camera track brings its rig's hidden-joint and looker tracks with it; those are not in
        // <paramref name="created"/> and get their <c>RefArray</c> here.
        CameraRigKeys.FillLookerChains(stack, Evaluator);
        Evaluator.Invalidate();
        // The default tangent keys are identity transforms, which read as slopes; recompute them as
        // after any other key edit.
        foreach (var track in created) TangentRecompute.Recompute(track, Evaluator);
        Evaluator.Invalidate();
        return created;
    }

    /// <summary>
    /// Fills the <c>RefArray</c> of a new IK or reverse-foot track's value keys from the chain's
    /// evaluated locals at each key's time, and mirrors key 0 into the end key. A reverse-foot
    /// key's last entry is the controller relative to the ball
    /// Does nothing for other tracks.
    /// </summary>
    private void FillChainTransforms(AnimStack stack, AnimTrack track)
    {
        if (stack.Animation is not { } animation) return;
        IReadOnlyList<Object3D> joints = track.Target switch
        {
            ControllerIK controller => controller.Chain,
            CameraTarget target => target.Chain,
            _ => [],
        };
        if (track.Keys.Count == 0 || track.Keys[0] is not IKKey) return;
        if (joints.Count == 0 || joints.Any(j => Evaluator.IndexOf(j) < 0)) return;
        bool foot = track.Target is ControllerReverseFoot && joints.Count == 4;
        var keys = stack.Keys;
        for (int i = 0; i < Math.Min(keys.Count, track.Keys.Count); i++)
        {
            if (track.Keys[i] is not IKKey key) continue;
            var local = Evaluator.LocalTransforms(animation, keys[i].Time);
            var values = joints.Select(j => local[Evaluator.IndexOf(j)]).ToList();
            if (foot)
            {
                var world = Evaluator.WorldMatrices(animation, keys[i].Time);
                Matrix4x4.Invert(world[Evaluator.IndexOf(joints[3])], out var inverseBall);
                values.Add(Transform.FromMatrixLoose(world[Evaluator.IndexOf(track.Target!)] * inverseBall));
            }
            key.InitChainTransforms(values);
        }
        MirrorLoopKey(track);
    }

    /// <summary>
    /// The objects the hammer's remove takes out of a stack for <paramref name="target"/>: the
    /// target and its functional group of the remove kind
    /// An IK controller's group is itself; a reverse-foot controller's adds its
    /// shoe locators; any other object brings its constraint's group.
    /// </summary>
    public static IEnumerable<Object3D> RemoveGroup(Object3D target)
    {
        yield return target;
        var owner = target is ControllerIK ? target : target.Constraint;
        if (owner is ControllerReverseFoot foot)
            foreach (var locator in foot.Shoe) yield return locator;
        if (owner != null) yield return owner;
    }

    /// <summary>
    /// The hammer's remove: erases the track in
    /// <paramref name="stack"/> of every object of <paramref name="targets"/> and its remove group
    /// (<see cref="RemoveGroup"/>). Then the IK and reverse-foot controllers that drive
    /// <paramref name="clicked"/> or a descendant of it, and still have a track there, are brought
    /// up to date at every key as after a key edit (the chain may have moved). Returns the number
    /// of tracks removed.
    /// </summary>
    public int RemoveFromStack(AnimStack stack, IEnumerable<Object3D> targets, Object3D clicked)
    {
        var doomed = targets.SelectMany(RemoveGroup).Distinct()
            .Select(stack.TrackFor).OfType<AnimTrack>().ToList();
        if (doomed.Count == 0) return 0;
        foreach (var track in doomed) stack.Source.RemoveChild(track.Source);
        Evaluator.Invalidate();

        // Step 3: the controllers of the clicked object and its descendants, whose chain root is keyed here.
        var drivers = new[] { clicked }.Concat(clicked.Descendants<Object3D>())
            .Select(o => o.Constraint).OfType<ControllerIK>().Distinct()
            .Where(c => !ReferenceEquals(c, clicked) && stack.TrackFor(c) != null
                        && c.Chain.Count > 0 && stack.TrackFor(c.Chain[0]) != null)
            .ToHashSet();
        if (drivers.Count > 0)
        {
            int n = stack.Keys.Count;
            for (int k = 0; k < Math.Max(1, n - 1); k++)
                foreach (var c in Evaluator.IKControllersInSolveOrder)
                    if (drivers.Contains(c)) Follow(stack, k, c, clicked);
        }
        Evaluator.Invalidate();
        return doomed.Count;
    }

    /// <summary>
    /// Deletes the time entries <paramref name="keyIndices"/> of <paramref name="stack"/> from the
    /// stack and every track. The end key is never
    /// deleted. With <paramref name="closeGaps"/> (the stacker) the interval before each deleted
    /// key disappears; without it (the timeline) the other keys keep their times. If only the end
    /// key would remain, a default entry (rest pose) is inserted at index 0. Returns the number of
    /// entries deleted.
    /// </summary>
    public int DeleteKeys(AnimStack stack, IEnumerable<int> keyIndices, bool closeGaps)
    {
        var keys = stack.Keys;
        int n = keys.Count;
        var selected = keyIndices.Where(i => i >= 0 && i < n - 1).Distinct().OrderBy(i => i).ToArray();
        if (selected.Length == 0) return 0;
        var old = keys.Select(k => k.Time).ToArray();
        var tracks = stack.Tracks.ToList();

        // New times of the kept keys, in order.
        var kept = Enumerable.Range(0, n).Except(selected).ToArray();
        var times = new int[kept.Length];
        for (int j = 0; j < kept.Length; j++)
        {
            int i = kept[j];
            times[j] = !closeGaps ? old[i]
                : j > 0 ? times[j - 1] + old[i] - old[i - 1]
                : i == n - 1 ? old[n - 1] - old[n - 2]
                : old[0];
        }

        for (int s = selected.Length - 1; s >= 0; s--)
        {
            stack.RemoveKeyTime(selected[s]);
            foreach (var track in tracks) track.RemoveKey(selected[s]);
        }
        for (int j = 0; j < times.Length; j++) stack.SetKeyTime(j, times[j]);

        if (kept.Length == 1)
        {
            // Only the end key is left: a default entry goes before it, at the old first time.
            stack.InsertKeyTime(0, 0, old[0]);
            stack.SetKeyFlags(0, 0, 0, 0);
            if (times[0] <= old[0]) stack.SetKeyTime(1, old[0] + Math.Max(old[n - 1] - old[n - 2], 1));
            foreach (var track in tracks)
            {
                track.InsertKeyCopy(0, 0);
                ResetToDefault(track, 0);
            }
        }

        if (selected[0] == 0 || kept.Length == 1)
            foreach (var track in tracks) MirrorLoopKey(track);
        // The stacker delete retimes every remaining key, which runs the space fix-up on each.
        if (closeGaps) FixSpacesAt(stack, Enumerable.Range(0, stack.Keys.Count));

        RecomputeAll(stack);
        return selected.Length;
    }

    /// <summary>
    /// Removes <paramref name="controller"/>'s key <paramref name="keyIndex"/> from its curve
    /// without deleting the stack's time entry
    /// the key takes the value the track has at that time when the key is
    /// left out, in the previous key's space. The end key cannot be deleted.
    /// </summary>
    public void DeleteTrackKey(AnimStack stack, int keyIndex, Object3D controller)
    {
        var track = stack.TrackFor(controller) ?? throw new ArgumentException($"{stack} has no track for {controller}.", nameof(controller));
        var keys = stack.Keys;
        int n = keys.Count;
        if (keyIndex < 0 || keyIndex >= n - 1) throw new ArgumentOutOfRangeException(nameof(keyIndex), "The end key cannot be deleted.");
        if (keyIndex >= track.Keys.Count || track.Keys[keyIndex] is not TransformKey) throw new InvalidOperationException($"{track} has no key {keyIndex}.");
        if (n <= 2) return; // no other key to take a value from
        int frame = keys[keyIndex].Time;

        // Take the entry out of the whole stack, read the track there, and put everything back.
        var tracks = stack.Tracks.ToList();
        var entry = stack.TakeKeyTime(keyIndex);
        var taken = tracks.Select(t => t.TakeKey(keyIndex)).ToArray();
        Transform value;
        Object3D? space;
        Action<IKKey>? scalars;
        float? fov;
        try
        {
            if (keyIndex == 0) MirrorLoopKey(track);
            TangentRecompute.Recompute(track);
            Evaluator.Invalidate();
            value = Evaluator.LayerValue(stack, track, frame, out _);
            space = Evaluator.SpaceOf(stack, track, frame);
            scalars = ScalarSnapshot(stack, track, frame);
            fov = track.HasFov ? Evaluator.LayerFov(stack, track, frame) : null;
        }
        finally
        {
            stack.PutKeyTime(keyIndex, entry);
            for (int j = 0; j < tracks.Count; j++) tracks[j].PutKey(keyIndex, taken[j]);
        }

        var key = (TransformKey)track.Keys[keyIndex]!;
        key.Value = value;
        key.Space = space;
        if (key is CameraKey camera && fov is { } f) camera.Fov = f;
        if (key is IKKey ik) scalars?.Invoke(ik);
        if (keyIndex == 0) MirrorLoopKey(track);
        RecomputeAll(stack);
    }

    /// <summary>
    /// The timeline move: shifts keys <paramref name="keyIndices"/> by <paramref name="delta"/>
    /// frames. A key whose new time is taken
    /// steps back toward its old time; the keys are then sorted by time and the end key is pushed
    /// past the last key if needed. The end key is not moved, except a cycle's loop key, which moves
    /// and stays after the key before it. Returns each old key index's new index.
    /// </summary>
    public int[] MoveKeys(AnimStack stack, IEnumerable<int> keyIndices, int delta)
    {
        var keys = stack.Keys;
        int n = keys.Count;
        var identity = Enumerable.Range(0, n).ToArray();
        var indices = keyIndices.ToArray();
        var selected = indices.Where(i => i >= 0 && i < n - 1).Distinct().ToArray();
        // A cycle's loop key is a key of the timeline and moves too, staying after the key before
        // it; its values stay key 0's.
        bool loopKey = stack.IsCycle && n >= 2 && indices.Contains(n - 1);
        if ((selected.Length == 0 && !loopKey) || delta == 0) return identity;
        var times = keys.Select(k => k.Time).ToArray();
        var oldTimes = times.ToArray();
        var taken = new HashSet<int>(Enumerable.Range(0, n).Except(selected).Select(i => times[i]));
        int step = Math.Sign(delta);
        foreach (int i in delta > 0 ? selected.OrderByDescending(i => i) : selected.OrderBy(i => i))
        {
            int t = times[i] + delta;
            while (t != times[i] && taken.Contains(t)) t -= step;
            times[i] = t;
            taken.Add(t);
        }

        // Stable sort of keys 0 … n−2; the end key stays last.
        var order = Enumerable.Range(0, n - 1).OrderBy(i => times[i]).Append(n - 1).ToArray();
        Reorder(stack, order);
        for (int j = 0; j < n; j++) stack.SetKeyTime(j, times[order[j]]);
        if (loopKey) stack.SetKeyTime(n - 1, Math.Max(times[n - 1] + delta, times[order[n - 2]] + 1));
        else if (n >= 2 && times[n - 1] < times[order[n - 2]]) stack.SetKeyTime(n - 1, times[order[n - 2]] + 1);
        if (order[0] != 0) foreach (var track in stack.Tracks) MirrorLoopKey(track);
        var retimed = stack.Keys;
        FixSpacesAt(stack, Enumerable.Range(0, n).Where(j => retimed[j].Time != oldTimes[order[j]]));

        RecomputeAll(stack);
        var result = new int[n];
        for (int j = 0; j < n; j++) result[order[j]] = j;
        return result;
    }

    /// <summary>
    /// The stacker reorder: moves keys <paramref name="keyIndices"/> (in their order) before key
    /// <paramref name="destination"/>. Each key keeps the gap before it (key 0 the gap to key 1)
    /// and the times are rebuilt from the old first time. The end key stays last. Returns each old
    /// key index's new index.
    /// </summary>
    public int[] ReorderKeys(AnimStack stack, IEnumerable<int> keyIndices, int destination)
    {
        var keys = stack.Keys;
        int n = keys.Count;
        var selected = keyIndices.Where(i => i >= 0 && i < n - 1).Distinct().OrderBy(i => i).ToArray();
        destination = Math.Clamp(destination, 0, n - 1);
        var rest = Enumerable.Range(0, n - 1).Except(selected).ToList();
        int at = rest.Count(i => i < destination);
        rest.InsertRange(at, selected);
        var order = rest.Append(n - 1).ToArray();
        if (n < 2 || order.SequenceEqual(Enumerable.Range(0, n))) return Enumerable.Range(0, n).ToArray();

        var old = keys.Select(k => k.Time).ToArray();
        int Gap(int i) => i == 0 ? old[1] - old[0] : old[i] - old[i - 1];
        Reorder(stack, order);
        int time = old[0];
        for (int j = 0; j < n; j++)
        {
            if (j > 0) time += Gap(order[j]);
            stack.SetKeyTime(j, time);
        }
        if (order[0] != 0) foreach (var track in stack.Tracks) MirrorLoopKey(track);
        var retimed = stack.Keys;
        FixSpacesAt(stack, Enumerable.Range(0, n).Where(j => retimed[j].Time != old[order[j]]));

        RecomputeAll(stack);
        var result = new int[n];
        for (int j = 0; j < n; j++) result[order[j]] = j;
        return result;
    }

    /// <summary>
    /// Sets key <paramref name="keyIndex"/> to <paramref name="frame"/> (at least one frame after
    /// the previous key) and moves every later key by the same amount. Returns the time set.
    /// </summary>
    public int SetKeyTime(AnimStack stack, int keyIndex, int frame)
    {
        var keys = stack.Keys;
        if (keyIndex < 0 || keyIndex >= keys.Count) throw new ArgumentOutOfRangeException(nameof(keyIndex));
        if (keyIndex > 0) frame = Math.Max(frame, keys[keyIndex - 1].Time + 1);
        int delta = frame - keys[keyIndex].Time;
        if (delta == 0) return frame;
        for (int i = keyIndex; i < keys.Count; i++) stack.SetKeyTime(i, keys[i].Time + delta);
        FixSpacesAt(stack, Enumerable.Range(keyIndex, keys.Count - keyIndex));
        RecomputeAll(stack);
        return frame;
    }

    /// <summary>
    /// Sets the gap before each of keys <paramref name="keyIndices"/> (key 0 has none) to
    /// <paramref name="gap"/> frames (at least 1) and rebuilds the times from the first key's.
    /// </summary>
    public void SetKeyGap(AnimStack stack, IEnumerable<int> keyIndices, int gap) =>
        ChangeGaps(stack, keyIndices, _ => Math.Max(gap, 1));

    /// <summary>
    /// Adds <paramref name="step"/> to the gap before each of keys <paramref name="keyIndices"/>.
    /// Nothing changes if a gap would drop below 1. Returns whether the gaps changed.
    /// </summary>
    public bool ChangeKeyGap(AnimStack stack, IEnumerable<int> keyIndices, int step)
    {
        var keys = stack.Keys;
        var selected = keyIndices.Where(i => i > 0 && i < keys.Count).ToArray();
        if (selected.Any(i => keys[i].Time - keys[i - 1].Time + step < 1)) return false;
        ChangeGaps(stack, selected, g => g + step);
        return true;
    }

    /// <summary>Key mark values of <c>CAnimStackKey.Flags</c>.</summary>
    public const uint NoMark = 0, ExtremeMark = 1, BreakdownMark = 2;

    /// <summary>
    /// Sets the pose mark (<see cref="NoMark"/>, <see cref="ExtremeMark"/> or
    /// <see cref="BreakdownMark"/>) of keys <paramref name="keyIndices"/>, keeping their fades. The
    /// end (loop) key is never marked. Marks do not affect evaluation, so tangents are left alone.
    /// Returns the number of keys whose mark changed.
    /// </summary>
    public static int SetKeyMarks(AnimStack stack, IEnumerable<int> keyIndices, uint mark)
    {
        if (mark > BreakdownMark) throw new ArgumentOutOfRangeException(nameof(mark));
        var keys = stack.Keys;
        int changed = 0;
        foreach (int i in keyIndices.Where(i => i >= 0 && i < keys.Count - 1).Distinct())
        {
            if (keys[i].Flags == mark) continue;
            stack.SetKeyFlags(i, mark, keys[i].FadeIn, keys[i].FadeOut);
            changed++;
        }
        return changed;
    }

    /// <summary>The fade length the add-fade commands give.</summary>
    public const ushort DefaultFade = 5;

    /// <summary>
    /// Sets the fade-in (<paramref name="fadeIn"/>) or fade-out length of keys
    /// <paramref name="keyIndices"/> to <paramref name="frames"/>: <see cref="DefaultFade"/> adds a
    /// fade, 0 removes it. The end key is not skipped, although its fades have no
    /// effect. Returns the number of keys whose fade changed.
    /// </summary>
    public int SetKeyFades(AnimStack stack, IEnumerable<int> keyIndices, bool fadeIn, ushort frames)
    {
        var keys = stack.Keys;
        int changed = 0;
        foreach (int i in keyIndices.Where(i => i >= 0 && i < keys.Count).Distinct())
        {
            var k = keys[i];
            if ((fadeIn ? k.FadeIn : k.FadeOut) == frames) continue;
            stack.SetKeyFlags(i, k.Flags, fadeIn ? frames : k.FadeIn, fadeIn ? k.FadeOut : frames);
            changed++;
        }
        if (changed > 0) Evaluator.Invalidate();
        return changed;
    }

    /// <summary>
    /// Sets the fade-in (<paramref name="fadeIn"/>) or fade-out length of key
    /// <paramref name="keyIndex"/> to <paramref name="frames"/>, at least 1 (a fade handle drag never
    /// removes the fade). Returns the length set.
    /// </summary>
    public ushort SetKeyFade(AnimStack stack, int keyIndex, bool fadeIn, int frames)
    {
        ushort value = (ushort)Math.Clamp(frames, 1, ushort.MaxValue);
        SetKeyFades(stack, [keyIndex], fadeIn, value);
        return value;
    }

    /// <summary>
    /// The fade length a handle drag sets: the length at the press plus the frames dragged for a
    /// fade-out (its handle is after the key), minus them for a fade-in, and at least 1.
    /// </summary>
    public static int DraggedFade(int pressFade, bool fadeIn, int deltaFrames) =>
        Math.Max(1, fadeIn ? pressFade - deltaFrames : pressFade + deltaFrames);

    private void ChangeGaps(AnimStack stack, IEnumerable<int> keyIndices, Func<int, int> newGap)
    {
        var keys = stack.Keys;
        var selected = keyIndices.Where(i => i > 0 && i < keys.Count).ToHashSet();
        if (selected.Count == 0) return;
        int time = keys[0].Time;
        var moved = new List<int>();
        for (int i = 1; i < keys.Count; i++)
        {
            int gap = keys[i].Time - keys[i - 1].Time;
            time += selected.Contains(i) ? newGap(gap) : gap;
            if (time != keys[i].Time) moved.Add(i);
            stack.SetKeyTime(i, time);
        }
        FixSpacesAt(stack, moved);
        RecomputeAll(stack);
    }

    /// <summary>Reorders the stack's time entries and every track's lists together.</summary>
    private static void Reorder(AnimStack stack, int[] order)
    {
        stack.ReorderKeyTimes(order);
        foreach (var track in stack.Tracks) track.ReorderKeys(order);
    }

    /// <summary>
    /// The end key becomes key 0's (it replays key 0): the value and space, and for an IK key its
    /// pole angle, IK flag and <c>RefArray</c> (and a reverse-foot key's angles)
    /// </summary>
    private static void MirrorLoopKey(AnimTrack track)
    {
        var keys = track.Keys;
        int last = (track.Stack?.Keys.Count ?? 0) - 1;
        if (last <= 0 || last >= keys.Count || keys[0] is not TransformKey first || keys[last] is not TransformKey loop) return;
        loop.Value = first.Value;
        loop.Space = first.Space;
        if (first is CameraKey cameraFirst && loop is CameraKey cameraLoop)
        {
            cameraLoop.Fov = cameraFirst.Fov;
            cameraLoop.Flags = cameraFirst.Flags;
        }
        if (first is not IKKey ikFirst || loop is not IKKey ikLoop) return;
        ikLoop.PoleAngle = ikFirst.PoleAngle;
        ikLoop.SetFlags(ikFirst.Flags);
        if (ikFirst.ChainTransforms is { Count: > 0 } chain && chain.Count == ikLoop.ChainTransforms.Count)
            ikLoop.SetChainTransforms(chain);
        if (first is ReverseFootKey footFirst && loop is ReverseFootKey footLoop)
        {
            footLoop.RollAngle = footFirst.RollAngle;
            footLoop.TwistAngle = footFirst.TwistAngle;
            footLoop.BankAngle = footFirst.BankAngle;
        }
    }

    /// <summary>Key <paramref name="index"/> of <paramref name="track"/> becomes a default key.</summary>
    private static void ResetToDefault(AnimTrack track, int index)
    {
        foreach (var list in new[] { track.Keys, track.TangentsIn, track.TangentsOut })
            if (index < list.Count && list[index] is TransformKey k)
            {
                k.Value = Transform.Identity;
                k.Space = null;
                if (k is CameraKey camera) (camera.Fov, camera.Flags) = (CameraKey.DefaultFov, 0u);
                if (k is IKKey ik) ik.PoleAngle = 0;
                if (k is ReverseFootKey r) (r.RollAngle, r.TwistAngle, r.BankAngle) = (0, 0, 0);
            }
        var modes = track.TangentModes;
        if (index < modes.Count)
            for (int c = 0; c < modes[index].Length; c++)
            {
                uint type = TangentRecompute.DefaultType(track.Target, c);
                track.SetTangentMode(index, c, new TangentMode((modes[index][c].Raw & 0x100) | (type << 4) | type));
            }
    }

    /// <summary>Recomputes every track of <paramref name="stack"/> with the space-aware lookup.</summary>
    private void RecomputeAll(AnimStack stack)
    {
        Evaluator.Invalidate();
        var converter = stack.Animation is { } animation ? new KeySpaceConverter(Evaluator, animation) : null;
        foreach (var track in stack.Tracks) TangentRecompute.Recompute(track, converter);
        Evaluator.Invalidate();
    }

    /// <summary>Key k+1 becomes a copy of key k; it and every later key move by the previous gap (10 frames after key 0).</summary>
    private static int DuplicateKey(AnimStack stack, int k)
    {
        var keys = stack.Keys;
        int gap = k == 0 ? 10 : keys[k].Time - keys[k - 1].Time;
        stack.InsertKeyTime(k + 1, k, keys[k].Time);
        for (int i = k + 1; i <= keys.Count; i++)
            stack.SetKeyTime(i, keys[i - 1].Time + gap);
        foreach (var track in stack.Tracks) track.InsertKeyCopy(k, k + 1);
        return k + 1;
    }

    /// <summary>
    /// Inserts a key at <paramref name="frame"/> before the first key later than it (or before the
    /// end key). Each track's new key holds its own stack's value and Space there, so the curve
    /// keeps its values at the existing key times.
    /// </summary>
    private int InsertKey(AnimStack stack, int frame)
    {
        var keys = stack.Keys;
        int n = keys.Count;
        int index = n - 1;
        for (int i = 0; i < n - 1; i++)
            if (keys[i].Time > frame) { index = i; break; }

        var tracks = stack.Tracks.ToList();
        var snapshots = tracks.Select(t => (
            Value: Evaluator.LayerValue(stack, t, frame, out _),
            Space: Evaluator.SpaceOf(stack, t, frame),
            Scalars: ScalarSnapshot(stack, t, frame),
            Fov: t.HasFov ? Evaluator.LayerFov(stack, t, frame) : (float?)null,
            Blend: t is BlendshapeTrack blend ? Evaluator.LayerBlendshapeValues(stack, blend, frame, out _) : null)).ToList();

        // A new stack key carries no flags or fades (an assumption).
        stack.InsertKeyTime(index, Math.Max(index - 1, 0), frame);
        stack.SetKeyFlags(index, 0, 0, 0);
        if (index == n - 1 && index > 0 && keys[n - 1].Time < frame)
            stack.SetKeyTime(n, frame + (keys[n - 1].Time - keys[index - 1].Time));

        int copyOf = Math.Max(index - 1, 0);
        for (int j = 0; j < tracks.Count; j++)
        {
            var track = tracks[j];
            track.InsertKeyCopy(copyOf, index);
            if (track is BlendshapeTrack blendshape && snapshots[j].Blend is { } blendValues)
            {
                // A blendshape key holds the stack's two values at the playhead rather than the
                // copied neighbour's; key 0's value is mirrored into the loop key
                BlendshapeEditor.SetKeyValues(blendshape, index, blendValues[0], blendValues[1], mirrorLoop: index == 0);
                continue;
            }
            if (index >= track.Keys.Count || track.Keys[index] is not TransformKey key) continue;
            key.Value = snapshots[j].Value;
            key.Space = snapshots[j].Space;
            if (key is CameraKey camera && snapshots[j].Fov is { } fov) camera.Fov = fov;
            if (key is IKKey ik) snapshots[j].Scalars?.Invoke(ik);

            var modes = track.TangentModes;
            if (index < modes.Count)
                for (int c = 0; c < modes[index].Length; c++)
                {
                    uint type = TangentRecompute.DefaultType(track.Target, c);
                    track.SetTangentMode(index, c, new TangentMode((modes[index][c].Raw & 0x100) | (type << 4) | type));
                }
        }
        return index;
    }

    /// <summary>
    /// For an IK track, a setter that writes the pole, roll, twist and bank angles interpolated at
    /// <paramref name="frame"/> (Hermite with the stored tangents, held on a stepped key).
    /// </summary>
    private static Action<IKKey>? ScalarSnapshot(AnimStack stack, AnimTrack track, int frame)
    {
        var keys = track.Keys;
        if (!keys.Any(k => k is IKKey)) return null;
        var stackKeys = stack.Keys;
        float time = PoseEvaluator.LocalTime(stackKeys, stack.Flags, frame);
        int b = 0;
        while (b < stackKeys.Count && stackKeys[b].Time <= time) b++;
        int a = Math.Max(b - 1, 0);
        b = Math.Min(b, stackKeys.Count - 1);
        // The end key plays key 0.
        int bValue = b == stackKeys.Count - 1 ? 0 : b;
        if (keys.ElementAtOrDefault(a) is not IKKey ka || keys.ElementAtOrDefault(bValue) is not IKKey kb) return null;
        var outA = track.TangentsOut.ElementAtOrDefault(a) as IKKey;
        var inB = track.TangentsIn.ElementAtOrDefault(b) as IKKey;
        float span = stackKeys[b].Time - stackKeys[a].Time;
        float u = span > 0 ? Math.Clamp((time - stackKeys[a].Time) / span, 0, 1) : 0;
        bool step = a < track.TangentModes.Count && track.TangentModes[a] is { Length: > 0 } m && m[0].LowMode == 5;

        float H(Func<IKKey, float> f)
        {
            if (step || a == b) return f(ka);
            float u2 = u * u, u3 = u2 * u;
            return (2 * u3 - 3 * u2 + 1) * f(ka) + (-2 * u3 + 3 * u2) * f(kb)
                   + (u3 - 2 * u2 + u) * (outA is null ? 0 : f(outA)) + (u3 - u2) * (inB is null ? 0 : f(inB));
        }

        float pole = H(k => k.PoleAngle);
        bool rf = ka is ReverseFootKey && kb is ReverseFootKey;
        float roll = rf ? H(k => ((ReverseFootKey)k).RollAngle) : 0;
        float twist = rf ? H(k => ((ReverseFootKey)k).TwistAngle) : 0;
        float bank = rf ? H(k => ((ReverseFootKey)k).BankAngle) : 0;
        return k =>
        {
            k.PoleAngle = pole;
            if (k is ReverseFootKey r && rf)
            {
                r.RollAngle = roll;
                r.TwistAngle = twist;
                r.BankAngle = bank;
            }
        };
    }

    /// <summary>
    /// The key value v of a stack with weight <paramref name="weight"/> that mixes
    /// <paramref name="below"/> into <paramref name="posed"/>.
    /// </summary>
    public static Transform InverseMix(Transform below, Transform posed, float weight, bool additive)
    {
        if (weight < 1e-6f) return posed; // the stack has no effect: nothing to solve
        float w = weight;
        if (additive)
            return new Transform(
                (posed.Translation - below.Translation) / w,
                PoseEvaluator.Power(Quaternion.Inverse(below.Rotation) * posed.Rotation, 1 / w),
                Pow(posed.Scale / below.Scale, 1 / w));
        return new Transform(
            (posed.Translation - (1 - w) * below.Translation) / w,
            InverseOverrideRotation(below.Rotation, posed.Rotation, w),
            (posed.Scale - (1 - w) * below.Scale) / w);
    }

    private static Vector3 Pow(Vector3 v, float e) => new(MathF.Pow(v.X, e), MathF.Pow(v.Y, e), MathF.Pow(v.Z, e));

    /// <summary>
    /// The rotation v with slerp(below, v, w) = posed. Candidates, in order: the exact slerp
    /// inverse below·(below⁻¹·posed)^(1/w), then the note's two roots; the first whose forward mix
    /// reproduces <paramref name="posed"/> (up to sign) wins, else identity.
    /// </summary>
    public static Quaternion InverseOverrideRotation(Quaternion below, Quaternion posed, float w)
    {
        var candidates = new List<Quaternion>
        {
            Quaternion.Normalize(below * PoseEvaluator.Power(Quaternion.Inverse(below) * posed, 1 / w)),
        };
        float d = Quaternion.Dot(below, posed);
        float disc = w * w - (1 - w) * (1 - w) * (1 - d * d);
        if (disc >= 0)
            foreach (float sign in new[] { 1f, -1f })
            {
                float k = ((1 - w) * d + sign * MathF.Sqrt(disc)) / w;
                var v = posed * k - below * ((1 - w) / w);
                if (v.Length() > 1e-6f) candidates.Add(Quaternion.Normalize(v));
            }
        foreach (var v in candidates)
        {
            var forward = Quaternion.Slerp(below, v, w);
            if (Close(forward, posed) || Close(forward, -posed)) return v;
        }
        return Quaternion.Identity;
    }

    /// <summary>Component-wise within 1e-4 (the note says 1e-5; float round-off in Power needs a little more).</summary>
    private static bool Close(Quaternion a, Quaternion b) =>
        MathF.Abs(a.X - b.X) < 1e-4f && MathF.Abs(a.Y - b.Y) < 1e-4f && MathF.Abs(a.Z - b.Z) < 1e-4f && MathF.Abs(a.W - b.W) < 1e-4f;

    /// <summary><paramref name="value"/>'s channels in <paramref name="mask"/>, the rest from <paramref name="kept"/>.</summary>
    private static Transform Merge(Transform kept, Transform value, AnimChannels mask)
    {
        Vector3 Pick(Vector3 k, Vector3 v, int shift) => new(
            ((uint)mask >> shift & 1) != 0 ? v.X : k.X,
            ((uint)mask >> (shift + 1) & 1) != 0 ? v.Y : k.Y,
            ((uint)mask >> (shift + 2) & 1) != 0 ? v.Z : k.Z);
        return new Transform(
            Pick(kept.Translation, value.Translation, 0),
            (mask & AnimChannels.Rotate) != 0 ? value.Rotation : kept.Rotation,
            Pick(kept.Scale, value.Scale, 6));
    }
}
