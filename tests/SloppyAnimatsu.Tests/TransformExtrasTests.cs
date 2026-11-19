using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The TRANSFORM window's extra rows and Reset &amp; Freeze:
/// a typed pole, reverse-foot or blendshape value is written to the editable track at the
/// playhead as one undo step; Reset &amp; Freeze bakes an unskinned mesh's LTM into its geometry.
/// </summary>
public class TransformExtrasTests
{
    [SceneTheory, SceneSamples]
    public void SetScalarChannel_WritesEachExtraChannelAtThePlayhead_AsOneUndoStep(string path)
    {
        var model = TestFiles.LoadScene(path);
        int tested = 0;
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                foreach (int channel in new[] { 9, 10, 11, 12, 13, 14 })
                {
                    var bit = (AnimChannels)(1u << channel);
                    var controller = character.Controllers.FirstOrDefault(c =>
                        PoseEditSession.EditableStack(animation, c) is { IsAdditive: false, Keys.Count: >= 3 } s
                        && s.TrackFor(c) is { } t && (t.Channels & bit) != 0);
                    if (controller == null) continue;
                    var session = new PoseEditSession(new ScenePose(model), character);
                    var stack = PoseEditSession.EditableStack(animation, controller)!;
                    int frame = (int)stack.Keys[1].Time;
                    float before = Read(session, animation, stack, controller, channel, frame);
                    float value = before + (channel >= 13 ? 0.25f : 0.2f);

                    int written = session.SetScalarChannel(animation, [controller], frame, channel, value, out var reason);

                    Assert.True(written == 1, reason);
                    Assert.Equal(value, Read(session, animation, stack, controller, channel, frame), 3);
                    Assert.True(session.Undo());
                    Assert.Equal(before, Read(session, animation, stack, controller, channel, frame), 3);
                    Assert.False(session.History.CanUndo);
                    tested++;
                    if (tested >= 4) return;
                }
        // 19_run keys IK chains, so the pole angle at least is met there (7_AddingIK has no animation).
        Assert.True(tested > 0 || !model.Scene.Characters.Any(c => c.IKControllers.Any() && c.Animations.Any()),
            "no extra channel was found to edit");
    }

    /// <summary>
    /// A roll, twist or bank edit changes the controller's key, so the leg follows the shoe at FK
    /// and IK keys alike: the ankle moves, and undo puts it back.
    /// </summary>
    [SceneTheory, SceneSamples]
    public void SetScalarChannel_FootAnglesMoveTheLeg(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                foreach (var rf in character.Controllers.OfType<ControllerReverseFoot>())
                {
                    var stack = PoseEditSession.EditableStack(animation, rf);
                    if (stack is not { IsAdditive: false, Keys.Count: >= 3 } || stack.TrackFor(rf) is not { } track
                        || (track.Channels & AnimChannels.Roll) == 0 || rf.Chain.Count != 4) continue;
                    var session = new PoseEditSession(new ScenePose(model), character);
                    int ankle = session.Evaluator.IndexOf(rf.Chain[2]);
                    if (ankle < 0) continue;
                    int frame = (int)stack.Keys[1].Time;
                    Vector3 Ankle() => session.Evaluator.WorldMatrices(animation, frame)[ankle].Translation;
                    foreach (int channel in new[] { 10, 12 })
                    {
                        var before = Ankle();
                        float angle = Curves.ScalarAt(stack, track, channel, frame);
                        float wanted = angle + (angle > 0 ? -0.4f : 0.4f);
                        Assert.True(session.SetScalarChannel(animation, [rf], frame, channel, wanted, out var reason) == 1, reason);
                        Assert.True(Vector3.Distance(before, Ankle()) > 1e-2f, $"the leg did not follow channel {channel}");
                        Assert.True(session.Undo());
                        Assert.True(Vector3.Distance(before, Ankle()) < 1e-3f, "undo did not put the leg back");
                    }
                    return;
                }
        // 19_run keys its feet as reverse feet; 7_AddingIK has no animation.
        Assert.False(model.Scene.Characters.Any(c => c.Animations.Any() && c.Controllers.OfType<ControllerReverseFoot>().Any()),
            "no reverse-foot controller with a roll track was found to edit");
    }

    [SceneTheory, SceneSamples]
    public void SetScalarChannel_RefusesATrackWithoutTheChannel(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault();
        var animation = character?.Animations.FirstOrDefault();
        if (character == null || animation == null) return;
        var plain = character.Controllers.FirstOrDefault(c => c is not ControllerIK
            && PoseEditSession.EditableStack(animation, c)?.TrackFor(c) is { } t && (t.Channels & AnimChannels.PoleAngle) == 0);
        if (plain == null) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        Assert.Equal(0, session.SetScalarChannel(animation, [plain], 0, 9, 1f, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
        Assert.False(session.History.CanUndo);
    }

    [SceneTheory, SceneSamples]
    public void ResetAndFreeze_KeepsEveryVertexWhereItWas_AndResetsTheLtm(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault();
        var mesh = model.Scene.Meshes.FirstOrDefault(m => m.ControlPoints.Length > 0);
        if (character == null || mesh == null) return;
        mesh.Skinned = false;
        var m = Matrix4x4.CreateScale(1.5f, 2f, 0.5f) * Matrix4x4.CreateFromYawPitchRoll(0.4f, -0.3f, 1.1f)
                * Matrix4x4.CreateTranslation(10, -20, 30);
        mesh.LocalMatrix = m;
        var oldPoints = mesh.ControlPoints;
        var oldTargets = mesh.BlendshapeTargets.Select(t => t.VertexArray).ToArray();
        var expected = oldPoints.Select(p => Vector3.Transform(p, m)).ToArray();
        var session = new PoseEditSession(new ScenePose(model), character);

        int frozen = session.ResetAndFreeze([mesh], out var reason);

        Assert.True(frozen == 1, reason);
        Assert.Equal(Matrix4x4.Identity, mesh.LocalMatrix);
        var points = mesh.ControlPoints;
        for (int i = 0; i < points.Length; i++)
            Assert.True(Vector3.Distance(expected[i], points[i]) < 1e-3f * (1 + expected[i].Length()), $"vertex {i}");
        var targets = mesh.BlendshapeTargets;
        for (int t = 0; t < targets.Count; t++)
        {
            var deltas = targets[t].VertexArray;
            for (int i = 0; i < Math.Min(deltas.Length, points.Length); i++)
            {
                var shaped = Vector3.Transform(oldPoints[i] + Xyz(oldTargets[t][i]), m);
                var now = points[i] + Xyz(deltas[i]);
                Assert.True(Vector3.Distance(shaped, now) < 1e-3f * (1 + shaped.Length()), $"target {t} vertex {i}");
            }
        }

        Assert.True(session.Undo());
        Assert.Equal(m, mesh.LocalMatrix);
        Assert.Equal(oldPoints, mesh.ControlPoints);
    }

    [SceneTheory, SceneSamples]
    public void ResetAndFreeze_RefusesASkinnedMesh(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault();
        var mesh = model.Scene.Meshes.FirstOrDefault(m => m.Skinned);
        if (character == null || mesh == null) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        Assert.Equal(0, session.ResetAndFreeze([mesh], out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
        Assert.False(session.History.CanUndo);
    }

    private static Vector3 Xyz(Vector4 v) => new(v.X, v.Y, v.Z);

    private static float Read(PoseEditSession session, Animation animation, AnimStack stack, Object3D controller, int channel, int frame)
    {
        var track = stack.TrackFor(controller)!;
        if (channel >= 13)
            return session.Evaluator.LayerBlendshapeValues(stack, (BlendshapeTrack)track, frame, out _)[channel - 13];
        return Curves.ScalarAt(stack, track, channel, frame);
    }
}
