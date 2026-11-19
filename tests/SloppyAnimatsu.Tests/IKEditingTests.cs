using System.Numerics;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>Posing IK chains through <see cref="KeyEditor.Pose"/>.</summary>
public class IKEditingTests
{
    [SceneTheory, SceneSamples]
    public void DraggingTheController_PullsTheChainAndKeepsTheEndOnIt(string path)
    {
        foreach (bool ikKey in new[] { true, false })
            foreach (var c in Candidates(path, ikKey).Take(3))
            {
                var refsBefore = c.Chain.Key.ChainTransforms.ToArray();
                var (world, _) = Evaluate(c);
                // Swung about the chain's root at the same distance: reachable by any chain, also a single bone.
                var root = world[c.JointIndices[0]].Translation;
                var arm = world[c.ControllerIndex].Translation - root;
                var axis = Vector3.Normalize(Vector3.Cross(arm, MathF.Abs(arm.Y) < 0.9f * arm.Length() ? Vector3.UnitY : Vector3.UnitX));
                var target = root + Vector3.Transform(arm, Quaternion.CreateFromAxisAngle(axis, 0.1f));

                c.Editor.Pose(c.Stack, c.KeyIndex, c.Chain.Controller, MovedTo(c, target));

                var (after, local) = Evaluate(c);
                float length = ChainLength(c, after);
                var end = after[c.JointIndices[^1]];
                Assert.True(Vector3.Distance(end.Translation, target) < 1e-3f * length, $"{c}: the chain misses the target by {Vector3.Distance(end.Translation, target)} (length {length}, chain {string.Join(",", c.Chain.Joints.Select(j => j.Name))}, root to target {Vector3.Distance(after[c.JointIndices[0]].Translation, target)}, controller {after[c.ControllerIndex].Translation}, target {target})");
                AssertSameWorld(end, after[c.ControllerIndex], length, c.ToString());
                var refs = c.Chain.Key.ChainTransforms;
                for (int j = 0; j < refs.Count; j++)
                    // A reached IK key keeps its string; an FK key stores the chain's locals.
                    AssertClose(ikKey ? refsBefore[j] : local[c.JointIndices[j]], refs[j], c.ToString());
            }
    }

    [SceneTheory, SceneSamples]
    public void IKDrag_SurvivesSaveAndReload(string path)
    {
        // An FK key: the drag rewrites RefArray as well as the joint and controller keys.
        if (Candidates(path, ikKey: false).FirstOrDefault() is not { } c) return;
        var (world, _) = Evaluate(c);
        var target = Vector3.Lerp(world[c.ControllerIndex].Translation, world[c.JointIndices[0]].Translation, 0.05f);
        c.Editor.Pose(c.Stack, c.KeyIndex, c.Chain.Controller, MovedTo(c, target));
        var (after, _) = Evaluate(c);
        var refs = c.Chain.Key.ChainTransforms.ToArray();

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-ik-edit-{Guid.NewGuid():N}.akt");
        try
        {
            c.Stack.Model.Save(temp);
            var model = TestFiles.LoadScene(temp);
            var character = model.Scene.Characters.Single(ch => ch.Name == c.Editor.Character.Name);
            var animation = character.Animations.First(a => a.Name == c.Animation.Name);
            var stack = animation.Stacks.First(s => s.IsEnabled);
            var controller = character.IKControllers.First(ik => ik.Name == c.Chain.Controller.Name);
            var key = (IKKey)stack.TrackFor(controller)!.Keys[c.KeyIndex]!;
            for (int j = 0; j < refs.Length; j++) AssertClose(refs[j], key.ChainTransforms[j], c.ToString());
            var evaluator = new PoseEvaluator(character);
            var reloaded = evaluator.WorldMatrices(animation, c.Time);
            AssertSameWorld(after[c.ControllerIndex], reloaded[evaluator.IndexOf(controller)], ChainLength(c, after), c.ToString());
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [SceneTheory, SceneSamples]
    public void DraggingOutOfReach_LeavesTheControllerOnTheStretchedChain(string path)
    {
        // The rule, kept behind KeyEditor.SnapUnreachedHandles.
        foreach (var c in Candidates(path, ikKey: true).Take(3))
        {
            var editor = new KeyEditor(c.Editor.Character, c.Editor.Evaluator) { SnapUnreachedHandles = true };
            var (world, _) = Evaluate(c);
            float length = ChainLength(c, world);
            var root = world[c.JointIndices[0]].Translation;
            var away = Vector3.Normalize(world[c.ControllerIndex].Translation - root);
            var target = root + away * length * 3;

            editor.Pose(c.Stack, c.KeyIndex, c.Chain.Controller, MovedTo(c, target));

            var (after, local) = Evaluate(c);
            var end = after[c.JointIndices[^1]];
            Assert.True(Vector3.Distance(end.Translation, target) > length, $"{c}: the chain cannot reach");
            AssertSameWorld(end, after[c.ControllerIndex], length, c.ToString());
            var refs = c.Chain.Key.ChainTransforms;
            for (int j = 0; j < refs.Count; j++) AssertClose(local[c.JointIndices[j]], refs[j], c.ToString());
        }
    }

    [SceneTheory, SceneSamples]
    public void DraggingOutOfReach_AtAnIKKey_LeavesTheHandleWhereItWasDropped_UntilSnapped(string path)
    {
        foreach (var c in Candidates(path, ikKey: true).Take(3))
        {
            var (world, _) = Evaluate(c);
            float length = ChainLength(c, world);
            var root = world[c.JointIndices[0]].Translation;
            var away = Vector3.Normalize(world[c.ControllerIndex].Translation - root);
            var target = root + away * length * 3;

            c.Editor.Pose(c.Stack, c.KeyIndex, c.Chain.Controller, MovedTo(c, target));

            // The handle stays at the goal; the chain stretches toward it and RefArray holds it.
            var (after, local) = Evaluate(c);
            var end = after[c.JointIndices[^1]];
            Assert.True(Vector3.Distance(after[c.ControllerIndex].Translation, target) < 1e-3f * length,
                $"{c}: the handle is at {after[c.ControllerIndex].Translation}, dropped at {target}");
            Assert.True(Vector3.Distance(end.Translation, target) > length, $"{c}: the chain cannot reach");
            var toTarget = Vector3.Normalize(target - root);
            Assert.True(Vector3.Dot(Vector3.Normalize(end.Translation - root), toTarget) > 0.99f, $"{c}: the chain points at the goal");
            var refs = c.Chain.Key.ChainTransforms;
            for (int j = 0; j < refs.Count; j++) AssertClose(local[c.JointIndices[j]], refs[j], c.ToString());

            // Snap puts it on the chain's end without moving the chain.
            Assert.True(c.Editor.SnapToChain(c.Stack, c.KeyIndex, c.Chain.Controller), c.ToString());
            var (snapped, snappedLocal) = Evaluate(c);
            AssertSameWorld(snapped[c.JointIndices[^1]], snapped[c.ControllerIndex], length, c.ToString());
            for (int j = 0; j < c.JointIndices.Length; j++)
                AssertClose(local[c.JointIndices[j]], snappedLocal[c.JointIndices[j]], c.ToString());
        }
    }

    [SceneTheory, SceneSamples]
    public void MovingAJointAboveTheChain_KeepsEveryIKKeyConsistent(string path)
    {
        foreach (var c in Candidates(path, ikKey: null).Take(4))
        {
            // The first joint's parent, keyed in the stack: moving it moves the chain's base.
            if (c.Chain.Joints[0].Parent3D is not { } above || c.Stack.TrackFor(above) is not { } track
                || (track.Channels & AnimChannels.Rotate) == 0)
                continue;
            int ai = c.Editor.Evaluator.IndexOf(above);
            if (ai < 0) continue;
            var (world, local) = Evaluate(c);
            var ctrlBefore = world[c.ControllerIndex];
            var posed = local[ai] with
            {
                Rotation = Quaternion.Normalize(local[ai].Rotation * Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 0.15f)),
            };

            c.Editor.Pose(c.Stack, c.KeyIndex, above, posed);

            var (after, _) = Evaluate(c);
            float length = ChainLength(c, after);
            AssertSameWorld(after[c.JointIndices[^1]], after[c.ControllerIndex], length, $"{c} after moving {above.Name}");
            if (c.IKKey)
                // The controller stays put and the chain follows it.
                AssertSameWorld(ctrlBefore, after[c.ControllerIndex], length, $"{c}: the IK controller moved");
        }
    }

    [SceneTheory, SceneSamples]
    public void IKControllers_AreNeverScalable_InAnyDrag(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
        {
            if (character.IKControllers.Count() == 0) continue;
            var session = new SloppyAnimatsu.Rendering.PoseEditSession(new SloppyAnimatsu.Rendering.ScenePose(model), character);
            var animation = character.Animations.FirstOrDefault(a => a.Stacks.Any(s => s.IsEnabled));
            foreach (var ik in character.IKControllers)
            {
                Assert.False(SloppyAnimatsu.Rendering.TransformDrag.IsScalable(ik), ik.Name);
                if (animation == null) continue;
                var drag = session.Begin(animation, [ik], 0, out _);
                if (drag == null) continue;
                Assert.False(drag.CanScale, $"{ik.Name} ({ik.GetType().Name}) offers scale");
                drag.Cancel();
            }
        }
    }

    [SceneTheory, SceneSamples]
    public void SwivellingTheChain_TurnsTheMiddleAboutRootToEnd_AndKeepsBothEnds(string path)
    {
        foreach (var c in Candidates(path, ikKey: null).Where(c => c.JointIndices.Length >= 3).Take(3))
        {
            var (world, _) = Evaluate(c);
            var root = world[c.JointIndices[0]].Translation;
            var end = world[c.JointIndices[^1]];
            var axis = Vector3.Normalize(end.Translation - root);
            var middle = world[c.JointIndices[1]].Translation;
            const float angle = 0.4f;
            var expected = root + Vector3.Transform(middle - root, Quaternion.CreateFromAxisAngle(axis, angle));

            Assert.True(c.Editor.SwivelChain(c.Stack, c.KeyIndex, c.Chain.Controller, angle), c.ToString());

            var (after, local) = Evaluate(c);
            float length = ChainLength(c, world);
            Assert.True(Vector3.Distance(after[c.JointIndices[0]].Translation, root) < 1e-3f * length, $"{c}: the root moved");
            AssertSameWorld(end, after[c.JointIndices[^1]], length, $"{c}: the chain's end");
            AssertSameWorld(after[c.JointIndices[^1]], after[c.ControllerIndex], length, $"{c}: the end left the controller");
            Assert.True(Vector3.Distance(after[c.JointIndices[1]].Translation, expected) < 1e-3f * length,
                $"{c}: the middle joint is at {after[c.JointIndices[1]].Translation}, expected {expected}");
            if (!c.IKKey)
            {
                var refs = c.Chain.Key.ChainTransforms;
                for (int j = 0; j < c.JointIndices.Length; j++) AssertClose(local[c.JointIndices[j]], refs[j], c.ToString());
            }
        }
    }

    private sealed record Case(KeyEditor Editor, Animation Animation, AnimStack Stack, int KeyIndex, KeyEditor.IKKeyChain Chain,
        int ControllerIndex, int[] JointIndices)
    {
        public bool IKKey => (Chain.Key.Flags & 1) != 0;
        public float Time => Stack.Keys[KeyIndex].Time;
        public override string ToString() => $"{Animation.Name} {Chain.Controller.Name} key {KeyIndex}";
    }

    /// <summary>
    /// An IK chain answers only to its own stack
    /// so a root-motion layer above it carries the whole pose. A drag of a
    /// controller must therefore key the same chain values whether that layer is enabled or not, and
    /// with it enabled the handle and the chain's end move by the step where the viewport draws
    /// them. The user's case: Dup's <c>Retargeted From DupTheClown/Common/Run</c> with
    /// its <c>TransN</c> stack enabled, where a drag of <c>IK_Hand_R</c> at frame 9 threw every limb
    /// back toward the controllers' un-moved positions. (At frame 9 the locked chains do not reach
    /// their handles to begin with, so any edit there snaps them on, with or without TransN.)
    /// </summary>
    [Fact]
    public void DraggingAController_UnderARootMotionLayer_KeysWhatItKeysWithoutIt()
    {
        string? path = TestFiles.UserSamples().FirstOrDefault(p => Path.GetFileName(p).StartsWith("26_retargetRedo", StringComparison.Ordinal));
        if (path == null) return;
        var step = new Vector3(0, 0, -2);
        foreach (int keyIndex in new[] { 3, 4 })
        foreach (bool foot in new[] { false, true })
        {
            Transform[]? without = null;
            foreach (bool rootMotion in new[] { false, true })
            {
                var model = TestFiles.LoadScene(path);
                var character = model.Scene.Characters.First(c => c.Name == "Dup");
                var animation = character.Animations.First(a => a.Name == "Run" && a.Parent?.Parent?.Name == "Retargeted From DupTheClown");
                var transN = animation.Stacks.First(s => s.Name == "TransN");
                Assert.True(transN.IsEnabled);
                if (!rootMotion) transN.Source.Set("Flags", transN.Flags & ~2u);
                var stack = animation.Stacks.First(s => s.Name == "All");
                var editor = new KeyEditor(character);
                var ev = editor.Evaluator;
                float time = stack.Keys[keyIndex].Time;
                var controller = foot ? character.IKControllers.FirstOrDefault(ik => ik is ControllerReverseFoot)
                    : character.IKControllers.FirstOrDefault(ik => ik.Name == "IK_Hand_R");
                Assert.NotNull(controller);
                int ci = ev.IndexOf(controller!);
                string what = $"{controller!.Name} at key {keyIndex} (frame {time})";

                var world = ev.WorldMatrices(animation, time);
                var shown = ev.DisplayWorldMatrices(animation, time);
                var local = ev.LocalTransforms(animation, time);
                bool active = ev.IsIKActive(animation, controller, time);
                editor.Pose(stack, keyIndex, controller, local[ci] with { Translation = local[ci].Translation + step });
                var after = ev.LocalTransforms(animation, time);

                // Every IK chain joint and every controller: TransN keys only the root.
                var watched = character.IKControllers.SelectMany(ik => ik.Chain.Append(ik)).Select(ev.IndexOf)
                    .Where(i => i >= 0).Distinct().ToArray();
                var values = watched.Select(i => after[i]).ToArray();
                if (!rootMotion) { without = values; continue; }
                for (int w = 0; w < watched.Length; w++)
                {
                    float d = Vector3.Distance(values[w].Translation, without![w].Translation);
                    float r = 1 - MathF.Abs(Quaternion.Dot(values[w].Rotation, without[w].Rotation));
                    Assert.True(d < 0.01f && r < 1e-5f,
                        $"{what}: {ev.Controllers[watched[w]].Name} keyed {values[w]} with TransN, {without[w]} without");
                }

                // Where the reverse foot's IK is active (key 3) its handle moves by the step where
                // it is drawn. (IK_Hand_R cannot reach its handle at key 3, and both are FK keys at
                // key 4, so those handles snap to the chain's end, as they do without TransN.)
                if (controller is not ControllerReverseFoot || !active) continue;
                var shownAfter = ev.DisplayWorldMatrices(animation, time);
                var moved = shownAfter[ci].Translation - shown[ci].Translation;
                var stepWorld = Vector3.TransformNormal(step, Matrix4x4.Invert(local[ci].ToMatrix(), out var inv) ? inv * world[ci] : Matrix4x4.Identity);
                Assert.True(Vector3.Distance(moved, stepWorld) < 0.05f * stepWorld.Length(), $"{what}: the handle moved {moved}, expected {stepWorld}");
            }
        }
    }

    /// <summary>
    /// IK chains keyed at key 1 of animations with a single enabled stack, where the chain end is
    /// already on the controller (reachable) and the controller's track animates translation.
    /// <paramref name="ikKey"/> filters by the key's IK flag (null: any). Each case edits its own
    /// freshly loaded copy of the scene.
    /// </summary>
    private static IEnumerable<Case> Candidates(string path, bool? ikKey)
    {
        int count = AllCases(TestFiles.LoadScene(path), ikKey).Count();
        for (int i = 0; i < count; i++)
            yield return AllCases(TestFiles.LoadScene(path), ikKey).ElementAt(i);
    }

    private static IEnumerable<Case> AllCases(SceneModel model, bool? ikKey)
    {
        foreach (var character in model.Scene.Characters)
        {
            var editor = new KeyEditor(character);
            foreach (var animation in character.Animations)
            {
                if (animation.Stacks.Count(s => s.IsEnabled) != 1) continue;
                var stack = animation.Stacks.First(s => s.IsEnabled);
                if (stack.Keys.Count < 3 || stack.IsAdditive || stack.Opacity < 0.99f) continue;
                foreach (var ik in character.IKControllers)
                {
                    if (editor.IKChain(stack, 1, ik) is not { } chain) continue;
                    if (ikKey is { } wanted && ((chain.Key.Flags & 1) != 0) != wanted) continue;
                    if ((stack.TrackFor(ik)!.Channels & AnimChannels.Translate) == 0) continue;
                    var c = new Case(editor, animation, stack, 1, chain, editor.Evaluator.IndexOf(ik),
                        chain.Joints.Select(editor.Evaluator.IndexOf).ToArray());
                    var (world, _) = Evaluate(c);
                    if (Vector3.Distance(world[c.JointIndices[^1]].Translation, world[c.ControllerIndex].Translation) > 1e-4f * ChainLength(c, world))
                        continue;
                    yield return c;
                }
            }
        }
    }

    private static (Matrix4x4[] World, Transform[] Local) Evaluate(Case c) =>
        (c.Editor.Evaluator.WorldMatrices(c.Animation, c.Time), c.Editor.Evaluator.LocalTransforms(c.Animation, c.Time));

    /// <summary>The controller's local transform that puts it at world position <paramref name="target"/>.</summary>
    private static Transform MovedTo(Case c, Vector3 target)
    {
        var (world, local) = Evaluate(c);
        var controllerLocal = local[c.ControllerIndex];
        Matrix4x4.Invert(controllerLocal.ToMatrix(), out var inverseLocal);
        var parent = inverseLocal * world[c.ControllerIndex];
        var wanted = world[c.ControllerIndex];
        wanted.Translation = target;
        Matrix4x4.Invert(parent, out var inverseParent);
        return Transform.FromMatrixLoose(wanted * inverseParent) with { Scale = controllerLocal.Scale };
    }

    private static float ChainLength(Case c, Matrix4x4[] world)
    {
        float length = 0;
        for (int j = 1; j < c.JointIndices.Length; j++)
            length += Vector3.Distance(world[c.JointIndices[j]].Translation, world[c.JointIndices[j - 1]].Translation);
        return length;
    }

    private static void AssertSameWorld(Matrix4x4 expected, Matrix4x4 actual, float length, string what)
    {
        Assert.True(Vector3.Distance(expected.Translation, actual.Translation) < 1e-3f * length,
            $"{what}: {expected.Translation} vs {actual.Translation}");
        var a = Transform.FromMatrixLoose(expected).Rotation;
        var b = Transform.FromMatrixLoose(actual).Rotation;
        Assert.True(MathF.Abs(Quaternion.Dot(a, b)) > 1 - 1e-4f, $"{what}: rotation {a} vs {b}");
    }

    private static void AssertClose(Transform expected, Transform actual, string what)
    {
        Assert.True(Vector3.Distance(expected.Translation, actual.Translation) <= 1e-3f * MathF.Max(1, expected.Translation.Length()),
            $"{what}: translation {expected.Translation} vs {actual.Translation}");
        Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected.Rotation), Quaternion.Normalize(actual.Rotation))) > 1 - 1e-5f,
            $"{what}: rotation {expected.Rotation} vs {actual.Rotation}");
    }

    /// <summary>
    /// At an FK key a controller is drawn at its rigged offset from its chain's end, while its stored
    /// key can be anywhere (Dup's retargeted Run at frame 13: IK_Hand_R 44 units from the hand). A
    /// drag starts where it is drawn (<see cref="PoseEvaluator.FollowingLocal"/>), so a small step
    /// moves the chain's end by that step instead of throwing it to the stored key.
    /// </summary>
    [Fact]
    public void DraggingAController_AtAnFKKey_StartsWhereItIsDrawn()
    {
        string? path = TestFiles.UserSamples().FirstOrDefault(p => Path.GetFileName(p).StartsWith("26_retargetRedo", StringComparison.Ordinal));
        if (path == null) return;
        var step = new Vector3(0, 0, -2);
        foreach (bool foot in new[] { false, true })
        foreach (bool rootMotion in new[] { false, true })
        {
            var model = TestFiles.LoadScene(path);
            var character = model.Scene.Characters.First(c => c.Name == "Dup");
            var animation = character.Animations.First(a => a.Name == "Run" && a.Parent?.Parent?.Name == "Retargeted From DupTheClown");
            var transN = animation.Stacks.First(s => s.Name == "TransN");
            if (!rootMotion) transN.Source.Set("Flags", transN.Flags & ~2u);
            var stack = animation.Stacks.First(s => s.Name == "All");
            var editor = new KeyEditor(character);
            var ev = editor.Evaluator;
            const int keyIndex = 4;
            float time = stack.Keys[keyIndex].Time;
            var controller = foot ? character.IKControllers.First(ik => ik is ControllerReverseFoot && ik.Name!.EndsWith("_R", StringComparison.Ordinal))
                : character.IKControllers.First(ik => ik.Name == "IK_Hand_R");
            string what = $"{controller.Name} at frame {time}, TransN {(rootMotion ? "on" : "off")}";
            Assert.False(ev.IsIKActive(animation, controller, time), what + " should be an FK key");
            var start = ev.FollowingLocal(animation, controller, time);
            Assert.NotNull(start);

            int ci = ev.IndexOf(controller);
            // The case only means something where the stored key is away from the drawn handle.
            float stale = Vector3.Distance(ev.LocalTransforms(animation, time)[ci].Translation, start!.Value.Translation);
            Assert.True(stale > 10, $"{what}: the stored key is only {stale} from where it is drawn");
            int end = ev.IndexOf(controller.Chain[^1]);
            var shown = ev.DisplayWorldMatrices(animation, time);
            var world = ev.WorldMatrices(animation, time);
            // The start puts the handle where it is drawn, so posing it there changes nothing.
            var startWorld = shown[ci].Translation;
            editor.Pose(stack, keyIndex, controller, start!.Value with { Translation = start.Value.Translation + step });
            var worldAfter = ev.WorldMatrices(animation, time);
            float moved = Vector3.Distance(worldAfter[end].Translation, world[end].Translation);
            Assert.True(moved < 4 * step.Length(), $"{what}: the chain end moved {moved} for a step of {step.Length()}");
            var shownAfter = ev.DisplayWorldMatrices(animation, time);
            float handle = Vector3.Distance(shownAfter[ci].Translation, startWorld);
            Assert.True(handle < 4 * step.Length(), $"{what}: the handle moved {handle} for a step of {step.Length()}");
        }
    }
}
