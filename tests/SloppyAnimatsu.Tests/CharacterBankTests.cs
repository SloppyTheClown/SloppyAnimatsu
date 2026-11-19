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
}
