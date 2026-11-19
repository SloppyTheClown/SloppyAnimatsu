using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Keyed Compensate Scale: stepped per-animation keys that
/// fall back to the rigging value, survive a save, and follow a retarget.
/// </summary>
public class CompensateScaleKeyTests
{
    [Fact]
    public void Keys_are_stepped_and_default_to_the_rigging_value()
    {
        if (Load("19_run.akt") is not { } model) return;
        var dup = model.Scene.Characters.First(c => c.Name == "Dup");
        var run = dup.Animations.First(a => a.Name == "Run_3");
        var evaluator = new PoseEvaluator(dup);
        var joint = evaluator.Controllers.OfType<Joint>().First(j => j.Parent3D != null);
        bool rig = joint.CompensatesParentScale;

        Assert.Empty(CompensateScaleKeys.Keys(run, joint));
        Assert.Equal(rig, evaluator.Compensates(joint, run, 5f));

        Assert.True(CompensateScaleKeys.SetKey(run, joint, 5, !rig));
        Assert.False(CompensateScaleKeys.SetKey(run, joint, 5, !rig));
        Assert.True(CompensateScaleKeys.SetKey(run, joint, 9, rig));
        evaluator.Invalidate();
        Assert.Equal(rig, evaluator.Compensates(joint, run, 4f));
        Assert.Equal(!rig, evaluator.Compensates(joint, run, 5f));
        Assert.Equal(!rig, evaluator.Compensates(joint, run, 8.5f));
        Assert.Equal(rig, evaluator.Compensates(joint, run, 9f));
        Assert.Equal(rig, evaluator.Compensates(joint, null, 6f));
        Assert.Equal(!rig, CompensateScaleKeys.KeyAt(run, joint, 5));
        Assert.Null(CompensateScaleKeys.KeyAt(run, joint, 6));
        // Another animation of the same character keeps the rigging value.
        if (dup.Animations.FirstOrDefault(a => !ReferenceEquals(a, run)) is { } other)
            Assert.Equal(rig, evaluator.Compensates(joint, other, 6f));

        Assert.True(CompensateScaleKeys.RemoveKey(run, joint, 9));
        evaluator.Invalidate();
        Assert.Equal(!rig, evaluator.Compensates(joint, run, 20f));
        Assert.True(CompensateScaleKeys.ClearKeys(run, joint));
        evaluator.Invalidate();
        Assert.Equal(rig, evaluator.Compensates(joint, run, 20f));
    }

    [Fact]
    public void Keys_survive_a_save()
    {
        if (Load("19_run.akt") is not { } model) return;
        var dup = model.Scene.Characters.First(c => c.Name == "Dup");
        var run = dup.Animations.First(a => a.Name == "Run_3");
        var joint = new PoseEvaluator(dup).Controllers.OfType<Joint>().First(j => j.Parent3D != null);
        bool rig = joint.CompensatesParentScale;
        CompensateScaleKeys.SetKey(run, joint, 3, !rig);
        CompensateScaleKeys.SetKey(run, joint, 12, rig);

        var reread = new SceneModel(AktReader.Read(AktWriter.Write(model.Document)));
        var dup2 = reread.Scene.Characters.First(c => c.Name == "Dup");
        var run2 = dup2.Animations.First(a => a.Name == "Run_3");
        var joint2 = dup2.Source.PreOrder().Select(o => reread.Wrap(o)).OfType<Joint>().First(j => j.Name == joint.Name);
        Assert.Equal([new CompensateScaleKeys.Key(3, !rig), new CompensateScaleKeys.Key(12, rig)],
            CompensateScaleKeys.Keys(run2, joint2));
    }

    /// <summary>
    /// A retarget gives each target the source's compensation at every frame: the source's keys,
    /// plus a start key when the two rigging values differ (the SpecialAirLand case: Dup compensates,
    /// Citro_Knight_OLD does not).
    /// </summary>
    [Fact]
    public void A_retarget_copies_the_compensation()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = model.Scene.Characters.First(c => c.Name == "Dup");
        var run = source.Animations.First(a => a.Name == "Run_3");
        var target = Clone(model, source, "Copy");
        var joints = new PoseEvaluator(source).Controllers.OfType<Joint>().Where(j => j.Parent3D != null).Take(2).ToArray();
        Assert.Equal(2, joints.Length);
        var keyed = joints[0];
        var differing = joints[1];
        CompensateScaleKeys.SetKey(run, keyed, 4, !keyed.CompensatesParentScale);
        var targetJoint = (Object3D)model.Wrap(target.Source.PreOrder().First(o => o.Name == differing.Name));
        targetJoint.CompensatesParentScale = !differing.CompensatesParentScale;

        var result = Retargeting.Transfer(model, source, target, new RetargetingOptions { Animations = [run] });
        var copy = result.Animations[0];
        var sourceEvaluator = new PoseEvaluator(source);
        var targetEvaluator = new PoseEvaluator(target);
        foreach (var joint in joints)
        {
            var to = result.Map.TargetOf(joint);
            Assert.NotNull(to);
            foreach (float t in new[] { 0f, 3f, 4f, 10f })
                Assert.Equal(sourceEvaluator.Compensates(joint, run, t), targetEvaluator.Compensates(to!, copy, t));
        }
        // An unmapped or matching joint gets no keys.
        Assert.All(targetEvaluator.Controllers.Where(c => result.Map.SourceOf(c) is { } s
                && !joints.Contains(s) && s.CompensatesParentScale == c.CompensatesParentScale),
            c => Assert.Empty(CompensateScaleKeys.Keys(copy, c)));
    }

    private static SceneModel? Load(string fileName) =>
        TestFiles.AllSceneSamples.FirstOrDefault(p => Path.GetFileName(p) == fileName) is { } path
            ? TestFiles.LoadScene(path)
            : null;

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
