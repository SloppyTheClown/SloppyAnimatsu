using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>Dragging several controllers at once.</summary>
public class MultiSelectionEditingTests
{
    [SceneTheory, SceneSamples]
    public void Drag_GivesEveryControllerTheLeadsWorldChange_AsOneUndoStep(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidates(model) is not var (character, animation, controllers, frame)) return;
        // Two controllers apart in the hierarchy (neither above the other).
        var (other, lead) = controllers.SelectMany(a => controllers.Where(b => a != b && !IsAncestor(a, b) && !IsAncestor(b, a))
            .Select(b => (a, b))).FirstOrDefault();
        if (other == null) return;
        // A scale-compensated controller's world is composed from its parent's scale, not by
        // multiplying the parent's world,
        // so the plain change identity asserted below does not hold for it.
        if (lead.CompensatesParentScale || other.CompensatesParentScale) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var stacks = new[] { other, lead }.Select(c => PoseEditSession.EditableStack(animation, c)!).Distinct().ToArray();
        var original = stacks.Select(Values).ToArray();
        var start = Worlds(session, animation, frame);

        var drag = Assert.IsType<GroupDrag>(session.Begin(animation, [other, lead], frame, out var reason));
        Assert.Null(reason);
        Assert.Same(lead, drag.Controller);
        drag.Rotate(2, 0.6f);
        drag.Commit();

        var end = Worlds(session, animation, frame);
        int li = session.Evaluator.IndexOf(lead), oi = session.Evaluator.IndexOf(other);
        Matrix4x4.Invert(start[li], out var leadInverse);
        var change = leadInverse * end[li];
        // A non-uniform scale about a pivot shears a rotated object, and SloppyAnimatsu deliberately
        // drops the shear when it stores the result as a local transform
        // so the exact world
        // change is only asserted where the composed target is shear-free.
        if (!IsShearFree(start[oi] * change)) return;
        // The other controller turned about the lead's pivot, not about its own.
        AssertClose(start[oi] * change, end[oi]);
        Assert.True(Vector3.Distance(start[oi].Translation, end[oi].Translation) > 1e-4f
                    || Vector3.Distance(start[oi].Translation, start[li].Translation) < 1e-3f, "the other controller did not move");

        Assert.True(session.Undo());
        Assert.False(session.History.CanUndo);
        Assert.Equal(original, stacks.Select(Values).ToArray());
    }

    [SceneTheory, SceneSamples]
    public void SelectedChild_OfASelectedParent_KeepsItsLocalTransform_WhicheverLeads(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidates(model) is not var (character, animation, controllers, frame)) return;
        var pair = controllers.SelectMany(p => controllers.Where(c => c.Parent3D == p).Select(c => (Parent: p, Child: c))).FirstOrDefault();
        if (pair.Parent == null) return;

        foreach (bool parentLeads in new[] { true, false })
        {
            var session = new PoseEditSession(new ScenePose(model), character);
            int pi = session.Evaluator.IndexOf(pair.Parent), ci = session.Evaluator.IndexOf(pair.Child);
            var startLocal = session.Evaluator.LocalTransforms(animation, frame);
            var start = Worlds(session, animation, frame);

            Object3D[] selection = parentLeads ? [pair.Child, pair.Parent] : [pair.Parent, pair.Child];
            var drag = session.Begin(animation, selection, frame, out _)!;
            drag.Rotate(0, 0.5f);
            var posed = drag.Posed;

            var local = session.Evaluator.LocalTransforms(animation, frame);
            var end = Worlds(session, animation, frame);
            if (parentLeads)
            {
                // The child rides along: same local, so the same world change as its parent.
                AssertClose(startLocal[ci].ToMatrix(), local[ci].ToMatrix());
                AssertClose(posed.ToMatrix(), local[pi].ToMatrix());
            }
            else
            {
                // The child leads; its parent turns about the child's pivot, and the child ends
                // where the drag alone would have put it.
                Matrix4x4.Invert(start[ci], out var childInverse);
                AssertClose(start[pi] * (childInverse * end[ci]), end[pi]);
                AssertClose(startLocal[ci].ToMatrix(), local[ci].ToMatrix());
            }
            drag.Cancel();
            AssertClose(start[ci], Worlds(session, animation, frame)[ci]);
        }
    }

    [SceneTheory, SceneSamples]
    public void Followers_ThatCannotBeKeyed_AreLeftAlone(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidates(model) is not var (character, animation, controllers, frame)) return;
        var untracked = character.Controllers.FirstOrDefault(c => PoseEditSession.EditableStack(animation, c) == null);
        if (untracked == null) return;
        var session = new PoseEditSession(new ScenePose(model), character);

        var drag = Assert.IsType<GroupDrag>(session.Begin(animation, [untracked, controllers[0], controllers[^1]], frame, out _));
        Assert.DoesNotContain(drag.Members, m => m.Controller == untracked);
        Assert.Equal(2, drag.Members.Count);
        drag.Cancel();

        // A lead that cannot be keyed refuses the whole drag.
        Assert.Null(session.Begin(animation, [controllers[0], untracked], frame, out var reason));
        Assert.NotNull(reason);
    }

    /// <summary>
    /// An animation and at least two of its character's controllers that a drag can key in
    /// translation and rotation (no IK, transform keys only), with a frame just after the first
    /// key of the first one's stack. Shoe locators are left out: their reverse-foot controller
    /// follows its leg at an FK key, so a locator moves with any selected joint above the leg
    /// even though neither is the other's ancestor.
    /// </summary>
    private static (Character, Animation, List<Object3D>, int)? Candidates(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
            {
                var found = character.Controllers.Where(c =>
                    c is not ControllerIK && c is not ReverseFootLocator && !character.IKControllers.Any(ik => ik.Chain.Contains(c))
                    && PoseEditSession.EditableStack(animation, c) is { } stack && stack.Keys.Count >= 3
                    && stack.TrackFor(c) is { } track
                    && (track.Channels & (AnimChannels.Translate | AnimChannels.Rotate)) == (AnimChannels.Translate | AnimChannels.Rotate)
                    && track.Keys.Take(stack.Keys.Count).All(k => k is TransformKey)).Take(40).ToList();
                if (found.Count < 2) continue;
                var keys = PoseEditSession.EditableStack(animation, found[0])!.Keys;
                return (character, animation, found, keys[0].Time + 1);
            }
        return null;
    }

    private static bool IsAncestor(Object3D ancestor, Object3D o)
    {
        for (var p = o.Parent3D; p != null; p = p.Parent3D)
            if (p == ancestor) return true;
        return false;
    }

    private static Matrix4x4[] Worlds(PoseEditSession session, Animation animation, int frame) =>
        session.Evaluator.WorldMatrices(animation, frame);

    private static string Values(AnimStack stack) =>
        string.Join(";", stack.Keys) + "|" + string.Join("|", stack.Tracks.Select(t =>
            string.Join(";", t.Keys.Select(k => k is TransformKey tk ? tk.Value.ToString() : "-"))));

    /// <summary>
    /// True when the matrix's three axes stay orthogonal, i.e. it carries rotation, translation
    /// and scale but no shear - the only kind of world change a local transform can store.
    /// </summary>
    private static bool IsShearFree(Matrix4x4 m)
    {
        var x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
        var y = Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23));
        var z = Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33));
        return MathF.Abs(Vector3.Dot(x, y)) < 1e-3f
            && MathF.Abs(Vector3.Dot(x, z)) < 1e-3f
            && MathF.Abs(Vector3.Dot(y, z)) < 1e-3f;
    }

    private static void AssertClose(Matrix4x4 expected, Matrix4x4 actual)
    {
        float size = MathF.Max(1, expected.Translation.Length());
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.True(MathF.Abs(expected[r, c] - actual[r, c]) < 2e-3f * size, $"[{r},{c}] {expected} != {actual}");
    }
}
