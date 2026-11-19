using System.Numerics;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The hierarchy panel's row drag: the
/// 6-DIP threshold that separates a click from a drag, the drops it refuses, and the agreement of
/// its verdicts with <see cref="RigEditor.Reparent"/>.
/// </summary>
public class OutlinerDragTests
{
    [Fact]
    public void StartsDrag_KeepsAShortMoveAClickAndStartsAtSixDips()
    {
        var press = new Vector2(10, 10);

        Assert.False(OutlinerDrag.StartsDrag(press, press));
        Assert.False(OutlinerDrag.StartsDrag(press, new Vector2(15.9f, 10)));   // 5.9 DIP right
        Assert.False(OutlinerDrag.StartsDrag(press, new Vector2(13, 14)));       // 5 DIP diagonal
        Assert.False(OutlinerDrag.StartsDrag(press, new Vector2(14.2f, 14.2f))); // 5.94 DIP diagonal
        Assert.True(OutlinerDrag.StartsDrag(press, new Vector2(16, 10)));        // 6 DIP right
        Assert.True(OutlinerDrag.StartsDrag(press, new Vector2(4, 10)));         // 6 DIP left
        Assert.True(OutlinerDrag.StartsDrag(press, new Vector2(10, 16)));        // 6 DIP down
        Assert.True(OutlinerDrag.StartsDrag(press, new Vector2(14.3f, 14.3f)));  // 6.08 DIP diagonal
    }

    [SceneTheory, SceneSamples]
    public void Verdict_RefusesTheRowItselfAndACycleAndAcceptsItsParent(string path)
    {
        var model = TestFiles.LoadScene(path);
        var rows = Outliner.Rows(model.Scene);
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(OutlinerDropVerdict.Self, OutlinerDrag.Verdict(row.Object, row.Object)));

        int accepted = 0, constrained = 0;
        foreach (var (row, parent) in ParentPairs(model.Scene))
        {
            // A row dropped on its listed parent is the reparent the panel is for; a constrained
            // (or NoReparentFlag) object is the one thing refused at that distance.
            var verdict = OutlinerDrag.Verdict(row.Object, parent);
            if (verdict == OutlinerDropVerdict.Move) accepted++;
            else
            {
                Assert.Equal(OutlinerDropVerdict.Constrained, verdict);
                constrained++;
            }
            // The parent dropped on the row would make a cycle, except that a character row is
            // refused as a character before the cycle is considered.
            Assert.Equal(
                parent is Character ? OutlinerDropVerdict.Character : OutlinerDropVerdict.Cycle,
                OutlinerDrag.Verdict(parent, row.Object));
            Assert.True(OutlinerDrag.AllowsDrop(row.Object, parent) == (verdict == OutlinerDropVerdict.Move));
        }
        Assert.True(accepted > 0, "no row's parent accepted its drop");
        Assert.Equal(ParentPairs(model.Scene).Count(), accepted + constrained);
    }

    [SceneTheory, SceneSamples]
    public void Verdict_RefusesAFlaggedObjectACharacterRowAndASceneLevelMesh(string path)
    {
        var model = TestFiles.LoadScene(path);
        var scene = model.Scene;
        var pairs = ParentPairs(scene).ToList();

        // The same pair as above, first accepted and then carrying the NoReparentFlag.
        var accepted = pairs.FirstOrDefault(p => OutlinerDrag.AllowsDrop(p.Row.Object, p.Parent));
        if (accepted.Row.Object == null) return;
        var flagged = accepted.Row.Object;
        flagged.Flags |= RigEditor.NoReparentFlag;
        try
        {
            Assert.Equal(OutlinerDropVerdict.Constrained, OutlinerDrag.Verdict(flagged, accepted.Parent));
            Assert.False(OutlinerDrag.AllowsDrop(flagged, accepted.Parent));
        }
        finally
        {
            flagged.Flags &= ~RigEditor.NoReparentFlag;
        }
        Assert.True(OutlinerDrag.AllowsDrop(flagged, accepted.Parent));

        var character = scene.Characters.FirstOrDefault(Outliner.IsListed);
        if (character == null) return;
        var rows = Outliner.Rows(scene);
        var other = rows.FirstOrDefault(r => OutlinerDrag.OwnerOf(r.Object) != null
            && !ReferenceEquals(r.Object, character));
        Assert.True(other.Object != null, "the character lists no second row");
        // A character has no row to be given as a parent...
        Assert.Equal(OutlinerDropVerdict.Character, OutlinerDrag.Verdict(character, other.Object));
        Assert.False(OutlinerDrag.AllowsDrop(character, other.Object));

        // ...and a scene-level mesh belongs to no character, so it may not be dropped on one either.
        var mesh = scene.Meshes.FirstOrDefault(m => Outliner.IsListed(m) && OutlinerDrag.OwnerOf(m) == null);
        if (mesh != null)
        {
            Assert.Equal(OutlinerDropVerdict.OtherCharacter, OutlinerDrag.Verdict(mesh, other.Object));
            Assert.False(OutlinerDrag.AllowsDrop(mesh, other.Object));
        }
    }

    [SceneTheory, SceneSamples]
    public void Reparent_RefusesTheDropsTheVerdictRefuses(string path)
    {
        var model = TestFiles.LoadScene(path);
        var pairs = ParentPairs(model.Scene).ToList();
        var accepted = pairs.FirstOrDefault(p =>
            OutlinerDrag.OwnerOf(p.Parent) != null && OutlinerDrag.AllowsDrop(p.Row.Object, p.Parent));
        if (accepted.Row.Object == null) return;
        var character = OutlinerDrag.OwnerOf(accepted.Parent)!;
        var editor = new RigEditor(character);

        // The accepted pair really reparents...
        Assert.Equal(1, editor.Reparent([accepted.Row.Object], accepted.Parent));
        Assert.True(ReferenceEquals(accepted.Parent, accepted.Row.Object.Parent));

        // ...the reverse makes a cycle, which the editor refuses and the verdict already named.
        if (accepted.Parent is not Character)
            Assert.Throws<InvalidOperationException>(() => editor.Reparent([accepted.Parent], accepted.Row.Object));

        // A row dropped on itself is refused by both.
        var self = pairs[0].Row.Object;
        Assert.Throws<InvalidOperationException>(() => editor.Reparent([self], self));

        // A constrained row is refused by the editor as well.
        var constrained = pairs.FirstOrDefault(p =>
            OutlinerDrag.OwnerOf(p.Parent) != null &&
            OutlinerDrag.Verdict(p.Row.Object, p.Parent) == OutlinerDropVerdict.Constrained);
        if (constrained.Row.Object != null)
            Assert.Throws<InvalidOperationException>(() =>
                new RigEditor(OutlinerDrag.OwnerOf(constrained.Parent)!).Reparent([constrained.Row.Object], constrained.Parent));
    }

    [Fact]
    public void PlaceAt_SplitsARowIntoAboveOntoAndBelow()
    {
        Assert.Equal(OutlinerDropPlace.Before, OutlinerDrag.PlaceAt(0, 22));
        Assert.Equal(OutlinerDropPlace.Before, OutlinerDrag.PlaceAt(6.5, 22));
        Assert.Equal(OutlinerDropPlace.Inside, OutlinerDrag.PlaceAt(11, 22));
        Assert.Equal(OutlinerDropPlace.After, OutlinerDrag.PlaceAt(15.5, 22));
        Assert.Equal(OutlinerDropPlace.After, OutlinerDrag.PlaceAt(22, 22));
    }

    [SceneTheory, SceneSamples]
    public void PlaceBeside_ReordersSiblingsAndReparentsBesideACousin(string path)
    {
        var model = TestFiles.LoadScene(path);
        // Two siblings under one 3D parent of a character.
        var group = Outliner.Rows(model.Scene).Select(r => r.Object)
            .Where(o => o.Parent is Object3D && OutlinerDrag.OwnerOf(o) != null && o is not Character)
            .GroupBy(o => o.Parent!.Source).FirstOrDefault(g => g.Count() >= 2);
        if (group == null) return;
        var first = group.ElementAt(0);
        var second = group.ElementAt(1);
        var parent = (Object3D)first.Parent!;
        var editor = new RigEditor(OutlinerDrag.OwnerOf(first)!);

        // A sibling dropped below the other is a reorder, accepted even beside the row itself's parent.
        Assert.Equal(OutlinerDropVerdict.Move, OutlinerDrag.Verdict(first, second, OutlinerDropPlace.After));
        Assert.Same(parent, OutlinerDrag.NewParent(second, OutlinerDropPlace.After));
        editor.PlaceBeside(first, second, after: true);
        var order = parent.Source.Children.ToList();
        Assert.Equal(order.IndexOf(second.Source) + 1, order.IndexOf(first.Source));
        editor.PlaceBeside(first, second, after: false);
        order = parent.Source.Children.ToList();
        Assert.Equal(order.IndexOf(second.Source) - 1, order.IndexOf(first.Source));
        Assert.Same(parent, first.Parent);

        // Onto a row is the old reparent rule; beside a character goes into it.
        Assert.Equal(OutlinerDrag.Verdict(first, second), OutlinerDrag.Verdict(first, second, OutlinerDropPlace.Inside));
        var character = OutlinerDrag.OwnerOf(first)!;
        Assert.Same(character, OutlinerDrag.NewParent(character, OutlinerDropPlace.Before));
        Assert.Equal(OutlinerDropVerdict.Self, OutlinerDrag.Verdict(first, first, OutlinerDropPlace.Before));
    }

    /// <summary>Every listed row with a level down, paired with its closest 3D parent (folders skipped).</summary>
    private static IEnumerable<(OutlinerRow Row, Object3D Parent)> ParentPairs(Scene scene)
    {
        foreach (var row in Outliner.Rows(scene).Where(r => r.Depth > 0))
        {
            SceneObject? parent = row.Object.Parent;
            while (parent is Folder) parent = parent.Parent;
            if (parent is Object3D target) yield return (row, target);
        }
    }
}