using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The Cyclemaker: <see cref="CycleStep"/> appends a mirrored
/// second step to a stack, and <see cref="CycleSide"/> mirrors the selected controllers between
/// the two halves of an existing cycle.
/// </summary>
public sealed partial class KeyEditor
{
    /// <summary>The shipped refusal of Cycle Side on a layer whose keys without the loop key are odd.</summary>
    public const string CycleSideOddKeysMessage =
        "Cycle side was not processed because current layer has an odd number of keys (without considering loop key).";

    /// <summary>
    /// Whether <paramref name="stack"/> can take <see cref="CycleSide"/>, with the reason when it
    /// cannot: it must be a cycle stack with an even number of keys without the loop key.
    /// </summary>
    public static bool CanCycleSide(AnimStack stack, out string? reason)
    {
        int keys = stack.Keys.Count;
        if (!stack.IsCycle)
            reason = $"{stack.Name} is not a cycle stack; Cycle Side mirrors the two halves of a cycle.";
        else if (keys < 3)
            reason = $"{stack.Name} needs at least two keys besides the loop key.";
        else if (((keys - 1) & 1) != 0)
            reason = CycleSideOddKeysMessage;
        else
            reason = null;
        return reason == null;
    }

    /// <summary>Whether <paramref name="stack"/> can take <see cref="CycleStep"/>, with the reason when it cannot.</summary>
    public static bool CanCycleStep(AnimStack stack, out string? reason)
    {
        reason = stack.Keys.Count < 2 ? $"{stack.Name} needs at least two keys (a key and a loop key)." : null;
        return reason == null;
    }

    /// <summary>
    /// Cycle Step: inserts one mirrored copy of
    /// <paramref name="stack"/>'s keys without the loop key as a second step before the loop key,
    /// and extends the time array by one step. The new keys are default keys and then take the
    /// mirror of the first step; tangents are recomputed. Returns the number of track keys written.
    /// </summary>
    /// <param name="filter">The channels to write (the original's channel-mask parameter).</param>
    /// <param name="match">The name-based symmetry to use (defaults to the shipped lists).</param>
    public int CycleStep(AnimStack stack, AnimChannels filter = AllChannels, MirrorMatch? match = null)
    {
        if (!CanCycleStep(stack, out string? reason)) throw new InvalidOperationException(reason);
        if (stack.Animation == null) throw new InvalidOperationException($"{stack} is not in an animation.");
        match ??= MirrorMatch.Default;
        var keys = stack.Keys;
        int n = keys.Count;
        int step = n - 1;
        int delta = stack.IsCycle ? keys[n - 1].Time - keys[n - 2].Time
            : n >= 3 ? keys[n - 2].Time - keys[n - 3].Time
            : 5;

        // The times of the second step, computed from the old array before it grows.
        var times = new int[step];
        times[0] = keys[n - 2].Time + delta;
        for (int k = 1; k < step; k++)
            times[k] = keys[k].Time - keys[k - 1].Time + times[k - 1];

        // A cycle's old loop entry moves to the end and keeps its role at the new loop time.
        int loopTime = stack.IsCycle ? times[step - 1] + delta : keys[n - 1].Time;
        for (int k = 0; k < step; k++) stack.InsertDefaultKeyTime(step + k, times[k]);
        if (stack.IsCycle) stack.SetKeyTime(2 * step, loopTime);

        foreach (var track in stack.Tracks)
            for (int k = 0; k < step; k++)
                InsertDefaultKey(track, step + k);

        var map = Enumerable.Range(0, step).Select(i => (From: i, To: step + i)).ToArray();
        return MirrorMapped(stack, map, null, match, filter);
    }

    /// <summary>
    /// Cycle Side: mirrors the keys of
    /// <paramref name="sources"/> (and their functional groups) between the two halves of the
    /// cycle, key <c>i</c> going to key <c>(i + half) mod (keys - 1)</c>. The loop key is not
    /// mirrored here; it is set from key 0 afterwards. Throws when the stack cannot take it
    /// (<see cref="CanCycleSide"/>). Returns the number of track keys written.
    /// </summary>
    public int CycleSide(AnimStack stack, IEnumerable<Object3D> sources, AnimChannels filter = AllChannels,
        MirrorMatch? match = null)
    {
        if (!CanCycleSide(stack, out string? reason)) throw new InvalidOperationException(reason);
        if (stack.Animation == null) throw new InvalidOperationException($"{stack} is not in an animation.");
        match ??= MirrorMatch.Default;
        var selected = sources.Distinct().ToArray();
        if (selected.Length == 0) return 0;

        int period = stack.Keys.Count - 1;
        int half = period / 2;
        var map = Enumerable.Range(0, period).Select(i => (From: i, To: (i + half) % period)).ToArray();
        return MirrorMapped(stack, map, selected, match, filter);
    }

    /// <summary>
    /// The shared copy of the two cycle actions: for every allowed source track, mirrors keys
    /// <c>From</c> onto keys <c>To</c> of the track's counterpart (or of itself). Tracks are
    /// processed in stack order and the source keys are read as they are reached, so a copy made
    /// by an earlier track is visible to a later one.
    /// </summary>
    private int MirrorMapped(AnimStack stack, IReadOnlyList<(int From, int To)> map,
        IReadOnlyCollection<Object3D>? sources, MirrorMatch match, AnimChannels filter)
    {
        var animation = stack.Animation ?? throw new InvalidOperationException($"{stack} is not in an animation.");
        var allowed = sources == null
            ? null
            : new HashSet<Object3D>(sources.SelectMany(AddGroup), ReferenceEqualityComparer.Instance);
        var written = new List<AnimTrack>();
        int count = 0;

        foreach (var track in stack.Tracks)
        {
            if (track.Target is not { } source || Evaluator.IndexOf(source) < 0) continue;
            if (allowed != null && !allowed.Contains(source)) continue;
            var target = match.Find(Character, source) ?? source;
            var targetTrack = stack.TrackFor(target);
            if (targetTrack == null) continue;
            var mask = track.Channels & filter;
            if (mask == AnimChannels.None) continue;
            if (!ReferenceEquals(source, target) && (targetTrack.Channels & filter) != mask) continue;

            // Earlier tracks may have written keys that these frames depend on.
            Evaluator.Invalidate();
            var worlds = new FrameWorlds(Evaluator, animation);
            bool any = false;
            foreach (var (from, to) in map)
            {
                var stackKeys = stack.Keys;
                if (from < 0 || from >= stackKeys.Count || to < 0 || to >= stackKeys.Count) continue;
                var sourceKeys = track.Keys;
                var targetKeys = targetTrack.Keys;
                if (from >= sourceKeys.Count || to >= targetKeys.Count) continue;
                if (sourceKeys[from] is not TransformKey sourceKey || targetKeys[to] is not TransformKey targetKey) continue;

                float sourceTime = stackKeys[from].Time;
                float targetTime = stackKeys[to].Time;
                var sourceFrame = sourceKey.Space ?? source.Parent3D;
                var sourceWorld = PoseWorld(source, sourceKey.Value, sourceFrame, sourceTime, worlds);
                var mirror = MirrorMatrix(sourceTime, worlds);
                var targetSpace = MirroredSpace(match, Character, sourceKey.Space, target);
                var targetFrame = targetSpace ?? target.Parent3D;
                var targetLocal = LocalUnder(target, mirror * sourceWorld * mirror, targetFrame, targetTime, worlds);
                var value = PoseEvaluator.Decompose(RestOf(target), Transform.FromMatrixLoose(targetLocal));
                var extras = sourceKey is IKKey ik && targetKey is IKKey
                    ? MirrorKeyExtrasAt(ik, mask, source, target, sourceTime, targetTime, worlds)
                    : null;

                targetKey.Value = Merge(targetKey.Value, value, mask);
                targetKey.Space = targetSpace;
                if (extras is { } extra && targetKey is IKKey targetIk) ApplyMirrorExtras(targetIk, extra);
                count++;
                any = true;
            }
            if (any && !written.Contains(targetTrack)) written.Add(targetTrack);
        }

        // Cycle Side can rewrite key 0; the end key replays key 0, so mirror it back before the
        // tangents are recomputed from it.
        if (map.Any(p => p.To == 0))
            foreach (var track in written) MirrorLoopKey(track);
        RecomputeAll(stack);
        return count;
    }

    /// <summary>
    /// Inserts a default key at <paramref name="index"/>: a copy of key 0's struct, reset to the
    /// new-key defaults (<see cref="ResetToDefault"/>), with the IK flag clear and an identity
    /// render key.
    /// </summary>
    private static void InsertDefaultKey(AnimTrack track, int index)
    {
        track.InsertKeyCopy(0, index);
        ResetToDefault(track, index);
        if (index < track.Keys.Count && track.Keys[index] is IKKey ik)
        {
            ik.SetIkKey(false);
            ik.RenderTransform = Transform.Identity;
        }
    }

    /// <summary>
    /// The IK and reverse-foot parts of a key mirrored from one key time to another: the pole
    /// angle, the reverse-foot angles whose channel bits are in the mask, the IK flag, and the
    /// stored chain mirrored joint by joint.
    /// </summary>
    private MirrorExtras? MirrorKeyExtrasAt(IKKey source, AnimChannels mask, Object3D sourceObject,
        Object3D targetObject, float sourceTime, float targetTime, FrameWorlds worlds)
    {
        float pole = (mask & AnimChannels.PoleAngle) != 0 ? source.PoleAngle : float.NaN;
        float? roll = null, twist = null, bank = null;
        if (source is ReverseFootKey foot)
        {
            if ((mask & AnimChannels.Roll) != 0) roll = foot.RollAngle;
            if ((mask & AnimChannels.Twist) != 0) twist = foot.TwistAngle;
            if ((mask & AnimChannels.Bank) != 0) bank = foot.BankAngle;
        }

        Transform[]? chain = null;
        if (source.ChainTransforms is { Count: > 0 } stored && sourceObject is ControllerIK sourceController
            && targetObject is ControllerIK targetController
            && stored.Count == sourceController.Chain.Count && stored.Count == targetController.Chain.Count)
            chain = MirrorChainAt(sourceController, targetController, stored, sourceTime, targetTime, worlds);
        return new MirrorExtras(pole, source.Flags, roll, twist, bank, chain);
    }

    /// <summary>
    /// The source chain's stored locals mirrored: the source joints' worlds are built down the
    /// chain at <paramref name="sourceTime"/> and reflected about the character's plane, then
    /// expressed as the target chain's locals under the target's parent frames at
    /// <paramref name="targetTime"/>.
    /// </summary>
    private Transform[] MirrorChainAt(ControllerIK source, ControllerIK target, IReadOnlyList<Transform> chain,
        float sourceTime, float targetTime, FrameWorlds worlds)
    {
        int n = chain.Count;
        var mirror = MirrorMatrix(sourceTime, worlds);
        var sourceWorld = new Matrix4x4[n];
        for (int j = 0; j < n; j++)
        {
            var parentWorld = j == 0
                ? (source.Chain[0].Parent3D is { } root ? worlds.World(root, sourceTime) : Matrix4x4.Identity)
                : ReferenceEquals(source.Chain[j].Parent3D, source.Chain[j - 1]) ? sourceWorld[j - 1]
                : source.Chain[j].Parent3D is { } above ? worlds.World(above, sourceTime) : Matrix4x4.Identity;
            sourceWorld[j] = chain[j].ToMatrix() * parentWorld;
        }
        var mirrored = new Matrix4x4[n];
        for (int j = 0; j < n; j++) mirrored[j] = mirror * sourceWorld[j] * mirror;

        var result = new Transform[n];
        for (int j = 0; j < n; j++)
        {
            var parentWorld = j == 0
                ? (target.Chain[0].Parent3D is { } root ? worlds.World(root, targetTime) : Matrix4x4.Identity)
                : ReferenceEquals(target.Chain[j].Parent3D, target.Chain[j - 1]) ? mirrored[j - 1]
                : target.Chain[j].Parent3D is { } above ? worlds.World(above, targetTime) : Matrix4x4.Identity;
            result[j] = Transform.FromMatrixLoose(mirrored[j] * Inverse(parentWorld));
        }
        return result;
    }
}
