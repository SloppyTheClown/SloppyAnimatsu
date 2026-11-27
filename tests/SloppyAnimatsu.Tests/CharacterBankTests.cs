using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>The Rigging context's CHAR BANK.</summary>
public class CharacterBankTests
{
    [Fact]
    public void NewScene_ShowsBuildPoseCurrent_ThenPaintAndCheck()
    {
        var model = SceneFactory.NewSceneModel();
        var character = Assert.Single(model.Scene.Characters);

        var rows = CharacterBank.Rows(character);
        Assert.Equal(2, rows.Count);
        Assert.Equal(CharacterBankRowKind.BuildPose, rows[0].Kind);
        Assert.Equal("Build Pose", rows[0].Label);
        Assert.Same(character.RefPose, rows[0].Pose);
        Assert.True(rows[0].IsCurrent);
        Assert.True(rows[0].ActsAsRefPose);
        Assert.Equal(CharacterBankRowKind.PaintAndCheck, rows[1].Kind);
        Assert.Equal(CharacterBank.PaintAndCheckLabel, rows[1].Label);
        Assert.Null(rows[1].Pose);
        Assert.False(rows[1].IsCurrent);

        Assert.True(CharacterBank.IsBindPoseCurrent(character));
        Assert.False(CharacterBank.IsPaintAndCheck(character));
        // An unbound character has no bind pose for a custom reference pose to stand beside.
        Assert.False(CharacterBank.CanCreateRefPose(character));
        Assert.False(CharacterBank.CanDeleteRefPose(character));
        Assert.Null(CharacterBank.CreateRefPose(character));
        Assert.False(CharacterBank.DeleteRefPose(character));
    }

    [Fact]
    public void Select_SwitchesBetweenThePoseRowAndPaintAndCheck()
    {
        var model = SceneFactory.NewSceneModel();
        var character = Assert.Single(model.Scene.Characters);
        var buildPose = character.RefPose;

        Assert.True(CharacterBank.Select(character, CharacterBank.Rows(character)[^1]));
        Assert.Null(character.CurrentPose);
        Assert.True(CharacterBank.IsPaintAndCheck(character));
        Assert.Equal(CharacterBankRowKind.PaintAndCheck, CharacterBank.CurrentRow(character).Kind);
        Assert.False(CharacterBank.Select(character, CharacterBank.Rows(character)[^1]));

        Assert.True(CharacterBank.Select(character, CharacterBank.Rows(character)[0]));
        Assert.Same(buildPose, character.CurrentPose);
        Assert.True(CharacterBank.IsBindPoseCurrent(character));
    }

    [Fact]
    public void CreateCharacter_AddsANumberedCharacter_AndMakesItCurrent()
    {
        var model = SceneFactory.NewSceneModel();
        var first = Assert.Single(model.Scene.Characters);

        var second = CharacterBank.CreateCharacter(model);
        var third = CharacterBank.CreateCharacter(model);

        Assert.Equal(3, model.Scene.Characters.Count());
        Assert.Same(third, model.Scene.CurrentCharacter);
        Assert.Equal(3, new[] { first.Name, second.Name, third.Name }.Distinct().Count());
        Assert.Contains(second.Name, new[] { "Character", "Character 2" });
        foreach (var c in new[] { second, third })
        {
            // A new character is an empty one: a reference pose that is current (Build Pose), no bind pose.
            Assert.NotNull(c.RefPose);
            Assert.Same(c.RefPose, c.CurrentPose);
            Assert.Empty(c.BindPoses);
            Assert.NotNull(c.AnimBank);
            Assert.Equal(CharacterBankRowKind.BuildPose, CharacterBank.CurrentRow(c).Kind);
        }
    }

    [Fact]
    public void NextCharacterName_SkipsTakenNames()
    {
        var model = SceneFactory.NewSceneModel();
        var first = Assert.Single(model.Scene.Characters);
        first.Source.Set("Name", "Character");
        Assert.Equal("Character 2", CharacterBank.NextCharacterName(model.Scene));
        CharacterBank.CreateCharacter(model);
        Assert.Equal("Character 3", CharacterBank.NextCharacterName(model.Scene));
    }

    [SceneTheory, SceneSamples]
    public void RefPose_CreateThenDelete_OnABoundCharacter(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.BindPoses.Any());
        if (character == null) return;
        var bind = character.BindPoses.First();

        // Start from the bind pose acting as the reference pose.
        if (CharacterBank.CanDeleteRefPose(character)) Assert.True(CharacterBank.DeleteRefPose(character));
        Assert.Same(bind, character.RefPose);
        Assert.True(CharacterBank.CanCreateRefPose(character));
        Assert.False(CharacterBank.CanDeleteRefPose(character));
        Assert.Contains(CharacterBank.Rows(character), r => r.Kind == CharacterBankRowKind.BindPose && r.ActsAsRefPose);
        Assert.DoesNotContain(CharacterBank.Rows(character), r => r.Kind == CharacterBankRowKind.RefPose);

        var custom = CharacterBank.CreateRefPose(character);
        Assert.NotNull(custom);
        Assert.Same(custom, character.RefPose);
        Assert.Same(custom, character.CurrentPose);
        Assert.Equal("__RefPose", custom!.Name);
        Assert.Equal(bind.Count, custom.Count);
        var rows = CharacterBank.Rows(character);
        var refRow = Assert.Single(rows, r => r.Kind == CharacterBankRowKind.RefPose);
        Assert.True(refRow.IsCurrent);
        Assert.False(Assert.Single(rows, r => ReferenceEquals(r.Pose, bind)).ActsAsRefPose);
        Assert.False(CharacterBank.CanCreateRefPose(character));
        Assert.True(CharacterBank.CanDeleteRefPose(character));

        Assert.True(CharacterBank.DeleteRefPose(character));
        Assert.Same(bind, character.RefPose);
        Assert.Same(bind, character.CurrentPose);
        if (!ReferenceEquals(custom, character.RefPoseOld))
            Assert.Null(custom.Source.Parent);
        Assert.DoesNotContain(CharacterBank.Rows(character), r => r.Kind == CharacterBankRowKind.RefPose);
    }

    /// <summary>
    /// Paint &amp; Check poses a runtime check pose: the joint moves (and the skin with it), the
    /// scene's poses and the rest transforms stay as they were, and picking a pose row hides it.
    /// </summary>
    [SceneTheory, SceneSamples]
    public void PaintAndCheck_PosesAJointWithoutTouchingTheScene(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.BindPoses.Any() && c.Joints.Any());
        Assert.NotNull(character);
        var poseRow = CharacterBank.Rows(character)[0];
        CharacterBank.Select(character, CharacterBank.Rows(character).First(r => r.Kind == CharacterBankRowKind.PaintAndCheck));
        var pose = new SloppyAnimatsu.Rendering.ScenePose(model) { RiggingContext = true };
        var session = new SloppyAnimatsu.Rendering.PoseEditSession(pose, character);
        var joint = character.Joints.First(j => j.Constraint == null);
        int index = session.Evaluator.IndexOf(joint);
        var rest = session.Evaluator.RestTransforms.ToArray();
        var bind = character.BindPoses.First();
        var bindBefore = Enumerable.Range(0, bind.Count).Select(bind.Matrix).ToArray();
        var worldBefore = session.Evaluator.WorldMatrices(null, 0)[index];

        var drag = Assert.IsType<SloppyAnimatsu.Rendering.CheckPoseDrag>(session.BeginCheckPose(joint, out var reason));
        Assert.Null(reason);
        drag.Set(drag.BaseLocal with { Translation = drag.BaseLocal.Translation + new System.Numerics.Vector3(50, 0, 0) },
            AnimChannels.Translate);
        drag.Commit();

        Assert.True(session.Evaluator.HasCheckPose);
        Assert.NotEqual(worldBefore, session.Evaluator.WorldMatrices(null, 0)[index]);
        Assert.Equal(rest, session.Evaluator.RestTransforms);
        Assert.Equal(bindBefore, Enumerable.Range(0, bind.Count).Select(bind.Matrix).ToArray());

        // A pose row current: the check pose no longer shows.
        CharacterBank.Select(character, poseRow);
        session.Evaluator.ReloadRestPose();
        var restWorld = new SloppyAnimatsu.Rendering.ScenePose(model) { RiggingContext = true }.Evaluator(character).WorldMatrices(null, 0)[index];
        Assert.Equal(restWorld, session.Evaluator.WorldMatrices(null, 0)[index]);
    }

    /// <summary>
    /// Paint &amp; Check poses a selection of joints in one drag: every joint turns about its own X,
    /// none is lost when the next one is posed, and Cancel puts the whole check pose back.
    /// </summary>
    [SceneTheory, SceneSamples]
    public void PaintAndCheck_GroupRotate_TurnsEverySelectedJoint(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.BindPoses.Any() && c.Joints.Count() >= 3);
        Assert.NotNull(character);
        CharacterBank.Select(character, CharacterBank.Rows(character).First(r => r.Kind == CharacterBankRowKind.PaintAndCheck));
        var pose = new SloppyAnimatsu.Rendering.ScenePose(model) { RiggingContext = true };
        var session = new SloppyAnimatsu.Rendering.PoseEditSession(pose, character);
        var joints = character.Joints.Where(j => j.Constraint == null).Take(3).ToArray();
        Assert.Equal(3, joints.Length);
        var before = session.Evaluator.LocalTransforms(null, 0);

        var drag = Assert.IsType<SloppyAnimatsu.Rendering.GroupDrag>(session.BeginCheckPose(joints, out var reason));
        Assert.Null(reason);
        Assert.Equal(3, drag.Members.Count);
        var turn = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, 0.5f);
        drag.Rotate(0, 0.25f);
        drag.Rotate(0, 0.5f);   // a second update must not compound the first

        var locals = session.Evaluator.LocalTransforms(null, 0);
        foreach (var member in drag.Members)
        {
            var local = locals[session.Evaluator.IndexOf(member.Controller)];
            var expected = System.Numerics.Quaternion.Normalize(member.BaseLocal.Rotation * turn);
            Assert.True(MathF.Abs(System.Numerics.Quaternion.Dot(expected, local.Rotation)) > 0.9999f,
                $"{member.Controller.Name} did not turn about its own X");
        }

        drag.Cancel();
        var after = session.Evaluator.LocalTransforms(null, 0);
        foreach (var joint in joints)
        {
            int i = session.Evaluator.IndexOf(joint);
            Assert.True(MathF.Abs(System.Numerics.Quaternion.Dot(before[i].Rotation, after[i].Rotation)) > 0.9999f, $"{joint.Name} not put back");
        }
    }

    /// <summary>
    /// Paint &amp; Check poses an IK controller: its chain follows, the joints' new locals are among
    /// the check pose's changes, and Create Ref Pose writes that pose (chain included) into the new
    /// reference pose, which then differs from the old one so leaving Rigging asks about it.
    /// </summary>
    [SceneTheory, SceneSamples]
    public void PaintAndCheck_IKControllerDragsItsChain_AndCreateRefPoseKeepsIt(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.BindPoses.Any()
            && c.IKControllers.Any(k => k is not ControllerReverseFoot && k.Chain.Count >= 2 && k.Constraint == null));
        if (character == null) return;
        if (CharacterBank.CanDeleteRefPose(character)) Assert.True(CharacterBank.DeleteRefPose(character));
        CharacterBank.Select(character, CharacterBank.Rows(character).First(r => r.Kind == CharacterBankRowKind.PaintAndCheck));
        var pose = new SloppyAnimatsu.Rendering.ScenePose(model) { RiggingContext = true };
        var session = new SloppyAnimatsu.Rendering.PoseEditSession(pose, character);
        var ik = character.IKControllers.First(k => k is not ControllerReverseFoot && k.Chain.Count >= 2 && k.Constraint == null);
        var leaf = ik.Chain[^1];
        int leafIndex = session.Evaluator.IndexOf(leaf);
        var leafBefore = session.Evaluator.WorldMatrices(null, 0)[leafIndex];

        var drag = Assert.IsType<SloppyAnimatsu.Rendering.CheckPoseDrag>(session.BeginCheckPose(ik, out var reason));
        Assert.Null(reason);
        // A small move toward the chain's base stays reachable.
        var toBase = session.Evaluator.WorldMatrices(null, 0)[session.Evaluator.IndexOf(ik.Chain[0])].Translation - leafBefore.Translation;
        var parentInverse = System.Numerics.Matrix4x4.Identity;
        if (ik.Parent3D is { } parent) System.Numerics.Matrix4x4.Invert(parent.WorldMatrix, out parentInverse);
        var step = System.Numerics.Vector3.TransformNormal(toBase * 0.1f, parentInverse);
        drag.Set(drag.BaseLocal with { Translation = drag.BaseLocal.Translation + step }, AnimChannels.Translate);
        drag.Commit();

        var leafAfter = session.Evaluator.WorldMatrices(null, 0)[leafIndex];
        Assert.True((leafAfter.Translation - leafBefore.Translation).Length() > 1e-3f, "the chain did not follow the IK controller");
        var changes = session.Evaluator.CheckPoseChanges();
        Assert.Contains(ik, changes.Keys);
        Assert.Contains(changes.Keys, o => ik.Chain.Contains(o));

        var custom = CharacterBank.CreateRefPose(character, changes);
        Assert.NotNull(custom);
        var editor = new RestPoseEditor(character);
        foreach (var (o, local) in changes)
            Assert.True(AktMath.MaxDifference(local.ToMatrix(), custom!.Matrix(editor.SlotOf(o))) < 1e-4f, o.Name + " lost its posed value");
        if (character.RefPoseOld != null) Assert.True(editor.IsResolutionPending);
    }

    private static (Character, SloppyAnimatsu.Rendering.PoseEditSession)? PaintAndCheckSession(string path, Func<Character, bool> pick)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.BindPoses.Any() && pick(c));
        if (character == null) return null;
        if (CharacterBank.CanDeleteRefPose(character)) Assert.True(CharacterBank.DeleteRefPose(character));
        CharacterBank.Select(character, CharacterBank.Rows(character).First(r => r.Kind == CharacterBankRowKind.PaintAndCheck));
        var pose = new SloppyAnimatsu.Rendering.ScenePose(model) { RiggingContext = true };
        return (character, new SloppyAnimatsu.Rendering.PoseEditSession(pose, character));
    }

    private static void Rotate(SloppyAnimatsu.Rendering.CheckPoseDrag drag, float degrees)
    {
        var turn = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.Normalize(new(1, 0.3f, 0.2f)), degrees * MathF.PI / 180);
        drag.Set(drag.BaseLocal with { Rotation = System.Numerics.Quaternion.Normalize(drag.BaseLocal.Rotation * turn) }, AnimChannels.Rotate);
        drag.Commit();
    }

    [SceneTheory, SceneSamples]
    public void PaintAndCheck_ChainJointPoses_AndItsIKControllerFollows_AsOneUndoStep(string path)
    {
        if (PaintAndCheckSession(path, c => c.IKControllers.Any(k => k.Chain.Count >= 2)) is not var (character, session)) return;
        var ik = character.IKControllers.First(k => k.Chain.Count >= 2);
        var evaluator = session.Evaluator;
        int target = evaluator.IndexOf(ik);
        int anchor = evaluator.IndexOf(ik is ControllerReverseFoot && ik.Chain.Count > 2 ? ik.Chain[2] : ik.Chain[^1]);
        var rest = evaluator.WorldMatrices(null, 0);
        Assert.True(System.Numerics.Matrix4x4.Invert(rest[anchor], out var inverseAnchor));
        var offset = rest[target] * inverseAnchor;

        // A chain joint is constrained by its IK controller and still poses, as while animating.
        var drag = Assert.IsType<SloppyAnimatsu.Rendering.CheckPoseDrag>(session.BeginCheckPose(ik.Chain[0], out var reason));
        Assert.Null(reason);
        Rotate(drag, 25);

        var posed = evaluator.WorldMatrices(null, 0);
        Assert.True((posed[target].Translation - rest[target].Translation).Length() > 1e-3f, ik.Name + " stayed behind its chain");
        Assert.True(AktMath.MaxDifference(offset * posed[anchor], posed[target]) < 1e-2f * MathF.Max(1, rest[target].Translation.Length()),
            ik.Name + " lost its rest offset to the chain");

        // The drag is an undo step that keeps Paint & Check current.
        Assert.True(session.Undo());
        Assert.Null(character.CurrentPose);
        Assert.Empty(evaluator.CheckPoseChanges());
        Assert.True(session.Redo());
        Assert.Contains(ik, evaluator.CheckPoseChanges().Keys);
    }

    [SceneTheory, SceneSamples]
    public void PaintAndCheck_CreateRefPose_KeepsShowingThePose(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.BindPoses.Any() && c.IKControllers.Any(k => k.Chain.Count >= 2));
        if (character == null) return;
        if (CharacterBank.CanDeleteRefPose(character)) Assert.True(CharacterBank.DeleteRefPose(character));
        CharacterBank.Select(character, CharacterBank.Rows(character).First(r => r.Kind == CharacterBankRowKind.PaintAndCheck));
        var pose = new SloppyAnimatsu.Rendering.ScenePose(model) { RiggingContext = true };
        var session = new SloppyAnimatsu.Rendering.PoseEditSession(pose, character);
        var evaluator = session.Evaluator;
        var joint = character.IKControllers.First(k => k.Chain.Count >= 2).Chain[0];
        Rotate(Assert.IsType<SloppyAnimatsu.Rendering.CheckPoseDrag>(session.BeginCheckPose(joint, out _)), 40);
        var posed = evaluator.WorldMatrices(null, 0);

        var created = CharacterBank.CreateRefPoseFromCheckPose(character, evaluator);
        Assert.NotNull(created);
        Assert.True(CharacterBank.IsPaintAndCheck(character), "Paint & Check stopped being current");
        pose.ReloadRestPose(character);
        AssertShows(evaluator.WorldMatrices(null, 0), "after Create Ref Pose");

        // The new reference pose holds exactly what was shown.
        CharacterBank.Select(character, CharacterBank.Rows(character).First(r => r.Kind == CharacterBankRowKind.RefPose));
        evaluator.ClearCheckPose();
        pose.ReloadRestPose(character);
        AssertShows(evaluator.WorldMatrices(null, 0), "in the Ref Pose row");

        // Coming back to Paint & Check starts from the reference pose, not the bind pose.
        CharacterBank.Select(character, CharacterBank.Rows(character).First(r => r.Kind == CharacterBankRowKind.PaintAndCheck));
        pose.ReloadRestPose(character);
        evaluator.StartCheckPoseFrom(created!);
        AssertShows(evaluator.WorldMatrices(null, 0), "back in Paint & Check");

        void AssertShows(System.Numerics.Matrix4x4[] now, string when)
        {
            for (int i = 0; i < posed.Length; i++)
                Assert.True(AktMath.MaxDifference(posed[i], now[i]) < 1e-2f * MathF.Max(1, posed[i].Translation.Length()),
                    evaluator.Controllers[i].Name + " moved " + when);
        }
    }

    [SceneTheory, SceneSamples]
    public void PaintAndCheck_ReverseFootFollowsTheHips(string path)
    {
        if (PaintAndCheckSession(path, c => c.IKControllers.OfType<ControllerReverseFoot>()
                .Any(rf => rf.Chain.Count == 4 && rf.Chain[0].Parent3D != null)) is not var (character, session)) return;
        var rf = character.IKControllers.OfType<ControllerReverseFoot>().First(r => r.Chain.Count == 4 && r.Chain[0].Parent3D != null);
        var hips = rf.Chain[0].Parent3D!;
        var evaluator = session.Evaluator;
        if (evaluator.IndexOf(hips) < 0 || hips.Constraint != null) return;
        int target = evaluator.IndexOf(rf);
        var before = evaluator.WorldMatrices(null, 0)[target];

        Rotate(Assert.IsType<SloppyAnimatsu.Rendering.CheckPoseDrag>(session.BeginCheckPose(hips, out _)), 30);
        Assert.True((evaluator.WorldMatrices(null, 0)[target].Translation - before.Translation).Length() > 1e-3f,
            rf.Name + " stayed on the ground while the hips rotated");
        // Posing the reverse foot itself is allowed too.
        Assert.NotNull(session.BeginCheckPose(rf, out var reason));
        Assert.Null(reason);
    }

    [SceneTheory, SceneSamples]
    public void ReturnToPrevious_MakesTheBindPoseTheRefPoseAgain(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.BindPoses.Any());
        if (character == null) return;
        if (CharacterBank.CanDeleteRefPose(character)) Assert.True(CharacterBank.DeleteRefPose(character));
        var bind = character.BindPoses.First();
        if (!ReferenceEquals(character.RefPose, bind) || !CharacterBank.CanCreateRefPose(character)) return;
        // The animations follow the bind pose first (a kept older Ref Pose then holds its matrices).
        new RestPoseEditor(character).Resolve(RestPoseResolution.KeepAnimations);
        var joint = character.Controllers.FirstOrDefault(j => j is Joint && j.Parent3D != null && j.Constraint == null);
        if (joint == null) return;
        var editor = new RestPoseEditor(character);
        var moved = Transform.FromMatrixLoose(bind.Matrix(editor.SlotOf(joint)));
        moved = moved with { Translation = moved.Translation + new System.Numerics.Vector3(5, 0, 0) };
        var custom = CharacterBank.CreateRefPose(character, new Dictionary<Object3D, Transform> { [joint] = moved });
        Assert.NotNull(custom);

        var pose = new SloppyAnimatsu.Rendering.ScenePose(model) { RiggingContext = true };
        var session = new SloppyAnimatsu.Rendering.PoseEditSession(pose, character);
        session.ResolveRestPose(RestPoseResolution.ReturnToPrevious);
        Assert.Equal(bind.Source, character.RefPose?.Source);
        Assert.Equal(bind.Source, character.RefPoseOld?.Source);
        Assert.Null(custom!.Source.Parent);
        Assert.False(new RestPoseEditor(character).IsResolutionPending);
        Assert.DoesNotContain(CharacterBank.Rows(character), r => r.Kind == CharacterBankRowKind.RefPose);

        Assert.True(session.Undo());
        Assert.Contains(CharacterBank.Rows(character), r => r.Kind == CharacterBankRowKind.RefPose);
    }
}
