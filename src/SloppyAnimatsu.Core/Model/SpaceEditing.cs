namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// Space switching edits: Change Space on keys, and
/// the fix-ups that keep a key's space in step with the other stacks when keys are retimed,
/// deleted or added by the hammer.
/// </summary>
public sealed partial class KeyEditor
{
    /// <summary>The objects of <paramref name="scene"/> that Change Space offers (<see cref="Object3D.IsSpaceable"/>).</summary>
    public static IEnumerable<Object3D> SpaceableObjects(Scene scene) =>
        scene.Characters.SelectMany(c => new[] { (Object3D)c }.Concat(c.Descendants<Object3D>()))
            .Where(o => o.IsSpaceable).Distinct();

    /// <summary>
    /// Toggle Spaceable: every object of <paramref name="objects"/> takes the opposite of
    /// <paramref name="clicked"/>'s spaceable state. Returns the new state.
    /// </summary>
    public static bool ToggleSpaceable(Object3D clicked, IEnumerable<Object3D> objects)
    {
        bool state = !clicked.IsSpaceable;
        foreach (var o in objects.Append(clicked).Distinct()) o.IsSpaceable = state;
        return state;
    }

    /// <summary>
    /// Whether <paramref name="target"/>'s keys may be switched to <paramref name="space"/> (null =
    /// its parent). The app refuses a joint of an IK or reverse-foot chain, and an IK controller,
    /// chain root or an ancestor of one going to another character's object. SloppyAnimatsu also
    /// refuses a space that is the target itself or one of its descendants (a loop).
    /// </summary>
    public static bool CanChangeSpace(Object3D target, Object3D? space, out string? reason)
    {
        reason = null;
        var character = target.Ancestor<Character>();
        var iks = character?.IKControllers.ToList() ?? [];
        if (iks.Any(c => c.Chain.Any(j => ReferenceEquals(j, target))))
            reason = "Space switching a joint of an ik/rf chain is not allowed for now";
        else if (space != null && !ReferenceEquals(space.Ancestor<Character>() ?? space as Character, character ?? target as Character)
                 && iks.Any(c => IsAncestorOrSelf(target, c) || (c.Chain.Count > 0 && IsAncestorOrSelf(target, c.Chain[0]))))
            reason = "Space switching an ik/rf controller or chain to another character is not allowed for now";
        else if (space != null && IsAncestorOrSelf(target, space))
            reason = "This action leads to a hierachical/space loop, which is not allowed";
        return reason == null;
    }

    /// <summary>
    /// Change Space: at the frames of
    /// <paramref name="keyIndices"/> in <paramref name="stack"/>, every stack of the animation that
    /// has a key there (but its end key) converts the tracks of <paramref name="targets"/> from
    /// the space in force at that frame in that stack into <paramref name="space"/> (null = the
    /// parent) and stores it. Throws before changing anything when a target is refused
    /// (<see cref="CanChangeSpace"/>). Returns the number of keys changed.
    /// </summary>
    public int ChangeSpace(AnimStack stack, IEnumerable<int> keyIndices, IEnumerable<Object3D> targets, Object3D? space)
    {
        var animation = stack.Animation ?? throw new InvalidOperationException($"{stack} is not in an animation.");
        var objects = targets.Distinct().ToList();
        foreach (var o in objects)
            if (!CanChangeSpace(o, space, out string? reason))
                throw new InvalidOperationException($"{o.Name}: {reason}");
        var keys = stack.Keys;
        var frames = keyIndices.Where(i => i >= 0 && i < keys.Count - 1).Select(i => keys[i].Time).ToHashSet();
        if (frames.Count == 0 || objects.Count == 0) return 0;

        Evaluator.Invalidate();
        var converter = new KeySpaceConverter(Evaluator, animation);
        int changed = 0;
        var touched = new List<AnimStack>();
        foreach (var s in animation.Stacks)
        {
            var sKeys = s.Keys;
            bool any = false;
            for (int i = 0; i < sKeys.Count - 1; i++)
            {
                if (!frames.Contains(sKeys[i].Time)) continue;
                foreach (var target in objects)
                {
                    var track = s.TrackFor(target);
                    if (track == null || i >= track.Keys.Count || track.Keys[i] is not TransformKey key) continue;
                    var from = Evaluator.SpaceOf(s, track, sKeys[i].Time) ?? target.Parent3D;
                    SetSpace(converter, s, key, target, from, space, sKeys[i].Time);
                    if (i == 0) MirrorLoopKey(track);
                    changed++;
                    any = true;
                }
            }
            if (any) touched.Add(s);
        }
        foreach (var s in touched) RecomputeAll(s);
        return changed;
    }

    /// <summary>
    /// The space <paramref name="target"/> has at <paramref name="frame"/> over the animation's
    /// stacks:
    /// the first enabled stack (additive ones included), leaving out <paramref name="exclude"/>,
    /// with a track for it decides. <paramref name="answered"/> is false when none does; the
    /// result is then the parent. The result is never null when the target has a parent.
    /// </summary>
    public Object3D? SpaceInStacks(Animation animation, Object3D target, int frame, AnimStack? exclude, out bool answered)
    {
        foreach (var s in animation.Stacks)
        {
            if (ReferenceEquals(s, exclude) || !s.IsEnabled || s.TrackFor(target) is not { } track) continue;
            answered = true;
            return Evaluator.SpaceOf(s, track, frame) ?? target.Parent3D;
        }
        answered = false;
        return target.Parent3D;
    }

    /// <summary>Whether a stack of <paramref name="animation"/> other than <paramref name="stack"/> has a key stored at exactly <paramref name="frame"/>.</summary>
    public static bool OtherStackKeysFrame(Animation animation, AnimStack stack, int frame) =>
        animation.Stacks.Any(s => !ReferenceEquals(s, stack) && s.Keys.Any(k => k.Time == frame));

    /// <summary>
    /// The retime and delete fix-up
    /// each key of <paramref name="keyIndices"/> (the end key excepted) whose frame another
    /// stack also keys takes, on every track, the space its object has there in the other stacks,
    /// converted from the key's stored space (null: the space over all stacks). Tracks no other
    /// stack answers for are left alone. Tangents are not recomputed.
    /// </summary>
    private void FixSpacesAt(AnimStack stack, IEnumerable<int> keyIndices)
    {
        if (stack.Animation is not { } animation) return;
        var keys = stack.Keys;
        var todo = keyIndices.Where(i => i >= 0 && i < keys.Count - 1).Distinct().OrderBy(i => i)
            .Where(i => OtherStackKeysFrame(animation, stack, keys[i].Time)).ToList();
        if (todo.Count == 0) return;
        Evaluator.Invalidate();
        var converter = new KeySpaceConverter(Evaluator, animation);
        foreach (int i in todo)
        {
            int frame = keys[i].Time;
            foreach (var track in stack.Tracks)
            {
                if (track.Target is not { } target || i >= track.Keys.Count || track.Keys[i] is not TransformKey key) continue;
                var space = SpaceInStacks(animation, target, frame, stack, out bool answered);
                if (!answered) continue;
                var from = key.Space ?? SpaceInStacks(animation, target, frame, null, out _);
                SetSpace(converter, stack, key, target, from, space, frame);
                if (i == 0) MirrorLoopKey(track);
            }
        }
    }

    /// <summary>
    /// Converts <paramref name="key"/> from <paramref name="from"/> into <paramref name="to"/>
    /// (null = the parent) at <paramref name="frame"/> and stores the space, as null when it is
    /// the target's parent.
    /// </summary>
    private static void SetSpace(KeySpaceConverter converter, AnimStack stack, TransformKey key, Object3D target,
        Object3D? from, Object3D? to, int frame)
    {
        var parent = target.Parent3D;
        from ??= parent;
        to ??= parent;
        if (!ReferenceEquals(from, to))
            key.Value = converter.Convert(target, key.Value, NullIfParent(from), NullIfParent(to), frame, stack.IsAdditive);
        key.Space = NullIfParent(to);

        Object3D? NullIfParent(Object3D? o) => ReferenceEquals(o, parent) ? null : o;
    }
}
