using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>Editing the rest pose and resolving the animations.</summary>
public class RestPoseEditingTests
{
    private static readonly Transform Offset = new(new Vector3(1.5f, -2f, 0.75f),
        Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.3f, -0.2f, 0.15f)), new Vector3(1.1f, 0.9f, 1.25f));

    [SceneTheory, SceneSamples]
    public void Convert_KeepsTheAnimatedPose(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, joint, animations)) return;
        var editor = new RestPoseEditor(character);
        var evaluator = new PoseEvaluator(character);
        int slot = editor.SlotOf(joint);
        var before = animations.Select(a => Sample(evaluator, a, slot)).ToList();
        var keysBefore = KeyValues(character, joint);

        Assert.False(editor.IsResolutionPending);
        Assert.True(editor.CanEdit(joint, out _));
        editor.SetLocal(joint, PoseEvaluator.Compose(editor.GetLocal(joint), Offset));
        Assert.True(editor.IsResolutionPending);
        // The edit is forwarded to the constrained controllers below the joint (the smart update
        // keeps the world), so more than the joint's slot can differ.
        Assert.Contains(slot, editor.ChangedSlots);

        int converted = editor.Resolve(RestPoseResolution.ConvertAnimations);

        // The app converts every MRS track of the Anim Bank, not only the changed controller's
        // the count covers every non-additive, non-reverse-foot track.
        Assert.Equal(ConvertedKeyCount(character), converted);
        Assert.True(converted >= keysBefore.Count);
        Assert.False(editor.IsResolutionPending);
        Assert.True(AktMath.MaxDifference(character.RefPose!.Matrix(slot), character.RefPoseOld!.Matrix(slot)) == 0);
        evaluator.ReloadRestPose();
        for (int a = 0; a < animations.Count; a++)
        {
            var after = Sample(evaluator, animations[a], slot);
            for (int t = 0; t < after.Length; t++)
                AssertClose(before[a][t], after[t], $"{animations[a].Name} sample {t}");
        }
    }

    [SceneTheory, SceneSamples]
    public void Keep_LeavesTheKeys_AndReturn_PutsThePoseBack(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, joint, _)) return;
        var editor = new RestPoseEditor(character);
        int slot = editor.SlotOf(joint);
        var original = character.RefPose!.Matrix(slot);
        var keysBefore = KeyValues(character, joint);

        editor.SetLocal(joint, PoseEvaluator.Compose(editor.GetLocal(joint), Offset));
        Assert.Equal(0, editor.Resolve(RestPoseResolution.ReturnToPrevious));
        Assert.Equal(original, character.RefPose.Matrix(slot));
        Assert.False(editor.IsResolutionPending);

        editor.SetLocal(joint, PoseEvaluator.Compose(editor.GetLocal(joint), Offset));
        var edited = character.RefPose.Matrix(slot);
        Assert.Equal(0, editor.Resolve(RestPoseResolution.KeepAnimations));
        Assert.Equal(edited, character.RefPoseOld!.Matrix(slot));
        Assert.Equal(keysBefore, KeyValues(character, joint));
        Assert.False(editor.IsResolutionPending);

        // The edit is saved, and the pending state is read back from the file.
        editor.SetLocal(joint, PoseEvaluator.Compose(editor.GetLocal(joint), Offset));
        var reloaded = new SceneModel(AktReader.Read(AktWriter.Write(model.Document)));
        var again = reloaded.Scene.Characters.Single(c => c.Name == character.Name);
        Assert.Equal(character.RefPose.Matrix(slot), again.RefPose!.Matrix(slot));
        Assert.True(new RestPoseEditor(again).IsResolutionPending);
    }

    [SceneTheory, SceneSamples]
    public void Constrained_ObjectsCannotBeEdited(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
        {
            var editor = new RestPoseEditor(character);
            foreach (var c in character.Controllers.Where(c => c.Constraint != null && c is not Camera))
            {
                Assert.False(editor.CanEdit(c, out var reason));
                Assert.Contains("constrained", reason);
                Assert.Throws<InvalidOperationException>(() => editor.SetLocal(c, Transform.Identity));
            }
        }
    }

    [SceneTheory, SceneSamples]
    public void Session_DragsResolvesAndUndoes(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Candidate(model) is not var (character, joint, _)) return;
        var pose = new ScenePose(model) { RiggingContext = true };
        var session = new PoseEditSession(pose, character);
        int slot = session.RestPose.SlotOf(joint);
        var original = character.RefPose!.Matrix(slot);
        var keysBefore = KeyValues(character, joint);
        pose.Update(character, null, 0, false);
        var meshBefore = pose.MeshVertices.ToArray();

        var drag = session.BeginRestPose(joint, out _)!;
        drag.Rotate(1, 0.6f);
        drag.Translate(0, 0.5f);
        drag.Commit();
        Assert.True(session.RestPose.IsResolutionPending);
        Assert.NotEqual(original, character.RefPose.Matrix(slot));
        pose.Update(character, null, 0, false);
        AssertClose(drag.Posed, pose.Evaluator(character).LocalTransforms(null, 0)[slot], "shown rest pose");
        // A bind pose edit moves the joint inside the skin without deforming it.
        if (character.BindPoses.Any(b => ReferenceEquals(b, session.RestPose.EditedPose)))
            Assert.True(meshBefore.Zip(pose.MeshVertices).All(p => MathF.Abs(p.First - p.Second) < 1e-3f), "the skin deformed");

        Assert.True(session.Undo());
        Assert.Equal(original, character.RefPose.Matrix(slot));
        Assert.False(session.RestPose.IsResolutionPending);
        Assert.True(session.Redo());
        Assert.True(session.RestPose.IsResolutionPending);

        Assert.True(session.ResolveRestPose(RestPoseResolution.ConvertAnimations) >= keysBefore.Count);
        Assert.False(session.RestPose.IsResolutionPending);
        Assert.NotEqual(keysBefore, KeyValues(character, joint));
        Assert.True(session.Undo());
        Assert.Equal(keysBefore, KeyValues(character, joint));
        Assert.True(session.RestPose.IsResolutionPending);
    }

    [SceneTheory, SceneSamples]
    public void FirstEdit_SplitsTheBindPoseIntoACustomRefPose(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
        {
            var editor = new RestPoseEditor(character);
            if (character.RefPose is not { } reference) continue;
            if (!character.BindPoses.Any(b => ReferenceEquals(b, reference))) continue;
            if (!ReferenceEquals(editor.EditedPose, reference)) continue;
            var joint = character.Joints.FirstOrDefault(j => editor.CanEdit(j, out _));
            if (joint == null) continue;
            var bindBefore = Enumerable.Range(0, reference.Count).Select(reference.Matrix).ToArray();

            editor.SetLocal(joint, PoseEvaluator.Compose(editor.GetLocal(joint), Offset));

            var pose = character.RefPose!;
            Assert.NotSame(reference, pose);
            Assert.Equal("__RefPose", pose.Name);
            Assert.Same(pose, character.CurrentPose);
            Assert.Same(pose, editor.EditedPose);
            for (int i = 0; i < reference.Count; i++)
                Assert.Equal(bindBefore[i], reference.Matrix(i));
            Assert.Contains(editor.SlotOf(joint), editor.ChangedSlots);
            return;
        }
    }

    [SceneTheory, SceneSamples]
    public void EditingAJoint_ReDerivesTheIkControllersBelowIt(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
        {
            var editor = new RestPoseEditor(character);
            var rig = new RigEditor(character);
            foreach (var ik in character.IKControllers.Where(c => c is not ControllerReverseFoot && c.Chain.Count > 0))
            {
                Object3D? upper = null;
                for (var p = ik.Chain[0].Parent3D; p != null && upper == null; p = p.Parent3D)
                    if (p is Joint j && editor.CanEdit(j, out _)) upper = j;
                if (upper == null) continue;
                var before = editor.EditedPose!.Matrix(editor.SlotOf(ik));

                editor.SetLocal(upper, PoseEvaluator.Compose(editor.GetLocal(upper),
                    new Transform(new Vector3(37f, 11f, 5f), Quaternion.Identity, Vector3.One)));

                var pose = editor.EditedPose!;
                Assert.True(Matrix4x4.Invert(rig.WorldInPose(pose, character), out var inverse));
                var expected = rig.WorldInPose(pose, ik.Chain[^1]) * inverse;
                Assert.True(AktMath.MaxDifference(expected, pose.Matrix(editor.SlotOf(ik))) < 1e-3f,
                    $"{ik.Name} did not follow {ik.Chain[^1].Name}");
                Assert.False(AktMath.MaxDifference(before, pose.Matrix(editor.SlotOf(ik))) < 1e-6f,
                    $"{ik.Name} kept its pose");
                return;
            }
        }
    }

    [SceneTheory, SceneSamples]
    public void Convert_RecomputesTangents_AndSkipsTheReverseFootTracks(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
        {
            if (character.RefPose is not { } current || character.RefPoseOld is not { } old
                || ReferenceEquals(current, old)) continue;
            var tracks = character.Animations.SelectMany(a => a.Stacks).Where(s => !s.IsAdditive)
                .SelectMany(s => s.Tracks).Where(t => t.Target != null).ToList();
            var plain = tracks.FirstOrDefault(t => t.Target is Joint && RestPoseEditor.IsConvertedTrack(character.Model, t)
                && t.TangentsIn.OfType<TransformKey>().Any());
            var reverse = tracks.FirstOrDefault(t => !RestPoseEditor.IsConvertedTrack(character.Model, t)
                && t.TangentsIn.OfType<TransformKey>().Any());
            if (plain == null || reverse == null) continue;
            var plainTangents = plain.TangentsIn.Concat(plain.TangentsOut).OfType<TransformKey>().ToList();
            var reverseTangents = reverse.TangentsIn.Concat(reverse.TangentsOut).OfType<TransformKey>().ToList();
            foreach (var t in plainTangents) t.Value = t.Value with { Scale = t.Value.Scale * 2f };
            foreach (var t in reverseTangents) t.Value = t.Value with { Scale = t.Value.Scale * 2f };
            var plainStale = plainTangents.Select(t => t.Value).ToList();
            var reverseStale = reverseTangents.Select(t => t.Value).ToList();

            Assert.True(new RestPoseEditor(character).Resolve(RestPoseResolution.ConvertAnimations) > 0);

            Assert.False(plainStale.SequenceEqual(plainTangents.Select(t => t.Value)),
                "the conversion did not recompute the track's tangents");
            Assert.True(reverseStale.SequenceEqual(reverseTangents.Select(t => t.Value)),
                "the conversion recomputed a reverse-foot track's tangents");
            return;
        }
    }

    [SceneTheory, SceneSamples]
    public void FirstEdit_SplitsTheBindPose_FromTheCurrentState(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
        {
            var editor = new RestPoseEditor(character);
            if (character.RefPose is not { } reference) continue;
            if (!character.BindPoses.Any(b => ReferenceEquals(b, reference))) continue;
            if (!ReferenceEquals(editor.EditedPose, reference)) continue;
            if (character.RigAnimation?.Stacks.FirstOrDefault() is not { } rig) continue;
            var joint = character.Joints.FirstOrDefault(j => editor.CanEdit(j, out _));
            if (joint == null) continue;
            // The app builds the split pose from the character's *current state*:
            // The pose built from the current state composes the __RigAnim key at the scene
            // time onto the pose.
            var evaluator = new PoseEvaluator(character);
            float time = model.Scene.Time ?? 0;
            var expected = new Matrix4x4[reference.Count];
            int strongest = -1;
            float strongestKey = 1f;
            for (int i = 0; i < expected.Length; i++) expected[i] = reference.Matrix(i);
            for (int i = 0; i < character.Controllers.Count && i < expected.Length; i++)
            {
                if (rig.TrackFor(character.Controllers[i]) is not { } track) continue;
                var key = evaluator.LayerValue(rig, track, time, out _);
                expected[i] = PoseEvaluator.Compose(Transform.FromMatrixLoose(reference.Matrix(i)), key).ToMatrix();
                if (key.Translation.Length() > strongestKey) { strongestKey = key.Translation.Length(); strongest = i; }
            }

            editor.EnsureEditableRefPose();

            var pose = character.RefPose!;
            Assert.NotSame(reference, pose);
            for (int i = 0; i < expected.Length; i++)
                Assert.True(AktMath.MaxDifference(expected[i], pose.Matrix(i)) < 1e-3f,
                    $"{character.Name} slot {i} '{character.Controllers[i].Name}' is not the current state");
            if (strongest >= 0)
            {
                // The key is folded in, not dropped: the slot moved by about the key's translation.
                Assert.True(AktMath.MaxDifference(pose.Matrix(strongest), reference.Matrix(strongest)) > 1f,
                    $"{character.Name}: the {strongestKey:0.###}-unit rig key on '{character.Controllers[strongest].Name}' was not applied");
            }
            return;
        }
    }

    [SceneTheory, SceneSamples]
    public void EditingAJoint_RefitsTheReverseFootShoe(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
        {
            var editor = new RestPoseEditor(character);
            var rig = new RigEditor(character);
            foreach (var foot in character.Controllers.OfType<ControllerReverseFoot>())
            {
                if (foot.Chain.Count != 4 || foot.Shoe.Count < 7) continue;
                Object3D? upper = null;
                for (var p = foot.Chain[0].Parent3D; p != null && upper == null; p = p.Parent3D)
                    if (p is Joint j && editor.CanEdit(j, out _)) upper = j;
                if (upper == null) continue;

                // Split first: the split folds the rig's current keys in and would itself move the
                // controller, so capture the shoe after it.
                editor.EnsureEditableRefPose();
                var before = editor.EditedPose!;
                int controller = editor.SlotOf(foot);
                var sole = Enumerable.Range(0, 4).Select(i => before.Matrix(editor.SlotOf(foot.Shoe[i]))).ToArray();
                editor.SetLocal(upper, PoseEvaluator.Compose(editor.GetLocal(upper),
                    new Transform(new Vector3(80f, 25f, -40f), Quaternion.Identity, Vector3.One)));

                var pose = editor.EditedPose!;
                // The shoe is fitted to the mesh once: the controller and the four sole pivots stay.
                Assert.Equal(before.Matrix(controller), pose.Matrix(controller));
                for (int i = 0; i < 4; i++)
                    Assert.Equal(sole[i], pose.Matrix(editor.SlotOf(foot.Shoe[i])));
                // The three skeleton locators follow the chain: tip, ball and ankle.
                var inverse = Matrix4x4.Invert(rig.WorldInPose(pose, character), out var c) ? c : Matrix4x4.Identity;
                var ball = rig.WorldInPose(pose, foot.Chain[^1]) * inverse;
                var ankle = rig.WorldInPose(pose, foot.Chain[^2]) * inverse;
                var tip = ball.Translation + (ball.Translation - ankle.Translation) * 0.5f;
                var world = new Matrix4x4[7];
                var parent = pose.Matrix(controller);
                for (int i = 0; i < 7; i++) { world[i] = pose.Matrix(editor.SlotOf(foot.Shoe[i])) * parent; parent = world[i]; }
                AssertCloseTo(tip, world[ReverseFootSolver.SkeletonToes].Translation, $"{foot.Name} toes");
                AssertCloseTo(ball.Translation, world[ReverseFootSolver.SkeletonPlant].Translation, $"{foot.Name} plant");
                AssertCloseTo(ankle.Translation, world[ReverseFootSolver.SkeletonAnkle].Translation, $"{foot.Name} ankle");
                return;
            }
        }
    }

    private static void AssertCloseTo(Vector3 expected, Vector3 actual, string where)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-2f * MathF.Max(1f, expected.Length()),
            $"{where}: {expected} vs {actual}");
    }

    /// <summary>Keys the conversion visits: every non-additive, non-reverse-foot MRS track's transform keys.</summary>
    private static int ConvertedKeyCount(Character character) =>
        character.Animations.SelectMany(a => a.Stacks).Where(s => !s.IsAdditive)
            .SelectMany(s => s.Tracks).Where(t => t.Target != null && RestPoseEditor.IsConvertedTrack(character.Model, t)
                && t.Target is not ReverseFootLocator and not ControllerReverseFoot)
            .SelectMany(t => t.Keys.OfType<TransformKey>()).Count();

    /// <summary>
    /// A character whose ref pose is the edited pose and has a separate <c>RefPoseOld</c>, with no
    /// pending change, and a joint that every non-additive stack of some animations keys in all channels.
    /// Those animations show the joint from its keys alone, so a converted rest pose must not move it.
    /// </summary>
    private static (Character, Object3D, List<Animation>)? Candidate(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
        {
            var editor = new RestPoseEditor(character);
            if (character.RefPose is not { } refPose || character.RefPoseOld is not { } old || ReferenceEquals(refPose, old)
                || !ReferenceEquals(editor.EditedPose, refPose) || editor.ChangedSlots.Count > 0) continue;
            foreach (var joint in character.Joints)
            {
                if (!editor.CanEdit(joint, out _)) continue;
                var animations = character.Animations.Where(a =>
                {
                    var stacks = a.Stacks.Where(s => !s.IsAdditive).ToList();
                    return stacks.Count > 0 && stacks.All(s => s.TrackFor(joint) is { } t
                        && (t.Channels & (AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale)) == (AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale))
                        && a.Stacks.Any(s => s.IsAdditive && s.TrackFor(joint) != null) == false;
                }).Take(5).ToList();
                if (animations.Count > 0) return (character, joint, animations);
            }
        }
        return null;
    }

    private static Transform[] Sample(PoseEvaluator evaluator, Animation animation, int slot)
    {
        var (start, end) = animation.TimeRange;
        return Enumerable.Range(0, 7).Select(i => evaluator.LocalTransforms(animation, start + (end - start) * i / 6f)[slot]).ToArray();
    }

    private static List<Transform> KeyValues(Character character, Object3D joint) =>
        character.Animations.SelectMany(a => a.Stacks).Where(s => !s.IsAdditive)
            .SelectMany(s => s.Tracks).Where(t => t.Target == joint)
            .SelectMany(t => t.Keys.OfType<TransformKey>()).Select(k => k.Value).ToList();

    private static void AssertClose(Transform expected, Transform actual, string where)
    {
        float scale = MathF.Max(1, expected.Translation.Length());
        Assert.True(Vector3.Distance(expected.Translation, actual.Translation) < 1e-3f * scale, $"{where}: translation {expected.Translation} vs {actual.Translation}");
        Assert.True(MathF.Abs(Quaternion.Dot(expected.Rotation, actual.Rotation)) > 1 - 1e-5f, $"{where}: rotation {expected.Rotation} vs {actual.Rotation}");
        Assert.True(Vector3.Distance(expected.Scale, actual.Scale) < 1e-3f, $"{where}: scale {expected.Scale} vs {actual.Scale}");
    }
}
