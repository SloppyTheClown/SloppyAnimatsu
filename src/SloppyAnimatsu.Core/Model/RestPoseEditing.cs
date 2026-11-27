using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>What to do with the animations after the reference pose changed.</summary>
public enum RestPoseResolution
{
    /// <summary>Rewrite the keys so that the animations look the same, then accept the new pose.</summary>
    ConvertAnimations,
    /// <summary>Keep the keys as they are and accept the new pose.</summary>
    KeepAnimations,
    /// <summary>Put the previous reference pose back.</summary>
    ReturnToPrevious,
}

/// <summary>
/// Editing of a character's rest pose in the rigging context, and converting its animations
/// afterwards.
/// </summary>
public sealed partial class RestPoseEditor
{
    /// <summary>Largest matrix element difference still counted as unchanged.</summary>
    public const float Tolerance = 1e-6f;

    public RestPoseEditor(Character character)
    {
        Character = character;
        Controllers = character.Controllers;
    }

    public Character Character { get; }
    public IReadOnlyList<Object3D> Controllers { get; }

    /// <summary>The pose shown and edited in the rigging context (<see cref="PoseEvaluator.RestPose"/>).</summary>
    public CharacterPose? EditedPose => PoseEvaluator.RestPose(Character, riggingContext: true);

    /// <summary>Slot of <paramref name="controller"/> in the character's poses, or -1.</summary>
    public int SlotOf(Object3D controller)
    {
        for (int i = 0; i < Controllers.Count; i++)
            if (ReferenceEquals(Controllers[i], controller)) return i;
        return -1;
    }

    /// <summary>
    /// Whether <paramref name="controller"/>'s slot can be edited: it must be one of the
    /// character's controllers, a constrained object cannot be, and neither can a controller 3D
    /// except the animated camera - the ref-pose access refuses every <c>CController3D</c>
    /// </summary>
    public bool CanEdit(Object3D controller, out string? reason)
    {
        reason = null;
        if (EditedPose is not { } pose) reason = $"{Character.Name} has no rest pose.";
        else if (SlotOf(controller) is var slot && (slot < 0 || slot >= pose.Count)) reason = $"{controller.Name} is not in {Character.Name}'s poses.";
        else if (controller.Constraint is { } c && controller is not Camera) reason = $"{controller.Name} is constrained by {c.Name}.";
        else if (controller is Controller3D && controller is not Camera) reason = $"{controller.Name} is a controller: the rest pose of a 3D controller cannot be edited (only a camera's can).";
        return reason == null;
    }

    /// <summary>The local rest transform of <paramref name="controller"/> in <see cref="EditedPose"/>.</summary>
    public Transform GetLocal(Object3D controller) => Transform.FromMatrixLoose(EditedPose!.Matrix(SlotOf(controller)));

    /// <summary>Writes <paramref name="local"/> into <paramref name="controller"/>'s slot of <see cref="EditedPose"/>.</summary>
    public void SetLocal(Object3D controller, Transform local)
    {
        if (!CanEdit(controller, out var reason)) throw new InvalidOperationException(reason);
        EnsureEditableRefPose();
        var feetBefore = AkeytsuParity ? null : ReverseFootWorlds(controller);
        EditedPose!.SetMatrix(SlotOf(controller), local.ToMatrix());
        ForwardPoseModification(controller, feetBefore);
    }

    /// <summary>
    /// Writes several local rest transforms at once - IK-chain joints included, which
    /// <see cref="CanEdit"/> refuses one by one because their controller drives them - and then
    /// carries every IK controller and reverse foot whose anchor joint moved, keeping its rest offset
    /// to that joint (the last chain joint, a reverse foot's ankle), the way Paint &amp; Check does.
    /// Used to give a linked character another character's ref-pose change. Returns the slots written.
    /// </summary>
    public int SetLocalsCarryingIK(IReadOnlyDictionary<Object3D, Transform> locals)
    {
        if (EditedPose == null) return 0;
        EnsureEditableRefPose();
        if (EditedPose is not { } pose) return 0;
        var rig = new RigEditor(Character);
        var carried = new List<(ControllerIK Controller, Matrix4x4 World, Matrix4x4 Anchor)>();
        foreach (var controller in Character.Controllers.OfType<ControllerIK>())
        {
            if (controller.Chain.Count == 0 || SlotOf(controller) < 0 || locals.ContainsKey(controller)) continue;
            var anchor = IKAnchor(controller);
            if (!locals.Keys.Any(edited => IsBelow(anchor, edited))) continue;
            carried.Add((controller, rig.WorldInPose(pose, controller), rig.WorldInPose(pose, anchor)));
        }
        int written = 0;
        foreach (var (controller, local) in locals)
        {
            int slot = SlotOf(controller);
            if (slot < 0 || slot >= pose.Count) continue;
            pose.SetMatrix(slot, local.ToMatrix());
            written++;
        }
        foreach (var (controller, world, anchorBefore) in carried)
        {
            if (!Matrix4x4.Invert(anchorBefore, out var toAnchor)) continue;
            var after = world * toAnchor * rig.WorldInPose(pose, IKAnchor(controller));
            var parentWorld = controller.Parent3D is { } parent ? rig.WorldInPose(pose, parent) : Matrix4x4.Identity;
            if (!Matrix4x4.Invert(parentWorld, out var toParent)) continue;
            pose.SetMatrix(SlotOf(controller), after * toParent);
        }
        return written;
    }

    /// <summary>The joint an IK controller keeps its offset to: a reverse foot's ankle, else the chain's last joint.</summary>
    private static Object3D IKAnchor(ControllerIK controller) =>
        controller is ControllerReverseFoot && controller.Chain.Count > 2 ? controller.Chain[2] : controller.Chain[^1];

    /// <summary>
    /// Before an edit of <paramref name="edited"/>: the rest world of every reverse foot whose chain
    /// runs below it and of that chain's leaf, so the foot can be carried with the leg afterwards.
    /// </summary>
    private Dictionary<ControllerReverseFoot, (Matrix4x4 Foot, Matrix4x4 Leaf)>? ReverseFootWorlds(Object3D edited)
    {
        if (EditedPose is not { } pose) return null;
        var rig = new RigEditor(Character);
        var result = new Dictionary<ControllerReverseFoot, (Matrix4x4, Matrix4x4)>(ReferenceEqualityComparer.Instance);
        foreach (var foot in Character.Controllers.OfType<ControllerReverseFoot>())
            if (foot.Chain.Count > 0 && SlotOf(foot) >= 0 && foot.Chain.Any(joint => IsBelow(joint, edited)))
                result[foot] = (rig.WorldInPose(pose, foot), rig.WorldInPose(pose, foot.Chain[^1]));
        return result;
    }

    /// <summary>
    /// The first ref-pose edit splits the reference pose off the bind pose: it adds a
    /// <c>CCharacterPose</c> named <c>__RefPose</c> as the character's last child and points
    /// <c>CurrentPose</c> and <c>RefPose</c> at it, so the bind pose - which skinning reads -
    /// keeps its matrices. On 19_run the edit adds
    /// <c>#8489 CCharacterPose '__RefPose'</c> at the character's end, the saved 'Bind Pose'
    /// kept its matrices, and <c>CurrentPose</c> and <c>RefPose</c> both named it
    /// <para>
    /// The new pose is <b>the character's current state</b>, not a copy of the edited pose: it is
    /// builds it from the character's current state, composing each
    /// controller's current local transform key onto the reference pose - in
    /// the rigging context the <c>__RigAnim</c> pseudo-animation's key at the scene time. On 19_run
    /// that key carries a 594.0125 offset on `LookAt` and `IK_LookAt`
    /// which is the whole of the "LookAt chain re-solve": no chain solve happens.
    /// The same composition explains the 1e-6..1e-5 on the untouched slots (a float
    /// decompose/compose round trip), so the result cannot be byte-identical to a copy.
    /// </para>
    /// </summary>
    public void EnsureEditableRefPose()
    {
        if (Character.RefPose is not { } current) return;
        // Only a ref-pose edit of the pose that *is* the reference pose splits it off the bind pose;
        // editing another selected pose (a bind pose with a custom ref pose) is a bind-pose edit.
        if (!ReferenceEquals(PoseEvaluator.RestPose(Character, riggingContext: true), current)) return;
        if (!Character.BindPoses.Any(b => ReferenceEquals(b, current))) return;
        if (Character.Child("__RefPose") is CharacterPose existing)
        {
            Character.CurrentPose = existing;
            Character.RefPose = existing;
            return;
        }
        AktObject source;
        try
        {
            source = Character.Model.Document.Schema.NewObject("CCharacterPose");
        }
        catch (InvalidOperationException)
        {
            return; // a scene whose schema lacks the class keeps editing the pose it has
        }
        source.Set("Name", "__RefPose");
        Character.Source.AddChild(source);
        source.Set("Matrices", CurrentStateMatrices(current).Select(m => (object?)AktMath.ToArray(m)).ToList());
        var pose = (CharacterPose)Character.Model.Wrap(source);
        Character.CurrentPose = pose;
        Character.RefPose = pose;
    }

    /// <summary>
    /// The matrices of the character's current state (see <see cref="EnsureEditableRefPose"/>): the
    /// edited pose with every controller's current <c>__RigAnim</c> key composed onto its slot. The
    /// key is the one at the scene's time, read the way the evaluator reads a stack
    /// (<see cref="PoseEvaluator.LayerValue"/>); a controller without a track keeps its slot.
    /// </summary>
    private Matrix4x4[] CurrentStateMatrices(CharacterPose pose)
    {
        var matrices = new Matrix4x4[pose.Count];
        for (int i = 0; i < matrices.Length; i++) matrices[i] = pose.Matrix(i);
        if (Character.RigAnimation?.Stacks.FirstOrDefault() is not { } stack) return matrices;
        var evaluator = new PoseEvaluator(Character);
        float time = Character.Model.Scene.Time ?? 0;
        for (int i = 0; i < matrices.Length && i < Controllers.Count; i++)
        {
            if (stack.TrackFor(Controllers[i]) is not { } track) continue;
            matrices[i] = PoseEvaluator.Compose(Transform.FromMatrixLoose(pose.Matrix(i)),
                evaluator.LayerValue(stack, track, time, out _)).ToMatrix();
        }
        return matrices;
    }

    /// <summary>
    /// The app forwards a ref-pose edit to the constrained controllers that depend on the moved
    /// object: every IK controller whose chain runs through a descendant of the edited object takes
    /// its chain leaf's matrix in the character's frame, and a reverse foot re-fits its shoe. For example, a 400-unit
    /// rest move of Root put <c>IK_Hand_R</c>'s slot at exactly 400. See
    /// <see cref="RefitReverseFoot"/> for what the reverse foot does.
    /// </summary>
    private void ForwardPoseModification(Object3D edited,
        Dictionary<ControllerReverseFoot, (Matrix4x4 Foot, Matrix4x4 Leaf)>? feetBefore = null)
    {
        if (EditedPose is not { } pose) return;
        var rig = new RigEditor(Character);
        if (!Matrix4x4.Invert(rig.WorldInPose(pose, Character), out var inverseCharacter)) return;
        foreach (var controller in Character.Controllers.OfType<ControllerIK>())
        {
            if (controller.Chain.Count == 0) continue;
            if (!controller.Chain.Any(joint => IsBelow(joint, edited))) continue;
            if (controller is ControllerReverseFoot foot)
            {
                // The chain joints are constrained, so an edit above them moves the ankle and the
                // ball together and the foot keeps its shape. The whole shoe is carried rigidly with
                // the chain's leaf, so it stays on the foot in the rest pose and keeps its rest offset
                // from the leaf - the offset an unpinned handle is drawn at. The akeytsu-parity mode
                // leaves the controller where it was and re-fits the shoe's skeleton locators instead.
                if (AkeytsuParity) RefitReverseFoot(pose, foot, rig, inverseCharacter);
                else if (feetBefore != null && feetBefore.TryGetValue(foot, out var before))
                    CarryWithLeaf(pose, foot, before.Foot, before.Leaf, rig);
                continue;
            }
            var leaf = controller.Chain[^1];
            pose.SetMatrix(SlotOf(controller), rig.WorldInPose(pose, leaf) * inverseCharacter);
        }
    }

    /// <summary>
    /// The reverse foot's own receive: the four sole pivots and the controller keep their matrices
    /// (the shoe is fitted to the mesh once and never re-fitted from a moved foot), while the three
    /// skeleton locators are rebuilt from the new chain - <c>SkeletonToes</c> half a foot bone past
    /// the ball, <c>SkeletonPlant</c> on the ball and <c>SkeletonAnkle</c> on the ankle, the tip and
    /// the ball carrying the ball joint's rotation and the ankle its own. On
    /// 19_run's converted file: a 400-unit Root move left the controller and the sole locators alone,
    /// moved <c>ShoeSkeletonToes</c> by (399.7644, 0, -13.7194) and the plant/ankle by float noise,
    /// and each locator's reconstructed world sits on the chain joint
    /// </summary>
    private void RefitReverseFoot(CharacterPose pose, ControllerReverseFoot foot, RigEditor rig,
        Matrix4x4 inverseCharacter)
    {
        if (foot.Chain.Count != 4 || foot.Shoe.Count < 7 || SlotOf(foot) < 0) return;
        var world = new Matrix4x4[7];
        var parent = pose.Matrix(SlotOf(foot));
        for (int i = 0; i < 7; i++)
        {
            int slot = SlotOf(foot.Shoe[i]);
            if (slot < 0) return;
            world[i] = pose.Matrix(slot) * parent;
            parent = world[i];
        }
        var ball = rig.WorldInPose(pose, foot.Chain[^1]) * inverseCharacter;
        var ankle = rig.WorldInPose(pose, foot.Chain[^2]) * inverseCharacter;
        var tip = ball.Translation + (ball.Translation - ankle.Translation) * 0.5f;
        var toes = Frame(ball, tip);
        var plant = Frame(ball, ball.Translation);
        var skeletonAnkle = Frame(ankle, ankle.Translation);
        Local(ReverseFootSolver.SkeletonToes, toes, world[3]);
        Local(ReverseFootSolver.SkeletonPlant, plant, toes);
        Local(ReverseFootSolver.SkeletonAnkle, skeletonAnkle, plant);

        void Local(int index, Matrix4x4 target, Matrix4x4 parentWorld)
        {
            if (!Matrix4x4.Invert(parentWorld, out var inverse)) return;
            pose.SetMatrix(SlotOf(foot.Shoe[index]), target * inverse);
        }
    }

    /// <summary>
    /// Moves <paramref name="foot"/>'s slot so its rest world keeps its offset from the chain's leaf:
    /// <c>footWorld' = footWorld * leafWorld^-1 * leafWorld'</c>, written as a local under its parent.
    /// </summary>
    private void CarryWithLeaf(CharacterPose pose, ControllerReverseFoot foot, Matrix4x4 footBefore, Matrix4x4 leafBefore,
        RigEditor rig)
    {
        if (!Matrix4x4.Invert(leafBefore, out var toLeaf)) return;
        var footAfter = footBefore * toLeaf * rig.WorldInPose(pose, foot.Chain[^1]);
        var parentWorld = foot.Parent3D is { } parent ? rig.WorldInPose(pose, parent) : Matrix4x4.Identity;
        if (!Matrix4x4.Invert(parentWorld, out var toParent)) return;
        pose.SetMatrix(SlotOf(foot), footAfter * toParent);
    }

    /// <summary><paramref name="world"/> with its translation replaced by <paramref name="position"/> (a rigid frame).</summary>
    private static Matrix4x4 Frame(Matrix4x4 world, Vector3 position) => new(
        world.M11, world.M12, world.M13, 0f,
        world.M21, world.M22, world.M23, 0f,
        world.M31, world.M32, world.M33, 0f,
        position.X, position.Y, position.Z, 1f);

    /// <summary>Whether <paramref name="node"/> is <paramref name="ancestor"/> or below it.</summary>
    private static bool IsBelow(Object3D node, Object3D ancestor)
    {
        for (Object3D? n = node; n != null; n = n.Parent3D)
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    /// <summary>
    /// Puts an animated camera's looker target at the camera's look point in the edited pose: the
    /// hidden joint's own distance along the camera's -Z, which is where the rest-pose edit
    /// leaves it. Moving the camera's rest X by
    /// 4,879.56 moved the target's rest by the same amount (its rotation, the camera's, unchanged)
    /// and left the hidden joint's local at (0,0,-40). The target's own ref-pose transform is
    /// refused by <see cref="CanEdit"/>, so this is the only way its rest pose changes.
    /// </summary>
    public void DeriveLooker(CameraAnimated camera)
    {
        if (EditedPose is not { } pose || CameraEditor.Rig(camera) is not { } rig) return;
        int ci = SlotOf(camera), hi = SlotOf(rig.HideJoint), ti = SlotOf(rig.Target);
        if (ci < 0 || hi < 0 || ti < 0) return;
        if (rig.Target.RestLocked)
        {
            // A locked target stays put: the camera turns about its own eye to keep looking at it
            // and the hidden joint slides to the new distance (SloppyAnimatsu's lock).
            var lookPoint = pose.Matrix(ti).Translation;
            if (!CameraRigKeys.Aim(pose.Matrix(ci), lookPoint, out var aimed, out float reach)) return;
            pose.SetMatrix(ci, aimed.ToMatrix());
            var joint = Transform.FromMatrixLoose(pose.Matrix(hi));
            pose.SetMatrix(hi, new Transform(new Vector3(0f, 0f, -reach), joint.Rotation, joint.Scale).ToMatrix());
            return;
        }
        float distance = -Transform.FromMatrixLoose(pose.Matrix(hi)).Translation.Z;
        if (distance <= 1e-4f) distance = CameraEditor.HideJointDistance;
        pose.SetMatrix(ti, Matrix4x4.CreateTranslation(0f, 0f, -distance) * pose.Matrix(ci));
    }

    /// <summary>Whether moving <paramref name="controller"/> in the ref pose asks for the animations to be converted (joints and cameras).</summary>
    public static bool MovesAnimations(Object3D controller) => controller is Joint or Camera;

    /// <summary>
    /// Slots whose <c>RefPose</c> matrix differs from <c>RefPoseOld</c>. Empty when either is
    /// missing or they are the same object. A slot that only carries the rig animation's current
    /// key - which <see cref="EnsureEditableRefPose"/> folds into the new pose - is not a change:
    /// the split itself must not make the animations look pending
    /// </summary>
    public IReadOnlyList<int> ChangedSlots
    {
        get
        {
            if (Character.RefPose is not { } current || Character.RefPoseOld is not { } old || ReferenceEquals(current, old)) return [];
            var changed = new List<int>();
            var rig = RigKeyTracks();
            for (int i = 0; i < Math.Min(Math.Min(current.Count, old.Count), Controllers.Count); i++)
            {
                if (AktMath.MaxDifference(current.Matrix(i), old.Matrix(i)) <= Tolerance) continue;
                if (rig != null && rig.ContainsKey(Controllers[i])
                    && AktMath.MaxDifference(CurrentStateMatrix(old, i, rig), current.Matrix(i)) <= Tolerance) continue;
                changed.Add(i);
            }
            return changed;
        }
    }

    /// <summary>The <c>__RigAnim</c> stack's tracks by target, or null when there is none.</summary>
    private Dictionary<Object3D, AnimTrack>? RigKeyTracks()
    {
        if (Character.RigAnimation?.Stacks.FirstOrDefault() is not { } stack) return null;
        var tracks = new Dictionary<Object3D, AnimTrack>(ReferenceEqualityComparer.Instance);
        foreach (var track in stack.Tracks)
            if (track.Target is { } target) tracks[target] = track;
        return tracks;
    }

    /// <summary>The current-state matrix of <paramref name="pose"/>'s slot <paramref name="i"/>.</summary>
    private Matrix4x4 CurrentStateMatrix(CharacterPose pose, int i, Dictionary<Object3D, AnimTrack> rig)
    {
        var matrix = pose.Matrix(i);
        if (!rig.TryGetValue(Controllers[i], out var track)) return matrix;
        var key = new PoseEvaluator(Character).LayerValue(Character.RigAnimation!.Stacks.First(), track,
            Character.Model.Scene.Time ?? 0, out _);
        return PoseEvaluator.Compose(Transform.FromMatrixLoose(matrix), key).ToMatrix();
    }

    /// <summary>
    /// Whether the reference pose has a change on a joint or camera that the animations have not
    /// been resolved against. SloppyAnimatsu derives this from the saved poses rather than a flag, so
    /// it survives saving and undo.
    /// </summary>
    public bool IsResolutionPending => ChangedSlots.Any(i => MovesAnimations(Controllers[i]));

    /// <summary>
    /// Applies the user's choice; returns the number of key values rewritten. The conversion
    /// visits every MRS track of the Anim Bank (not only the changed controllers'), so the count
    /// is larger than the edited joint's own keys - see <see cref="ConvertAnimations"/>.
    /// </summary>
    public int Resolve(RestPoseResolution resolution)
    {
        if (Character.RefPose is not { } current || Character.RefPoseOld is not { } old || ReferenceEquals(current, old)) return 0;
        int converted = 0;
        switch (resolution)
        {
            case RestPoseResolution.ConvertAnimations:
                converted = ConvertAnimations(current, old);
                old.CopyMatricesFrom(current);
                break;
            case RestPoseResolution.KeepAnimations:
                old.CopyMatricesFrom(current);
                break;
            case RestPoseResolution.ReturnToPrevious:
                ReturnToPreviousRefPose(current, old);
                break;
        }
        return converted;
    }

    /// <summary>
    /// Return to Previous Ref Pose: the previous pose is the reference pose again, and the edited
    /// pose is removed (a bind pose is never removed). When the previous pose holds a bind pose's
    /// matrices - the bind pose acted as the reference pose - that bind pose takes the role back and
    /// the copy is removed too, so no separate Ref Pose row is left behind. A previous pose no longer
    /// in the scene is copied over the edited one instead.
    /// </summary>
    private void ReturnToPreviousRefPose(CharacterPose current, CharacterPose old)
    {
        if (old.Source.Parent == null)
        {
            current.CopyMatricesFrom(old);
            return;
        }
        var target = Character.BindPoses.FirstOrDefault(b => ReferenceEquals(b, old))
            ?? Character.BindPoses.FirstOrDefault(b => SameMatrices(b, old)) ?? old;
        Character.RefPose = target;
        if (Character.Source.Has("RefPoseOld")) Character.RefPoseOld = target;
        if (ReferenceEquals(Character.CurrentPose, current) || ReferenceEquals(Character.CurrentPose, old)) Character.CurrentPose = target;
        foreach (var pose in new[] { current, old })
            if (!ReferenceEquals(pose, target) && !Character.BindPoses.Any(b => ReferenceEquals(b, pose)))
                pose.Source.Parent?.RemoveChild(pose.Source);
    }

    private static bool SameMatrices(CharacterPose a, CharacterPose b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (AktMath.MaxDifference(a.Matrix(i), b.Matrix(i)) > Tolerance) return false;
        return true;
    }

    /// <summary>
    /// Rewrites the keys of the character's Anim Bank tracks against the new reference pose, as
    /// the reference-pose update does
    /// every non-additive stack (<c>Flags</c> bit 3) of every
    /// user animation - <c>__RigAnim</c> is not in the Anim Bank - and every <c>CAnimTrackMRS</c>
    /// track, including the camera, IK and looker tracks that inherit it, except the reverse-foot
    /// tracks, which handle the recompute themselves. The value per channel is
    /// <c>key' = Decompose(newBase, Compose(oldBase, key))</c> (translation add, rotation multiply,
    /// scale divide), and the track's tangents are recomputed as after a key edit - the app changed
    /// 116,719 tangent entries on 6,096 tracks in the 19_run experiment, 5,259 of them on tracks
    /// whose values did not move. Audio and blendshape tracks are not MRS tracks and are left alone.
    /// <para>
    /// Two target kinds are special: a reverse-foot
    /// **shoe locator**'s track is not converted at all (the app leaves it byte-for-byte, although
    /// its ref-pose slots move), and a reverse foot's own track keeps its animated world
    /// (<see cref="KeepReverseFootInPlace"/>, or <see cref="ConvertReverseFoot"/> under
    /// <see cref="AkeytsuParity"/>), with the shoe's skeleton locators restored.
    /// </para>
    /// </summary>
    private int ConvertAnimations(CharacterPose current, CharacterPose old)
    {
        var bases = new Dictionary<Object3D, (Transform Old, Transform New)>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < Math.Min(Math.Min(current.Count, old.Count), Controllers.Count); i++)
            bases[Controllers[i]] = (Transform.FromMatrixLoose(old.Matrix(i)), Transform.FromMatrixLoose(current.Matrix(i)));
        if (bases.Count == 0) return 0;

        int count = 0;
        var rig = new RigEditor(Character);
        Matrix4x4.Invert(rig.WorldInPose(old, Character), out var inverseOldCharacter);
        Matrix4x4.Invert(rig.WorldInPose(current, Character), out var inverseNewCharacter);
        foreach (var animation in Character.Animations)
            foreach (var stack in animation.Stacks)
            {
                if (stack.IsAdditive) continue;
                foreach (var track in stack.Tracks)
                {
                    if (track.Target is not { } target || !bases.TryGetValue(target, out var b)) continue;
                    if (target is ReverseFootLocator) continue;
                    if (target is ControllerReverseFoot foot)
                    {
                        if (AkeytsuParity)
                            ConvertReverseFoot(current, old, track, foot, rig, inverseOldCharacter, inverseNewCharacter);
                        else
                            count += KeepReverseFootInPlace(track, b.Old, b.New);
                        continue;
                    }
                    if (!IsConvertedTrack(Character.Model, track)) continue;
                    count += RebaseTrack(track, b.Old, b.New, AkeytsuParity);
                }
            }
        return count;
    }

    /// <summary>
    /// Off by default. True keeps the akeytsu-style conversion for comparison: every edit above a
    /// reverse foot re-fits the shoe's skeleton locators onto the new rest leg
    /// (<see cref="RefitReverseFoot"/>), the conversion rebases the reverse-foot keys by the chain
    /// leaf's rest change (<see cref="ConvertReverseFoot"/>), and every converted track - also one
    /// whose rest did not change - gets its tangents recomputed. All three change the animation:
    /// a 400-unit Root move put every pinned foot handle 400 units off, folded the legs between
    /// pinned keys, and bent the in-betweens of tracks whose stored tangents differ from computed
    /// ones. False keeps every animated world where it was, keys and in-betweens.
    /// </summary>
    public bool AkeytsuParity { get; init; }

    /// <summary>
    /// Rebases one track's keys from <paramref name="oldBase"/> to <paramref name="newBase"/>:
    /// <c>key' = Decompose(new, Compose(old, key))</c>. Without <paramref name="recomputeTangents"/>
    /// the stored tangents are carried over, which keeps every in-between exactly: the rebase adds a
    /// constant translation (tangents unchanged), left-multiplies the rotation by a constant (the
    /// rotation tangents are log vectors in the key's own frame, so SQUAD's result is
    /// left-multiplied by the same constant) and multiplies the scale by a constant (the scale
    /// tangents take the same factor). A track whose base did not change is left alone.
    /// Returns the number of keys rewritten.
    /// </summary>
    private static int RebaseTrack(AnimTrack track, Transform oldBase, Transform newBase, bool recomputeTangents)
    {
        if (!recomputeTangents && AktMath.MaxDifference(oldBase.ToMatrix(), newBase.ToMatrix()) <= Tolerance) return 0;
        int count = 0;
        foreach (var key in track.Keys.OfType<TransformKey>())
        {
            key.Value = PoseEvaluator.Decompose(newBase, PoseEvaluator.Compose(oldBase, key.Value));
            count++;
        }
        if (recomputeTangents)
        {
            TangentRecompute.Recompute(track);
            return count;
        }
        var scaleFactor = oldBase.Scale / newBase.Scale;
        foreach (var tangent in track.TangentsIn.Concat(track.TangentsOut).OfType<TransformKey>())
            tangent.Value = tangent.Value with { Scale = tangent.Value.Scale * scaleFactor };
        return count;
    }

    /// <summary>
    /// The reverse foot's keys after a ref-pose change, keeping the animation as it looked: the key
    /// is a local offset from the controller's own ref slot, so it takes the generic rebase
    /// (<see cref="RebaseTrack"/>, tangents carried). <c>RefArray</c> needs nothing: entries 0..3
    /// are the leg joints' full locals, which the joint keys' conversion preserves, and entry 4 is
    /// the controller relative to the ball's animated world, which is preserved too. Returns the
    /// number of keys rewritten.
    /// </summary>
    private static int KeepReverseFootInPlace(AnimTrack track, Transform oldBase, Transform newBase) =>
        RebaseTrack(track, oldBase, newBase, recomputeTangents: false);

    /// <summary>
    /// A reverse-foot track's keys are not rewritten by the generic formula: the shoe is fitted to
    /// the chain, so a reference-pose change that moves the chain moves the key's controller
    /// target and its <c>RefArray</c> relation to the chain with it, keeping the shoe where it was
    /// in the world. On 19_run's converted file a 400-unit Root move leaves
    /// every reverse-foot key's translation and <c>RefArray[4]</c> (the controller relative to the
    /// ball) shifted by -400, the four chain entries untouched. The shift is the chain leaf's world
    /// delta in the character's frame; the locator tracks are not converted at all (see
    /// <see cref="IsConvertedTrack"/>'s caller), which is the behaviour.
    /// </summary>
    private void ConvertReverseFoot(CharacterPose pose, CharacterPose old, AnimTrack track, ControllerReverseFoot foot,
        RigEditor rig, Matrix4x4 inverseOldCharacter, Matrix4x4 inverseNewCharacter)
    {
        if (foot.Chain.Count == 0) return;
        var oldLeaf = rig.WorldInPose(old, foot.Chain[^1]) * inverseOldCharacter;
        var newLeaf = rig.WorldInPose(pose, foot.Chain[^1]) * inverseNewCharacter;
        if (!Matrix4x4.Invert(oldLeaf, out var inverseOldLeaf) || !Matrix4x4.Invert(newLeaf, out var inverseNewLeaf)) return;
        // The app keeps the shoe where it was: the chain leaf's world change pre-multiplies the
        // key and its controller relation.
        var shift = inverseNewLeaf * oldLeaf;
        foreach (var key in track.Keys.OfType<ReverseFootKey>())
        {
            key.Value = Transform.FromMatrixLoose(shift * key.Value.ToMatrix());
            if (key.ChainTransforms.Count < 5) continue;
            var chain = key.ChainTransforms.ToArray();
            chain[4] = Transform.FromMatrixLoose(shift * chain[4].ToMatrix());
            key.SetChainTransforms(chain);
        }
    }

    /// <summary>
    /// The conversion visits every track that is kind-of <c>CAnimTrackMRS</c> (the plain MRS
    /// tracks and the camera, IK and looker tracks that inherit it) and skips the reverse-foot
    /// tracks, which handle the recompute themselves; audio and blendshape tracks
    /// are not MRS tracks. The class chain comes from the file's schema (19_run declares
    /// CAnimTrackMRS > CAnimTrackController3D_IK_RF > CAnimTrackIK_VK > CAnimTrackReverseFoot_VK).
    /// This is the class test only: <see cref="ConvertAnimations"/> handles a reverse-foot target
    /// with <see cref="ConvertReverseFoot"/> and skips a shoe locator's track.
    /// </summary>
    public static bool IsConvertedTrack(SceneModel model, AnimTrack track)
    {
        var def = model.Document.Schema.FindClass(track.Source.ClassKey);
        return def != null && def.IsA("CAnimTrackMRS") && !def.IsA("CAnimTrackReverseFoot_VK");
    }
}
