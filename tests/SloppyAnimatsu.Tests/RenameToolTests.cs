using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The Rename Tool's naming rule, the scene tree's pickable
/// toggle, the retarget window's joint bank and the scene-driven ground grid.
/// </summary>
public class RenameToolTests
{
    [Theory]
    [InlineData("Arm_L", "", "", "", "", "", "Arm_L")]
    [InlineData("Arm_L", "", "Leg", "", "", "", "Leg")]
    [InlineData("Arm_L", "_L", "_R", "", "", "", "Arm_R")]
    [InlineData("Arm_L", "_L", "", "", "", "", "Arm_L")]
    [InlineData("Arm_L", "", "", "pre_", "_suf", "", "pre_Arm_L_suf")]
    [InlineData("Arm_L", "", "", "", "_x", "_01", "Arm_L_x_01")]
    [InlineData("a_b_a", "a", "c", "", "", "", "c_b_c")]
    public void ApplyFollowsTheManual(string name, string search, string replace, string prefix, string suffix,
        string number, string expected) =>
        Assert.Equal(expected, RenameTool.Apply(name, new RenameToolOptions(search, replace, prefix, suffix, number), 0));

    [Theory]
    [InlineData("", 3, "")]
    [InlineData("_01", 0, "_01")]
    [InlineData("_01", 2, "_03")]
    [InlineData("_09", 1, "_10")]
    [InlineData("_9", 1, "_10")]
    [InlineData("v1x", 4, "v5x")]
    [InlineData("_end", 2, "_end")]
    [InlineData("_001_2", 1, "_002_2")]
    public void TheNumberSuffixCountsFromItsFirstDigits(string setting, int index, string expected) =>
        Assert.Equal(expected, RenameTool.NumberSuffix(setting, index));

    [Fact]
    public void PreviewNumbersTheSelectionInOrder()
    {
        var names = RenameTool.Preview(["a", "b", "c"], new RenameToolOptions(Replace: "Bone", Number: "_1"));
        Assert.Equal(["Bone_1", "Bone_2", "Bone_3"], names);
    }

    [Fact]
    public void NothingToChangeIsReported()
    {
        Assert.False(new RenameToolOptions().ChangesAnything);
        Assert.False(new RenameToolOptions(Search: "x").ChangesAnything);
        Assert.True(new RenameToolOptions(Prefix: "p").ChangesAnything);
    }

    [SceneTheory, SceneSamples]
    public void ATreeRenameIsUniqueInsideTheCharacter(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.First(c => c.Descendants<Joint>().Count() >= 2);
        var joints = character.Descendants<Joint>().Where(j => j.Name != null).Take(2).ToList();
        string taken = joints[0].Name!;

        // The object's own name is never taken.
        Assert.Equal(taken, RenameTool.UniqueTreeName(joints[0], taken));

        string got = RenameTool.RenameTreeObject(joints[1], taken);
        Assert.NotEqual(taken, got);
        Assert.Equal(got, joints[1].Name);
        Assert.Single(character.Descendants<Object3D>(), o => o.Name == got);
        Assert.Matches("_[0-9]{2,}$", got);
    }

    [SceneTheory, SceneSamples]
    public void SetPickableCountsOnlyTheObjectsItChanged(string path)
    {
        var model = TestFiles.LoadScene(path);
        var objects = model.Scene.Characters.SelectMany(c => c.Descendants<Joint>()).Take(3).Cast<Object3D>().ToList();
        Assert.NotEmpty(objects);
        foreach (var o in objects) o.IsUnpickable = false;
        objects[0].IsUnpickable = true;

        Assert.Equal(objects.Count - 1, Outliner.SetPickable(objects.Append(objects[^1]), false));
        Assert.All(objects, o => Assert.True(o.IsUnpickable));
        Assert.Equal(objects.Count, Outliner.SetPickable(objects, true));
        Assert.All(objects, o => Assert.False(o.IsUnpickable));
    }

    [Fact]
    public void TheJointBankHoldsTheTargetsNoRowUses()
    {
        var path = TestFiles.AllSceneSamples.FirstOrDefault(p => Path.GetFileName(p) == "19_run.akt");
        if (path == null) return;
        var model = TestFiles.LoadScene(path);
        var session = new RetargetSession(model);
        if (!session.CanRetarget) return;

        void Check()
        {
            var used = session.Rows.Where(r => r.Target != null).Select(r => r.Target!).ToList();
            Assert.Equal(session.TargetChoices.Count, used.Count + session.UnusedTargets.Count);
            Assert.DoesNotContain(session.UnusedTargets, t => used.Contains(t));
        }

        Check();
        var row = session.Rows.First(r => r.Target != null);
        var freed = row.Target!;
        Assert.True(session.SetTarget(row.Source, null));
        Assert.Contains(freed, session.UnusedTargets);
        Check();
        Assert.True(session.SetTarget(row.Source, freed));
        Assert.DoesNotContain(freed, session.UnusedTargets);
        Check();
    }

    [Fact]
    public void TheGroundGridFollowsTheScenesUnitAndSubdivisions()
    {
        // Metre unit, 4 units each way, 5 subdivisions: lines every 20 cm out to 400 cm.
        var lines = SceneRenderer.GroundGrid(3, 4, 5, 0);
        int count = lines.Length / 24; // two lines (x and z) of 2 vertices x 6 floats per step
        Assert.Equal(2 * 4 * 5 + 1, count);
        Assert.Equal(-400f, lines[0]);
        Assert.Equal(-380f, lines[24]);

        // No grid values: the old power-of-ten grid around the scene's size.
        var fallback = SceneRenderer.GroundGrid(-1, 0, 0, 180);
        Assert.Equal(10f, fallback[24] - fallback[0]);

        // Too fine to draw: the subdivisions go first.
        var coarse = SceneRenderer.GroundGrid(0, 300, 10, 0);
        Assert.Equal(2 * 300 + 1, coarse.Length / 24);
        Assert.Empty(SceneRenderer.GroundGrid(0, 100000, 1, 0));
    }

    [Fact]
    public void TheSizeUnitsAreCentimetres()
    {
        Assert.Equal(100f, Scene.UnitLength(3));
        Assert.Equal(2.54f, Scene.UnitLength(4));
        Assert.Equal(0f, Scene.UnitLength(9));
        var model = SceneFactory.NewSceneModel(30);
        Assert.Equal(3, model.Scene.SizeUnit);
        Assert.Equal(4u, model.Scene.GridSize);
        Assert.Equal(5u, model.Scene.UnitSubdivision);
    }
}
