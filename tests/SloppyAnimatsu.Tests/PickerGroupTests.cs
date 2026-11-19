using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The Picker window's selection stamps:
/// The eleven <c>Aux_Pick_Select_*</c> buttons pick whole groups of the character's
/// controllers, so the rules are Core's (<see cref="PickerGroups"/>) and tested here.
/// </summary>
public class PickerGroupTests
{
    [Fact]
    public void IsSide_ReadsTheMirrorSuffixes()
    {
        Assert.True(PickerGroups.IsSide("Hand_L", left: true));
        Assert.True(PickerGroups.IsSide("Hand.l", left: true));
        Assert.True(PickerGroups.IsSide("Hand_R", left: false));
        Assert.True(PickerGroups.IsSide("Hand.R", left: false));
        Assert.False(PickerGroups.IsSide("Hand_L", left: false));
        Assert.False(PickerGroups.IsSide("Hand_R", left: true));
        Assert.False(PickerGroups.IsSide("Hand", left: true));
        Assert.False(PickerGroups.IsSide("Hand_LL", left: true));
        Assert.False(PickerGroups.IsSide("L", left: true));
        Assert.False(PickerGroups.IsSide(null, left: true));
    }

    [SceneTheory, SceneSamples]
    public void Pick_GroupsTheControllersTheStampsName(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.Controllers.Count > 0);
        if (character == null) return;

        var all = PickerGroups.Pick(character, PickerGroup.All);
        Assert.Equal(character.Controllers.Count, all.Count);
        Assert.Equal(character.Controllers, all);

        var left = PickerGroups.Pick(character, PickerGroup.LeftSide);
        var right = PickerGroups.Pick(character, PickerGroup.RightSide);
        Assert.NotEmpty(left);
        Assert.NotEmpty(right);
        Assert.All(left, c => Assert.True(PickerGroups.IsSide(c.Name, left: true)));
        Assert.All(right, c => Assert.True(PickerGroups.IsSide(c.Name, left: false)));
        Assert.Empty(left.Intersect(right));

        var ik = PickerGroups.Pick(character, PickerGroup.IK);
        Assert.NotEmpty(ik);
        Assert.All(ik, c => Assert.IsAssignableFrom<ControllerIK>(c));
        Assert.All(PickerGroups.Pick(character, PickerGroup.ReverseFoot), c => Assert.IsAssignableFrom<ControllerReverseFoot>(c));

        // The samples have no blendshapes, so those stamps pick nothing there.
        Assert.Empty(PickerGroups.Pick(character, PickerGroup.Blendshape));
        Assert.Empty(PickerGroups.Pick(character, PickerGroup.Face));
        Assert.Empty(PickerGroups.Pick(character, PickerGroup.FaceUp));
        Assert.Empty(PickerGroups.Pick(character, PickerGroup.FaceDown));
        Assert.Empty(PickerGroups.Pick(character, PickerGroup.FaceLeft));
        Assert.Empty(PickerGroups.Pick(character, PickerGroup.FaceRight));

        // Every group keeps the character's order, so the Picker's rows match a group pick.
        var order = character.Controllers.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i);
        foreach (PickerGroup group in Enum.GetValues<PickerGroup>())
        {
            var picked = PickerGroups.Pick(character, group);
            Assert.Equal(picked.OrderBy(c => order[c]), picked);
        }
    }
}