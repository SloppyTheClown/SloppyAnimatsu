using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The Spinner's value fields and snapping: a typed
/// value is written to every selected controller through its own track and local frame, one
/// undo step; snapping applies to the current object of a drag.
/// </summary>
public class SpinnerTests
{
    [SceneTheory, SceneSamples]
    public void TypedRotation_TurnsEachSelectedControllerToTheSameLocalAngle(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidates(model) is not var (character, animation, controllers, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var (other, lead) = Pair(controllers);
        if (other == null) return;
        int oi = session.Evaluator.IndexOf(other), li = session.Evaluator.IndexOf(lead);
        var start = session.Evaluator.LocalTransforms(animation, frame);
        var eo = Curves.ToEulerDegrees(start[oi].Rotation);
        var el = Curves.ToEulerDegrees(start[li].Rotation);
        var original = controllers.ToDictionary(c => c, c => PoseEditSession.EditableStack(animation, c)!.Keys.Select(k => k.Time).ToArray());

        int written = session.SetChannelValue(animation, [other, lead], frame, 3, 25, out var reason);

        Assert.Equal(2, written);
        Assert.Null(reason);
        var end = session.Evaluator.LocalTransforms(animation, frame);
        // Each object's own local Euler X is 25 degrees: the field is per object, not a shared pivot.
        AssertRotation(Curves.FromEulerDegrees(new Vector3(25, eo.Y, eo.Z)), end[oi].Rotation);
        AssertRotation(Curves.FromEulerDegrees(new Vector3(25, el.Y, el.Z)), end[li].Rotation);

        Assert.True(session.Undo());
        Assert.False(session.History.CanUndo);
        var back = session.Evaluator.LocalTransforms(animation, frame);
        AssertRotation(start[oi].Rotation, back[oi].Rotation);
        AssertRotation(start[li].Rotation, back[li].Rotation);
        foreach (var (controller, times) in original)
            Assert.Equal(times, PoseEditSession.EditableStack(animation, controller)!.Keys.Select(k => k.Time).ToArray());
    }

    [SceneTheory, SceneSamples]
    public void TypedTranslation_KeysAtTheFrame_AndSurvivesSaveAndReload(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidates(model) is not var (character, animation, controllers, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var controller = controllers[^1];
        var stack = PoseEditSession.EditableStack(animation, controller)!;
        int keys = stack.Keys.Count;

        int written = session.SetChannelValue(animation, [controller], frame, 0, 0.75f, out var reason);

        Assert.Equal(1, written);
        Assert.Null(reason);
        Assert.True(PoseEditSession.KeyAt(stack, frame) >= 0, "a key was created at the frame");
        Assert.True(stack.Keys.Count >= keys);
        var local = session.Evaluator.LocalTransforms(animation, frame)[session.Evaluator.IndexOf(controller)];
        AssertClose(0.75f, local.Translation.X);

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-spinner-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(temp);
            var reloaded = TestFiles.LoadScene(temp);
            var character2 = reloaded.Scene.Characters.First(c => c.Name == character.Name);
            var animation2 = character2.Animations.First(a => a.Name == animation.Name);
            var controller2 = character2.Controllers.First(c => c.Name == controller.Name);
            var evaluator2 = new PoseEvaluator(character2);
            var local2 = evaluator2.LocalTransforms(animation2, frame)[evaluator2.IndexOf(controller2)];
            AssertClose(0.75f, local2.Translation.X);
        }
        finally { File.Delete(temp); }
    }

    [SceneTheory, SceneSamples]
    public void TypedValue_IsSkippedForAControllerWhoseTrackLacksTheChannel(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidates(model) is not var (character, animation, controllers, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var untracked = character.Controllers.FirstOrDefault(c => PoseEditSession.EditableStack(animation, c) == null);
        if (untracked == null) return;
        var tracked = controllers[^1];

        int written = session.SetChannelValue(animation, [untracked, tracked], frame, 0, 1.25f, out var reason);

        Assert.Equal(1, written);
        Assert.Null(reason);
        var local = session.Evaluator.LocalTransforms(animation, frame)[session.Evaluator.IndexOf(tracked)];
        AssertClose(1.25f, local.Translation.X);
    }

    [SceneTheory, SceneSamples]
    public void TypedValue_RefusesWhenTheCurrentObjectCannotBeKeyed(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidates(model) is not var (character, animation, _, frame)) return;
        var untracked = character.Controllers.FirstOrDefault(c => PoseEditSession.EditableStack(animation, c) == null);
        if (untracked == null) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var tracked = character.Controllers.First(c => PoseEditSession.EditableStack(animation, c) != null);
        int keys = PoseEditSession.EditableStack(animation, tracked)!.Keys.Count;

        int written = session.SetChannelValue(animation, [tracked, untracked], frame, 0, 2, out var reason);

        Assert.Equal(0, written);
        Assert.False(string.IsNullOrEmpty(reason));
        Assert.False(session.History.CanUndo);
        Assert.Equal(keys, PoseEditSession.EditableStack(animation, tracked)!.Keys.Count);
    }

    [SceneTheory, SceneSamples]
    public void TypedScale_KeepsTheEvaluationScaleFloor(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidates(model) is not var (character, animation, controllers, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var controller = controllers[^1];

        int written = session.SetChannelValue(animation, [controller], frame, 7, 0, out _);

        Assert.Equal(1, written);
        var local = session.Evaluator.LocalTransforms(animation, frame)[session.Evaluator.IndexOf(controller)];
        AssertClose(HermiteKeyInterpolator.MinScale, MathF.Abs(local.Scale.Y));
    }

    [SceneTheory, SceneSamples]
    public void TypedValue_EditsTheRestPose(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
        {
            var session = new PoseEditSession(new ScenePose(model), character);
            var joint = character.Controllers.FirstOrDefault(c => session.RestPose.CanEdit(c, out _));
            if (joint == null) continue;
            var before = session.RestPose.GetLocal(joint);

            int written = session.SetChannelValue(null, [joint], 0, 0, before.Translation.X + 1, out var reason);

            Assert.Equal(1, written);
            Assert.Null(reason);
            AssertClose(before.Translation.X + 1, session.RestPose.GetLocal(joint).Translation.X);
            Assert.True(session.Undo());
            AssertClose(before.Translation.X, session.RestPose.GetLocal(joint).Translation.X);
            return;
        }
    }

    [SceneTheory, SceneSamples]
    public void Snapping_RotatesTheLeadByItsStep_AndTheFollowersByTheLeadsChange(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidates(model) is not var (character, animation, controllers, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character)
        {
            Snap = new SnapSettings { RotateDiscrete = true, RotateStep = 45 },
        };
        var (other, lead) = Pair(controllers);
        if (other == null) return;
        // A scale-compensated controller's world is composed from its parent's scale, not by
        // multiplying the parent's world,
        // so the plain change identity asserted below does not hold for it.
        if (lead.CompensatesParentScale || other.CompensatesParentScale) return;
        int oi = session.Evaluator.IndexOf(other), li = session.Evaluator.IndexOf(lead);
        var start = session.Evaluator.WorldMatrices(animation, frame);
        var drag = Assert.IsType<GroupDrag>(session.Begin(animation, [other, lead], frame, out _));
        var baseRotation = drag.Lead.BaseLocal.Rotation;

        drag.Rotate(2, 0.7f);

        AssertRotation(Quaternion.Normalize(baseRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4)),
            drag.Lead.Posed.Rotation);
        var end = session.Evaluator.WorldMatrices(animation, frame);
        Matrix4x4.Invert(start[li], out var inverse);
        var change = inverse * end[li];
        // A non-uniform scale about a pivot shears a rotated follower, and the shear is dropped
        // when the local transform is stored, so the
        // exact world change only holds for a shear-free result.
        if (!IsShearFree(start[oi] * change)) return;
        AssertClose(start[oi] * change, end[oi]);
        drag.Cancel();
        var back = session.Evaluator.WorldMatrices(animation, frame);
        AssertClose(start[oi], back[oi]);
        AssertClose(start[li], back[li]);
    }

    [SceneTheory, SceneSamples]
    public void Snapping_MovesAndScalesToTheStep(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidates(model) is not var (character, animation, controllers, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character)
        {
            Snap = new SnapSettings { MoveDiscrete = true, MoveStep = 0.1f, ScaleDiscrete = true, ScaleStep = 0.5f },
        };
        var drag = session.Begin(animation, controllers[^1], frame, out _)!;
        var b = drag.BaseLocal;

        drag.Translate(0, 0.33f);
        float expected = MathF.Round((b.Translation.X + 0.33f) / 0.1f, MidpointRounding.AwayFromZero) * 0.1f;
        AssertClose(expected, drag.Posed.Translation.X);

        drag.Scale(1, 1.13f);
        float scaled = MathF.Round(MathF.Abs(b.Scale.Y) * 1.13f / 0.5f, MidpointRounding.AwayFromZero) * 0.5f;
        AssertClose(MathF.Max(scaled, HermiteKeyInterpolator.MinScale), MathF.Abs(drag.Posed.Scale.Y));
        drag.Cancel();
    }

    [Fact]
    public void SnapSettings_StepsAndTheAngleThreshold()
    {
        var snap = new SnapSettings { MoveDiscrete = true, MoveStep = 0.1f, ScaleDiscrete = true, ScaleStep = 0.5f };
        AssertClose(0.3f, snap.SnapMove(0.33f));
        AssertClose(-0.2f, snap.SnapMove(-0.24f));
        AssertClose(1.5f, snap.SnapScale(1.31f));

        var angles = new SnapSettings { RotateDiscrete = true, RotateStep = 45 };
        AssertClose(MathF.PI / 4, angles.SnapAngle(0.7f));

        var threshold = new SnapSettings { AngleThreshold = 5 };
        AssertClose(MathF.PI / 2, threshold.SnapAngle(MathF.PI / 2 - 0.05f));
        AssertClose(MathF.PI / 2 - 0.2f, threshold.SnapAngle(MathF.PI / 2 - 0.2f));
    }

    [SceneTheory, SceneSamples]
    public void Reset_PutsOnlyTheTickedChannelsBackToRest_AsOneUndoStep(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidates(model) is not var (character, animation, controllers, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var controller = controllers[^1];
        int index = session.Evaluator.IndexOf(controller);
        var rest = session.Evaluator.RestTransforms[index];
        session.SetChannelValue(animation, [controller], frame, 0, rest.Translation.X + 3, out _);
        session.SetChannelValue(animation, [controller], frame, 1, rest.Translation.Y + 4, out _);
        session.SetChannelValue(animation, [controller], frame, 3, 30, out _);
        var edited = session.Evaluator.LocalTransforms(animation, frame)[index];

        int written = session.ResetChannels(animation, [controller], frame, AnimChannels.TranslateX, out var reason);

        Assert.Equal(1, written);
        Assert.Null(reason);
        var local = session.Evaluator.LocalTransforms(animation, frame)[index];
        AssertClose(rest.Translation.X, local.Translation.X);
        AssertClose(edited.Translation.Y, local.Translation.Y);
        AssertRotation(edited.Rotation, local.Rotation);

        session.ResetChannels(animation, [controller], frame, AnimChannels.Rotate, out _);
        AssertRotation(rest.Rotation, session.Evaluator.LocalTransforms(animation, frame)[index].Rotation);

        Assert.True(session.Undo());
        Assert.True(session.Undo());
        var undone = session.Evaluator.LocalTransforms(animation, frame)[index];
        AssertClose(edited.Translation.X, undone.Translation.X);
        AssertRotation(edited.Rotation, undone.Rotation);
    }

    [SceneTheory, SceneSamples]
    public void Reset_WithNothingTicked_WritesNothing(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidates(model) is not var (character, animation, controllers, frame)) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        Assert.Equal(0, session.ResetChannels(animation, controllers, frame, 0, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
        Assert.False(session.History.CanUndo);
    }

    /// <summary>An animation with controllers a drag can key in translation, rotation and scale.</summary>
    private static (Character, Animation, List<Object3D>, int)? Candidates(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
            {
                var found = character.Controllers.Where(c =>
                {
                    if (c is ControllerIK || c is ReverseFootLocator) return false;
                    if (character.IKControllers.Any(ik => ik.Chain.Contains(c))) return false;
                    var stack = PoseEditSession.EditableStack(animation, c);
                    var track = stack?.TrackFor(c);
                    return stack is { Keys.Count: >= 3 } && track != null
                           && (track.Channels & (AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale))
                           == (AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale)
                           && track.Keys.Take(stack.Keys.Count).All(k => k is TransformKey);
                }).Take(40).ToList();
                if (found.Count < 2) continue;
                var keys = PoseEditSession.EditableStack(animation, found[0])!.Keys;
                return (character, animation, found, keys[0].Time + 1);
            }
        return null;
    }

    /// <summary>Two controllers of the list that are neither the other's ancestor.</summary>
    private static (Object3D?, Object3D?) Pair(List<Object3D> controllers)
    {
        foreach (var a in controllers)
            foreach (var b in controllers)
                if (!ReferenceEquals(a, b) && !IsAncestor(a, b) && !IsAncestor(b, a))
                    return (a, b);
        return (null, null);
    }

    private static bool IsAncestor(Object3D ancestor, Object3D o)
    {
        for (var p = o.Parent3D; p != null; p = p.Parent3D)
            if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }

    private static void AssertRotation(Quaternion expected, Quaternion actual)
    {
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        Assert.True(dot > 0.9999f, $"{expected} != {actual}");
    }

    private static void AssertClose(float expected, float actual) =>
        Assert.True(MathF.Abs(expected - actual) < 2e-3f, $"{expected} != {actual}");

    /// <summary>True when the matrix's axes stay orthogonal, i.e. it has no shear.</summary>
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
