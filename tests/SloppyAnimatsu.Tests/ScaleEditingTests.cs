using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>The gizmo's scale mode and <see cref="EditDrag.Scale"/>.</summary>
public class ScaleEditingTests
{
    [Fact]
    public void Gizmo_DragScale_DoublesPerHandleLength_AndIsNeverNegative()
    {
        var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5, Yaw = 0, Pitch = 0 };
        var viewport = new Vector2(800, 600);
        // Scale handles follow the controller's own axes, not its parent's.
        var gizmo = new TransformGizmo { Visible = true, Mode = GizmoMode.Scale, CanScale = true,
            Frame = Matrix4x4.CreateRotationZ(MathF.PI / 2), ParentFrame = Matrix4x4.CreateScale(3) };
        float size = gizmo.Size(camera);
        var c = camera.Project(Vector3.Zero, viewport)!.Value;
        var tipX = camera.Project(gizmo.Axis(0) * size, viewport)!.Value;

        Assert.Equal(2f, gizmo.DragScale(camera, viewport, 0, tipX - c), 3);
        Assert.Equal(1f, gizmo.DragScale(camera, viewport, 0, new Vector2(40, 0)), 3); // across the (screen-vertical) X handle
        Assert.Equal(0f, gizmo.DragScale(camera, viewport, 0, (c - tipX) * 3), 3);

        float handle = TransformGizmo.SizeOfView * viewport.Y;
        Assert.Equal(2f, gizmo.DragScale(camera, viewport, TransformGizmo.Uniform, new Vector2(handle, 0)), 3);
        Assert.Equal(1.5f, gizmo.DragScale(camera, viewport, TransformGizmo.Uniform, new Vector2(0, -handle / 2)), 3);
    }

    [Fact]
    public void Gizmo_HitTest_FindsScaleAndUniformHandles()
    {
        var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5, Yaw = 0, Pitch = 0 };
        var viewport = new Vector2(800, 600);
        var gizmo = new TransformGizmo { Visible = true, Mode = GizmoMode.Scale, CanScale = true };
        float size = gizmo.Size(camera);
        var onX = camera.Project(new Vector3(size * 0.7f, 0, 0), viewport)!.Value;
        // The uniform triangle's edge between X and Y at 0.3 of the handle length.
        var onUniform = camera.Project(new Vector3(size * 0.15f, size * 0.15f, 0), viewport)!.Value;

        Assert.Equal(0, gizmo.HitTest(camera, viewport, onX + new Vector2(0, 3), 6));
        Assert.Equal(TransformGizmo.Uniform, gizmo.HitTest(camera, viewport, onUniform, 4));

        gizmo.CanScale = false;
        Assert.Equal(-1, gizmo.HitTest(camera, viewport, onX, 6));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InverseMix_Scale_RoundTripsThroughTheForwardMix(bool additive)
    {
        var below = new Transform(Vector3.Zero, Quaternion.Identity, new Vector3(1.5f, 0.8f, 2f));
        var posed = new Transform(Vector3.Zero, Quaternion.Identity, new Vector3(0.6f, 1.2f, 2.5f));
        var v = KeyEditor.InverseMix(below, posed, 0.4f, additive);
        var forward = additive
            ? PoseEvaluator.MixAdditive(below, v, 0.4f, AnimChannels.Scale)
            : PoseEvaluator.MixOverride(below, v, 0.4f, AnimChannels.Scale);
        Assert.True(Vector3.Distance(posed.Scale, forward.Scale) < 1e-4f, $"{forward.Scale} != {posed.Scale}");
    }

    [SceneTheory, SceneSamples]
    public void ScaleDrag_KeysOneAxisOrAll_AndMixesToTheDraggedPose(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, controller, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character);

        var drag = session.Begin(animation, controller, frame, out var reason);
        Assert.NotNull(drag);
        Assert.Null(reason);
        Assert.True(drag.CanScale);
        var b = drag.BaseLocal.Scale;

        drag.Scale(1, 1.5f);
        AssertClose(drag.BaseLocal with { Scale = b with { Y = b.Y * 1.5f } }, Mixed(session, animation, controller, frame));

        // A later scale on the same handle replaces the earlier one.
        drag.Scale(1, 0.5f);
        AssertClose(drag.BaseLocal with { Scale = b with { Y = b.Y * 0.5f } }, Mixed(session, animation, controller, frame));

        drag.Scale(TransformGizmo.Uniform, 2f);
        AssertClose(drag.BaseLocal with { Scale = b * 2 }, Mixed(session, animation, controller, frame));

        // Scaling to nothing stops at the evaluation floor, keeping the sign.
        drag.Scale(0, 0f);
        Assert.Equal(MathF.Sign(b.X) * HermiteKeyInterpolator.MinScale, drag.Posed.Scale.X, 6);
        Assert.Equal(b.Y * 2, drag.Posed.Scale.Y, 4);
        drag.Cancel();
    }

    [SceneTheory, SceneSamples]
    public void ScaleDrag_OnAPartlyOpaqueStack_InverseMixesTheKey(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, controller, frame)) return;
        var stack = PoseEditSession.EditableStack(animation, controller)!;
        stack.Source.Set("Opacity", 0.5f);
        var session = new PoseEditSession(new ScenePose(model), character);

        var drag = session.Begin(animation, controller, frame, out _)!;
        drag.Scale(TransformGizmo.Uniform, 1.7f);
        AssertClose(drag.Posed, Mixed(session, animation, controller, frame));
        // The key holds more than the shown change, since only half of it is mixed in.
        var key = ((TransformKey)drag.Track.Keys[drag.KeyIndex]).Value.Scale;
        var shown = PoseEvaluator.Decompose(session.Evaluator.RestTransforms[session.Evaluator.IndexOf(controller)], drag.Posed).Scale;
        Assert.NotEqual(shown, key);
    }

    [SceneTheory, SceneSamples]
    public void ScaleDrag_Cancel_Undo_Redo(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, controller, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var stack = PoseEditSession.EditableStack(animation, controller)!;
        var original = Values(stack);

        var drag = session.Begin(animation, controller, frame, out _)!;
        drag.Scale(2, 1.8f);
        drag.Cancel();
        Assert.Equal(original, Values(stack));
        Assert.False(session.History.CanUndo);

        drag = session.Begin(animation, controller, frame, out _)!;
        drag.Scale(TransformGizmo.Uniform, 0.6f);
        var posed = drag.Posed;
        drag.Commit();
        var edited = Values(stack);
        Assert.NotEqual(original, edited);

        Assert.True(session.Undo());
        Assert.Equal(original, Values(stack));
        Assert.Equal(-1, PoseEditSession.KeyAt(stack, frame));

        Assert.True(session.Redo());
        Assert.Equal(edited, Values(stack));
        AssertClose(posed, Mixed(session, animation, controller, frame));
    }

    [SceneTheory, SceneSamples]
    public void ScaleKey_SurvivesSaveAndReload(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, controller, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var drag = session.Begin(animation, controller, frame, out _)!;
        drag.Scale(0, 1.25f);
        drag.Commit();
        var posed = drag.Posed;

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-scale-edit-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(temp);
            var reloaded = TestFiles.LoadScene(temp);
            var c2 = reloaded.Scene.Characters.Single(c => c.Name == character.Name && c.Controllers.Any(o => o.Name == controller.Name));
            var a2 = c2.Animations.First(a => a.Name == animation.Name);
            var o2 = c2.Controllers.First(o => o.Name == controller.Name);
            var stack2 = PoseEditSession.EditableStack(a2, o2)!;
            Assert.True(PoseEditSession.KeyAt(stack2, frame) >= 0);
            var evaluator = new PoseEvaluator(c2);
            var mixed = PoseEvaluator.Compose(evaluator.RestTransforms[evaluator.IndexOf(o2)], evaluator.MixDelta(a2, o2, frame));
            AssertClose(posed, mixed);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [SceneTheory, SceneSamples]
    public void ScaleDrag_OnATrackWithoutScale_DoesNothing(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                foreach (var controller in character.Controllers)
                {
                    if (controller is ControllerIK || character.IKControllers.Any(ik => ik.Chain.Contains(controller))) continue;
                    if (PoseEditSession.EditableStack(animation, controller) is not { } stack) continue;
                    if ((stack.TrackFor(controller)!.Channels & AnimChannels.Scale) != 0 || stack.Keys.Count < 2) continue;
                    var session = new PoseEditSession(new ScenePose(model), character);
                    var drag = session.Begin(animation, controller, stack.Keys[0].Time, out _);
                    if (drag == null) continue;
                    var before = Values(stack);
                    Assert.False(drag.CanScale);
                    drag.Scale(TransformGizmo.Uniform, 2f);
                    Assert.Equal(before, Values(stack));
                    drag.Cancel();
                    return;
                }
    }

    /// <summary>
    /// A controller whose track animates all of scale, keyed in a stack whose first two keys are
    /// at least two frames apart, with the frame just after the first key, and a mixed scale
    /// well above the floor there.
    /// </summary>
    private static (Character, Animation, Object3D, int)? Candidate(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                foreach (var controller in character.Controllers)
                {
                    if (controller is ControllerIK) continue;
                    if (character.IKControllers.Any(ik => ik.Chain.Contains(controller))) continue;
                    if (PoseEditSession.EditableStack(animation, controller) is not { } stack) continue;
                    var track = stack.TrackFor(controller)!;
                    if ((track.Channels & AnimChannels.Scale) != AnimChannels.Scale) continue;
                    var keys = stack.Keys;
                    if (keys.Count < 3 || keys[1].Time - keys[0].Time < 2) continue;
                    if (track.Keys.Take(keys.Count).Any(k => k is not TransformKey)) continue;
                    int frame = keys[0].Time + 1;
                    var evaluator = new PoseEvaluator(character);
                    var mixed = PoseEvaluator.Compose(evaluator.RestTransforms[evaluator.IndexOf(controller)], evaluator.MixDelta(animation, controller, frame));
                    var s = Vector3.Abs(mixed.Scale);
                    if (MathF.Min(s.X, MathF.Min(s.Y, s.Z)) < 0.05f) continue;
                    return (character, animation, controller, frame);
                }
        return null;
    }

    [SceneTheory, SceneSamples]
    public void PlaneDrag_MovesAlongTheTwoOtherAxes_AndReplacesTheEarlierMove(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, animation, controller, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var drag = session.Begin(animation, controller, frame, out _);
        Assert.NotNull(drag);
        if (!drag.CanTranslate) return;
        var b = drag.BaseLocal.Translation;

        // The plane across Y moves along Z then X (PlaneAxes order).
        Assert.Equal((2, 0), TransformGizmo.PlaneAxes(TransformGizmo.PlaneX + 1));
        drag.TranslatePlane(1, 7f, -3f);
        AssertClose(drag.BaseLocal with { Translation = b + new Vector3(-3f, 0, 7f) }, Mixed(session, animation, controller, frame));

        drag.TranslatePlane(1, 1f, 2f);
        AssertClose(drag.BaseLocal with { Translation = b + new Vector3(2f, 0, 1f) }, Mixed(session, animation, controller, frame));

        // Discrete move snaps each of the two axes to the step.
        drag.Snap.MoveDiscrete = true;
        drag.Snap.MoveStep = 5f;
        drag.TranslatePlane(2, 6.4f, 0f);
        var moved = Mixed(session, animation, controller, frame).Translation;
        Assert.Equal(0f, MathF.IEEERemainder(moved.X, 5f), 2);
        Assert.Equal(0f, MathF.IEEERemainder(moved.Y, 5f), 2);
        Assert.Equal(b.Z, moved.Z, 2);
    }

    private static Transform Mixed(PoseEditSession session, Animation animation, Object3D controller, int frame) =>
        PoseEvaluator.Compose(session.Evaluator.RestTransforms[session.Evaluator.IndexOf(controller)],
            session.Evaluator.MixDelta(animation, controller, frame));

    private static string Values(AnimStack stack) =>
        string.Join(";", stack.Keys) + "|" + string.Join("|", stack.Tracks.Select(t =>
            string.Join(";", t.Keys.Select(k => k is TransformKey tk ? tk.Value.ToString() : "-"))));

    private static void AssertClose(Transform expected, Transform actual)
    {
        Assert.True(Vector3.Distance(expected.Translation, actual.Translation) < 1e-3f * Math.Max(1, expected.Translation.Length()),
            $"translation {expected.Translation} != {actual.Translation}");
        Assert.True(Vector3.Distance(expected.Scale, actual.Scale) < 1e-3f * Math.Max(1, expected.Scale.Length()),
            $"scale {expected.Scale} != {actual.Scale}");
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected.Rotation), Quaternion.Normalize(actual.Rotation)));
        Assert.True(dot > 1 - 1e-4f, $"rotation {expected.Rotation} != {actual.Rotation}");
    }
}
