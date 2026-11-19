using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The undo snapshot of a rig edit (<see cref="EditSnapshot.TakeRig"/>): it shares mesh geometry
/// instead of copying it, so it must still put every list, pose, parent and track back. Restoring
/// it and writing the document byte for byte is the check that the shortcut changes nothing.
/// This is the one rig test that runs on every sample: the snapshot has to cover whatever a scene
/// contains, and the edits below are the Viewer's Rig menu commands and a hierarchy drop.
/// </summary>
public class RigEditSnapshotTests
{
    [Fact]
    public void Resource3D_RigSnapshotRestoresTheSceneBytes()
    {
        Assert.NotNull(TestFiles.Resource3D);
        Verify(TestFiles.Resource3D!);
    }

    [AllSceneTheory, AllSceneSamples]
    public void Sample_RigSnapshotRestoresTheSceneBytes(string path) => Verify(path);

    private static void Verify(string path)
    {
        SceneModel model = TestFiles.LoadScene(path);
        Character? character = model.Scene.Characters
            .FirstOrDefault(c => c.Controllers.OfType<Joint>().Any());
        if (character == null) return; // a scene with no rig has nothing to edit

        byte[] before = AktWriter.Write(model.Document);
        var snapshot = EditSnapshot.TakeRig(character, model.Scene);
        var editor = new RigEditor(character);
        Joint parent = character.Controllers.OfType<Joint>().First();
        string name = RigEditor.UniqueControllerName(character, "SnapshotTest");
        int controllers = character.Controllers.Count;

        // Rig > New joint.
        editor.CreateJoint(parent, name, Matrix4x4.Identity);
        Assert.Equal(controllers + 1, character.Controllers.Count);
        Assert.Contains(character.Controllers, c => c.Name == name);

        // Rig > Parent selected joints / Unparent selected joints.
        if (FreePair(character) is var (joint, newParent))
        {
            Assert.Equal(1, editor.Parent([joint], newParent));
            Assert.Same(newParent, joint.Parent3D);
            Assert.Equal(1, editor.Unparent([joint]));
            Assert.Same(character, joint.Parent);
        }

        // Hierarchy drag and drop: Reparent (which re-syncs LTM first), then dropped on the character.
        if (FreePair(character) is var (drag, target))
        {
            editor.Reparent([drag], target);
            Assert.Same(target, drag.Parent3D);
            editor.Reparent([drag], character);
            Assert.Same(character, drag.Parent);
        }

        snapshot.Restore();

        Assert.Equal(controllers, character.Controllers.Count);
        Assert.DoesNotContain(character.Controllers, c => c.Name == name);
        Assert.Equal(before, AktWriter.Write(model.Document));
    }

    /// <summary>
    /// Two joints that may be reparented onto each other: unconstrained, not flagged against
    /// reparenting, not each other's ancestor and not already parent and child.
    /// </summary>
    private static (Joint, Joint)? FreePair(Character character)
    {
        var free = character.Joints
            .Where(j => j.Constraint == null && (j.Flags & RigEditor.NoReparentFlag) == 0)
            .ToList();
        foreach (var a in free)
            foreach (var b in free)
            {
                if (ReferenceEquals(a, b) || ReferenceEquals(a.Parent3D, b)) continue;
                if (IsAncestor(a, b) || IsAncestor(b, a)) continue;
                return (a, b);
            }
        return null;
    }

    private static bool IsAncestor(Object3D ancestor, Object3D node)
    {
        for (var p = node.Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }
}
