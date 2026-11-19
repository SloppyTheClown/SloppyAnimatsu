using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// SloppyAnimatsu's retargeting additions: the
/// body-part name keys of the extended auto-match, the mappings saved per pair of skeletons (reused
/// for every character made from those rigs and in either direction) and the copy rows that let one
/// source joint drive several target joints. Also the Anim Bank's multi-row delete as one undo step.
/// </summary>
public sealed class RetargetMappingTests
{
    [Fact]
    public void BodyKeysFoldTheCommonNamingConventionsTogether()
    {
        if (Load("19_run.akt") is not { } model) return;
        var character = Dup(model);
        var joint = RetargetingMap.Candidates(character).First(c => c is Joint);

        string? Key(string name)
        {
            joint.Source.Set("Name", name);
            return RetargetNames.BodyKey(character, joint);
        }

        Assert.Equal(Key("LeftUpLeg"), Key("thigh_l"));
        Assert.Equal(Key("LeftUpLeg"), Key("Bip01 L Thigh"));
        Assert.Equal(Key("RightForeArm"), Key("lowerarm_r"));
        Assert.Equal(Key("LeftHandPinky1"), Key("pinkie_01_l"));
        Assert.Equal(Key("LeftHandIndex2"), Key("L_Finger_Index_02"));
        Assert.Equal(Key("Hips"), Key("pelvis"));
        Assert.Equal(Key("Dup_LeftFoot"), Key("ankle_l"));
        Assert.NotEqual(Key("LeftUpLeg"), Key("RightUpLeg"));
        Assert.NotEqual(Key("LeftHandIndex1"), Key("LeftHandIndex2"));
    }

    [Fact]
    public void ASavedMappingComesBackForTheSameRigsInEitherDirection()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        // One renamed joint gives the target rig a signature of its own, so the swap below is a real
        // reversal (two identical rigs share one signature and have no direction to swap).
        var renamed = RetargetingMap.Candidates(target).OfType<Joint>().Last();
        renamed.Source.Set("Name", "ExtraBone");
        var other = Clone(model, source, "Other");
        Assert.NotEqual(RetargetNames.Signature(source), RetargetNames.Signature(target));
        string path = TempFile();
        try
        {
            var session = new RetargetSession(model, store: new RetargetMappingStore(path)) { Source = source, Target = target };
            var rows = session.Rows.Where(r => r.Target != null).Take(2).ToList();
            Assert.Equal(2, rows.Count);
            Assert.DoesNotContain(rows, r => r.Target == renamed);
            // Cross two rows: row 0 drives row 1's joint (row 1 loses it and reads "none").
            Assert.True(session.SetTarget(rows[0].Source, rows[1].Target));
            string from = rows[0].Source.Name!, to = rows[1].Target!.Name!;
            Assert.Null(session.StoreError);
            Assert.True(File.Exists(path));

            // A fresh store on the same file, another character made from the same rig.
            var again = new RetargetSession(model, store: new RetargetMappingStore(path)) { Source = source, Target = other };
            Assert.Equal(to, again.Rows.Single(r => r.Source.Name == from).Target?.Name);
            Assert.Null(again.Rows.Single(r => r.Source.Name == rows[1].Source.Name).Target);

            // Swapped: the target rig's joint now drives the source rig's one.
            var swapped = new RetargetSession(model, store: new RetargetMappingStore(path)) { Source = target, Target = source };
            Assert.Equal(from, swapped.Rows.Single(r => r.Source.Name == to).Target?.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ACopyRowDrivesASecondTargetAndIsRemembered()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        string path = TempFile();
        try
        {
            var session = new RetargetSession(model, store: new RetargetMappingStore(path)) { Source = source, Target = target };
            var rows = session.Rows.Where(r => r.Target != null).Take(2).ToList();
            var freed = rows[1].Target!;
            Assert.True(session.SetTarget(rows[1].Source, null));
            int index = session.AddCopy(rows[0].Source);
            Assert.Equal(0, index);
            Assert.True(session.SetCopyTarget(index, freed));
            Assert.DoesNotContain(freed, session.UnusedTargets);
            Assert.Contains("copy row", session.Summary);

            var map = Assert.IsType<RetargetingMap>(session.Map);
            var extra = Assert.Single(map.ExtraPairs);
            Assert.Same(rows[0].Source, extra.Source);
            Assert.Same(freed, extra.Target);

            var reopened = new RetargetSession(model, store: new RetargetMappingStore(path)) { Source = source, Target = target };
            var copy = Assert.Single(reopened.Copies);
            Assert.Same(rows[0].Source, copy.Source);
            Assert.Same(freed, copy.Target);

            // A row taking the copy's joint empties the copy; removing the copy frees nothing more.
            Assert.True(reopened.SetTarget(rows[1].Source, freed));
            Assert.Null(Assert.Single(reopened.Copies).Target);
            Assert.True(reopened.RemoveCopy(0));
            Assert.Empty(reopened.Copies);

            // The transfer runs with the copy row in place.
            var run = new RetargetSession(model, store: new RetargetMappingStore(path)) { Source = source, Target = target };
            int i = run.AddCopy(rows[0].Source);
            run.SetCopyTarget(i, run.UnusedTargets.FirstOrDefault() ?? freed);
            Assert.Equal(source.Animations.Count(), run.Run().Animations.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MirrorNamesSwapTheSideAndKeepTheCase()
    {
        Assert.Equal("Thumb_Distal_R", RetargetNames.MirrorName("Thumb_Distal_L"));
        Assert.Equal("Thumb_3_L", RetargetNames.MirrorName("Thumb_3_R"));
        Assert.Equal("r_hand", RetargetNames.MirrorName("l_hand"));
        Assert.Equal("hand.R", RetargetNames.MirrorName("hand.L"));
        Assert.Equal("Arm_R1", RetargetNames.MirrorName("Arm_L1"));
        Assert.Equal("mixamorig:RightHandThumb1", RetargetNames.MirrorName("mixamorig:LeftHandThumb1"));
        Assert.Equal("ARM_LEFT", RetargetNames.MirrorName("ARM_RIGHT"));
        Assert.Null(RetargetNames.MirrorName("Hips"));
        Assert.Null(RetargetNames.MirrorName("Spine_Low"));
        Assert.Null(RetargetNames.MirrorName(null));
    }

    [Fact]
    public void ASourceDrivesSeveralTargetsAddedReplacedAndRemovedInPlace()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        string path = TempFile();
        try
        {
            var session = new RetargetSession(model, store: new RetargetMappingStore(path)) { Source = source, Target = target };
            var rows = session.Rows.Where(r => r.Target != null).Take(3).ToList();
            var a = rows[0].Source;
            var first = rows[0].Target!;
            var second = rows[1].Target!;
            var third = rows[2].Target!;

            // Adding a joint another row holds moves it here as a second target.
            Assert.True(session.AddTarget(a, second));
            Assert.Equal([first, second], session.TargetsOf(a));
            Assert.Empty(session.TargetsOf(rows[1].Source));
            Assert.False(session.AddTarget(a, second));
            var extra = Assert.Single(Assert.IsType<RetargetingMap>(session.Map).ExtraPairs);
            Assert.Same(second, extra.Target);

            // Replacing keeps the slot; the replaced joint goes back to the bank.
            Assert.True(session.ReplaceTarget(a, second, third));
            Assert.Equal([first, third], session.TargetsOf(a));
            Assert.Contains(second, session.UnusedTargets);

            // Removing the row's own target moves the next one up into the row.
            Assert.True(session.RemoveTarget(a, first));
            Assert.Equal([third], session.TargetsOf(a));
            Assert.Same(third, session.Rows.Single(r => r.Source == a).Target);
            Assert.Empty(session.Copies);

            // Several targets are remembered for the next window.
            Assert.True(session.AddTarget(a, first));
            var reopened = new RetargetSession(model, store: new RetargetMappingStore(path)) { Source = source, Target = target };
            Assert.Equal([third, first], reopened.TargetsOf(a));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MirrorRepeatsAnEditOnTheOtherSideAndIsRemembered()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        string path = TempFile();
        try
        {
            var session = new RetargetSession(model, store: new RetargetMappingStore(path)) { Source = source, Target = target };
            // A side-named row whose other side and target's other side exist.
            var row = session.Rows.First(r => r.Target != null && session.Mirrored(r.Source, r.Target) != null);
            Assert.NotNull(row);
            var (ms, mt) = session.Mirrored(row.Source, row.Target!)!.Value;
            Assert.Contains(mt, session.TargetsOf(ms));

            // Off: only the edited side changes.
            Assert.True(session.RemoveTarget(row.Source, row.Target!));
            Assert.Contains(mt, session.TargetsOf(ms));
            Assert.True(session.AddTarget(row.Source, row.Target!));

            session.Mirror = true;
            Assert.True(session.RemoveTarget(row.Source, row.Target!));
            Assert.Empty(session.TargetsOf(row.Source));
            Assert.Empty(session.TargetsOf(ms));
            Assert.True(session.AddTarget(row.Source, row.Target!));
            Assert.Equal([mt], session.TargetsOf(ms));

            // A second side-named target mirrors as a second target on the other side.
            var other = session.Rows.First(r => r.Source != row.Source && r.Source != ms && r.Target != null
                                                && session.Mirrored(r.Source, r.Target) is { } m && m.Source != row.Source);
            var mother = session.Mirrored(other.Source, other.Target!)!.Value.Target;
            Assert.True(session.AddTarget(row.Source, other.Target!));
            Assert.Equal([row.Target!, other.Target!], session.TargetsOf(row.Source));
            Assert.Equal([mt, mother], session.TargetsOf(ms));

            Assert.True(new RetargetSession(model, store: new RetargetMappingStore(path)).Mirror);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SceneTheory, SceneSamples]
    public void DeletingSeveralBankRowsIsOneUndoStep(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.Animations.Count() >= 2);
        if (character == null) return;
        var session = new PoseEditSession(new ScenePose(model), character);
        var doomed = character.Animations.Take(2).Cast<SceneObject>().ToList();
        int count = character.Animations.Count();

        session.DeleteBankItems(doomed);
        Assert.Equal(count - 2, character.Animations.Count());
        Assert.StartsWith("Delete 2 items", session.History.NextUndo!.Description);

        Assert.True(session.Undo());
        Assert.Equal(count, character.Animations.Count());
        Assert.False(session.History.CanUndo);
    }

    // ---- helpers --------------------------------------------------------------------------

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-retarget-{Guid.NewGuid():N}.json");

    private static SceneModel? Load(string fileName) =>
        TestFiles.AllSceneSamples.FirstOrDefault(path => Path.GetFileName(path) == fileName) is { } path
            ? TestFiles.LoadScene(path)
            : null;

    private static Character Dup(SceneModel model) => model.Scene.Characters.First(character => character.Name == "Dup");

    /// <summary>A rig-only deep copy of <paramref name="source"/> (as <c>RetargetSessionTests</c> makes).</summary>
    private static Character Clone(SceneModel model, Character source, string name)
    {
        var copy = source.Source.CloneHierarchy();
        copy.Set("Name", name);
        foreach (var child in copy.Children.ToList())
            if (child.Name is "__Anims" or "__RigAnim" || child.ClassKey == "CMesh")
                copy.RemoveChild(child);
        model.Scene.Source.AddChild(copy);
        var character = model.Scene.Characters.First(c => c.Name == name);
        character.CurrentAnimation = null;
        return character;
    }
}
