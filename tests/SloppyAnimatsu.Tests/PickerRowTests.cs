using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The Picker's rows: the flat list, the
/// current mixer layer's filter, the layer-edit ("hammer") mode, the layer membership the hammer
/// shows, and the bones an IK controller solves (the rows that are dimmed).
/// </summary>
public class PickerRowTests
{
    private static (Character? Character, AnimStack? Stack) Sample(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.Controllers.Count > 0);
        var animation = character?.Animations.FirstOrDefault(a => a.Stacks.Any(s => s.Tracks.Any()));
        return (character, animation?.Stacks.FirstOrDefault(s => s.Tracks.Any()));
    }

    [Fact]
    public void InLayer_NeedsALayerAndATrack()
    {
        var model = TestFiles.LoadScene(TestFiles.SceneSamples.First());
        var character = model.Scene.Characters.FirstOrDefault(c => c.Controllers.Count > 0);
        if (character == null) return;
        var stack = character.Animations.SelectMany(a => a.Stacks).FirstOrDefault(s => s.Tracks.Any());
        Assert.False(PickerRows.InLayer(null, character.Controllers[0]));
        if (stack == null) return;
        foreach (var controller in character.Controllers)
        {
            Assert.Equal(stack.TrackFor(controller) != null, PickerRows.InLayer(stack, controller));
        }
    }

    [SceneTheory, SceneSamples]
    public void Rows_WithoutALayer_ListEveryController(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.Controllers.Count > 0);
        if (character == null) return;

        // The rigging context has no layer, and the hammer mode ignores the layer: both are flat
        // lists of every controller in the character's order.
        var all = character.Controllers;
        Assert.Equal(all, PickerRows.Rows(all, null, everyController: false));
        Assert.Equal(all, PickerRows.Rows(all, null, everyController: true));
        var stack = character.Animations.SelectMany(a => a.Stacks).FirstOrDefault(s => s.Tracks.Any());
        Assert.Equal(all, PickerRows.Rows(all, stack, everyController: true));
    }

    [SceneTheory, SceneSamples]
    public void Rows_WithALayer_ListOnlyTheControllersItKeys(string path)
    {
        var (character, stack) = Sample(path);
        if (character == null || stack == null) return;

        var rows = PickerRows.Rows(character.Controllers, stack, everyController: false);
        Assert.All(rows, c => Assert.True(PickerRows.InLayer(stack, c)));
        Assert.Equal(character.Controllers.Where(c => PickerRows.InLayer(stack, c)), rows);
        Assert.All(stack.Tracks, t => Assert.Contains(t.Target, rows));
        // A layer does not reorder or duplicate the character's controllers.
        Assert.Equal(rows.Distinct(), rows);
    }

    [SceneTheory, SceneSamples]
    public void IkDriven_IsTheChainsBones_AndNotTheControllers(string path)
    {
        var (character, _) = Sample(path);
        if (character == null) return;
        var ikControllers = character.Controllers.OfType<ControllerIK>().ToList();
        if (ikControllers.Count == 0) return;

        var driven = PickerRows.IkDriven(character);
        var inChains = new HashSet<Object3D>(ikControllers.SelectMany(c => c.Chain), ReferenceEqualityComparer.Instance);
        Assert.All(character.Controllers, c =>
            Assert.Equal(inChains.Contains(c) && c is Joint, driven.Contains(c)));
        Assert.All(driven, j => Assert.IsAssignableFrom<Joint>(j));
        Assert.All(ikControllers, c => Assert.All(c.Chain, j => Assert.Contains(j, driven)));
        // The stamps' IK controllers are the driven ones' owners, never the dimmed bones.
        Assert.All(PickerGroups.Pick(character, PickerGroup.IK), c => Assert.DoesNotContain(c, driven));
    }
}