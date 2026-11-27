using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The TRANSFORM window's tools in every pose layer: typed values, Reset and Mirror go through one
/// path for an animation, the rest pose and Paint &amp; Check's runtime pose; and the viewport's
/// hierarchy picks (middle click: the children, right click: the joint and its children).
/// </summary>
public class PoseToolTests
{
    private static readonly MirrorMatch Match = MirrorMatch.Default;

    // ---- Paint & Check: typed values and Reset ------------------------------------------------

    [SceneTheory, SceneSamples]
    public void CheckPose_TypedValue_PosesEverySelectedController_AsOneUndoStep(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Plain(model) is not var (character, controllers) || controllers.Count < 2) return;
        var session = Rigging(model, character);
        var pair = controllers.Take(2).ToArray();

        int written = session.SetChannelValue(null, pair, 0, 3, 25, out var reason, checkPose: true);

        Assert.True(written == 2, reason);
        Assert.True(session.Evaluator.HasCheckPose);
        foreach (var c in pair)
        {
            var local = session.Evaluator.LocalTransforms(null, 0)[session.Evaluator.IndexOf(c)];
            Assert.True(MathF.Abs(Curves.ToEulerDegrees(local.Rotation).X - 25) < 0.05f, $"{c.Name} X = {Curves.ToEulerDegrees(local.Rotation).X}");
        }
        Assert.True(session.Undo());
        foreach (var c in pair)
        {
            int i = session.Evaluator.IndexOf(c);
            AssertRotation(session.Evaluator.RestTransforms[i].Rotation, session.Evaluator.LocalTransforms(null, 0)[i].Rotation);
        }
    }

    [SceneTheory, SceneSamples]
    public void CheckPose_Reset_PutsTheTickedChannelsBackToTheReferencePose(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Plain(model) is not var (character, controllers)) return;
        var session = Rigging(model, character);
        var controller = controllers[0];
        int index = session.Evaluator.IndexOf(controller);
        Assert.Equal(1, session.SetChannelValue(null, [controller], 0, 4, 40, out _, checkPose: true));
        Assert.Equal(1, session.SetChannelValue(null, [controller], 0, 0,
            session.Evaluator.RestTransforms[index].Translation.X + 3, out _, checkPose: true));

        int reset = session.ResetChannels(null, [controller], 0, AnimChannels.Rotate, out var reason, checkPose: true);

        Assert.True(reset == 1, reason);
        var rest = session.Evaluator.RestTransforms[index];
        var local = session.Evaluator.LocalTransforms(null, 0)[index];
        AssertRotation(rest.Rotation, local.Rotation);
        Assert.True(MathF.Abs(local.Translation.X - (rest.Translation.X + 3)) < 1e-3f, "translation was not ticked and must stay");
    }

    [SceneTheory, SceneSamples]
    public void RestPose_Reset_IsRefused_WithAReason(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Plain(model) is not var (character, controllers)) return;
        var session = Rigging(model, character);
        Assert.Equal(0, session.ResetChannels(null, [controllers[0]], 0, AnimChannels.Rotate, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
        Assert.False(session.History.CanUndo);
    }

    // ---- Mirror ---------------------------------------------------------------------------------

    [SceneTheory, SceneSamples]
    public void CheckPose_Mirror_PutsTheCounterpartWhereTheReflectedSourceIs(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Plain(model) is not var (character, controllers)) return;
        var session = Rigging(model, character);
        var pair = SidePair(character, controllers, session.Evaluator);
        if (pair is not var (source, target)) return;
        Assert.Equal(1, session.SetChannelValue(null, [source], 0, 3, 30, out _, checkPose: true));
        Assert.Equal(1, session.SetChannelValue(null, [source], 0, 5, -20, out _, checkPose: true));
        var expected = Mirrored(session, null, 0, source, target);
        var targetBefore = Worlds(session, null, 0)[session.Evaluator.IndexOf(target)];

        int written = session.MirrorPoseAt(null, [source], 0, checkPose: true, out var reason);

        Assert.True(written == 1, reason);
        AssertNear(expected, Worlds(session, null, 0)[session.Evaluator.IndexOf(target)], $"{source.Name} -> {target.Name}");
        Assert.True(session.Undo());
        AssertNear(targetBefore, Worlds(session, null, 0)[session.Evaluator.IndexOf(target)], "undo");
    }

    [SceneTheory, SceneSamples]
    public void Mirror_BothSidesSelected_SwapsThemAtTheFrame(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
            {
                var session = new PoseEditSession(new ScenePose(model), character);
                var keyable = Keyable(character, animation, session.Evaluator);
                if (SidePair(character, keyable, session.Evaluator) is not var (source, target)) continue;
                int frame = PoseEditSession.EditableStack(animation, source)!.Keys[0].Time + 1;
                var toTarget = Mirrored(session, animation, frame, source, target);
                var toSource = Mirrored(session, animation, frame, target, source);

                int written = session.MirrorPoseAt(animation, [source, target], frame, checkPose: false, out var reason);

                Assert.True(written == 2, reason);
                var worlds = Worlds(session, animation, frame);
                AssertNear(toTarget, worlds[session.Evaluator.IndexOf(target)], target.Name ?? "target");
                AssertNear(toSource, worlds[session.Evaluator.IndexOf(source)], source.Name ?? "source");
                return;
            }
    }

    [SceneTheory, SceneSamples]
    public void Mirror_CentralJoint_TurnsOntoItself_AndTwiceIsTheIdentity(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Plain(model) is not var (character, controllers)) return;
        var session = Rigging(model, character);
        var central = controllers.FirstOrDefault(c => ReferenceEquals(Match.Find(character, c), c));
        if (central == null) return;
        int index = session.Evaluator.IndexOf(central);
        Assert.Equal(1, session.SetChannelValue(null, [central], 0, 4, 35, out _, checkPose: true));
        var posed = Worlds(session, null, 0)[index];
        var expected = Mirrored(session, null, 0, central, central);

        Assert.Equal(1, session.MirrorPoseAt(null, [central], 0, checkPose: true, out _));
        AssertNear(expected, Worlds(session, null, 0)[index], "once");
        Assert.Equal(1, session.MirrorPoseAt(null, [central], 0, checkPose: true, out _));
        AssertNear(posed, Worlds(session, null, 0)[index], "twice");
    }

    // ---- Paint & Check drag: the animation drag's options --------------------------------------

    [SceneTheory, SceneSamples]
    public void CheckPoseDrag_KeepChildrenFixed_LeavesTheChildWhereItWas(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Plain(model) is not var (character, controllers)) return;
        var session = Rigging(model, character);
        var parent = controllers.FirstOrDefault(c => c.Children.OfType<Object3D>().Any(controllers.Contains));
        if (parent == null) return;
        var child = parent.Children.OfType<Object3D>().First(controllers.Contains);
        int childIndex = session.Evaluator.IndexOf(child);
        var childBefore = Worlds(session, null, 0)[childIndex];

        session.KeepChildrenFixed = true;
        var drag = session.BeginPose(null, [parent], 0, checkPose: true, out var reason);
        Assert.True(drag != null, reason);
        drag!.Rotate(2, 0.6f);
        drag.Commit();

        AssertNear(childBefore, Worlds(session, null, 0)[childIndex], "kept child");
        Assert.False(Matrix4x4.Equals(Worlds(session, null, 0)[session.Evaluator.IndexOf(parent)],
            session.Evaluator.RestWorldMatrices()[session.Evaluator.IndexOf(parent)]), "the parent did not turn");

        // Without the option the child follows its parent.
        Assert.True(session.Undo());
        session.KeepChildrenFixed = false;
        drag = session.BeginPose(null, [parent], 0, checkPose: true, out _);
        drag!.Rotate(2, 0.6f);
        drag.Commit();
        var moved = Worlds(session, null, 0)[childIndex];
        Assert.False(MathF.Abs(moved.M11 - childBefore.M11) + MathF.Abs(moved.M12 - childBefore.M12)
                     + MathF.Abs(moved.M21 - childBefore.M21) < 1e-4f, "the child did not follow");
    }

    [SceneTheory, SceneSamples]
    public void CheckPoseDrag_Swivel_TurnsTheChainAndKeepsItsEnd(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
        {
            var session = Rigging(model, character);
            foreach (var ik in character.IKControllers.Where(i => i is not ControllerReverseFoot))
            {
                var chain = ik.Chain;
                if (chain.Count < 3 || chain.Any(j => session.Evaluator.IndexOf(j) < 0)) continue;
                var drag = session.BeginPose(null, [ik], 0, checkPose: true, out _);
                if (drag is not { CanSwivel: true }) continue;
                int root = session.Evaluator.IndexOf(chain[0]), end = session.Evaluator.IndexOf(chain[^1]);
                var before = Worlds(session, null, 0);

                drag.Swivel(0.7f);
                drag.Commit();

                var after = Worlds(session, null, 0);
                Assert.True(Vector3.Distance(before[end].Translation, after[end].Translation) < 1e-2f * MathF.Max(1, before[end].Translation.Length()),
                    $"{ik.Name}: the chain's end moved");
                Assert.True(Vector3.Distance(before[root].Translation, after[root].Translation) < 1e-2f * MathF.Max(1, before[root].Translation.Length()),
                    $"{ik.Name}: the chain's root moved");
                var middle = session.Evaluator.IndexOf(chain[1]);
                Assert.True(Vector3.Distance(before[middle].Translation, after[middle].Translation) > 1e-4f,
                    $"{ik.Name}: the middle joint did not swing");
                Assert.True(session.Undo());
                return;
            }
        }
        // The IK sample has arm and leg chains, so it must have reached the swivel.
        Assert.False(path.Contains("AddingIK"), "no IK chain could swivel in the IK sample");
    }

    // ---- hierarchy picks ------------------------------------------------------------------------

    [SceneTheory, SceneSamples]
    public void PickHierarchy_ChildrenOnly_JointAndChildren_AndAltRemoves(string path)
    {
        var model = TestFiles.LoadScene(path);
        var root = model.Scene.Characters.SelectMany(c => c.Controllers)
            .FirstOrDefault(c => c.Descendants<Object3D>().Count() >= 2);
        if (root == null) return;
        var other = model.Scene.Characters.SelectMany(c => c.Controllers).First(c => !IsUnder(c, root));
        var selection = new List<Object3D> { other };

        var children = Outliner.PickHierarchy(selection, root, includeRoot: false, PickCombine.Replace);
        Assert.DoesNotContain(root, selection);
        Assert.DoesNotContain(other, selection);
        Assert.Equal(children.Count, selection.Count);
        Assert.Same(children[0], selection[^1]); // the first child leads
        Assert.Equal(root.Descendants<Object3D>().Count(), children.Count);

        Outliner.PickHierarchy(selection, root, includeRoot: true, PickCombine.Add);
        Assert.Contains(root, selection);
        Assert.Same(root, selection[^1]);

        selection.Add(other);
        Outliner.PickHierarchy(selection, root, includeRoot: true, PickCombine.Remove);
        Assert.Equal([other], selection);

        Outliner.PickHierarchy(selection, root, includeRoot: true, PickCombine.Toggle);
        Assert.Contains(root, selection);
        Assert.Contains(other, selection);
        Outliner.PickHierarchy(selection, root, includeRoot: true, PickCombine.Toggle);
        Assert.Equal([other], selection);
    }

    private static bool IsUnder(Object3D o, Object3D root)
    {
        return Outliner.Subtree(root).Contains(o);
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>A session in the Rigging context with the CHAR BANK's Paint &amp; Check row current, where its runtime pose is shown.</summary>
    private static PoseEditSession Rigging(SceneModel model, Character character)
    {
        CharacterBank.Select(character, CharacterBank.Rows(character).First(r => r.Kind == CharacterBankRowKind.PaintAndCheck));
        return new(new ScenePose(model) { RiggingContext = true }, character);
    }

    /// <summary>A character's plain FK controllers (no IK controllers, chains or foot locators).</summary>
    private static (Character, List<Object3D>)? Plain(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
        {
            var evaluator = new PoseEditSession(new ScenePose(model), character).Evaluator;
            var found = character.Controllers.Where(c => IsPlain(character, c) && evaluator.IndexOf(c) >= 0).ToList();
            if (found.Count > 0) return (character, found);
        }
        return null;
    }

    private static bool IsPlain(Character character, Object3D c) =>
        c is not ControllerIK && c is not ReverseFootLocator && !character.IKControllers.Any(ik => ik.Chain.Contains(c));

    /// <summary>Plain controllers whose track keys translation and rotation in <paramref name="animation"/>.</summary>
    private static List<Object3D> Keyable(Character character, Animation animation, PoseEvaluator evaluator) =>
        character.Controllers.Where(c =>
        {
            if (!IsPlain(character, c) || evaluator.IndexOf(c) < 0) return false;
            var stack = PoseEditSession.EditableStack(animation, c);
            var track = stack?.TrackFor(c);
            return stack is { Keys.Count: >= 3 } && track != null
                   && (track.Channels & (AnimChannels.Translate | AnimChannels.Rotate)) == (AnimChannels.Translate | AnimChannels.Rotate)
                   && track.Keys.Take(stack.Keys.Count).All(k => k is TransformKey);
        }).ToList();

    private static (Object3D Source, Object3D Target)? SidePair(Character character, List<Object3D> candidates, PoseEvaluator evaluator)
    {
        foreach (var source in candidates)
            if (Match.Find(character, source) is { } target && !ReferenceEquals(target, source)
                && candidates.Contains(target) && evaluator.IndexOf(target) >= 0)
                return (source, target);
        return null;
    }

    private static Matrix4x4[] Worlds(PoseEditSession session, Animation? animation, int frame)
    {
        session.Evaluator.Invalidate();
        session.Evaluator.LocalTransforms(animation, frame, out var worlds);
        return worlds;
    }

    /// <summary>The counterpart's world the mirror should produce: Rt·M·Rs⁻¹·Ws·M.</summary>
    private static Matrix4x4 Mirrored(PoseEditSession session, Animation? animation, int frame, Object3D source, Object3D target)
    {
        var worlds = Worlds(session, animation, frame);
        var rest = session.Evaluator.RestWorldMatrices();
        Matrix4x4.Invert(session.Character.WorldMatrix, out var characterInverse);
        var reflect = characterInverse * Matrix4x4.CreateScale(-1, 1, 1) * session.Character.WorldMatrix;
        int s = session.Evaluator.IndexOf(source), t = session.Evaluator.IndexOf(target);
        Matrix4x4.Invert(rest[s], out var restInverse);
        return rest[t] * reflect * restInverse * worlds[s] * reflect;
    }

    private static void AssertNear(Matrix4x4 expected, Matrix4x4 actual, string what)
    {
        float scale = 1;
        float difference = 0;
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                scale = MathF.Max(scale, MathF.Abs(expected[r, c]));
                difference = MathF.Max(difference, MathF.Abs(expected[r, c] - actual[r, c]));
            }
        Assert.True(difference <= 2e-3f * scale, $"{what}: off by {difference} (scale {scale})");
    }

    private static void AssertRotation(Quaternion expected, Quaternion actual) =>
        Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual))) > 0.9999f,
            $"rotation {actual} is not {expected}");
}
