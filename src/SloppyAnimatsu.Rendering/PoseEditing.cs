using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Rendering;

/// <summary>
/// Interactive key editing of one character through <see cref="KeyEditor"/>: a drag poses one
/// controller at one frame, keying the top-most stack that animates it, and keys can be created,
/// deleted and moved on the timeline. Every committed edit is recorded in an undo history. The history and drag handling are a viewer design, not the.
/// </summary>
public sealed class PoseEditSession
{
    /// <param name="history">
    /// The undo history to record into; sessions of several characters of one scene may share one.
    /// </param>
    public PoseEditSession(ScenePose pose, Character character, EditHistory? history = null)
    {
        Pose = pose;
        Character = character;
        History = history ?? new EditHistory();
        // Shared with the display, so that the editor's invalidation re-poses what is drawn.
        Evaluator = pose.Evaluator(character);
        Editor = new KeyEditor(character, Evaluator);
    }

    public ScenePose Pose { get; }
    public Character Character { get; }
    public PoseEvaluator Evaluator { get; }
    public KeyEditor Editor { get; }
    public EditHistory History { get; }

    /// <summary>
    /// Whether a drag leaves the edited object's direct children where they are in the world
    /// Off by default, like
    /// The pivot mode; a viewer design choice.
    /// </summary>
    public bool KeepChildrenFixed { get; set; }

    /// <summary>
    /// The discrete steps gizmo drags snap to
    /// (discrete steps)"). Off by default, like the shipped preferences. Only the current
    /// object's change is snapped; the other selected objects undergo that change.
    /// </summary>
    public SnapSettings Snap { get; set; } = new();

    /// <summary>
    /// The stack a drag on <paramref name="controller"/> keys: the first (top-most) enabled stack
    /// of <see cref="Animation.Stacks"/> with a visible track for it. Keying it shows exactly, since
    /// <see cref="KeyEditor.SetKey"/> ignores the stacks above and none of those animate it.
    /// </summary>
    public static AnimStack? EditableStack(Animation animation, Object3D controller)
    {
        foreach (var stack in animation.Stacks)
            if (stack.IsEnabled && stack.Opacity > 1e-6f && stack.TrackFor(controller) != null)
                return stack;
        return null;
    }

    /// <summary>The key of <paramref name="stack"/> at animation frame <paramref name="frame"/>, or -1.</summary>
    public static int KeyAt(AnimStack stack, int frame)
    {
        var keys = stack.Keys;
        if (keys.Count == 0) return -1;
        PoseEvaluator.Timing(keys, PoseEvaluator.LocalTime(keys, stack.Flags, frame), out int exact);
        return exact;
    }

    /// <summary>
    /// Starts posing <paramref name="controller"/> at <paramref name="frame"/>: finds the stack to
    /// key and creates a key there if there is none. Returns null, with the reason, when the
    /// controller cannot be keyed.
    /// </summary>
    public EditDrag? Begin(Animation animation, Object3D controller, int frame, out string? reason) =>
        Begin(animation, controller, frame, null, out reason);

    private EditDrag? Begin(Animation animation, Object3D controller, int frame, IReadOnlyCollection<Object3D>? excluded,
        out string? reason)
    {
        reason = null;
        if (Evaluator.IndexOf(controller) < 0) { reason = $"{controller.Name} is not a controller of {Character.Name}."; return null; }
        var stack = EditableStack(animation, controller);
        if (stack == null) { reason = $"No enabled stack of {animation.Name} has a track for {controller.Name}."; return null; }
        var keys = stack.Keys;
        if (keys.Count == 0) { reason = $"{stack.Name} has no keys."; return null; }

        var before = EditSnapshot.Take(stack);
        var children = ChildBaselines(stack, controller, animation, frame, excluded);
        float local = PoseEvaluator.LocalTime(keys, stack.Flags, frame);
        PoseEvaluator.Timing(keys, local, out int keyIndex);
        bool created = keyIndex < 0;
        if (created) keyIndex = Editor.CreateKey(stack, (int)MathF.Round(local));
        if (SolvingIK(stack, keyIndex, controller) is { } solver)
        {
            reason = $"{controller.Name} is solved by {solver.Name} at this key (an IK key).";
            if (created) Restore(before);
            return null;
        }
        return new EditDrag(this, animation, stack, keyIndex, controller, frame, before, created, children) { Snap = Snap };
    }

    /// <summary>
    /// The direct children a drag with <see cref="KeepChildrenFixed"/> must keep in place, with
    /// their mixed local transforms now (before the drag changes the parent).
    /// </summary>
    private (Object3D Child, Transform Local)[] ChildBaselines(AnimStack stack, Object3D controller, Animation animation,
        int frame, IReadOnlyCollection<Object3D>? excluded)
    {
        if (!KeepChildrenFixed) return [];
        var locals = Evaluator.LocalTransforms(animation, frame);
        var children = new List<(Object3D, Transform)>();
        foreach (var child in controller.Children.OfType<Object3D>())
        {
            if (excluded?.Contains(child) == true || stack.TrackFor(child) == null) continue;
            int index = Evaluator.IndexOf(child);
            if (index >= 0) children.Add((child, locals[index]));
        }
        return [.. children];
    }

    /// <summary>
    /// The IK controller whose chain contains <paramref name="joint"/> and whose key at
    /// <paramref name="keyIndex"/> is an IK key, so that the joint is solved back to it after any
    /// edit; null
    /// if there is none.
    /// </summary>
    public ControllerIK? SolvingIK(AnimStack stack, int keyIndex, Object3D joint)
    {
        int n = stack.Keys.Count;
        if (keyIndex == n - 1 && n > 1) keyIndex = 0;
        foreach (var ik in Character.IKControllers)
        {
            var (key, joints) = ik is ControllerReverseFoot rf
                ? Editor.FootChain(stack, keyIndex, rf) is { } foot ? ((IKKey?)foot.Key, foot.Joints) : (null, null)
                : Editor.IKChain(stack, keyIndex, ik) is { } chain ? (chain.Key, chain.Joints) : (null, null);
            if (key != null && (key.Flags & 1) != 0 && joints!.Contains(joint))
                return ik;
        }
        return null;
    }

    /// <summary>
    /// The create-key action on <paramref name="stack"/> at animation frame <paramref name="frame"/>
    /// (<see cref="KeyEditor.CreateKey"/> at the stack's own time: on a key it duplicates it).
    /// Returns the new key's index.
    /// </summary>
    public int CreateKey(AnimStack stack, int frame)
    {
        var keys = stack.Keys;
        if (keys.Count == 0) throw new InvalidOperationException($"{stack.Name} has no keys.");
        int local = (int)MathF.Round(PoseEvaluator.LocalTime(keys, stack.Flags, frame));
        return Edit(stack, $"Create key in '{stack.Name}' at frame {local}", () => Editor.CreateKey(stack, local), _ => true);
    }

    /// <summary>
    /// The hammer's add: gives each of <paramref name="targets"/> and its functional group (an IK
    /// controller's chain, a reverse-foot controller's shoe and leg) that has none a new track in
    /// <paramref name="stack"/>, holding the reference pose (<see cref="KeyEditor.AddToStack"/>).
    /// Returns the number of tracks created.
    /// </summary>
    public int AddToStack(AnimStack stack, IReadOnlyCollection<Object3D> targets)
    {
        string what = targets.Count == 1 ? $"'{targets.First().Name}'" : $"{targets.Count} objects";
        return Edit(stack, $"Add {what} to '{stack.Name}'", () => Editor.AddToStack(stack, targets).Count, added => added > 0);
    }

    /// <summary>
    /// The hammer's remove: erases the tracks in <paramref name="stack"/> of
    /// <paramref name="targets"/> and their remove groups, then updates the IK keys that drive
    /// <paramref name="clicked"/>'s descendants (<see cref="KeyEditor.RemoveFromStack"/>).
    /// Returns the number of tracks removed.
    /// </summary>
    public int RemoveFromStack(AnimStack stack, IReadOnlyCollection<Object3D> targets, Object3D clicked)
    {
        string what = targets.Count == 1 ? $"'{targets.First().Name}'" : $"{targets.Count} objects";
        return Edit(stack, $"Remove {what} from '{stack.Name}'", () => Editor.RemoveFromStack(stack, targets, clicked), removed => removed > 0);
    }

    /// <summary>
    /// Deletes keys <paramref name="keyIndices"/> of <paramref name="stack"/>: the timeline delete
    /// (other keys keep their times), or the stacker delete with <paramref name="closeGaps"/>.
    /// The end key is never deleted. Returns the number of keys deleted.
    /// </summary>
    public int DeleteKeys(AnimStack stack, IReadOnlyCollection<int> keyIndices, bool closeGaps) =>
        Edit(stack, $"Delete {Count(keyIndices)} in '{stack.Name}'{(closeGaps ? ", closing the gaps" : "")}",
            () => Editor.DeleteKeys(stack, keyIndices, closeGaps), deleted => deleted > 0);

    /// <summary>
    /// The timeline move of keys <paramref name="keyIndices"/> of <paramref name="stack"/> by
    /// <paramref name="delta"/> frames. Returns each old key index's new index.
    /// </summary>
    public int[] MoveKeys(AnimStack stack, IReadOnlyCollection<int> keyIndices, int delta)
    {
        var before = stack.Keys.Select(k => k.Time).ToArray();
        return Edit(stack, $"Move {Count(keyIndices)} in '{stack.Name}' by {delta:+0;-0} frame(s)",
            () => Editor.MoveKeys(stack, keyIndices, delta), _ => !stack.Keys.Select(k => k.Time).SequenceEqual(before));
    }

    /// <summary>
    /// The ripple retime: key <paramref name="keyIndex"/> of <paramref name="stack"/> goes to stack
    /// time <paramref name="frame"/> (at least one frame after the previous key) and every later key
    /// moves with it (<see cref="KeyEditor.SetKeyTime"/>). Returns the time set.
    /// </summary>
    public int SetKeyTime(AnimStack stack, int keyIndex, int frame)
    {
        int old = stack.Keys[keyIndex].Time;
        return Edit(stack, $"Set key {keyIndex} of '{stack.Name}' to frame {frame}, moving the later keys",
            () => Editor.SetKeyTime(stack, keyIndex, frame), time => time != old);
    }

    /// <summary>
    /// Sets the gap before each of keys <paramref name="keyIndices"/> of <paramref name="stack"/> to
    /// <paramref name="gap"/> frames, moving the later keys (<see cref="KeyEditor.SetKeyGap"/>).
    /// Returns whether a time changed.
    /// </summary>
    public bool SetKeyGap(AnimStack stack, IReadOnlyCollection<int> keyIndices, int gap)
    {
        var before = stack.Keys.Select(k => k.Time).ToArray();
        return Edit(stack, $"Set the gap before {Count(keyIndices)} in '{stack.Name}' to {Math.Max(gap, 1)}",
            () => { Editor.SetKeyGap(stack, keyIndices, gap); return !stack.Keys.Select(k => k.Time).SequenceEqual(before); }, changed => changed);
    }

    /// <summary>
    /// Adds <paramref name="step"/> frames to the gap before each of keys <paramref name="keyIndices"/>
    /// (<see cref="KeyEditor.ChangeKeyGap"/>); nothing changes if a gap would drop below 1. Returns
    /// whether the gaps changed.
    /// </summary>
    public bool ChangeKeyGap(AnimStack stack, IReadOnlyCollection<int> keyIndices, int step) =>
        Edit(stack, $"{(step > 0 ? "Widen" : "Narrow")} the gap before {Count(keyIndices)} in '{stack.Name}' by {Math.Abs(step)}",
            () => step != 0 && keyIndices.Any(i => i > 0 && i < stack.Keys.Count) && Editor.ChangeKeyGap(stack, keyIndices, step), changed => changed);

    /// <summary>
    /// The stacker reorder: moves keys <paramref name="keyIndices"/> of <paramref name="stack"/>
    /// before key <paramref name="destination"/>, each keeping the gap before it
    /// (<see cref="KeyEditor.ReorderKeys"/>). The end key stays last. Returns each old key index's new index.
    /// </summary>
    public int[] ReorderKeys(AnimStack stack, IReadOnlyCollection<int> keyIndices, int destination) =>
        Edit(stack, $"Reorder {Count(keyIndices)} in '{stack.Name}' before key {destination}",
            () => Editor.ReorderKeys(stack, keyIndices, destination), order => order.Where((j, i) => j != i).Any());

    /// <summary>
    /// Marks keys <paramref name="keyIndices"/> of <paramref name="stack"/> as extreme or breakdown
    /// poses, or removes their marks (<see cref="KeyEditor.SetKeyMarks"/>; the end key is never
    /// marked). Returns the number of keys whose mark changed.
    /// </summary>
    public int SetKeyMarks(AnimStack stack, IReadOnlyCollection<int> keyIndices, uint mark)
    {
        string what = mark switch { KeyEditor.ExtremeMark => "Mark {0} as extreme", KeyEditor.BreakdownMark => "Mark {0} as breakdown", _ => "Remove the marks of {0}" };
        return Edit(stack, string.Format(what, Count(keyIndices)) + $" in '{stack.Name}'",
            () => KeyEditor.SetKeyMarks(stack, keyIndices, mark), changed => changed > 0);
    }

    /// <summary>
    /// Toggles the IK lock (<c>CKeyIK.Flags</c> bit 0) of <paramref name="clicked"/>'s track and of
    /// every selected IK controller's track in <paramref name="stack"/>: the right-click on a
    /// controller, or on a camera, which passes its looker target (
    /// <see cref="KeyEditor.ToggleIkLock"/>). One undo step.
    /// </summary>
    public int ToggleIkLock(AnimStack stack, Object3D clicked, IReadOnlyList<Object3D> selection, int frame,
        IReadOnlyCollection<int>? keyIndices, out bool locked, out string? reason)
    {
        bool value = false;
        string? why = null;
        int written = Edit(stack, $"Toggle the IK lock of '{clicked.Name}' in '{stack.Name}'",
            () =>
            {
                int changed = Editor.ToggleIkLock(stack, clicked, selection, frame, keyIndices, out bool now, out string? refusal);
                value = now;
                why = refusal;
                return changed;
            }, changed => changed > 0);
        locked = value;
        reason = why;
        return written;
    }

    /// <summary>
    /// The Spinner's Snap: puts each IK / reverse-foot controller of <paramref name="controllers"/>
    /// on the end of its chain at the key exactly at <paramref name="frame"/>
    /// (<see cref="KeyEditor.SnapToChain"/>). One undo step. Returns how many were snapped, 0 with
    /// <paramref name="reason"/> when there is nothing to snap.
    /// </summary>
    public int SnapToChain(AnimStack stack, IReadOnlyList<ControllerIK> controllers, int frame, out string? reason)
    {
        reason = null;
        int keyIndex = -1;
        for (int i = 0; i < stack.Keys.Count; i++)
            if ((int)MathF.Round(stack.Keys[i].Time) == frame) { keyIndex = i; break; }
        if (keyIndex < 0) { reason = $"'{stack.Name}' has no key at frame {frame}"; return 0; }
        string what = controllers.Count == 1 ? $"'{controllers[0].Name}'" : $"{controllers.Count} handles";
        int snapped = Edit(stack, $"Snap {what} to the chain in '{stack.Name}'",
            () => controllers.Count(c => Editor.SnapToChain(stack, keyIndex, c)), n => n > 0);
        if (snapped == 0) reason = $"'{stack.Name}' does not key the chain of {what} at frame {frame}";
        return snapped;
    }

    /// <summary>
    /// Adds (<paramref name="frames"/> = <see cref="KeyEditor.DefaultFade"/>) or removes (0) the
    /// fade-in or fade-out of keys <paramref name="keyIndices"/> of <paramref name="stack"/>
    /// (<see cref="KeyEditor.SetKeyFades"/>). Returns the number of keys whose fade changed.
    /// </summary>
    public int SetKeyFades(AnimStack stack, IReadOnlyCollection<int> keyIndices, bool fadeIn, ushort frames)
    {
        string fade = fadeIn ? "fade-in" : "fade-out", keys = Count(keyIndices);
        string what = frames == 0 ? $"Remove the {fade} of {keys}" : frames == KeyEditor.DefaultFade ? $"Add a {fade} to {keys}" : $"Set the {fade} of {keys} to {frames}";
        return Edit(stack, $"{what} in '{stack.Name}'",
            () => Editor.SetKeyFades(stack, keyIndices, fadeIn, frames), changed => changed > 0);
    }

    /// <summary>
    /// Change Space (<see cref="KeyEditor.ChangeSpace"/>): the keys at the frames of
    /// <paramref name="keyIndices"/> in every stack of the animation are converted for
    /// <paramref name="targets"/> into <paramref name="space"/> (null = the parent). One undo step
    /// covers all the stacks. Returns the number of track keys changed.
    /// </summary>
    public int ChangeSpace(AnimStack stack, IReadOnlyCollection<int> keyIndices, IReadOnlyCollection<Object3D> targets, Object3D? space)
    {
        var animation = stack.Animation ?? throw new InvalidOperationException($"{stack.Name} is not in an animation.");
        if (animation.Character is { } owner && !ReferenceEquals(owner, Character))
            throw new ArgumentException($"{stack.Name} does not belong to {Character.Name}.", nameof(stack));
        var stacks = animation.Stacks.ToList();
        var before = EditSnapshot.Take(Character, stacks);
        int changed;
        try
        {
            changed = Editor.ChangeSpace(stack, keyIndices, targets, space);
        }
        catch
        {
            Restore(before);
            throw;
        }
        string what = targets.Count == 1 ? $"'{targets.First().Name}'" : $"{targets.Count} objects";
        if (changed > 0)
            Record(new EditRecord($"Change the space of {what} to {(space == null ? "the parent" : $"'{space.Name}'")} at {Count(keyIndices)}",
                before, EditSnapshot.Take(Character, stacks)));
        return changed;
    }

    /// <summary>
    /// The TRANSFORM window's Space row: switches <paramref name="targets"/> to
    /// <paramref name="space"/> (null = the parent) at the key of the editable stack at
    /// <paramref name="frame"/> - created first, like a typed value, when there is none - and also
    /// at the other <paramref name="selectedKeys"/> of that stack when the playhead's key is one of
    /// them. One undo step covers the new key and the conversion. Returns the number of track keys
    /// changed, or 0 with <paramref name="reason"/>.
    /// </summary>
    public int ChangeSpaceAt(Animation animation, IReadOnlyList<Object3D> targets, int frame, Object3D? space,
        IReadOnlyCollection<int>? selectedKeys, out string? reason)
    {
        reason = null;
        if (targets.Count == 0) { reason = "Select the controllers whose space to change."; return 0; }
        if (EditableStack(animation, targets[0]) is not { } stack) { reason = $"{targets[0].Name} has no track in an enabled layer."; return 0; }
        foreach (var t in targets)
            if (!KeyEditor.CanChangeSpace(t, space, out string? refusal)) { reason = $"{t.Name}: {refusal}"; return 0; }
        var stacks = animation.Stacks.ToList();
        var before = EditSnapshot.Take(Character, stacks);
        int changed;
        try
        {
            int key = KeyAt(stack, frame);
            if (key < 0)
            {
                int local = (int)MathF.Round(PoseEvaluator.LocalTime(stack.Keys, stack.Flags, frame));
                key = Editor.CreateKey(stack, local);
            }
            if (key >= stack.Keys.Count - 1) key = 0; // the end key mirrors key 0
            var keys = new HashSet<int> { key };
            if (selectedKeys != null && selectedKeys.Contains(key))
                keys.UnionWith(selectedKeys.Where(k => k < stack.Keys.Count - 1));
            changed = Editor.ChangeSpace(stack, keys, targets.ToArray(), space);
        }
        catch
        {
            Restore(before);
            throw;
        }
        if (changed == 0)
        {
            Restore(before);
            reason = "None of the selected controllers has a key there.";
            return 0;
        }
        string what = targets.Count == 1 ? $"'{targets[0].Name}'" : $"{targets.Count} objects";
        Record(new EditRecord($"Change the space of {what} to {(space == null ? "the parent" : $"'{space.Name}'")} at frame {frame}",
            before, EditSnapshot.Take(Character, stacks)));
        return changed;
    }

    /// <summary>
    /// Toggle Spaceable (<see cref="KeyEditor.ToggleSpaceable"/>): <paramref name="objects"/> and
    /// <paramref name="clicked"/> take the opposite of <paramref name="clicked"/>'s state. Returns the new state.
    /// </summary>
    public bool ToggleSpaceable(Object3D clicked, IReadOnlyCollection<Object3D> objects) =>
        Structure($"Make {(objects.Append(clicked).Distinct().Count() is var n && n == 1 ? $"'{clicked.Name}'" : $"{n} objects")} {(clicked.IsSpaceable ? "not " : "")}spaceable",
            objects.Append(clicked).Distinct().ToArray<SceneObject>(), () => KeyEditor.ToggleSpaceable(clicked, objects), _ => true);

    /// <summary>
    /// Mirrors the selected keys of <paramref name="sources"/> in <paramref name="stack"/> onto the
    /// other side
    /// One undo step. Returns the number of keys written.
    /// </summary>
    public int MirrorKeys(AnimStack stack, IReadOnlyCollection<int> keyIndices, IReadOnlyCollection<Object3D> sources,
        MirrorMatch? match = null, AnimChannels filter = KeyEditor.AllChannels)
    {
        string what = sources.Count == 1 ? $"'{sources.First().Name}'" : $"{sources.Count} controllers";
        return Edit(stack, $"Mirror {Count(keyIndices)} of {what} in '{stack.Name}'",
            () => Editor.MirrorKeys(stack, keyIndices, sources, match, filter), written => written > 0);
    }

    /// <summary>
    /// The Cyclemaker's Cycle Step (<see cref="KeyEditor.CycleStep"/>,
    ///): appends a mirrored second step before the loop key. One undo
    /// step. Returns the number of track keys written.
    /// </summary>
    public int CycleStep(AnimStack stack, AnimChannels filter = KeyEditor.AllChannels)
    {
        int before = stack.Keys.Count;
        return Edit(stack, $"Cycle Step in '{stack.Name}'", () => Editor.CycleStep(stack, filter),
            _ => stack.Keys.Count != before);
    }

    /// <summary>
    /// The Cyclemaker's Cycle Side (<see cref="KeyEditor.CycleSide"/>,
    ///): mirrors the selected controllers between the two halves of
    /// the cycle. One undo step. Returns the number of track keys written.
    /// </summary>
    public int CycleSide(AnimStack stack, IReadOnlyCollection<Object3D> sources,
        AnimChannels filter = KeyEditor.AllChannels) =>
        Edit(stack, $"Cycle Side in '{stack.Name}'", () => Editor.CycleSide(stack, sources, filter), written => written > 0);

    /// <summary>
    /// Mirrors the rest pose's slots of <paramref name="sources"/> onto the other side
    /// keeping the direct children in place when
    /// <see cref="KeepChildrenFixed"/> is set. One undo step. Returns the number of slots written.
    /// </summary>
    public int MirrorPose(IReadOnlyCollection<Object3D> sources, MirrorMatch? match = null)
    {
        var objects = new List<SceneObject>(Poses());
        var before = EditSnapshot.TakeRestPose(Character, objects);
        int written;
        try
        {
            written = RestPose.MirrorPose(sources, match, KeepChildrenFixed);
        }
        catch
        {
            Restore(before);
            throw;
        }
        if (written > 0)
        {
            Pose.ReloadRestPose(Character);
            string what = sources.Count == 1 ? $"'{sources.First().Name}'" : $"{sources.Count} objects";
            Record(new EditRecord($"Mirror {what} in the rest pose", before, EditSnapshot.TakeRestPose(Character, objects)));
        }
        return written;
    }

    /// <summary>
    /// Starts a fade handle drag of key <paramref name="keyIndex"/> of <paramref name="stack"/>:
    /// every <see cref="FadeDrag.Update"/> re-applies from the length at the press, and
    /// <see cref="FadeDrag.Commit"/> records one undo step.
    /// </summary>
    public FadeDrag BeginFadeDrag(AnimStack stack, int keyIndex, bool fadeIn) => new(this, stack, keyIndex, fadeIn);

    /// <summary>
    /// Pastes <paramref name="clipboard"/> onto keys <paramref name="keyIndices"/> of
    /// <paramref name="stack"/> for <paramref name="objects"/> (<see cref="KeyEditor.PasteKeys"/>).
    /// Returns the number of track keys written.
    /// </summary>
    public int PasteKeys(AnimStack stack, KeyClipboard clipboard, IReadOnlyCollection<int> keyIndices, IReadOnlyCollection<Object3D> objects,
        bool withKeyFlags = false) =>
        Edit(stack, $"Paste onto {Count(keyIndices)} in '{stack.Name}'",
            () => Editor.PasteKeys(stack, clipboard, keyIndices, objects, withKeyFlags), written => written > 0);

    /// <summary>
    /// Pastes <paramref name="clipboard"/> at animation frame <paramref name="frame"/> of
    /// <paramref name="stack"/> (<see cref="KeyEditor.PasteKeysAt"/> at the stack's own time):
    /// keys there are overwritten, missing ones created. Returns the first pasted key's index, or -1.
    /// </summary>
    public int PasteKeysAt(AnimStack stack, KeyClipboard clipboard, int frame, IReadOnlyCollection<Object3D> objects)
    {
        var keys = stack.Keys;
        if (keys.Count == 0) return -1;
        int local = (int)MathF.Round(PoseEvaluator.LocalTime(keys, stack.Flags, frame));
        return Edit(stack, $"Paste {clipboard.Gaps.Count} key(s) at frame {local} in '{stack.Name}'",
            () => Editor.PasteKeysAt(stack, clipboard, local, objects), first => first >= 0);
    }

    /// <summary>
    /// Inserts the copied keys of <paramref name="clipboard"/> after the last of keys
    /// <paramref name="keyIndices"/> of <paramref name="stack"/> (<see cref="KeyEditor.PasteKeysAfter"/>).
    /// Returns the first new key's index, or -1.
    /// </summary>
    public int PasteKeysAfter(AnimStack stack, KeyClipboard clipboard, IReadOnlyCollection<int> keyIndices, IReadOnlyCollection<Object3D> objects) =>
        Edit(stack, $"Paste {clipboard.Gaps.Count} key(s) after {Count(keyIndices)} in '{stack.Name}'",
            () => Editor.PasteKeysAfter(stack, clipboard, keyIndices, objects), first => first >= 0);

    /// <summary>
    /// Sets the tangent type of the Curveboard selection in <paramref name="stack"/>
    /// (<see cref="KeyEditor.ChangeTangentType"/>). Returns the number of channel modes changed.
    /// </summary>
    public int ChangeTangentType(AnimStack stack, IReadOnlyCollection<CurveKeySelection> selection, uint type, AnimChannels displayed) =>
        Edit(stack, $"Set the tangents of {CurveCount(selection)} to {TangentTypes.Name(type)}",
            () => Editor.ChangeTangentType(InStack(stack, selection), type, displayed), changed => changed > 0);

    /// <summary>Unifies or breaks the tangents of the Curveboard selection (<see cref="KeyEditor.SetTangentsUnified"/>).</summary>
    public int SetTangentsUnified(AnimStack stack, IReadOnlyCollection<CurveKeySelection> selection, bool unified, AnimChannels displayed) =>
        Edit(stack, $"{(unified ? "Unify" : "Break")} the tangents of {CurveCount(selection)}",
            () => Editor.SetTangentsUnified(InStack(stack, selection), unified, displayed), changed => changed > 0);

    /// <summary>Moves the selected key values by <paramref name="delta"/> (<see cref="KeyEditor.MoveKeyValues"/>).</summary>
    public int MoveKeyValues(AnimStack stack, IReadOnlyCollection<CurveKeySelection> selection, float delta) =>
        Edit(stack, $"Move the values of {CurveCount(selection)} by {delta:+0.###;-0.###}",
            () => Editor.MoveKeyValues(InStack(stack, selection), delta), changed => changed > 0);

    /// <summary>
    /// Starts a Curveboard drag of key values or one handle in <paramref name="stack"/>: each
    /// update re-applies from the state at the press, and <see cref="CurveDrag.Commit"/> records one undo step.
    /// </summary>
    public CurveDrag BeginCurveDrag(AnimStack stack, string description) => new(this, stack, description);

    private static IEnumerable<CurveKeySelection> InStack(AnimStack stack, IEnumerable<CurveKeySelection> selection) =>
        selection.Where(s => ReferenceEquals(s.Track.Stack?.Source, stack.Source));

    private static string CurveCount(IReadOnlyCollection<CurveKeySelection> selection) =>
        selection.Count == 1 ? $"key {selection.First().Key} of {selection.First().Track.Target?.Name}" : $"{selection.Count} keys";

    private static string Count(IReadOnlyCollection<int> keyIndices) => keyIndices.Count == 1 ? $"key {keyIndices.First()}" : $"{keyIndices.Count} keys";

    /// <summary>
    /// Runs a key edit of <paramref name="stack"/> and records it for undo when
    /// <paramref name="changed"/> says it did something. A failed edit puts the stack back.
    /// </summary>
    private T Edit<T>(AnimStack stack, string description, Func<T> edit, Func<T, bool> changed)
    {
        if (stack.Animation?.Character is { } owner && !ReferenceEquals(owner, Character))
            throw new ArgumentException($"{stack.Name} does not belong to {Character.Name}.", nameof(stack));
        var before = EditSnapshot.Take(stack);
        T result;
        try
        {
            result = edit();
        }
        catch
        {
            Restore(before);
            throw;
        }
        if (changed(result)) Record(new EditRecord(description, before, EditSnapshot.Take(stack)));
        return result;
    }

    /// <summary>Records a finished edit.</summary>
    internal void Record(EditRecord record) => History.Push(record);

    /// <summary>Puts <paramref name="snapshot"/> back and re-poses.</summary>
    internal void Restore(EditSnapshot snapshot)
    {
        snapshot.Restore();
        // A shared history may hold another character's edits.
        var owner = snapshot.Owner ?? Character;
        if (snapshot.RestPose) Pose.ReloadRestPose(owner);
        else Pose.Evaluator(owner).Invalidate();
        if (snapshot.CheckPose is { } check) check.Evaluator.RestoreCheckPose(check.Values);
        Pose.RefreshPlacements();
    }

    /// <summary>
    /// Starts placing <paramref name="character"/> in the scene: a drag of the character object
    /// itself, which moves or turns the whole character, rig and meshes, at every frame
    /// </summary>
    public PlacementDrag BeginPlacement(Character character) => new(this, character, allowScale: false) { Snap = Snap };

    /// <summary>
    /// Starts placing any object that is not a controller (a character, a mesh, a group): a drag of
    /// its own transform, which everything below it follows. <paramref name="allowScale"/> lets the
    /// drag scale it too, which the Rigging context allows.
    /// </summary>
    public PlacementDrag BeginPlacement(Object3D target, bool allowScale) => new(this, target, allowScale) { Snap = Snap };

    /// <summary>
    /// Starts editing <paramref name="controller"/>'s slot of the rigging context's pose. Returns
    /// null, with the reason, when it cannot be edited.
    /// </summary>
    public RestPoseDrag? BeginRestPose(Object3D controller, out string? reason) =>
        BeginRestPose(controller, null, out reason);

    private RestPoseDrag? BeginRestPose(Object3D controller, IReadOnlyCollection<Object3D>? excluded, out string? reason)
    {
        if (!RestPose.CanEdit(controller, out reason)) return null;
        RestPose.EnsureEditableRefPose();
        return new RestPoseDrag(this, controller, TakeRestPoseSnapshot(), RestChildBaselines(controller, excluded))
        {
            Snap = Snap,
        };
    }

    /// <summary>
    /// Starts posing <paramref name="controller"/> in Paint &amp; Check's runtime pose
    /// (<see cref="PoseEvaluator.HasCheckPose"/>): the skin follows, the scene keeps nothing.
    /// An IK or reverse-foot controller drags its chain along, and a posed joint takes the IK
    /// controllers of the chains it moves with it (<see cref="PoseEvaluator.FollowCheckPose"/>);
    /// a constrained object other than a chain joint, a camera target or a blendshape controller is refused.
    /// </summary>
    public CheckPoseDrag? BeginCheckPose(Object3D controller, out string? reason) =>
        BeginCheckPose(controller, null, out reason);

    private CheckPoseDrag? BeginCheckPose(Object3D controller, IReadOnlyCollection<Object3D>? excluded, out string? reason)
    {
        reason = CheckPoseRefusal(controller);
        return reason == null ? new CheckPoseDrag(this, controller, CheckChildBaselines(controller, excluded)) { Snap = Snap } : null;
    }

    /// <summary>Why <paramref name="controller"/> cannot be posed in Paint &amp; Check, or null.</summary>
    private string? CheckPoseRefusal(Object3D controller)
    {
        if (Evaluator.IndexOf(controller) < 0) return $"{controller.Name} is not one of {Character.Name}'s controllers.";
        // A chain joint's constraint is its IK controller: posing it moves the controller along.
        if (controller.Constraint is { } c && !(c is ControllerIK ik && ik.Chain.Contains(controller)))
            return $"{controller.Name} is constrained by {c.Name}.";
        if (controller is CameraTarget or BlendshapeController)
            return $"{controller.Name} cannot be posed in Paint & Check; pose the joints or an IK controller.";
        return null;
    }

    /// <summary>The direct children a Paint &amp; Check drag with <see cref="KeepChildrenFixed"/> must keep in place.</summary>
    private (Object3D Child, Transform Local)[] CheckChildBaselines(Object3D controller, IReadOnlyCollection<Object3D>? excluded)
    {
        if (!KeepChildrenFixed) return [];
        var children = new List<(Object3D, Transform)>();
        foreach (var child in controller.Children.OfType<Object3D>())
        {
            if (excluded?.Contains(child) == true || CheckPoseRefusal(child) != null) continue;
            children.Add((child, Evaluator.CheckLocal(Evaluator.IndexOf(child))));
        }
        return [.. children];
    }

    /// <summary>
    /// Starts posing a selection in Paint &amp; Check's runtime pose, the last of
    /// <paramref name="controllers"/> leading; every member gets the same change on the same axis
    /// (<see cref="GroupDrag"/>). The whole drag is one undo step that puts the check pose back.
    /// </summary>
    public TransformDrag? BeginCheckPose(IReadOnlyList<Object3D> controllers, out string? reason)
    {
        if (controllers.Count == 0) throw new ArgumentException("No controller to pose.", nameof(controllers));
        if (controllers.Count == 1) return BeginCheckPose(controllers[0], out reason);
        var evaluator = Evaluator;
        EditSnapshot TakeCheckPose()
        {
            var snapshot = EditSnapshot.Take(Character, Array.Empty<AnimStack>());
            snapshot.CheckPose = (evaluator, evaluator.CheckPoseValues());
            return snapshot;
        }
        var before = TakeCheckPose();
        var group = BeginGroup(controllers,
            c => BeginCheckPose(c, controllers, out var r) is { } d ? (d.WithGrouped(), r) : (null, r),
            before, TakeCheckPose, "for Paint & Check", out reason);
        if (group != null) group.ResetEachUpdate = () => evaluator.RestoreCheckPose(before.CheckPose!.Value.Values);
        return group;
    }

    /// <summary>
    /// Types one channel's value into the check pose of <paramref name="controller"/>
    /// (<see cref="SetChannelValue"/>'s channel numbering: 0-2 translate, 3-5 rotate in degrees, 6-8 scale).
    /// </summary>
    public bool SetCheckChannelValue(Object3D controller, int channel, float value, out string? reason)
    {
        if (BeginCheckPose(controller, out reason) is not { } drag) return false;
        if (channel >= 6) value = FloorScale(value);
        drag.Set(WithChannel(drag.BaseLocal, channel, value), (AnimChannels)(1u << channel));
        return true;
    }

    /// <summary>The direct children a rest-pose drag with <see cref="KeepChildrenFixed"/> must keep in place.</summary>
    private (Object3D Child, Transform Local)[] RestChildBaselines(Object3D controller, IReadOnlyCollection<Object3D>? excluded)
    {
        if (!KeepChildrenFixed) return [];
        var children = new List<(Object3D, Transform)>();
        foreach (var child in controller.Children.OfType<Object3D>().Where(c => excluded?.Contains(c) != true))
            if (RestPose.SlotOf(child) >= 0 && RestPose.CanEdit(child, out _))
                children.Add((child, RestPose.GetLocal(child)));
        return [.. children];
    }

    /// <summary>
    /// Starts posing a selection at <paramref name="frame"/>: the last of
    /// <paramref name="controllers"/> (the current object) leads, the others undergo the same
    /// world-space change. Returns null, with the
    /// reason, when the lead cannot be keyed; other controllers that cannot be keyed are left alone.
    /// </summary>
    public TransformDrag? Begin(Animation animation, IReadOnlyList<Object3D> controllers, int frame, out string? reason)
    {
        if (controllers.Count == 0) throw new ArgumentException("No controller to pose.", nameof(controllers));
        if (controllers.Count == 1) return Begin(animation, controllers[0], frame, out reason);
        var stacks = controllers.Select(c => EditableStack(animation, c)).OfType<AnimStack>()
            .Distinct(ReferenceEqualityComparer.Instance).Cast<AnimStack>().ToArray();
        var before = EditSnapshot.Take(Character, stacks);
        return BeginGroup(controllers, c => Begin(animation, c, frame, controllers, out var r) is { } d ? (d, r) : (null, r),
            before, () => EditSnapshot.Take(Character, stacks), $"at frame {frame}", out reason);
    }

    /// <summary>
    /// Starts editing a selection's slots of the rigging context's pose, the last of
    /// <paramref name="controllers"/> leading (<see cref="Begin(Animation, IReadOnlyList{Object3D}, int, out string?)"/>).
    /// </summary>
    public TransformDrag? BeginRestPose(IReadOnlyList<Object3D> controllers, out string? reason)
    {
        if (controllers.Count == 0) throw new ArgumentException("No controller to pose.", nameof(controllers));
        if (controllers.Count == 1) return BeginRestPose(controllers[0], out reason);
        return BeginGroup(controllers, c => BeginRestPose(c, controllers, out var r) is { } d ? (d, r) : (null, r),
            TakeRestPoseSnapshot(), TakeRestPoseSnapshot, "in the rest pose", out reason);
    }

    private GroupDrag? BeginGroup(IReadOnlyList<Object3D> controllers, Func<Object3D, (TransformDrag?, string?)> begin,
        EditSnapshot before, Func<EditSnapshot> after, string where, out string? reason)
    {
        var (lead, leadReason) = begin(controllers[^1]);
        reason = leadReason;
        if (lead == null) { Restore(before); return null; }
        var followers = new List<TransformDrag>();
        foreach (var controller in controllers.Take(controllers.Count - 1).Distinct(ReferenceEqualityComparer.Instance).Cast<Object3D>())
            if (!ReferenceEquals(controller, lead.Controller) && begin(controller).Item1 is { } drag)
                followers.Add(drag);
        var group = new GroupDrag(this, lead, followers, before, after, where) { Snap = Snap };
        foreach (var member in group.Members) member.Snap = Snap;
        return group;
    }

    /// <summary>
    /// Starts posing a selection the one way every pose is edited: the last of
    /// <paramref name="controllers"/> (the current object) leads, the others undergo the same
    /// change. <paramref name="checkPose"/> poses Paint &amp; Check's runtime pose; otherwise
    /// <paramref name="animation"/> null edits the rest pose and an animation keys
    /// <paramref name="frame"/>. Returns null, with the reason, when the lead cannot be posed.
    /// </summary>
    public TransformDrag? BeginPose(Animation? animation, IReadOnlyList<Object3D> controllers, int frame, bool checkPose,
        out string? reason) =>
        checkPose ? BeginCheckPose(controllers, out reason)
        : animation == null ? BeginRestPose(controllers, out reason)
        : Begin(animation, controllers, frame, out reason);

    /// <summary>
    /// One edit of a pose by several controllers in turn (typed values, Reset, Mirror): where it
    /// writes, how each controller is started, and the undo snapshots around it. Every pose layer
    /// (an animation's keys, the rest pose, Paint &amp; Check's runtime pose) goes through this one
    /// shape, so the TRANSFORM window's tools behave the same in all of them.
    /// </summary>
    private sealed class PosePass
    {
        public required EditSnapshot Before { get; init; }
        public required Func<EditSnapshot> After { get; init; }
        /// <summary>Starts one member; the selection is passed so its own members are not kept fixed as children.</summary>
        public required Func<Object3D, (TransformDrag? Drag, string? Reason)> Begin { get; init; }
        /// <summary>Whether a controller can take a channel (0-8) in this layer.</summary>
        public required Func<Object3D, int, bool> Takes { get; init; }
        /// <summary>The words that end the undo step's description ("at frame 12", "in the rest pose").</summary>
        public required string Where { get; init; }
        /// <summary>The animation the pass keys, or null for the rest or check pose.</summary>
        public Animation? Animation { get; init; }
        public int Frame { get; init; }
    }

    private PosePass OpenPass(Animation? animation, IReadOnlyList<Object3D> controllers, int frame, bool checkPose)
    {
        if (checkPose)
        {
            var evaluator = Evaluator;
            EditSnapshot TakeCheckPose()
            {
                var snapshot = EditSnapshot.Take(Character, Array.Empty<AnimStack>());
                snapshot.CheckPose = (evaluator, evaluator.CheckPoseValues());
                return snapshot;
            }
            return new PosePass
            {
                Before = TakeCheckPose(),
                After = TakeCheckPose,
                // Grouped: each member builds on what the members before it wrote.
                Begin = c => BeginCheckPose(c, controllers, out var r) is { } d ? (d.WithGrouped(), r) : (null, r),
                Takes = (_, _) => true,
                Where = "for Paint & Check",
            };
        }
        if (animation == null)
        {
            RestPose.EnsureEditableRefPose();
            return new PosePass
            {
                Before = TakeRestPoseSnapshot(),
                After = TakeRestPoseSnapshot,
                Begin = c => BeginRestPose(c, controllers, out var r) is { } d ? (d, r) : (null, r),
                Takes = (c, _) => RestPose.CanEdit(c, out string? _),
                Where = "in the rest pose",
            };
        }
        var stacks = controllers.Select(c => EditableStack(animation, c)).OfType<AnimStack>()
            .Distinct(ReferenceEqualityComparer.Instance).Cast<AnimStack>().ToArray();
        return new PosePass
        {
            Before = EditSnapshot.Take(Character, stacks),
            After = () => EditSnapshot.Take(Character, stacks),
            Begin = c => Begin(animation, c, frame, controllers, out var r) is { } d ? (d, r) : (null, r),
            Takes = (c, channel) => Animates(animation, c, channel),
            Where = $"at frame {frame}",
            Animation = animation,
            Frame = frame,
        };
    }


    /// <summary>
    /// Runs <paramref name="write"/> on each of <paramref name="members"/> in order through
    /// <paramref name="pass"/>, as one undo step named by <paramref name="describe"/> (given the
    /// count written). <paramref name="lead"/>, when given, must start: if it cannot, everything is
    /// put back and its reason returned. A write returns false when it left its member alone.
    /// Everything is put back when nothing was written or a write throws.
    /// </summary>
    private int RunPass(PosePass pass, IEnumerable<Object3D> members, Object3D? lead,
        Func<Object3D, TransformDrag, bool> write, Func<int, string> describe, out string? reason)
    {
        reason = null;
        int written = 0;
        try
        {
            foreach (var controller in members)
            {
                var (drag, why) = pass.Begin(controller);
                if (drag == null)
                {
                    if (ReferenceEquals(controller, lead))
                    {
                        reason = why;
                        Restore(pass.Before);
                        return 0;
                    }
                    continue;
                }
                if (write(controller, drag)) written++;
            }
        }
        catch
        {
            Restore(pass.Before);
            throw;
        }
        if (written == 0)
        {
            Restore(pass.Before);
            return 0;
        }
        Record(new EditRecord($"{describe(written)} {pass.Where}", pass.Before, pass.After()));
        return written;
    }

    /// <summary>
    /// The Spinner's value fields:
    /// sets <paramref name="channel"/> (0-2 translation X/Y/Z, 3-5 Euler rotation X/Y/Z in degrees,
    /// 6-8 scale X/Y/Z) of every selected controller's local transform at
    /// <paramref name="frame"/>, each through its own track and own local frame, creating a key
    /// where there is none. <paramref name="animation"/> null edits the rest pose, and
    /// <paramref name="checkPose"/> Paint &amp; Check's runtime pose. The current
    /// object (the last entry) must be editable and animate the channel; other controllers that
    /// cannot take the value are skipped. The whole edit is one undo step. Returns the number of
    /// controllers written.
    /// </summary>
    public int SetChannelValue(Animation? animation, IReadOnlyList<Object3D> controllers, int frame, int channel,
        float value, out string? reason, bool checkPose = false)
    {
        if (channel is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(channel));
        if (controllers.Count == 0) throw new ArgumentException("No controller to edit.", nameof(controllers));
        reason = null;
        var current = controllers[^1];
        if (channel >= 6) value = FloorScale(value);

        if (!checkPose && animation != null && !Animates(animation, current, channel))
        {
            reason = $"{current.Name}'s track does not animate {Curves.ChannelNames[channel]}.";
            return 0;
        }
        if (!checkPose && animation == null && !RestPose.CanEdit(current, out reason)) return 0;

        var pass = OpenPass(animation, controllers, frame, checkPose);
        var members = controllers.Distinct(ReferenceEqualityComparer.Instance).Cast<Object3D>().OrderBy(Depth)
            .Where(c => ReferenceEquals(c, current) || pass.Takes(c, channel)).ToArray();
        string unit = channel is >= 3 and <= 5 ? "°" : "";
        int written = RunPass(pass, members, current, (_, drag) =>
            {
                drag.Set(WithChannel(drag.BaseLocal, channel, value), (AnimChannels)(1u << channel));
                return true;
            },
            n => $"Set {Curves.ChannelNames[channel]} of {(n == 1 ? current.Name ?? "controller" : $"{n} controllers")} to {value:0.###}{unit}",
            out reason);
        if (written == 0) reason ??= $"No selected controller can take {Curves.ChannelNames[channel]}.";
        return written;
    }

    /// <summary>
    /// The TRANSFORM panel's typed value for an object that is not a controller (a character, a
    /// mesh, a group): sets <paramref name="channel"/> (numbered as in <see cref="SetChannelValue"/>)
    /// of its own transform as one undoable placement edit. Scale channels need
    /// <paramref name="allowScale"/> (the Rigging context).
    /// </summary>
    public bool SetPlacementValue(Object3D target, int channel, float value, bool allowScale, out string? reason)
    {
        if (channel is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(channel));
        reason = null;
        if (channel >= 6)
        {
            if (!allowScale)
            {
                reason = $"{target.Name} can be scaled in the Rigging context only.";
                return false;
            }
            value = FloorScale(value);
        }
        var drag = BeginPlacement(target, allowScale);
        drag.Set(WithChannel(drag.BaseLocal, channel, value), (AnimChannels)(1u << channel));
        drag.Commit();
        return true;
    }

    /// <summary>
    /// The TRANSFORM window's Reset: puts the channels of
    /// <paramref name="mask"/> (translation/rotation/scale bits only) of every selected controller
    /// back to its reference-pose value - at <paramref name="frame"/> of <paramref name="animation"/>
    /// (keying them where there is no key), or in Paint &amp; Check's runtime pose with
    /// <paramref name="checkPose"/> - as one undoable edit. Rotation is reset per Euler channel
    /// unless all three are in the mask. A controller that cannot take any of the masked channels
    /// is skipped. The rest pose itself is the reference, so it has nothing to reset to. Returns
    /// how many controllers were written.
    /// </summary>
    public int ResetChannels(Animation? animation, IReadOnlyList<Object3D> controllers, int frame, AnimChannels mask,
        out string? reason, bool checkPose = false)
    {
        reason = null;
        mask &= AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale;
        if (mask == 0) { reason = "No channel is ticked in the TRANSFORM window."; return 0; }
        if (controllers.Count == 0) { reason = "Select the controllers to reset."; return 0; }
        if (animation == null && !checkPose)
        {
            reason = "The rest pose is the reference pose; Reset works on an animation or on Paint & Check.";
            return 0;
        }
        var pass = OpenPass(checkPose ? null : animation, controllers, frame, checkPose);
        var members = controllers.Distinct(ReferenceEqualityComparer.Instance).Cast<Object3D>()
            .Where(c => Evaluator.IndexOf(c) >= 0).OrderBy(Depth).ToArray();
        string first = members.FirstOrDefault()?.Name ?? "controller";
        int written = RunPass(pass, members, null, (controller, drag) =>
            {
                var channels = Enumerable.Range(0, 9)
                    .Where(c => (mask & (AnimChannels)(1u << c)) != 0 && pass.Takes(controller, c)).ToArray();
                if (channels.Length == 0) return false;
                var rest = Evaluator.RestTransforms[Evaluator.IndexOf(controller)];
                var local = drag.BaseLocal;
                var restEuler = Curves.ToEulerDegrees(rest.Rotation);
                bool wholeRotation = channels.Count(c => c is >= 3 and <= 5) == 3;
                if (wholeRotation) local = local with { Rotation = rest.Rotation };
                AnimChannels keyed = 0;
                foreach (int c in channels)
                {
                    keyed |= (AnimChannels)(1u << c);
                    if (c < 3) local = WithChannel(local, c, rest.Translation[c]);
                    else if (c < 6) { if (!wholeRotation) local = WithChannel(local, c, restEuler[c - 3]); }
                    else local = WithChannel(local, c, FloorScale(rest.Scale[c - 6]));
                }
                if ((keyed & AnimChannels.Rotate) != 0) keyed |= AnimChannels.Rotate;
                drag.Set(local, keyed);
                return true;
            },
            n => $"Reset {(n == 1 ? first : $"{n} controllers")}", out reason);
        if (written == 0) reason ??= "None of the selected controllers can take the ticked channels.";
        return written;
    }

    private static Matrix4x4 InverseOf(Matrix4x4 m) => Matrix4x4.Invert(m, out var inverse) ? inverse : Matrix4x4.Identity;

    /// <summary>
    /// The TRANSFORM window's Mirror in pose mode: puts each selected controller's
    /// symmetric counterpart (<see cref="MirrorMatch.Find"/>) where the controller is reflected
    /// across the character's YZ plane, in the pose on screen - the animation at
    /// <paramref name="frame"/> (keyed), the rest pose, or Paint &amp; Check's runtime pose with
    /// <paramref name="checkPose"/>. A controller without a side marker is its own counterpart and
    /// turns as if reflected onto itself. The change is taken relative to the reference pose (the
    /// counterpart's world is <c>Rt·M·Rs⁻¹·Ws·M</c>, M the reflection), so a rig whose two sides
    /// use mirrored axes comes out right. Every source is read before anything is written, so
    /// selecting both sides swaps them. Only the channels of <paramref name="mask"/> are written.
    /// The rest pose mirrors its side pairs only (it has no reference of its own to mirror central
    /// joints against). One undo step; returns how many controllers were written.
    /// </summary>
    public int MirrorPoseAt(Animation? animation, IReadOnlyCollection<Object3D> sources, int frame, bool checkPose,
        out string? reason, MirrorMatch? match = null, AnimChannels mask = KeyEditor.AllChannels)
    {
        reason = null;
        match ??= MirrorMatch.Default;
        if (sources.Count == 0) { reason = "Select the controllers to mirror."; return 0; }
        if (!checkPose && animation == null)
        {
            int pairs = MirrorPose(sources, match);
            if (pairs == 0) reason = "None of the selected objects has a counterpart on the other side to mirror onto.";
            return pairs;
        }

        // Phase one: every counterpart's mirrored world, from the pose before the edit.
        var poseAnimation = checkPose ? null : animation;
        Evaluator.Invalidate();
        Evaluator.LocalTransforms(poseAnimation, frame, out var worlds);
        var rest = Evaluator.RestWorldMatrices();
        var characterWorld = Character.WorldMatrix;
        var reflect = InverseOf(characterWorld) * Matrix4x4.CreateScale(-1, 1, 1) * characterWorld;
        var plan = new Dictionary<Object3D, Matrix4x4>(ReferenceEqualityComparer.Instance);
        foreach (var source in sources.Distinct())
        {
            int s = Evaluator.IndexOf(source);
            if (s < 0 || match.Find(Character, source) is not { } target) continue;
            int t = Evaluator.IndexOf(target);
            if (t < 0) continue;
            plan[target] = rest[t] * reflect * InverseOf(rest[s]) * worlds[s] * reflect;
        }
        if (plan.Count == 0)
        {
            reason = "None of the selected objects is a controller with a counterpart to mirror onto.";
            return 0;
        }

        // Phase two: write them, parents first, so each one is placed under its parent's new world.
        var targets = plan.Keys.OrderBy(Depth).ToList();
        var pass = OpenPass(poseAnimation, targets, frame, checkPose);
        var channels = mask & (AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale);
        if (channels == 0) channels = AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale;
        int written = RunPass(pass, targets, null, (target, drag) => drag.SetWorld(plan[target], channels),
            n => $"Mirror {(n == 1 ? targets[0].Name ?? "controller" : $"{n} controllers")}", out reason);
        if (written == 0) reason ??= "None of the counterparts can be posed here.";
        return written;
    }

    /// <summary>
    /// Sets the FOV of every selected animated camera at <paramref name="frame"/> as one undoable
    /// edit: each camera through its own editable stack, creating a key where it has none
    /// Returns how many were
    /// written.
    /// </summary>
    public int SetFov(Animation? animation, IReadOnlyList<Object3D> controllers, int frame, float fov, out string? reason)
    {
        reason = null;
        if (animation == null)
        {
            reason = "The camera FOV is keyed in an animation; leave the rest pose first.";
            return 0;
        }
        var cameras = controllers.OfType<CameraAnimated>().Distinct().OrderBy(c => Depth(c)).ToArray();
        if (cameras.Length == 0)
        {
            reason = "Select an animated camera to set its FOV.";
            return 0;
        }
        var current = controllers[^1] as CameraAnimated;
        var stacks = cameras.Select(c => EditableStack(animation, c)).OfType<AnimStack>().Distinct().ToList();
        var before = EditSnapshot.Take(Character, stacks);
        int written = 0;
        try
        {
            foreach (var camera in cameras)
            {
                if (EditableStack(animation, camera) is not { } stack || stack.TrackFor(camera) is not { } track
                    || !track.HasFov || stack.IsAdditive)
                {
                    string skip = $"{camera.Name}'s track does not animate the FOV.";
                    if (ReferenceEquals(camera, current) && written == 0)
                    {
                        reason = skip;
                        Restore(before);
                        return 0;
                    }
                    continue;
                }
                var keys = stack.Keys;
                float local = PoseEvaluator.LocalTime(keys, stack.Flags, frame);
                PoseEvaluator.Timing(keys, local, out int keyIndex);
                if (keyIndex < 0) keyIndex = Editor.CreateKey(stack, (int)MathF.Round(local));
                Editor.SetFov(stack, keyIndex, camera, fov);
                written++;
            }
        }
        catch
        {
            Restore(before);
            throw;
        }
        if (written == 0)
        {
            Restore(before);
            reason ??= "No selected camera can take an FOV value.";
            return 0;
        }
        Evaluator.Invalidate();
        var after = EditSnapshot.Take(Character, stacks);
        string what = written == 1 ? current?.Name ?? "camera" : $"{written} cameras";
        Record(new EditRecord($"Set FOV of {what} to {fov:0.###}° at frame {frame}", before, after));
        return written;
    }

    /// <summary>The TRANSFORM window's names of the extra channels 9-14.</summary>
    public static string ScalarChannelName(int channel) => channel switch
    {
        9 => "pole angle",
        10 => "roll",
        11 => "twist",
        12 => "bank",
        13 => "blendshape X",
        14 => "blendshape Y",
        _ => $"channel {channel}",
    };

    /// <summary>
    /// Paint &amp; Check's reverse-foot roll, twist or bank (<paramref name="channel"/> 10-12,
    /// radians): every selected reverse foot turns its shoe and re-solves its leg, as one undo step
    /// that puts the check pose back.
    /// </summary>
    private int SetCheckFootAngle(IReadOnlyList<Object3D> controllers, int channel, float value, string name, out string? reason)
    {
        reason = null;
        var feet = controllers.OfType<ControllerReverseFoot>().Distinct().ToArray();
        if (feet.Length == 0)
        {
            reason = $"Select a reverse-foot controller to set its {name}.";
            return 0;
        }
        var evaluator = Evaluator;
        var before = EditSnapshot.Take(Character, Array.Empty<AnimStack>());
        before.CheckPose = (evaluator, evaluator.CheckPoseValues());
        int written = 0;
        foreach (var foot in feet)
        {
            var angles = evaluator.CheckFootAngles(foot);
            angles = channel switch
            {
                10 => angles with { X = value },
                11 => angles with { Y = value },
                _ => angles with { Z = value },
            };
            if (evaluator.SetCheckFootAngles(foot, angles)) written++;
        }
        if (written == 0)
        {
            reason = $"No selected reverse foot has a full shoe to set its {name}.";
            return 0;
        }
        var after = EditSnapshot.Take(Character, Array.Empty<AnimStack>());
        after.CheckPose = (evaluator, evaluator.CheckPoseValues());
        string who = written == 1 ? feet[0].Name ?? "reverse foot" : $"{written} reverse feet";
        Record(new EditRecord($"Set {name} of {who} for Paint & Check", before, after));
        Pose.RefreshPlacements();
        return written;
    }

    /// <summary>
    /// Sets an extra channel (9 pole, 10-12 roll/twist/bank in radians, 13-14 blendshape X/Y) of
    /// every selected controller whose editable track animates it, at <paramref name="frame"/>,
    /// creating a key where the stack has none (<see cref="KeyEditor.SetScalarChannel"/>). One
    /// undo step; returns how many controllers were written.
    /// </summary>
    public int SetScalarChannel(Animation? animation, IReadOnlyList<Object3D> controllers, int frame, int channel,
        float value, out string? reason, bool checkPose = false)
    {
        reason = null;
        string name = ScalarChannelName(channel);
        if (checkPose && channel is >= 10 and <= 12) return SetCheckFootAngle(controllers, channel, value, name, out reason);
        if (animation == null)
        {
            reason = $"The {name} is keyed in an animation; leave the rest pose first.";
            return 0;
        }
        var bit = (AnimChannels)(1u << channel);
        var targets = controllers.Distinct()
            .Where(c => EditableStack(animation, c) is { IsAdditive: false } s && s.TrackFor(c) is { } t && (t.Channels & bit) != 0)
            .ToArray();
        if (targets.Length == 0)
        {
            reason = $"No selected controller's track animates the {name}.";
            return 0;
        }
        var stacks = targets.Select(c => EditableStack(animation, c)!).Distinct().ToList();
        var before = EditSnapshot.Take(Character, stacks);
        int written = 0;
        try
        {
            foreach (var controller in targets)
            {
                var stack = EditableStack(animation, controller)!;
                float local = PoseEvaluator.LocalTime(stack.Keys, stack.Flags, frame);
                PoseEvaluator.Timing(stack.Keys, local, out int keyIndex);
                if (keyIndex < 0) keyIndex = Editor.CreateKey(stack, (int)MathF.Round(local));
                if (Editor.SetScalarChannel(stack, keyIndex, controller, channel, value)) written++;
            }
        }
        catch
        {
            Restore(before);
            throw;
        }
        if (written == 0)
        {
            Restore(before);
            reason = $"No selected controller has a {name} key to write.";
            return 0;
        }
        Evaluator.Invalidate();
        var after = EditSnapshot.Take(Character, stacks);
        string what = written == 1 ? $"'{targets[0].Name}'" : $"{written} controllers";
        bool angle = channel is >= 9 and <= 12;
        string shown = angle ? $"{value * 180 / MathF.PI:0.###}°" : $"{value:0.###}";
        Record(new EditRecord($"Set {name} of {what} to {shown} at frame {frame}", before, after));
        return written;
    }

    /// <summary>The undo description prefix of <see cref="ResetAndFreeze"/>, so a caller can reload skins after undo.</summary>
    public const string FreezeDescription = "Reset & Freeze";

    /// <summary>
    /// The TRANSFORM window's Reset &amp; Freeze: bakes each
    /// unskinned mesh's local transform into its geometry as one undo step. Every object must be
    /// an unskinned mesh; returns how many were frozen.
    /// </summary>
    public int ResetAndFreeze(IReadOnlyList<Object3D> selection, out string? reason)
    {
        reason = null;
        if (!MeshFreeze.CanFreeze(selection))
        {
            reason = "Reset & Freeze takes unskinned meshes only.";
            return 0;
        }
        var meshes = selection.OfType<Mesh>().Distinct().ToList();
        var touched = meshes.SelectMany(MeshFreeze.TouchedObjects).Distinct().ToArray();
        return Structure($"{FreezeDescription} {ObjectNames(meshes)}", touched, () => meshes.Count(MeshFreeze.Freeze), n => n > 0);
    }

    /// <summary>
    /// Adds an animated camera to the character and records it for undo (the camera is a child of
    /// the character and a new slot of its <see cref="Character.Controllers"/> and poses).
    /// </summary>
    public CameraAnimated AddAnimatedCamera(string? name = null)
    {
        var objects = new List<SceneObject> { Character };
        if (Character.RefPose is { } r) objects.Add(r);
        if (Character.CurrentPose is { } c) objects.Add(c);
        if (Character.RefPoseOld is { } o) objects.Add(o);
        objects.AddRange(Character.BindPoses);
        var before = EditSnapshot.TakeShallow(Character, objects);
        var camera = CameraEditor.AddAnimatedCamera(Character, name);
        Evaluator.Invalidate();
        Evaluator.ReloadRestPose();
        var after = EditSnapshot.TakeShallow(Character, objects);
        Record(new EditRecord($"Add animated camera '{camera.Name}'", before, after));
        return camera;
    }

    /// <summary>Whether <paramref name="controller"/>'s editable track animates <paramref name="channel"/>.</summary>
    private static bool Animates(Animation animation, Object3D controller, int channel)
    {
        var track = EditableStack(animation, controller)?.TrackFor(controller);
        if (track == null) return false;
        return channel switch
        {
            < 3 => (track.Channels & (AnimChannels)(1u << channel)) != 0,
            < 6 => (track.Channels & AnimChannels.Rotate) != 0,
            _ => (track.Channels & (AnimChannels)(1u << channel)) != 0,
        };
    }

    /// <summary><paramref name="local"/> with one channel (0-8) set; rotation channels are ZYX Euler degrees.</summary>
    private static Transform WithChannel(Transform local, int channel, float value)
    {
        if (channel < 3)
        {
            var t = local.Translation;
            return local with
            {
                Translation = channel switch
                {
                    0 => new Vector3(value, t.Y, t.Z),
                    1 => new Vector3(t.X, value, t.Z),
                    _ => new Vector3(t.X, t.Y, value),
                },
            };
        }
        if (channel < 6)
        {
            var e = Curves.ToEulerDegrees(local.Rotation);
            e = channel switch
            {
                3 => new Vector3(value, e.Y, e.Z),
                4 => new Vector3(e.X, value, e.Z),
                _ => new Vector3(e.X, e.Y, value),
            };
            return local with { Rotation = Curves.FromEulerDegrees(e) };
        }
        var s = local.Scale;
        return local with
        {
            Scale = channel switch
            {
                6 => new Vector3(value, s.Y, s.Z),
                7 => new Vector3(s.X, value, s.Z),
                _ => new Vector3(s.X, s.Y, value),
            },
        };
    }

    /// <summary>A scale component at least <see cref="HermiteKeyInterpolator.MinScale"/> in magnitude.</summary>
    private static float FloorScale(float value) =>
        value < 0 ? -MathF.Max(-value, HermiteKeyInterpolator.MinScale) : MathF.Max(value, HermiteKeyInterpolator.MinScale);

    private static int Depth(Object3D o)
    {
        int depth = 0;
        for (var p = o.Parent3D; p != null; p = p.Parent3D) depth++;
        return depth;
    }


    /// <summary>
    /// Resolves a changed reference pose (the "REFERENCE POSE" dialog) and records it for undo.
    /// With <paramref name="followers"/> (characters sharing this one's Anim Bank), they take the
    /// same ref-pose change first and receive the resolved animations, in the same undo step.
    /// Returns the number of keys converted.
    /// </summary>
    public int ResolveRestPose(RestPoseResolution resolution, IReadOnlyList<Character>? followers = null)
    {
        var objects = new List<SceneObject>(Poses());
        if (resolution == RestPoseResolution.ConvertAnimations)
            objects.AddRange(Character.Animations.SelectMany(a => a.Stacks).Where(s => !s.IsAdditive));
        followers = resolution == RestPoseResolution.ReturnToPrevious ? null : followers;
        // Returning removes the edited pose and repoints the character, and following edits other
        // characters' poses and banks: both steps keep the whole rig.
        bool structural = resolution == RestPoseResolution.ReturnToPrevious || followers is { Count: > 0 };
        var before = structural ? EditSnapshot.TakeRig(Character, Character.Model.Scene) : EditSnapshot.TakeRestPose(Character, objects);
        int converted;
        try
        {
            if (followers is { Count: > 0 }) SharedAnimBanks.FollowRefPose(Character, followers);
            converted = RestPose.Resolve(resolution);
            if (followers is { Count: > 0 } && Character.AnimBank is { } bank)
            {
                // Every animation was rewritten for the new pose: the followers get them all again.
                var all = new HashSet<AktObject>(
                    bank.Source.PreOrder().Where(o => Character.Model.Wrap(o) is Animation),
                    ReferenceEqualityComparer.Instance);
                SharedAnimBanks.Sync(Character.Model, Character, all);
            }
        }
        catch
        {
            Restore(before);
            throw;
        }
        Pose.ReloadRestPose(Character);
        foreach (var follower in followers ?? [])
            if (Pose.Characters.FirstOrDefault(c => ReferenceEquals(c.Source, follower.Source)) is { } shown)
                Pose.ReloadRestPose(shown);
        string description = resolution switch
        {
            RestPoseResolution.ConvertAnimations => $"Convert {converted} keys to the new ref pose",
            RestPoseResolution.KeepAnimations => "Keep the animations on the new ref pose",
            _ => "Return to the previous ref pose",
        };
        if (followers is { Count: > 0 }) description += $" (and {followers.Count} linked character(s))";
        Record(new EditRecord(description, before,
            structural ? EditSnapshot.TakeRig(Character, Character.Model.Scene) : EditSnapshot.TakeRestPose(Character, objects)));
        return converted;
    }

    // ---- Anim Bank and Mixer ---------------------

    /// <summary>The Anim Bank and Mixer editor of <see cref="Character"/>, sharing the display's evaluator.</summary>
    public AnimationBankEditor Bank => _bank ??= new AnimationBankEditor(Character, Evaluator);
    private AnimationBankEditor? _bank;

    /// <summary>Creates "New animation" (<see cref="AnimationBankEditor.CreateAnimation"/>), which becomes current.</summary>
    public Animation CreateAnimation(SceneObject? folder = null)
    {
        folder ??= Character.AnimBank ?? throw new InvalidOperationException($"{Character.Name} has no Anim Bank.");
        // A new animation keys every controller's rigged Compensate Scale at frame 0, so changing
        // the rigging value later does not change animations made before it (compensate-scale-keys.md).
        CompensateScaleKeys.EnsureMembers(Character.Model);
        return Structure("Create animation", [folder, Character], () =>
            {
                var animation = Bank.CreateAnimation(folder);
                foreach (var o in Evaluator.Controllers)
                    CompensateScaleKeys.SetKey(animation, o, 0, o.CompensatesParentScale);
                Evaluator.Invalidate();
                return animation;
            }, _ => true,
            a => $"Create animation '{a.Name}'");
    }

    public Animation CopyAnimation(Animation animation) =>
        Structure($"Copy animation '{animation.Name}'", [animation.Parent!, Character], () => Bank.CopyAnimation(animation), _ => true);

    public void DeleteAnimation(Animation animation) =>
        Structure($"Delete animation '{animation.Name}'", [animation.Parent!, Character],
            () => { Bank.DeleteAnimation(animation); return true; }, _ => true);

    /// <summary>Creates "New folder" at the end of the Anim Bank root.</summary>
    public Folder CreateFolder()
    {
        var root = Character.AnimBank ?? throw new InvalidOperationException($"{Character.Name} has no Anim Bank.");
        return Structure("Create folder", [root], () => Bank.CreateFolder(), _ => true, f => $"Create folder '{f.Name}'");
    }

    /// <summary>
    /// Deletes several Anim Bank rows (folders with everything inside them, and animations) as one
    /// undo step. A row inside another listed folder is left to that folder.
    /// </summary>
    public void DeleteBankItems(IReadOnlyList<SceneObject> items)
    {
        var folders = items.OfType<Folder>().ToList();
        var doomed = items.Where(i => (i is Folder or Animation) && !folders.Any(f => f != i && Inside(i, f)))
            .Distinct().ToList();
        if (doomed.Count == 0) return;
        if (doomed.Count == 1)
        {
            if (doomed[0] is Folder folder) DeleteFolder(folder);
            else DeleteAnimation((Animation)doomed[0]);
            return;
        }
        SceneObject[] parents = [.. doomed.Select(i => i.Parent).OfType<SceneObject>().Append(Character).Distinct()];
        Structure($"Delete {doomed.Count} items", parents, () =>
        {
            foreach (var item in doomed)
                if (item is Folder folder) Bank.DeleteFolder(folder);
                else Bank.DeleteAnimation((Animation)item);
            return true;
        }, _ => true);

        static bool Inside(SceneObject item, SceneObject folder)
        {
            for (var p = item.Source.Parent; p != null; p = p.Parent)
                if (p == folder.Source) return true;
            return false;
        }
    }

    /// <summary>Deletes a folder with everything inside it.</summary>
    public Folder CopyFolder(Folder folder) =>
        Structure($"Copy folder '{folder.Name}'", [folder.Parent!, Character], () => Bank.CopyFolder(folder), _ => true);

    public void DeleteFolder(Folder folder) =>
        Structure($"Delete folder '{folder.Name}'", [folder.Parent!, Character],
            () => { Bank.DeleteFolder(folder); return true; }, _ => true);

    /// <summary>Drops animations and folders into <paramref name="target"/> (<see cref="AnimationBankEditor.Move(IEnumerable{SceneObject}, SceneObject)"/>); returns those that moved.</summary>
    public IReadOnlyList<SceneObject> MoveToFolder(IReadOnlyCollection<SceneObject> items, SceneObject target)
    {
        SceneObject[] parents = [.. items.Select(i => i.Parent).OfType<SceneObject>().Append(target).Distinct()];
        string what = items.Count == 1 ? $"'{items.First().Name}'" : $"{items.Count} items";
        return Structure($"Move {what} to '{target.Name}'", parents, () => Bank.Move(items, target), moved => moved.Count > 0);
    }

    /// <summary>The Anim Bank's free sort: places items in <paramref name="target"/> before <paramref name="before"/> (<see cref="AnimationBankEditor.Place"/>).</summary>
    public bool PlaceInBank(IReadOnlyCollection<SceneObject> items, SceneObject target, SceneObject? before)
    {
        SceneObject[] parents = [.. items.Select(i => i.Parent).OfType<SceneObject>().Append(target).Distinct()];
        string what = items.Count == 1 ? $"'{items.First().Name}'" : $"{items.Count} items";
        return Structure($"Reorder {what}", parents, () => Bank.Place(items, target, before), moved => moved);
    }

    /// <summary>Drops a stack at place <paramref name="index"/> of the Mixer (0 = top).</summary>
    public bool MoveStackTo(AnimStack stack, int index) =>
        Structure($"Move stack '{stack.Name}'", [stack.Animation!], () => Bank.MoveStackTo(stack, index), moved => moved);

    /// <summary>Renames an animation or a stack (unique-name rule); returns the name it got.</summary>
    public string Rename(SceneObject o, string name) =>
        Structure($"Rename '{o.Name}' to '{name}'", [o], () => AnimationBankEditor.Rename(o, name), _ => true);

    /// <summary>Creates "New Stack" above the current stack with tracks for <paramref name="targets"/>; it becomes current.</summary>
    public AnimStack CreateStack(Animation animation, IReadOnlyCollection<Object3D> targets) =>
        Structure($"Create stack in '{animation.Name}'", [animation], () => Bank.CreateStack(animation, targets), _ => true);

    public AnimStack DuplicateStack(AnimStack stack) =>
        Structure($"Duplicate stack '{stack.Name}'", [stack.Animation!], () => Bank.DuplicateStack(stack), _ => true);

    /// <summary>Deletes a stack; the last stack of an animation is kept (returns false).</summary>
    public bool DeleteStack(AnimStack stack) =>
        Structure($"Delete stack '{stack.Name}'", [stack.Animation!], () => Bank.DeleteStack(stack), deleted => deleted);

    /// <summary>Moves a stack up (negative) or down in the Mixer.</summary>
    public bool MoveStack(AnimStack stack, int delta) =>
        Structure($"Move stack '{stack.Name}' {(delta < 0 ? "up" : "down")}", [stack.Animation!], () => Bank.MoveStack(stack, delta), moved => moved);

    /// <summary>Bakes the selected stacks into one and deletes them.</summary>
    public AnimStack MergeStacks(Animation animation, IReadOnlyList<AnimStack> selection) =>
        Structure($"Merge {selection.Count} stack(s) in '{animation.Name}'", [animation],
            () => Bank.MergeStacks(animation, selection), _ => true);

    /// <summary>Moves the selected channels of <paramref name="source"/>'s tracks into a new stack (*Extract stack*).</summary>
    public AnimStack ExtractChannels(AnimStack source, AnimChannels channels, IEnumerable<AnimTrack> selectedTracks)
    {
        var tracks = selectedTracks.ToList();
        // The tracks' ChannelMask is edited, so their fields are part of the undo snapshot too.
        return Structure($"Extract channels of '{source.Name}'", [source.Animation!, source, .. tracks],
            () => Bank.ExtractChannels(source, channels, tracks), _ => true);
    }

    /// <summary>Adds a sound file as a new audio stack of <paramref name="animation"/> (*Audio stack*).</summary>
    public AudioStack AddAudioStack(Animation animation, string fileName, byte[] sound, double durationSeconds) =>
        Structure($"Add audio '{Path.GetFileName(fileName)}'", [animation, Character],
            () => Bank.AddAudioStack(animation, fileName, sound, durationSeconds), _ => true);

    public void SetStackOpacity(AnimStack stack, float opacity) =>
        Structure($"Set opacity of '{stack.Name}'", [stack], () => { Bank.SetOpacity(stack, opacity); return true; }, _ => true);

    public void SetStackAdditive(AnimStack stack, bool additive) =>
        Structure($"Make '{stack.Name}' {(additive ? "additive" : "override")}", [stack],
            () => { Bank.SetAdditive(stack, additive); return true; }, _ => true);

    /// <summary>Flips one stack flag (<see cref="AnimationBankEditor.CycleFlag"/>, enabled, hammer, additive).</summary>
    public void ToggleStackFlag(AnimStack stack, uint flag) =>
        Structure($"Turn {FlagName(flag)} {((stack.Flags & flag) == 0 ? "on" : "off")} for '{stack.Name}'", [stack],
            () => { Bank.ToggleFlag(stack, flag); return true; }, _ => true);

    private static string FlagName(uint flag) => flag switch
    {
        AnimationBankEditor.CycleFlag => "cycle",
        AnimationBankEditor.EnabledFlag => "enabled",
        AnimationBankEditor.AdditiveFlag => "additive",
        AnimationBankEditor.HammerFlag => "hammer",
        _ => $"flag 0x{flag:X}",
    };

    /// <summary>Makes <paramref name="stack"/> its animation's current stack (not recorded, like a selection).</summary>
    public void SetCurrentStack(AnimStack stack) => Bank.SetCurrentStack(stack);

    // ---- hierarchy panel -------------------------------

    /// <summary>Hides the objects (see <see cref="Outliner.Hide"/>) as one undo step; returns how many objects changed.</summary>
    public int Hide(IEnumerable<Object3D> objects)
    {
        var list = objects.ToList();
        return Structure($"Hide {ObjectNames(list)}", list.ToArray(),
            () => Outliner.Hide(list), changed => changed > 0);
    }

    /// <summary>Shows the given objects only as one undo step; returns how many changed.</summary>
    public int Show(IEnumerable<Object3D> objects)
    {
        var list = objects.ToList();
        return Structure($"Show {ObjectNames(list)}", list.ToArray(),
            () => Outliner.Show(list), changed => changed > 0);
    }

    /// <summary>Flips the given objects' shown state as one undo step; returns how many changed.</summary>
    public int ToggleShown(IEnumerable<Object3D> objects)
    {
        var list = objects.ToList();
        string description = list.Count == 1 && list[0].IsShown
            ? $"Hide '{list[0].Name}'"
            : list.Count == 1 ? $"Show '{list[0].Name}'" : $"Toggle show of {list.Count} objects";
        return Structure(description, list.ToArray(), () => Outliner.ToggleShown(list), changed => changed > 0);
    }

    /// <summary>Makes the objects clickable or not in the viewport as one undo step; returns how many changed.</summary>
    public int SetPickable(IEnumerable<Object3D> objects, bool pickable)
    {
        var list = objects.ToList();
        return Structure($"Set {ObjectNames(list)} {(pickable ? "clickable" : "unclickable")}", list.ToArray(),
            () => Outliner.SetPickable(list, pickable), changed => changed > 0);
    }

    /// <summary>
    /// Renames scene-tree objects as one undo step, each through the tree's unique-name rule
    /// (<see cref="RenameTool.UniqueTreeName"/>), in the given order; returns how many changed.
    /// </summary>
    public int RenameTreeObjects(IReadOnlyList<(Object3D Object, string Name)> renames)
    {
        var objects = renames.Select(r => (SceneObject)r.Object).ToArray();
        string description = renames.Count == 1
            ? $"Rename '{renames[0].Object.Name}' to '{renames[0].Name}'"
            : $"Rename {renames.Count} objects";
        return Structure(description, objects, () =>
        {
            int changed = 0;
            foreach (var (o, name) in renames)
            {
                string? before = o.Name;
                if (RenameTool.RenameTreeObject(o, name) != before) changed++;
            }
            return changed;
        }, changed => changed > 0);
    }

    /// <summary>
    /// Renames Anim Bank items (animations, folders, stacks) as one undo step through the bank's
    /// unique-name rule, in the given order; returns how many changed.
    /// </summary>
    public int RenameBankItems(IReadOnlyList<(SceneObject Item, string Name)> renames)
    {
        var objects = renames.Select(r => r.Item).ToArray();
        string description = renames.Count == 1
            ? $"Rename '{renames[0].Item.Name}' to '{renames[0].Name}'"
            : $"Rename {renames.Count} items";
        return Structure(description, objects, () =>
        {
            int changed = 0;
            foreach (var (item, name) in renames)
            {
                string? before = item.Name;
                if (AnimationBankEditor.Rename(item, name) != before) changed++;
            }
            return changed;
        }, changed => changed > 0);
    }

    /// <summary>Shows every object of the scene as one undo step; returns how many changed.</summary>
    public int ShowAll() =>
        Structure("Show all objects", SceneObjects(), () => Outliner.ShowAll(Character.Model.Scene), changed => changed > 0);

    /// <summary>Hides every object of the scene as one undo step; returns how many changed.</summary>
    public int HideAll() =>
        Structure("Hide all objects", SceneObjects(), () => Outliner.HideAll(Character.Model.Scene), changed => changed > 0);

    /// <summary>Locks or unlocks the given meshes as one undo step; returns how many changed.</summary>
    public int SetMeshesLocked(IEnumerable<Mesh> meshes, bool locked)
    {
        var list = meshes.ToList();
        return Structure($"{(locked ? "Lock" : "Unlock")} {ObjectNames(list)}", list.ToArray(),
            () => Outliner.SetMeshesLocked(list, locked), changed => changed > 0);
    }

    /// <summary>Locks or unlocks every skinned mesh of the character as one undo step.</summary>
    public int SetCharacterMeshesLocked(bool locked)
    {
        var meshes = Character.Meshes.ToList();
        return Structure($"{(locked ? "Lock" : "Unlock")} {meshes.Count} skinned mesh(es)", meshes.ToArray(),
            () => Outliner.SetCharacterMeshesLocked(Character, locked), changed => changed > 0);
    }

    /// <summary>
    /// Runs a Properties-window edit of one object's own members (name, flags, target size) as
    /// one undo step; <paramref name="edit"/> returns whether anything changed.
    /// </summary>
    public bool EditObject(SceneObject o, string description, Func<bool> edit) =>
        Structure(description, [o], () =>
        {
            bool changed = edit();
            if (changed) Evaluator.Invalidate();
            return changed;
        }, changed => changed);

    /// <summary>
    /// <see cref="EditObject"/> over several objects: a material edit keeps the material, the
    /// scene and its <c>__Textures</c> folder (whose child list a new texture joins) and the
    /// animations whose texture keys it changes.
    /// </summary>
    public bool EditObjects(IEnumerable<SceneObject> objects, string description, Func<bool> edit) =>
        Structure(description, objects.ToArray(), () =>
        {
            bool changed = edit();
            if (changed) Evaluator.Invalidate();
            return changed;
        }, changed => changed);

    /// <summary>
    /// Sets Compensate Scale of <paramref name="objects"/> to <paramref name="on"/> as one undo
    /// step: with an animation, a stepped key at
    /// <paramref name="frame"/>; without one (the rigging context), the rig's own value. Returns
    /// whether anything changed.
    /// </summary>
    public bool SetCompensateScale(Animation? animation, IReadOnlyList<Object3D> objects, int frame, bool on)
    {
        if (objects.Count == 0) return false;
        if (animation == null)
            return EditObjects(objects, $"Compensate Scale {(on ? "on" : "off")} for {ObjectNames(objects)}", () =>
            {
                bool changed = false;
                foreach (var o in objects)
                    if (o.CompensatesParentScale != on) { o.CompensatesParentScale = on; changed = true; }
                return changed;
            });
        CompensateScaleKeys.EnsureMembers(Character.Model);
        return EditObject(animation, $"Key Compensate Scale {(on ? "on" : "off")} for {ObjectNames(objects)} at frame {frame}", () =>
        {
            bool changed = false;
            foreach (var o in objects) changed |= CompensateScaleKeys.SetKey(animation, o, frame, on);
            return changed;
        });
    }

    /// <summary>Deletes the Compensate Scale keys of <paramref name="objects"/> at <paramref name="frame"/> as one undo step.</summary>
    public bool RemoveCompensateKeys(Animation animation, IReadOnlyList<Object3D> objects, int frame) =>
        EditObject(animation, $"Delete Compensate Scale key of {ObjectNames(objects)} at frame {frame}", () =>
        {
            bool changed = false;
            foreach (var o in objects) changed |= CompensateScaleKeys.RemoveKey(animation, o, frame);
            return changed;
        });

    private SceneObject[] SceneObjects() => Character.Model.Scene.Descendants<Object3D>().ToArray();

    private static string ObjectNames<T>(IReadOnlyList<T> objects) where T : SceneObject =>
        objects.Count == 1 ? $"'{objects[0].Name}'" : $"{objects.Count} objects";

    /// <summary>
    /// Runs an Anim Bank or Mixer edit and records it for undo. Only the fields and child lists of
    /// <paramref name="objects"/> are kept: created or removed sub-trees are not changed afterwards,
    /// so attaching or detaching them again is enough.
    /// </summary>
    private T Structure<T>(string description, SceneObject[] objects, Func<T> edit, Func<T, bool> changed,
        Func<T, string>? describe = null)
    {
        var before = EditSnapshot.TakeShallow(Character, objects);
        T result;
        try
        {
            result = edit();
        }
        catch
        {
            Restore(before);
            throw;
        }
        if (changed(result))
            Record(new EditRecord(describe?.Invoke(result) ?? description, before, EditSnapshot.TakeShallow(Character, objects)));
        return result;
    }

    /// <summary>The rest-pose editor of <see cref="Character"/>.</summary>
    public RestPoseEditor RestPose => _restPose ??= new RestPoseEditor(Character);
    private RestPoseEditor? _restPose;

    internal EditSnapshot TakeRestPoseSnapshot() => EditSnapshot.TakeRestPose(Character, Poses());

    /// <summary>Every pose of the character a rest-pose edit or resolution can change.</summary>
    private IEnumerable<CharacterPose> Poses() =>
        new[] { RestPose.EditedPose, Character.RefPose, Character.RefPoseOld, Character.CurrentPose }
            .Concat(Character.BindPoses).OfType<CharacterPose>();

    public bool Undo()
    {
        if (History.Undo() is not { } record) return false;
        Restore(record.Before);
        return true;
    }

    public bool Redo()
    {
        if (History.Redo() is not { } record) return false;
        Restore(record.After);
        return true;
    }
}

/// <summary>
/// One gizmo drag in progress on a controller's local transform, applied live until committed or
/// cancelled: a key (<see cref="EditDrag"/>) or a rest-pose slot (<see cref="RestPoseDrag"/>).
/// </summary>
public abstract class TransformDrag
{
    protected TransformDrag(Object3D controller, Transform baseLocal)
    {
        Controller = controller;
        BaseLocal = baseLocal;
        Posed = baseLocal;
    }

    public Object3D Controller { get; }

    /// <summary>The controller's local transform at the start of the drag, in the frame the drag edits.</summary>
    public Transform BaseLocal { get; }

    /// <summary>The local transform last set by the drag.</summary>
    public Transform Posed { get; protected set; }

    /// <summary>
    /// The space the drag applies its change in: <see cref="TransformSpace.Local"/> (the default)
    /// edits the controller's own local transform about its own axes; World and Screen turn or move
    /// it in the world axes of <see cref="SpaceFrame"/> and convert the result back to the local
    /// transform the key stores.
    /// </summary>
    public TransformSpace Space { get; set; } = TransformSpace.Local;

    /// <summary>
    /// The world axes of a World or Screen drag (rows X, Y, Z), captured when it started so a view
    /// change mid-drag does not swing the handles; the Local space ignores it.
    /// </summary>
    public Matrix4x4 SpaceFrame { get; set; } = Matrix4x4.Identity;

    /// <summary>The world matrix of <see cref="FrameParent"/> when the drag started (identity for the scene).</summary>
    internal Matrix4x4 ParentWorldMatrix { get; set; } = Matrix4x4.Identity;

    /// <summary>The local scale of <see cref="FrameParent"/> when the drag started.</summary>
    internal Vector3 ParentWorldScale { get; set; } = Vector3.One;

    /// <summary>
    /// The discrete steps this drag snaps the current object's change to
    /// Off by default; a group's followers get the
    /// lead's snapped change, they are not snapped independently.
    /// </summary>
    public SnapSettings Snap { get; set; } = new();

    public abstract bool CanTranslate { get; }
    public abstract bool CanScale { get; }

    /// <summary>
    /// Whether <see cref="Swivel"/> applies: an IK or reverse-foot controller posed at a key (the
    /// Spinner's centre in rotate mode).
    /// </summary>
    public virtual bool CanSwivel => false;

    /// <summary>
    /// Swings the middle joints of the controller's IK chain by <paramref name="angle"/> radians
    /// from where they were when the swivel started (<see cref="KeyEditor.SwivelChain"/>).
    /// </summary>
    public virtual void Swivel(float angle) { }

    /// <summary>The swivel without going back to its start first (a group restores its whole pose once).</summary>
    internal virtual void SwivelOnce(float angle) { }

    /// <summary>
    /// IK and reverse-foot controllers are never scaled: their tracks carry scale channels, but the
    /// gizmo has no scale handles on them and the Spinner's scale parts are disabled.
    /// </summary>
    public static bool IsScalable(Object3D controller) => controller is not ControllerIK;

    /// <summary>
    /// Poses the controller rotated from <see cref="BaseLocal"/> by <paramref name="angle"/> radians
    /// about its own local axis <paramref name="axis"/>, keeping the drag's translation. With
    /// snapping on, the turned angle is snapped first.
    /// </summary>
    public virtual void Rotate(int axis, float angle)
    {
        if (Space != TransformSpace.Local) { RotateInSpace(axis, angle); return; }
        var delta = Quaternion.CreateFromAxisAngle(Unit(axis), Snap.SnapAngle(angle));
        Set(Posed with { Rotation = Quaternion.Normalize(BaseLocal.Rotation * delta) }, AnimChannels.Rotate);
    }

    /// <summary>
    /// Poses the controller moved from <see cref="BaseLocal"/> by <paramref name="distance"/> along
    /// its parent's axis <paramref name="axis"/>, keeping the drag's rotation. With snapping on, the
    /// resulting coordinate is snapped to the move step.
    /// </summary>
    public virtual void Translate(int axis, float distance)
    {
        if (!CanTranslate) return;
        if (Space != TransformSpace.Local) { TranslateInSpace(axis, distance); return; }
        var b = BaseLocal.Translation;
        float value = Snap.SnapMove(b[axis] + distance);
        var translation = axis switch
        {
            0 => new Vector3(value, b.Y, b.Z),
            1 => new Vector3(b.X, value, b.Z),
            _ => new Vector3(b.X, b.Y, value),
        };
        Set(Posed with { Translation = translation }, AnimChannels.Translate);
    }

    /// <summary>
    /// Poses the controller moved from <see cref="BaseLocal"/> in the plane across its parent's axis
    /// <paramref name="normal"/>: by <paramref name="first"/> along axis <c>(normal + 1) % 3</c> and
    /// by <paramref name="second"/> along axis <c>(normal + 2) % 3</c>. With snapping on, both
    /// resulting coordinates are snapped to the move step.
    /// </summary>
    public virtual void TranslatePlane(int normal, float first, float second)
    {
        if (!CanTranslate) return;
        if (Space != TransformSpace.Local) { TranslatePlaneInSpace(normal, first, second); return; }
        var b = BaseLocal.Translation;
        int a1 = (normal + 1) % 3, a2 = (normal + 2) % 3;
        Span<float> t = [b.X, b.Y, b.Z];
        t[a1] = Snap.SnapMove(b[a1] + first);
        t[a2] = Snap.SnapMove(b[a2] + second);
        Set(Posed with { Translation = new Vector3(t[0], t[1], t[2]) }, AnimChannels.Translate);
    }

    /// <summary>
    /// Poses the controller with <see cref="BaseLocal"/>'s scale multiplied by
    /// <paramref name="factor"/> along its own local axis <paramref name="axis"/>, or along all
    /// three for <paramref name="axis"/> 3 (uniform), keeping the drag's translation and rotation.
    /// With snapping on, each resulting component is snapped to the scale step. The factor never
    /// flips a component's sign, and each component keeps a magnitude of at least
    /// <see cref="HermiteKeyInterpolator.MinScale"/>, the floor evaluation applies to interpolated scale
    /// anyway, so a drag never sets a zero scale.
    /// </summary>
    public virtual void Scale(int axis, float factor)
    {
        if (!CanScale) return;
        if (Space != TransformSpace.Local) { ScaleInSpace(axis, factor); return; }
        factor = MathF.Max(factor, 0);
        bool uniform = axis is < 0 or > 2;
        var b = BaseLocal.Scale;
        var scale = new Vector3(Scaled(b.X, 0), Scaled(b.Y, 1), Scaled(b.Z, 2));
        var channels = uniform ? AnimChannels.Scale : (AnimChannels)((uint)AnimChannels.ScaleX << axis);
        Set(Posed with { Scale = scale }, channels);

        float Scaled(float value, int component)
        {
            if (!uniform && component != axis) return Posed.Scale[component];
            float s = Snap.SnapScale(MathF.Abs(value) * factor);
            return (value < 0 ? -1 : 1) * MathF.Max(s, HermiteKeyInterpolator.MinScale);
        }
    }

    /// <summary>
    /// Captures the frame parent's world and scale at the drag's start, so that a world or screen
    /// change is converted against the frame the key was written in (subclasses call it from their
    /// constructor, after their fields are set).
    /// </summary>
    protected void CaptureParentWorld(PoseEvaluator evaluator)
    {
        if (FrameParent is not { } parent) return;
        var worlds = Worlds(out var locals);
        int index = evaluator.IndexOf(parent);
        if (index >= 0)
        {
            ParentWorldMatrix = worlds[index];
            ParentWorldScale = locals[index].Scale;
        }
        else
        {
            ParentWorldMatrix = parent.WorldMatrix;
            ParentWorldScale = Object3D.LocalScale(parent.LocalMatrix);
        }
    }

    /// <summary>The controller's world matrix built from <see cref="BaseLocal"/> and its frame parent.</summary>
    private Matrix4x4 BaseWorld() =>
        WorldComposition.Compose(BaseLocal.ToMatrix(), ParentWorldMatrix, ParentWorldScale, CompensatesNow);

    /// <summary>
    /// Whether the controller composes with its parent's scale compensated where the drag edits:
    /// the rigging value, or in an animation its keyed Compensate Scale at the drag's frame.
    /// </summary>
    internal virtual bool CompensatesNow => Controller.CompensatesParentScale;

    /// <summary>Poses the controller from a world matrix, through the local transform the key stores.</summary>
    internal bool SetWorld(Matrix4x4 world, AnimChannels channels)
    {
        if (WorldComposition.TryDecompose(world, ParentWorldMatrix, ParentWorldScale, CompensatesNow, out var local))
        {
            Set(Transform.FromMatrixLoose(local), channels);
            return true;
        }
        return false;
    }

    /// <summary>A unit world direction of the drag's space (row <paramref name="axis"/> of <see cref="SpaceFrame"/>).</summary>
    private Vector3 SpaceAxis(int axis)
    {
        var v = axis switch
        {
            0 => new Vector3(SpaceFrame.M11, SpaceFrame.M12, SpaceFrame.M13),
            1 => new Vector3(SpaceFrame.M21, SpaceFrame.M22, SpaceFrame.M23),
            _ => new Vector3(SpaceFrame.M31, SpaceFrame.M32, SpaceFrame.M33),
        };
        return v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Unit(axis);
    }

    /// <summary>A rotation about the space's <paramref name="axis"/> through the controller's own origin.</summary>
    private void RotateInSpace(int axis, float angle)
    {
        var world = BaseWorld();
        var rotated = world * Matrix4x4.CreateFromAxisAngle(SpaceAxis(axis), Snap.SnapAngle(angle));
        // World * turn moves the origin too (the translation row turns); the pivot is the controller.
        rotated.M41 = world.M41;
        rotated.M42 = world.M42;
        rotated.M43 = world.M43;
        SetWorld(rotated, AnimChannels.Rotate);
    }

    /// <summary>A translation along the space's <paramref name="axis"/>, <paramref name="distance"/> units long.</summary>
    private void TranslateInSpace(int axis, float distance)
    {
        var world = BaseWorld();
        var direction = SpaceAxis(axis);
        Vector3 position;
        if (Space == TransformSpace.World)
        {
            // Snapping is on the resulting world coordinate, like the local path snaps the
            // resulting local one; a Screen axis is not a world coordinate, so it snaps the drag.
            position = world.Translation + direction * distance;
            position[axis] = Snap.SnapMove(position[axis]);
        }
        else
        {
            position = world.Translation + direction * Snap.SnapMove(distance);
        }
        world.Translation = position;
        SetWorld(world, AnimChannels.Translate);
    }

    /// <summary>A translation in the plane across the space's <paramref name="normal"/> axis.</summary>
    private void TranslatePlaneInSpace(int normal, float first, float second)
    {
        int a = (normal + 1) % 3, b = (normal + 2) % 3;
        var world = BaseWorld();
        var position = world.Translation + SpaceAxis(a) * first + SpaceAxis(b) * second;
        if (Space == TransformSpace.World)
        {
            position[a] = Snap.SnapMove(position[a]);
            position[b] = Snap.SnapMove(position[b]);
        }
        else
        {
            position = world.Translation + SpaceAxis(a) * Snap.SnapMove(first) + SpaceAxis(b) * Snap.SnapMove(second);
        }
        world.Translation = position;
        SetWorld(world, AnimChannels.Translate);
    }

    /// <summary>
    /// A scale along the space's <paramref name="axis"/> about the controller's own origin, applied
    /// to its world matrix and decomposed back. A world or screen axis that is not one of the
    /// controller's own axes shears it, and a local MRS key cannot store shear, so the decomposition
    /// keeps the scale lengths and the nearest orientation (the SloppyAnimatsu choice); uniform
    /// (<paramref name="axis"/> 3) is exact. Snapping rounds the factor.
    /// </summary>
    private void ScaleInSpace(int axis, float factor)
    {
        bool uniform = axis is < 0 or > 2;
        factor = MathF.Max(Snap.SnapScale(MathF.Max(factor, 0)), HermiteKeyInterpolator.MinScale);
        var world = BaseWorld();
        var pivot = world.Translation;
        world.Translation = Vector3.Zero;
        Matrix4x4 scale;
        if (uniform)
        {
            scale = Matrix4x4.CreateScale(factor);
        }
        else
        {
            var n = SpaceAxis(axis);
            float k = factor - 1;
            scale = new Matrix4x4(
                1 + k * n.X * n.X, k * n.X * n.Y, k * n.X * n.Z, 0,
                k * n.Y * n.X, 1 + k * n.Y * n.Y, k * n.Y * n.Z, 0,
                k * n.Z * n.X, k * n.Z * n.Y, 1 + k * n.Z * n.Z, 0,
                0, 0, 0, 1);
        }
        world *= scale;
        world.Translation = pivot;
        SetWorld(world, AnimChannels.Scale);
    }

    /// <summary>Applies <paramref name="local"/> (in <see cref="BaseLocal"/>'s frame); <paramref name="channels"/> are the ones the gesture changed.</summary>
    public abstract void Set(Transform local, AnimChannels channels);

    /// <summary>Ends the drag, keeping the change, and records it for undo.</summary>
    public abstract EditRecord Commit();

    /// <summary>Ends the drag, putting everything back as it was before it.</summary>
    public abstract void Cancel();

    /// <summary>Evaluates the character as this drag edits it: world matrices and local transforms of its controllers.</summary>
    internal abstract Matrix4x4[] Worlds(out Transform[] locals);

    /// <summary>The object whose frame <see cref="BaseLocal"/> is expressed in (null: the world).</summary>
    internal abstract Object3D? FrameParent { get; }

    private static Vector3 Unit(int axis) => axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
}

/// <summary>
/// A drag that places an object that is not a controller (a whole character, a mesh, a group):
/// it edits the object's own transform (its <c>LTM</c>), which everything below it composes under.
/// The placement is not animated, so it is the same at every frame; it is saved with the scene
/// and undone like any edit. It scales only when the Rigging context allows it.
/// </summary>
public sealed class PlacementDrag : TransformDrag
{
    private readonly PoseEditSession _session;
    private readonly Object3D _target;
    private readonly Character _owner;
    private readonly bool _allowScale;
    private readonly EditSnapshot _before;

    internal PlacementDrag(PoseEditSession session, Object3D target, bool allowScale)
        : base(target, Transform.FromMatrixLoose(target.LocalMatrix))
    {
        _session = session;
        _target = target;
        _allowScale = allowScale;
        _owner = target as Character ?? OwnerOf(target) ?? session.Character;
        _before = EditSnapshot.TakeShallow(_owner, [target]);
        if (target.Parent3D is { } parent)
        {
            ParentWorldMatrix = parent.WorldMatrix;
            ParentWorldScale = Object3D.LocalScale(parent.LocalMatrix);
        }
    }

    private static Character? OwnerOf(Object3D o)
    {
        for (var p = o.Parent3D; p != null; p = p.Parent3D)
            if (p is Character c) return c;
        return null;
    }

    public override bool CanTranslate => true;

    /// <summary>A placement moves and turns; it scales only in the Rigging context.</summary>
    public override bool CanScale => _allowScale;

    public override void Set(Transform local, AnimChannels channels)
    {
        _target.LocalMatrix = local.ToMatrix();
        _session.Pose.RefreshPlacements();
        Posed = local;
    }

    public override EditRecord Commit()
    {
        var record = new EditRecord($"Placed {_target.Name}", _before, EditSnapshot.TakeShallow(_owner, [_target]));
        _session.Record(record);
        return record;
    }

    public override void Cancel() => _session.Restore(_before);

    internal override Matrix4x4[] Worlds(out Transform[] locals)
    {
        locals = [];
        return [];
    }

    internal override Object3D? FrameParent => _target.Parent3D;
}

/// <summary>A drag that poses its controller at one key.</summary>
public sealed class EditDrag : TransformDrag
{
    private readonly PoseEditSession _session;
    private readonly EditSnapshot _before;
    private readonly (Object3D Child, Transform Local)[] _children;

    internal EditDrag(PoseEditSession session, Animation animation, AnimStack stack, int keyIndex, Object3D controller,
        int frame, EditSnapshot before, bool createdKey, (Object3D Child, Transform Local)[] children)
        : base(controller, StartLocal(session.Evaluator, animation, controller, frame))
    {
        _session = session;
        _before = before;
        _children = children;
        Animation = animation;
        Stack = stack;
        KeyIndex = keyIndex;
        Frame = frame;
        CreatedKey = createdKey;
        Track = stack.TrackFor(controller)!;
        CaptureParentWorld(_session.Evaluator);
    }

    /// <summary>
    /// The controller's mixed local transform, except for an IK controller at an FK key: that one is
    /// drawn at the end of its chain (<see cref="PoseEvaluator.FollowingLocal"/>), so the drag starts
    /// there instead of at its stored key, and the chain does not jump at the first move.
    /// </summary>
    private static Transform StartLocal(PoseEvaluator evaluator, Animation animation, Object3D controller, int frame) =>
        (controller is ControllerIK ik ? evaluator.FollowingLocal(animation, ik, frame) : null)
        ?? PoseEvaluator.Compose(evaluator.RestTransforms[evaluator.IndexOf(controller)], evaluator.MixDelta(animation, controller, frame));

    public Animation Animation { get; }
    public AnimStack Stack { get; }
    public AnimTrack Track { get; }
    public int KeyIndex { get; }
    public int Frame { get; }

    internal override bool CompensatesNow => _session.Evaluator.Compensates(Controller, Animation, Frame);

    /// <summary>Whether the drag inserted a new key (undone with the drag).</summary>
    public bool CreatedKey { get; }

    // BaseLocal is the controller's mixed local transform, in the frame its keys are expressed in
    // (its space-switching parent, if any).

    public override bool CanTranslate => (Track.Channels & AnimChannels.Translate) != 0;
    public override bool CanScale => (Track.Channels & AnimChannels.Scale) != 0 && IsScalable(Controller);

    public override bool CanSwivel => Controller is ControllerReverseFoot rf
        ? _session.Editor.FootChain(Stack, LoopKey, rf) != null
        : Controller is ControllerIK ik && _session.Editor.IKChain(Stack, LoopKey, ik) != null;

    private int LoopKey => KeyIndex == Stack.Keys.Count - 1 && Stack.Keys.Count > 1 ? 0 : KeyIndex;

    private EditSnapshot? _swivelBase;

    public override void Swivel(float angle)
    {
        if (_swivelBase == null) _swivelBase = EditSnapshot.Take(Stack);
        else _session.Restore(_swivelBase);
        SwivelOnce(angle);
    }

    /// <summary>The swivel without going back to its start first (a group restores all stacks once).</summary>
    internal override void SwivelOnce(float angle)
    {
        // The angle comes in the left-side convention; a right-side chain turns the other way.
        if (Controller is ControllerIK ik) _session.Editor.SwivelChain(Stack, KeyIndex, ik, angle * SpinnerDial.SwivelSide(ik));
    }

    /// <summary>
    /// Keys <paramref name="local"/> at the drag's key (<see cref="KeyEditor.Pose"/>: an IK
    /// controller pulls its chain, and the stack's other IK keys there are kept consistent).
    /// </summary>
    public override void Set(Transform local, AnimChannels channels)
    {
        _session.Editor.Pose(Stack, KeyIndex, Controller, local, channels);
        if (_children.Length > 0)
            _session.Editor.KeepChildrenFixed(Stack, KeyIndex, _children, BaseLocal, local);
        Posed = local;
    }

    public override EditRecord Commit()
    {
        var record = new EditRecord($"{(CreatedKey ? "Key" : "Edit")} {Controller.Name} at frame {Frame}", _before, EditSnapshot.Take(Stack));
        _session.Record(record);
        return record;
    }

    public override void Cancel() => _session.Restore(_before);

    internal override Matrix4x4[] Worlds(out Transform[] locals)
    {
        locals = _session.Evaluator.LocalTransforms(Animation, Frame, out var worlds);
        return worlds;
    }

    internal override Object3D? FrameParent => _session.Evaluator.SpaceOf(Animation, Controller, Frame) ?? Controller.Parent3D;
}

/// <summary>
/// A drag that edits its controller's slot of the rigging context's pose
/// Every channel can be edited.
/// </summary>
public sealed class RestPoseDrag : TransformDrag
{
    private readonly PoseEditSession _session;
    private readonly EditSnapshot _before;
    private readonly (Object3D Child, Transform Local)[] _children;

    internal RestPoseDrag(PoseEditSession session, Object3D controller, EditSnapshot before,
        (Object3D Child, Transform Local)[] children)
        : base(controller, session.RestPose.GetLocal(controller))
    {
        _session = session;
        _before = before;
        _children = children;
        CaptureParentWorld(_session.Evaluator);
    }

    public override bool CanTranslate => true;
    public override bool CanScale => IsScalable(Controller);

    public override void Set(Transform local, AnimChannels channels)
    {
        _session.RestPose.SetLocal(Controller, local);
        if (_children.Length > 0) _session.RestPose.KeepChildrenFixed(Controller, BaseLocal, local, _children);
        // An animated camera's looker target is derived from it in the rest pose too: the app keeps
        // the target at the camera's look point.
        if (Controller is CameraAnimated camera) _session.RestPose.DeriveLooker(camera);
        _session.Pose.ReloadRestPose(_session.Character);
        Posed = local;
    }

    public override EditRecord Commit()
    {
        var record = new EditRecord($"Edit {Controller.Name} in the rest pose", _before, _session.TakeRestPoseSnapshot());
        _session.Record(record);
        return record;
    }

    public override void Cancel() => _session.Restore(_before);

    internal override Matrix4x4[] Worlds(out Transform[] locals)
    {
        locals = _session.Evaluator.LocalTransforms(null, 0, out var worlds);
        return worlds;
    }

    internal override Object3D? FrameParent => Controller.Parent3D;
}

/// <summary>
/// A drag in Paint &amp; Check's runtime pose (<see cref="PoseEvaluator.HasCheckPose"/>): it poses
/// the controller so the bound skin can be checked, and writes nothing to the scene, so it has no
/// undo step. The app keeps nothing of that pose either.
/// </summary>
public sealed class CheckPoseDrag : TransformDrag
{
    private readonly PoseEditSession _session;
    private readonly int _index;
    private readonly Transform?[]? _before;
    private readonly (Object3D Child, Transform Local)[] _children;

    internal CheckPoseDrag(PoseEditSession session, Object3D controller, (Object3D Child, Transform Local)[] children)
        : base(controller, session.Evaluator.CheckLocal(session.Evaluator.IndexOf(controller)))
    {
        _session = session;
        _index = session.Evaluator.IndexOf(controller);
        _before = session.Evaluator.CheckPoseValues();
        _children = children;
        CaptureParentWorld(_session.Evaluator);
    }

    public override bool CanTranslate => true;
    public override bool CanScale => IsScalable(Controller);

    /// <summary>An IK or reverse-foot controller whose whole chain is posable swivels it, as at a key.</summary>
    public override bool CanSwivel => SwivelEnds() != null;

    /// <summary>The chain's first joint and the joint the swivel turns about (the ankle for a reverse foot).</summary>
    private (ControllerIK Ik, Object3D Root, Object3D End)? SwivelEnds()
    {
        if (Controller is not ControllerIK ik) return null;
        var chain = ik.Chain;
        bool foot = ik is ControllerReverseFoot;
        if (chain.Count < (foot ? 4 : 2) || chain.Any(j => _session.Evaluator.IndexOf(j) < 0)) return null;
        return (ik, chain[0], chain[foot ? 2 : chain.Count - 1]);
    }

    private Transform?[]? _swivelBase;
    private bool _swivelStarted;

    public override void Swivel(float angle)
    {
        if (!_swivelStarted) { _swivelBase = _session.Evaluator.CheckPoseValues(); _swivelStarted = true; }
        else _session.Evaluator.RestoreCheckPose(_swivelBase);
        SwivelOnce(angle);
    }

    /// <summary>
    /// <see cref="KeyEditor.SwivelLocals"/> on the check pose: the same swing a key gets, then the
    /// other IK controllers follow the moved chain while this one stays where it is.
    /// </summary>
    internal override void SwivelOnce(float angle)
    {
        if (SwivelEnds() is not var (ik, root, end)) return;
        var evaluator = _session.Evaluator;
        var local = evaluator.LocalTransforms(null, 0, out var world);
        // The angle comes in the left-side convention; a right-side chain turns the other way.
        if (!KeyEditor.SwivelLocals(evaluator, root, end, local, world, angle * SpinnerDial.SwivelSide(ik),
                out var rootLocal, out var endLocal))
            return;
        evaluator.SetCheckLocal(evaluator.IndexOf(root), rootLocal);
        evaluator.SetCheckLocal(evaluator.IndexOf(end), endLocal);
        evaluator.FollowCheckPose(root, keepInPlace: ik);
    }

    /// <summary>A member of a <see cref="GroupDrag"/>, which puts the pose back once per update itself.</summary>
    internal bool Grouped { get; private set; }

    internal CheckPoseDrag WithGrouped()
    {
        Grouped = true;
        return this;
    }

    public override void Set(Transform local, AnimChannels channels)
    {
        // Each update starts from the pose before the drag, so what follows the object is not
        // compounded across updates. In a group the group does that once for all its members.
        var evaluator = _session.Evaluator;
        if (!Grouped) evaluator.RestoreCheckPose(_before);
        evaluator.SetCheckLocal(_index, local);
        // Keep children fixed: the same per-child local the key and rest-pose drags write.
        foreach (var (child, childLocal) in _children)
            evaluator.SetCheckLocal(evaluator.IndexOf(child), KeyEditor.KeptChildLocal(childLocal, BaseLocal, local));
        evaluator.FollowCheckPose(Controller);
        Posed = local;
    }

    /// <summary>One undo step that puts the check pose back (the document itself is unchanged).</summary>
    public override EditRecord Commit()
    {
        var evaluator = _session.Evaluator;
        var before = EditSnapshot.Take(_session.Character, Array.Empty<AnimStack>());
        before.CheckPose = (evaluator, _before);
        var after = EditSnapshot.Take(_session.Character, Array.Empty<AnimStack>());
        after.CheckPose = (evaluator, evaluator.CheckPoseValues());
        var record = new EditRecord($"Posed {Controller.Name} for Paint & Check", before, after);
        _session.Record(record);
        return record;
    }

    public override void Cancel() => _session.Evaluator.RestoreCheckPose(_before);

    internal override Matrix4x4[] Worlds(out Transform[] locals)
    {
        locals = _session.Evaluator.LocalTransforms(null, 0, out var worlds);
        return worlds;
    }

    internal override Object3D? FrameParent => Controller.Parent3D;
}

/// <summary>
/// A drag of a key's fade handle: each
/// <see cref="Update"/> sets the length at the press moved by the frames dragged
/// (<see cref="KeyEditor.DraggedFade"/>), and the drag is one undo record.
/// </summary>
public sealed class FadeDrag
{
    private readonly PoseEditSession _session;
    private readonly EditSnapshot _before;

    internal FadeDrag(PoseEditSession session, AnimStack stack, int keyIndex, bool fadeIn)
    {
        _session = session;
        _before = EditSnapshot.Take(stack);
        Stack = stack;
        KeyIndex = keyIndex;
        FadeIn = fadeIn;
        PressFade = fadeIn ? stack.Keys[keyIndex].FadeIn : stack.Keys[keyIndex].FadeOut;
        Fade = PressFade;
    }

    public AnimStack Stack { get; }
    public int KeyIndex { get; }
    public bool FadeIn { get; }

    /// <summary>The fade length when the drag started.</summary>
    public int PressFade { get; }

    /// <summary>The fade length the drag has set.</summary>
    public int Fade { get; private set; }

    /// <summary>Sets the fade for a drag of <paramref name="deltaFrames"/> frames from the press. Returns the length set.</summary>
    public int Update(int deltaFrames)
    {
        Fade = _session.Editor.SetKeyFade(Stack, KeyIndex, FadeIn, KeyEditor.DraggedFade(PressFade, FadeIn, deltaFrames));
        return Fade;
    }

    /// <summary>Ends the drag, keeping the change, and records it for undo; null when the length did not change.</summary>
    public EditRecord? Commit()
    {
        if (Fade == PressFade) return null;
        var record = new EditRecord($"Set the fade-{(FadeIn ? "in" : "out")} of key {KeyIndex} in '{Stack.Name}' to {Fade}", _before, EditSnapshot.Take(Stack));
        _session.Record(record);
        return record;
    }

    /// <summary>Ends the drag, putting the fade back as it was.</summary>
    public void Cancel() => _session.Restore(_before);
}

/// <summary>
/// A Curveboard drag: every <see cref="Update"/> puts the stack back
/// as it was at the press and applies the edit for the whole drag so far, so a drag never
/// accumulates rounding. The drag is one undo record.
/// </summary>
public sealed class CurveDrag
{
    private readonly PoseEditSession _session;
    private readonly EditSnapshot _before;
    private bool _changed;

    internal CurveDrag(PoseEditSession session, AnimStack stack, string description)
    {
        _session = session;
        _before = EditSnapshot.Take(stack);
        Stack = stack;
        Description = description;
    }

    public AnimStack Stack { get; }
    public string Description { get; }

    /// <summary>Puts the press state back and runs <paramref name="edit"/> with the session's key editor.</summary>
    public void Update(Action<KeyEditor> edit)
    {
        _session.Restore(_before);
        edit(_session.Editor);
        _session.Editor.Evaluator.Invalidate();
        _changed = true;
    }

    /// <summary>Ends the drag, keeping the change, and records it for undo; null if nothing was applied.</summary>
    public EditRecord? Commit()
    {
        if (!_changed) return null;
        var record = new EditRecord(Description, _before, EditSnapshot.Take(Stack));
        _session.Record(record);
        return record;
    }

    public void Cancel() => _session.Restore(_before);
}

/// <summary>
/// A drag of several controllers:
/// the lead is posed as a single drag would pose it, and every member then undergoes the lead's
/// world-space change, members being posed ancestors first so that each one's local transform is
/// taken against its parent's new world. The whole drag is one undo record.
/// </summary>
public sealed class GroupDrag : TransformDrag
{
    private readonly PoseEditSession _session;
    private readonly TransformDrag _lead;
    private readonly TransformDrag[] _members;
    private readonly Matrix4x4[] _startWorlds;
    private readonly Matrix4x4[] _startParentWorlds;
    private readonly Vector3[] _startParentScales;
    private readonly Matrix4x4 _leadFrameWorld;
    private readonly Vector3 _leadFrameScale;
    private readonly bool _leadFollows;
    private readonly EditSnapshot _before;
    private readonly Func<EditSnapshot> _after;
    private readonly string _where;

    internal GroupDrag(PoseEditSession session, TransformDrag lead, IReadOnlyList<TransformDrag> followers,
        EditSnapshot before, Func<EditSnapshot> after, string where)
        : base(lead.Controller, lead.BaseLocal)
    {
        _session = session;
        _lead = lead;
        _before = before;
        _after = after;
        _where = where;
        // The group's space change is the lead's, converted in the lead's frame.
        ParentWorldMatrix = lead.ParentWorldMatrix;
        ParentWorldScale = lead.ParentWorldScale;
        _members = followers.Append(lead).OrderBy(d => Depth(d.Controller)).ToArray();
        var worlds = lead.Worlds(out var locals);
        _startWorlds = _members.Select(d => WorldOf(d.Controller, worlds)).ToArray();
        _startParentWorlds = _members.Select(d => d.ParentWorldMatrix).ToArray();
        _startParentScales = _members.Select(d => d.ParentWorldScale).ToArray();
        var frame = lead.FrameParent;
        _leadFrameWorld = WorldOf(frame, worlds);
        _leadFrameScale = ScaleOf(frame, locals);
        // When a member is above the lead, posing that member moves the lead too: the lead is then
        // placed like the others instead of being given the drag's local transform directly.
        _leadFollows = followers.Any(f => IsAncestorOrSelf(f.Controller, lead.Controller.Parent3D) || IsAncestorOrSelf(f.Controller, frame));
    }

    /// <summary>The lead (the current object), whose frame the drag works in.</summary>
    public TransformDrag Lead => _lead;

    /// <summary>Every posed controller's drag, ancestors first, the lead included.</summary>
    public IReadOnlyList<TransformDrag> Members => _members;

    public override bool CanTranslate => _lead.CanTranslate;
    public override bool CanScale => _lead.CanScale;

    public override bool CanSwivel => _members.Any(m => m.CanSwivel);

    private EditSnapshot? _swivelBase;

    /// <summary>Every member that is an IK controller swings its own chain by the same angle.</summary>
    public override void Swivel(float angle)
    {
        if (_swivelBase == null) _swivelBase = _after();
        else _session.Restore(_swivelBase);
        foreach (var member in _members)
            if (member.CanSwivel) member.SwivelOnce(angle);
    }

    public override void Set(Transform local, AnimChannels channels)
    {
        ResetEachUpdate?.Invoke();
        var leadNew = WorldComposition.Compose(local.ToMatrix(), _leadFrameWorld, _leadFrameScale, _lead.CompensatesNow);
        Matrix4x4.Invert(_startWorlds[Array.IndexOf(_members, _lead)], out var leadStartInverse);
        var change = leadStartInverse * leadNew;
        // Turning or scaling about another object's pivot also moves a member, and scaling a
        // rotated member can turn it once its shear is dropped.
        var moved = channels | AnimChannels.Translate | ((channels & AnimChannels.Scale) != 0 ? AnimChannels.Rotate : 0);
        for (int k = 0; k < _members.Length; k++)
        {
            var member = _members[k];
            if (member == _lead && !_leadFollows) { member.Set(local, channels); continue; }
            var worlds = member.Worlds(out var locals);
            var parent = member.FrameParent;
            if (!WorldComposition.TryDecompose(_startWorlds[k] * change, WorldOf(parent, worlds), ScaleOf(parent, locals),
                    member.CompensatesNow, out var m))
                continue;
            member.Set(Transform.FromMatrixLoose(m), moved);
        }
        Posed = local;
    }

    /// <summary>
    /// akeytsu's group drag: every member undergoes the current object's world-space change about
    /// the current object's pivot (<see cref="Set"/>). Off by default: a gizmo or Spinner drag then
    /// gives every member the same change on the same axis about its own pivot - in the Local space
    /// about its own axes, like the TRANSFORM fields - so turning X on three finger joints curls all three.
    /// </summary>
    public bool AkeytsuParity { get; set; }

    public override void Rotate(int axis, float angle)
    {
        if (AkeytsuParity) { base.Rotate(axis, angle); return; }
        EachMember(m => m.Rotate(axis, angle), carryParent: true);
    }

    public override void Translate(int axis, float distance)
    {
        if (AkeytsuParity) { base.Translate(axis, distance); return; }
        if (!CanTranslate) return;
        EachMember(m => m.Translate(axis, distance), carryParent: false);
    }

    public override void TranslatePlane(int normal, float first, float second)
    {
        if (AkeytsuParity) { base.TranslatePlane(normal, first, second); return; }
        if (!CanTranslate) return;
        EachMember(m => m.TranslatePlane(normal, first, second), carryParent: false);
    }

    public override void Scale(int axis, float factor)
    {
        if (AkeytsuParity) { base.Scale(axis, factor); return; }
        if (!CanScale) return;
        EachMember(m => m.Scale(axis, factor), carryParent: true);
    }

    /// <summary>
    /// Run before every update of a pose whose members' own updates would each go back to the
    /// drag's start (Paint &amp; Check's runtime pose): the group goes back once, then poses every member.
    /// </summary>
    internal Action? ResetEachUpdate { get; set; }

    /// <summary>
    /// Applies one drag operation to every member on its own, ancestors first.
    /// Local space edits each member's own channel about its own axes, so a selected child also
    /// follows its selected parent (a chain curls).
    /// World and Screen turn or scale each member about the space's axis through its own pivot, on
    /// top of what its selected parent now hands down (<paramref name="carryParent"/>): the same
    /// world direction for every bone, so a chain curls in that direction as it does in Local.
    /// A move instead gives every member the same world offset from where it started, so a
    /// selected child is not moved twice.
    /// </summary>
    private void EachMember(Action<TransformDrag> operation, bool carryParent)
    {
        ResetEachUpdate?.Invoke();
        for (int k = 0; k < _members.Length; k++)
        {
            var member = _members[k];
            member.Space = Space;
            member.SpaceFrame = SpaceFrame;
            if (Space == TransformSpace.Local) { operation(member); continue; }
            var worlds = member.Worlds(out var locals);
            var parent = member.FrameParent;
            var parentNow = WorldOf(parent, worlds);
            var parentScaleNow = ScaleOf(parent, locals);
            if (carryParent)
            {
                // The member's world-space edit starts from where its parent now puts it.
                member.ParentWorldMatrix = parentNow;
                member.ParentWorldScale = parentScaleNow;
                operation(member);
                continue;
            }
            member.ParentWorldMatrix = _startParentWorlds[k];
            member.ParentWorldScale = _startParentScales[k];
            operation(member);
            if (parentNow == _startParentWorlds[k] && parentScaleNow == _startParentScales[k]) continue;
            // Re-express the moved start world against the parent's new world.
            var world = WorldComposition.Compose(member.Posed.ToMatrix(), _startParentWorlds[k],
                _startParentScales[k], member.CompensatesNow);
            if (WorldComposition.TryDecompose(world, parentNow, parentScaleNow, member.CompensatesNow, out var m))
                member.Set(member.Posed with { Translation = Transform.FromMatrixLoose(m).Translation }, AnimChannels.Translate);
        }
        Posed = _lead.Posed;
    }

    public override EditRecord Commit()
    {
        var record = new EditRecord($"Edit {_members.Length} controllers {_where}", _before, _after());
        _session.Record(record);
        return record;
    }

    public override void Cancel() => _session.Restore(_before);

    internal override Matrix4x4[] Worlds(out Transform[] locals) => _lead.Worlds(out locals);

    internal override Object3D? FrameParent => _lead.FrameParent;

    internal override bool CompensatesNow => _lead.CompensatesNow;

    private Matrix4x4 WorldOf(Object3D? o, Matrix4x4[] worlds) =>
        o == null ? Matrix4x4.Identity : _session.Evaluator.IndexOf(o) is var i and >= 0 ? worlds[i] : o.WorldMatrix;

    private Vector3 ScaleOf(Object3D? o, Transform[] locals) =>
        o == null ? Vector3.One : _session.Evaluator.IndexOf(o) is var i and >= 0 ? locals[i].Scale : Object3D.LocalScale(o.LocalMatrix);

    private static int Depth(Object3D o)
    {
        int depth = 0;
        for (var p = o.Parent3D; p != null; p = p.Parent3D) depth++;
        return depth;
    }

    private static bool IsAncestorOrSelf(Object3D ancestor, Object3D? o)
    {
        for (; o != null; o = o.Parent3D)
            if (ReferenceEquals(o, ancestor)) return true;
        return false;
    }
}

/// <summary>A committed edit: what it changed, before and after.</summary>
public sealed record EditRecord(string Description, EditSnapshot Before, EditSnapshot After);

/// <summary>
/// Undo and redo of committed edits. Each step keeps only the side that is not on screen: an
/// undoable step its Before, a redoable step its After; the other side is dropped and captured
/// again from the document when the step is undone or redone. Past <see cref="MemoryBudget"/> the
/// oldest steps move to a temporary spill file and are read back when needed; steps are dropped
/// only past <see cref="Capacity"/> or once the spill file would pass <see cref="DiskBudget"/>.
/// </summary>
public sealed class EditHistory : IDisposable
{
    private readonly List<EditRecord> _done = [];
    private readonly List<EditRecord> _undone = [];
    // The side the caller restores after Undo/Redo; it stays in memory until the next step.
    private EditSnapshot? _restored;
    private SnapshotSpillFile? _spill;
    private bool _spillFailed;

    public int Capacity { get; init; } = 100;

    /// <summary>
    /// The most memory the undo steps may hold together (<see cref="EditSnapshot.EstimatedBytes"/>);
    /// past it the oldest steps move to the spill file. The newest step always stays in memory.
    /// </summary>
    public long MemoryBudget { get; init; } = 512L << 20;

    /// <summary>The most the spill file may hold; past it the oldest steps are dropped.</summary>
    public long DiskBudget { get; init; } = 8L << 30;

    /// <summary>The folder of the spill file (the temp folder by default).</summary>
    public string? SpillFolder { get; init; }

    /// <summary>The memory held by every undo and redo step.</summary>
    public long EstimatedBytes => Records().Sum(r => (r.Before?.EstimatedBytes ?? 0) + (r.After?.EstimatedBytes ?? 0));

    /// <summary>The bytes of undo and redo steps that live in the spill file.</summary>
    public long SpilledBytes => Records().Sum(r => (r.Before?.SpilledBytes ?? 0) + (r.After?.SpilledBytes ?? 0));

    /// <summary>The spill file's length, dropped steps included until it is compacted.</summary>
    public long SpillFileBytes => _spill?.Length ?? 0;

    private IEnumerable<EditRecord> Records() => _done.Concat(_undone);

    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;

    /// <summary>The steps held, undoable and redoable together.</summary>
    public int Count => _done.Count + _undone.Count;
    public EditRecord? NextUndo => _done.Count > 0 ? _done[^1] : null;
    public EditRecord? NextRedo => _undone.Count > 0 ? _undone[^1] : null;

    public void Push(EditRecord record)
    {
        var (before, after) = record.Before is null || record.After is null
            ? (record.Before, record.After)
            : EditSnapshot.Trim(record.Before, record.After);
        _done.Add(ReferenceEquals(before, record.Before) ? record : record with { Before = before, After = after });
        _undone.Clear();
        if (_done.Count > Capacity) _done.RemoveAt(0);
        SettleRestored(null);
        // The document is in the After state now: that side is not needed until this step is undone.
        ReleaseIfUnneeded(after);
        Enforce();
        Pushed?.Invoke(_done.Count > 0 ? _done[^1] : record);
    }

    /// <summary>Raised after a step was recorded (the document is in its After state).</summary>
    public event Action<EditRecord>? Pushed;

    /// <summary>The edit to undo (its <see cref="EditRecord.Before"/> is to be restored), or null.</summary>
    public EditRecord? Undo()
    {
        if (_done.Count == 0) return null;
        var record = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        _undone.Add(record);
        // The document is in this step's After state: capture it again if it was dropped.
        record.After?.Recapture();
        SettleRestored(record.Before);
        Enforce();
        return record;
    }

    /// <summary>The edit to redo (its <see cref="EditRecord.After"/> is to be restored), or null.</summary>
    public EditRecord? Redo()
    {
        if (_undone.Count == 0) return null;
        var record = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        _done.Add(record);
        record.Before?.Recapture();
        SettleRestored(record.After);
        Enforce();
        return record;
    }

    /// <summary>
    /// Drops <paramref name="record"/> from the redo end, for an edit that was only a preview (a
    /// TRANSFORM drag step undone again): it must not come back with Redo.
    /// </summary>
    public bool ForgetRedo(EditRecord record)
    {
        if (_undone.Count == 0 || !ReferenceEquals(_undone[^1], record)) return false;
        _undone.RemoveAt(_undone.Count - 1);
        return true;
    }

    public void Clear()
    {
        _done.Clear();
        _undone.Clear();
        _restored = null;
        _spill?.Dispose();
        _spill = null;
    }

    public void Dispose() => Clear();

    // The side restored by the previous Undo/Redo is on screen now; drop it unless a step still needs it.
    private void SettleRestored(EditSnapshot? next)
    {
        var previous = _restored;
        _restored = next;
        if (previous != null && !ReferenceEquals(previous, next)) ReleaseIfUnneeded(previous);
    }

    private void ReleaseIfUnneeded(EditSnapshot? snapshot)
    {
        if (snapshot == null || ReferenceEquals(snapshot, _restored)) return;
        foreach (var r in _done) if (ReferenceEquals(r.Before, snapshot)) return;
        foreach (var r in _undone) if (ReferenceEquals(r.After, snapshot)) return;
        snapshot.Release();
    }

    /// <summary>Moves the oldest steps to the spill file past the memory budget, drops the oldest past the disk budget.</summary>
    private void Enforce()
    {
        long inMemory = EstimatedBytes;
        if (inMemory > MemoryBudget)
        {
            // Oldest first: the undo end from its bottom, then the redo end from its far end; the
            // next undo and the next redo stay in memory.
            var candidates = _done.Take(_done.Count - 1).Select(r => r.Before)
                .Concat(_undone.Take(Math.Max(0, _undone.Count - 1)).Select(r => r.After));
            foreach (var snapshot in candidates)
            {
                if (inMemory <= MemoryBudget) break;
                // A spilled step keeps its entry overhead in memory, so test for unspilled fields, not zero bytes.
                if (snapshot == null || !snapshot.HasFieldsInMemory) continue;
                var file = SpillFile();
                if (file == null) break;
                long held = snapshot.EstimatedBytes;
                snapshot.SpillTo(file);
                inMemory -= held - snapshot.EstimatedBytes;
            }
            // Without a spill file the budget is kept the old way: drop the oldest steps.
            if (_spill == null)
                while (_done.Count > 1 && inMemory > MemoryBudget)
                {
                    inMemory -= Size(_done[0]);
                    _done.RemoveAt(0);
                }
        }
        if (_spill == null) return;
        long spilled = SpilledBytes;
        while (_done.Count > 1 && spilled > DiskBudget)
        {
            spilled -= (_done[0].Before?.SpilledBytes ?? 0) + (_done[0].After?.SpilledBytes ?? 0);
            _done.RemoveAt(0);
        }
        // Dropped and recaptured steps leave dead bytes behind: rewrite the file once they are most of it.
        if (_spill.Length > 64L << 20 && _spill.Length > 2 * spilled) Compact();
    }

    private static long Size(EditRecord r) => (r.Before?.EstimatedBytes ?? 0) + (r.After?.EstimatedBytes ?? 0);

    private SnapshotSpillFile? SpillFile()
    {
        if (_spill != null || _spillFailed) return _spill;
        try
        {
            _spill = new SnapshotSpillFile(SpillFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _spillFailed = true;
        }
        return _spill;
    }

    private void Compact()
    {
        var old = _spill!;
        SnapshotSpillFile fresh;
        try
        {
            fresh = new SnapshotSpillFile(SpillFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var r in Records())
        {
            r.Before?.SpillTo(fresh, alreadySpilled: true);
            r.After?.SpillTo(fresh, alreadySpilled: true);
        }
        _spill = fresh;
        old.Dispose();
    }
}

/// <summary>
/// The fields of some objects and every object below them, restored in place so that the
/// scene's wrappers stay valid. Edits only change field values of these objects: a key edit those
/// of one stack and its tracks, a rest-pose edit those of the character's poses (and, when
/// animations are converted, its stacks). The fields are held encoded (<see cref="AktFieldSnapshot"/>),
/// a few bytes per value instead of a copied value tree.
/// </summary>
public sealed class EditSnapshot
{
    private sealed class Entry(AktObject target, AktFieldSnapshot fields, AktObject[] children)
    {
        public AktObject Target { get; } = target;
        public AktFieldSnapshot? Fields { get; set; } = fields;
        public AktObject[]? Children { get; set; } = children;
        /// <summary>Captured by sharing the values (see <see cref="SharingEntry"/>) rather than encoding them.</summary>
        public bool Shared => Fields?.IsShared ?? _sharedCapture;
        private bool _sharedCapture;

        public void Release()
        {
            _sharedCapture = Shared;
            Fields = null;
            Children = null;
        }
    }

    private readonly Entry[] _objects;
    private readonly (Mesh Mesh, VertexWeight[][] Smooth, VertexWeight[][] Rigid)[] _weights;
    private readonly (Mesh Mesh, uint[] Flags)[] _hidden;
    private readonly (Mesh Mesh, Matrix4x4 Position, float Distance)[] _pointOfView;
    private readonly uint? _hiddenCounter;
    private readonly bool _shareLargeArrays;
    private readonly bool _wholeDocument;
    private bool _released;

    private EditSnapshot(Character? owner, bool restPose, Entry[] objects,
        bool shareLargeArrays = false,
        (Mesh, VertexWeight[][], VertexWeight[][])[]? weights = null,
        (Mesh, uint[])[]? hidden = null,
        (Mesh, Matrix4x4, float)[]? pointOfView = null,
        bool wholeDocument = false)
    {
        Owner = owner;
        RestPose = restPose;
        _objects = objects;
        _weights = weights ?? [];
        _hidden = hidden ?? [];
        _pointOfView = pointOfView ?? [];
        _hiddenCounter = owner?.HiddenVerticesCounter;
        _shareLargeArrays = shareLargeArrays;
        _wholeDocument = wholeDocument;
    }

    private EditSnapshot(EditSnapshot source, Entry[] objects)
    {
        Owner = source.Owner;
        RestPose = source.RestPose;
        _objects = objects;
        _weights = source._weights;
        _hidden = source._hidden;
        _pointOfView = source._pointOfView;
        _hiddenCounter = source._hiddenCounter;
        _shareLargeArrays = source._shareLargeArrays;
        _wholeDocument = source._wholeDocument;
        CheckPose = source.CheckPose;
    }

    /// <summary>
    /// Paint &amp; Check's runtime pose to put back with this snapshot (it lives in the evaluator, not
    /// in the document): the evaluator and its check-pose values, null values for none.
    /// </summary>
    public (PoseEvaluator Evaluator, Transform?[]? Values)? CheckPose { get; set; }

    /// <summary>The character whose evaluation must be refreshed after a restore.</summary>
    public Character? Owner { get; }

    /// <summary>Whether the snapshot holds rest poses, so that restoring it moves the rest pose and the skin's bind matrices.</summary>
    public bool RestPose { get; }

    /// <summary>The objects whose fields and child lists this snapshot holds.</summary>
    public IEnumerable<AktObject> Targets => _objects.Select(e => e.Target);

    public static EditSnapshot Take(AnimStack stack) =>
        new(stack.Animation?.Character, false, Copy([stack]));

    /// <summary>A snapshot of several stacks of <paramref name="owner"/>.</summary>
    public static EditSnapshot Take(Character owner, IEnumerable<AnimStack> stacks) =>
        new(owner, false, Copy(stacks));

    /// <summary>A snapshot of <paramref name="objects"/> (and everything below them), which include rest poses of <paramref name="owner"/>.</summary>
    public static EditSnapshot TakeRestPose(Character owner, IEnumerable<SceneObject> objects) =>
        new(owner, true, Copy(objects));

    /// <summary>
    /// A snapshot of the fields and child lists of <paramref name="objects"/> only, for edits that
    /// attach or detach whole sub-trees without changing them (the Anim Bank and the Mixer).
    /// </summary>
    public static EditSnapshot TakeShallow(Character owner, IEnumerable<SceneObject> objects) =>
        new(owner, false, objects.DistinctBy(o => (object)o.Source, ReferenceEqualityComparer.Instance)
            .Select(o => Encoded(o.Source, shareLargeArrays: false)).ToArray());

    /// <summary>
    /// A snapshot for a structural rig edit (create/delete/reparent joints, Add IK, Bind/Unbind):
    /// the scene root, every character and mesh node, and the whole of <paramref name="owner"/>'s
    /// subtree. Bulk data arrays are shared rather than copied - a rig edit changes lists, poses
    /// and parents, not geometry - so one edit does not duplicate every vertex of a 90 MB scene.
    /// </summary>
    public static EditSnapshot TakeRig(Character owner, Scene scene)
    {
        var nodes = new List<SceneObject> { scene };
        nodes.AddRange(scene.Meshes);
        // Every character's subtree: Unbind Skin can detach another character's mesh, and its
        // parent node's child list has to be restorable too.
        foreach (var character in scene.Characters)
            foreach (var o in character.Source.PreOrder())
                nodes.Add(character.Model.Wrap(o));
        foreach (var o in owner.Source.PreOrder()) nodes.Add(owner.Model.Wrap(o));
        return new EditSnapshot(owner, true,
            nodes.DistinctBy(o => (object)o.Source, ReferenceEqualityComparer.Instance)
                .Select(o => SharingEntry(o.Source))
                .ToArray(),
            shareLargeArrays: true);
    }

    /// <summary>
    /// A snapshot of every node of the document, for an edit that can touch any object's references
    /// (deleting whole objects). Bulk arrays are shared as in <see cref="TakeRig"/>. <paramref name="owner"/>
    /// is the character to re-evaluate after a restore; it must exist on both sides of the edit.
    /// Animation tracks the edit removes from the document stay shared after <see cref="Trim"/>:
    /// nothing can reach them any more, so the step keeps the removed tracks themselves.
    /// </summary>
    public static EditSnapshot TakeScene(Character? owner, Scene scene) =>
        new(owner, false,
            scene.Model.Document.AllObjects()
                .Distinct(ReferenceEqualityComparer.Instance).Cast<AktObject>()
                .Select(SharingEntry)
                .ToArray(),
            shareLargeArrays: true, wholeDocument: true);

    private static Entry[] Copy(IEnumerable<SceneObject> objects) =>
        objects.DistinctBy(o => (object)o.Source, ReferenceEqualityComparer.Instance)
            .SelectMany(o => o.Source.PreOrder()).Select(o => Encoded(o, shareLargeArrays: false)).ToArray();

    private static Entry Encoded(AktObject o, bool shareLargeArrays) =>
        new(o, AktFieldSnapshot.Capture(o, shareLargeArrays), o.Children.ToArray());

    /// <summary>
    /// A snapshot of the skin weight arrays of some meshes, for the Skin Workshop's edits: only the
    /// two weight fields and the hidden-vertex bits are copied, so a stroke on a 90 MB scene does
    /// not duplicate its geometry.
    /// </summary>
    public static EditSnapshot TakeWeights(Character? owner, IEnumerable<Mesh> meshes) =>
        new(owner, false, [],
            weights: meshes.Distinct()
                .Select(m => (m, m.SkinWeights, m.RigidSkinWeights))
                .ToArray(),
            hidden: meshes.Distinct().Select(m => (m, m.HiddenVertexFlags)).ToArray());

    /// <summary>
    /// A snapshot of the two blendshape point-of-view members of some meshes, for the Blendshapes
    /// panel's "Set point of view": that edit writes nothing else, and a mesh's geometry must not be
    /// copied to undo two floats and a matrix.
    /// </summary>
    public static EditSnapshot TakePointOfView(Character? owner, IEnumerable<Mesh> meshes) =>
        new(owner, false, [],
            pointOfView: meshes.Distinct()
                .Select(m => (m, m.BlendshapeCameraPositionFromAnchor, m.BlendshapeCameraDistanceFromAnchor))
                .ToArray());

    public void Restore()
    {
        if (_released)
            throw new InvalidOperationException("This undo step's state was dropped while it was on screen and has not been captured again.");
        if (_weights.Length > 0)
        {
            foreach (var (mesh, smooth, rigid) in _weights)
            {
                RestoreArray(mesh, smooth, rigid: false);
                RestoreArray(mesh, rigid, rigid: true);
            }
        }
        foreach (var (mesh, flags) in _hidden)
            mesh.Source.Set("SavedSelectedVerticesToHide", flags.Select(v => (object?)v).ToList());
        foreach (var (mesh, position, distance) in _pointOfView)
        {
            mesh.BlendshapeCameraPositionFromAnchor = position;
            mesh.BlendshapeCameraDistanceFromAnchor = distance;
        }
        if (_hiddenCounter is { } counter && Owner != null) Owner.HiddenVerticesCounter = counter;
        foreach (var entry in _objects)
        {
            entry.Fields!.RestoreInto(entry.Target);
            if (!entry.Target.Children.SequenceEqual(entry.Children!)) entry.Target.SetChildren(entry.Children!);
        }
    }

    /// <summary>Writes one weight array of a mesh back, through the writer so the document owns the values.</summary>
    private void RestoreArray(Mesh mesh, VertexWeight[][] values, bool rigid)
    {
        if (values.Length == 0) return;
        var table = new SkinWeightTable(mesh, rigid, Owner);
        for (int v = 0; v < table.VertexCount; v++)
            table.Replace(v, v < values.Length ? values[v].Select(w => (w.Bone, w.Weight)) : []);
        table.Write();
    }

    /// <summary>
    /// Drops the captured fields, keeping which objects they belong to: the history calls this for
    /// the side of a step that is on screen, and <see cref="Recapture"/> reads them back from the
    /// document before that side is needed. Weight and point-of-view snapshots keep their data.
    /// </summary>
    public void Release()
    {
        if (_released || _objects.Length == 0) return;
        foreach (var entry in _objects) entry.Release();
        _released = true;
    }

    /// <summary>Captures the fields again from the document after <see cref="Release"/>; the document must be in this side's state.</summary>
    public void Recapture()
    {
        if (!_released) return;
        foreach (var entry in _objects)
        {
            entry.Fields = entry.Shared
                ? AktFieldSnapshot.CaptureShared(entry.Target)
                : AktFieldSnapshot.Capture(entry.Target, _shareLargeArrays);
            entry.Children = entry.Target.Children.ToArray();
        }
        _released = false;
    }

    /// <summary>Whether the fields were dropped by <see cref="Release"/> and not captured again.</summary>
    public bool IsReleased => _released;

    /// <summary>
    /// Moves the encoded fields held in memory into <paramref name="file"/>; a restore reads them
    /// back. With <paramref name="alreadySpilled"/> it moves instead the fields that already live in
    /// another spill file (compaction).
    /// </summary>
    public void SpillTo(SnapshotSpillFile file, bool alreadySpilled = false)
    {
        foreach (var entry in _objects)
            if (entry.Fields is { IsShared: false } fields && fields.IsSpilled == alreadySpilled)
                fields.SpillTo(file);
    }

    /// <summary>Whether any encoded fields are still in memory (so <see cref="SpillTo"/> would free something).</summary>
    public bool HasFieldsInMemory
    {
        get
        {
            foreach (var entry in _objects)
                if (entry.Fields is { IsShared: false, IsSpilled: false }) return true;
            return false;
        }
    }

    /// <summary>
    /// An entry of <see cref="TakeRig"/>/<see cref="TakeScene"/>. Animation tracks are held shallowly
    /// (their field values shared, not copied): no rig or scene edit changes a key in place, and
    /// <see cref="Trim"/> drops every track an edit left alone before the record is kept, encoding
    /// the few it did change. Copying every key of a large scene twice per edit was most of the time
    /// and memory of a rig edit.
    /// </summary>
    private static Entry SharingEntry(AktObject o) =>
        o.TypeName.StartsWith("class CAnimTrack", StringComparison.Ordinal)
            ? new Entry(o, AktFieldSnapshot.CaptureShared(o), o.Children.ToArray())
            : Encoded(o, shareLargeArrays: true);

    /// <summary>
    /// The two sides of a committed edit reduced to the objects it changed: an object kept on both
    /// sides with equal fields and children is dropped (undo is linear, so it is already in that
    /// state whenever this record is restored), and shared track entries that stay are encoded so a
    /// later key edit cannot reach them - except, in a whole-document snapshot, tracks the edit
    /// removed from the document, which nothing can reach. Only rig and scene snapshots are trimmed.
    /// </summary>
    public static (EditSnapshot Before, EditSnapshot After) Trim(EditSnapshot before, EditSnapshot after)
    {
        if (!before._shareLargeArrays || !after._shareLargeArrays || before._released || after._released) return (before, after);
        var afterByTarget = new Dictionary<AktObject, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < after._objects.Length; i++) afterByTarget.TryAdd(after._objects[i].Target, i);
        var keepAfter = new bool[after._objects.Length];
        Array.Fill(keepAfter, true);
        var keptBefore = new List<Entry>();
        foreach (var entry in before._objects)
        {
            if (afterByTarget.TryGetValue(entry.Target, out int j))
            {
                var other = after._objects[j];
                if (entry.Children!.SequenceEqual(other.Children!) && entry.Fields!.ContentEquals(other.Fields!))
                {
                    keepAfter[j] = false;
                    continue;
                }
                keptBefore.Add(Unshare(entry));
            }
            else
            {
                keptBefore.Add(before._wholeDocument ? entry : Unshare(entry));
            }
        }
        var keptAfter = new List<Entry>();
        for (int j = 0; j < after._objects.Length; j++)
            if (keepAfter[j]) keptAfter.Add(Unshare(after._objects[j]));
        return (new EditSnapshot(before, keptBefore.ToArray()), new EditSnapshot(after, keptAfter.ToArray()));
    }

    private static Entry Unshare(Entry entry) =>
        entry.Shared ? new Entry(entry.Target, entry.Fields!.Encode(shareLargeArrays: true), entry.Children!) : entry;

    /// <summary>The number of objects the snapshot restores (for tests and measurements).</summary>
    public int ObjectCount => _objects.Length;

    /// <summary>
    /// The managed memory this snapshot holds on its own (what <see cref="EditHistory.MemoryBudget"/>
    /// is measured in): the encoded fields that are not spilled, the entries and child lists, and
    /// the weight arrays. Shared values and bulk arrays belong to the document and are not counted.
    /// </summary>
    public long EstimatedBytes
    {
        get
        {
            long bytes = ArrayBytes + PointerBytes * (long)_objects.Length;
            foreach (var entry in _objects)
            {
                bytes += ObjectBytes + 24;
                if (entry.Children is { } children) bytes += ArrayBytes + PointerBytes * children.Length;
                if (entry.Fields is { } fields) bytes += fields.EstimatedBytes;
            }
            foreach (var (_, smooth, rigid) in _weights)
                bytes += WeightBytes(smooth) + WeightBytes(rigid);
            foreach (var (_, flags) in _hidden)
                bytes += ArrayBytes + sizeof(uint) * (long)flags.Length;
            return bytes;
        }
    }

    /// <summary>The encoded bytes of this snapshot that live in a spill file.</summary>
    public long SpilledBytes
    {
        get
        {
            long bytes = 0;
            foreach (var entry in _objects)
                if (entry.Fields is { IsSpilled: true } fields) bytes += fields.EncodedLength;
            return bytes;
        }
    }

    // 64-bit CLR object sizes: a header plus method-table pointer is 16 bytes; an array adds its
    // length (8) before the elements.
    private const long ObjectBytes = 16, ArrayBytes = 24, PointerBytes = 8;

    private static long WeightBytes(VertexWeight[][] weights)
    {
        long bytes = ArrayBytes + PointerBytes * (long)weights.Length;
        foreach (var w in weights) bytes += ArrayBytes + 8L * (w?.Length ?? 0);   // VertexWeight is (int, float)
        return bytes;
    }
}
