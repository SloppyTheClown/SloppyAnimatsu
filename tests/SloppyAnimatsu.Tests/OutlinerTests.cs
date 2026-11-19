using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The hierarchy panel: the rows it lists, Show/Hide
/// (the <c>Flags</c> bit 0 walk), the mesh lock (<c>SkinnedMeshIsLocked</c>) and the saved
/// interface context.
/// </summary>
public class OutlinerTests
{
    [SceneTheory, SceneSamples]
    public void Rows_ListThreeDimensionalObjectsInPreOrderWithTheirDepth(string path)
    {
        var model = TestFiles.LoadScene(path);
        var rows = Outliner.Rows(model.Scene);

        Assert.NotEmpty(rows);
        Assert.Equal(rows.Count, rows.Select(r => (object)r.Object).Distinct(ReferenceEqualityComparer.Instance).Count());

        // File pre-order.
        var order = new Dictionary<AktObject, int>(ReferenceEqualityComparer.Instance);
        int n = 0;
        foreach (var o in model.Document.AllObjects()) order[o] = n++;
        Assert.Equal(rows.Select(r => order[r.Object.Source]).OrderBy(i => i), rows.Select(r => order[r.Object.Source]));

        // A non-top row sits one level below its closest listed ancestor (folders are skipped).
        foreach (var row in rows.Where(r => r.Depth > 0))
        {
            SceneObject? parent = row.Object.Parent;
            while (parent is Folder) parent = parent.Parent;
            Assert.NotNull(parent);
            var parentRow = rows.Single(r => ReferenceEquals(r.Object, parent));
            Assert.Equal(parentRow.Depth + 1, row.Depth);
        }
        Assert.All(rows, r => Assert.True(Outliner.IsListed(r.Object)));
    }

    [SceneTheory, SceneSamples]
    public void Rows_LeaveOutInternalContainersAndChildlessHelpers(string path)
    {
        var model = TestFiles.LoadScene(path);
        var rows = Outliner.Rows(model.Scene);

        Assert.DoesNotContain(rows, r => r.Object.IsInternal);
        Assert.DoesNotContain(rows, r => r.Object.ClassName == "CObject3DHiddenInTree");
        // A helper with bit 6 is listed only while it has children.
        Assert.DoesNotContain(rows, r => (r.Object.Flags & Outliner.HiddenInTreeFlag) != 0 && !r.Object.Children.Any());
        Assert.DoesNotContain(rows, r => r.Object.Name == "Blendshapes" && !r.Object.Children.Any());
        // The fixed view cameras are picked from a view's camera label, not listed; an animated
        // camera is a rig object and is not one of them.
        Assert.DoesNotContain(rows, r => r.Object is Camera && r.Object is not CameraAnimated);
        Assert.All(model.Scene.Cameras, camera =>
            Assert.Equal(camera is not CameraAnimated, Outliner.IsViewCamera(camera)));
    }

    [SceneTheory, SceneSamples]
    public void Hide_TouchesOnlyTheObjectAndHidesItsSubtreeInTheScene(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.Joints.Count > 0);
        if (character == null) return;
        Outliner.ShowAll(model.Scene);
        var joint = character.Joints[character.Joints.Count / 2];
        Outliner.Hide([joint]);

        Assert.Equal(1, Outliner.Hide([character]));
        Assert.False(character.IsShownInScene);
        // The children keep their own bits; the scene hides them through the parent.
        Assert.All(character.Joints.Where(j => j != joint), j => Assert.True(j.IsShown));
        Assert.All(Outliner.Subtree(character), o => Assert.False(o.IsShownInScene));

        Assert.Equal(1, Outliner.Show([character]));
        // Showing the parent brings back exactly what was shown before; the hidden joint stays hidden.
        Assert.False(joint.IsShownInScene);
        Assert.All(character.Joints.Where(j => j != joint && !Outliner.Subtree(joint).Contains(j)),
            j => Assert.True(j.IsShownInScene));
    }

    [SceneTheory, SceneSamples]
    public void Hide_ClearsTheSubtreeAndShowTouchesOnlyTheObject(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.Joints.Count > 0);
        if (character == null) return;
        if (!character.IsShown) Outliner.Show([character]);
        var sceneObjects = model.Scene.Descendants<Object3D>().ToList();
        var before = sceneObjects.ToDictionary(o => o, o => o.IsShown);

        var subtree = Outliner.Subtree(character).ToList();
        int expected = subtree.Count(o => before[o]);
        int hidden = Outliner.Hide([character], walkSubtree: true);

        Assert.Equal(expected, hidden);
        Assert.All(subtree, o => Assert.False(o.IsShown));
        // Everything outside the character's subtree keeps its state.
        foreach (var o in sceneObjects.Where(o => !subtree.Contains(o)))
            Assert.Equal(before[o], o.IsShown);

        int shown = Outliner.Show([character]);

        Assert.Equal(1, shown);
        Assert.True(character.IsShown);
        // The Show does not walk down: the joints stay hidden until Show All.
        Assert.All(character.Joints, j => Assert.False(j.IsShown));

        Outliner.ShowAll(model.Scene);
        Assert.All(model.Scene.Descendants<Object3D>(), o => Assert.True(o.IsShown));
    }

    [SceneTheory, SceneSamples]
    public void Toggle_FlipsOnlyTheGivenObjects(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault();
        if (character == null) return;
        bool before = character.IsShown;
        var jointStates = character.Joints.ToDictionary(j => j, j => j.IsShown);

        Assert.Equal(1, Outliner.ToggleShown([character]));

        Assert.Equal(!before, character.IsShown);
        foreach (var (joint, shown) in jointStates) Assert.Equal(shown, joint.IsShown);
        Assert.Equal(1, Outliner.ToggleShown([character]));
        Assert.Equal(before, character.IsShown);
    }

    [SceneTheory, SceneSamples]
    public void Lock_OnlyTouchesSkinnedMeshes(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.Meshes.Any(m => m.Skinned));
        if (character == null) return;
        var skinned = model.Scene.Meshes.Where(m => m.Skinned && ReferenceEquals(m.BindPose?.Character, character)).ToList();
        var unskinned = model.Scene.Meshes.Where(m => !m.Skinned).ToList();
        var unskinnedLocked = unskinned.ToDictionary(m => m, m => m.SkinnedMeshIsLocked);

        int expectedLocked = skinned.Count(m => !m.SkinnedMeshIsLocked);
        int locked = Outliner.SetCharacterMeshesLocked(character, true);

        Assert.Equal(expectedLocked, locked);
        Assert.All(skinned, m => Assert.True(m.SkinnedMeshIsLocked));
        foreach (var (mesh, state) in unskinnedLocked) Assert.Equal(state, mesh.SkinnedMeshIsLocked);
        // A non-skinned mesh is refused by name as well.
        if (unskinned.Count > 0) Assert.Equal(0, Outliner.SetMeshesLocked([unskinned[0]], true));

        int unlocked = Outliner.SetCharacterMeshesLocked(character, false);

        Assert.Equal(skinned.Count, unlocked);
        Assert.All(skinned, m => Assert.False(m.SkinnedMeshIsLocked));
    }

    [SceneTheory, SceneSamples]
    public void Context_IsSavedAndReloaded(string path)
    {
        var model = TestFiles.LoadScene(path);
        var scene = model.Scene;
        if (!scene.Source.Has("InterfaceContextIndex")) return;

        scene.Context = InterfaceContext.Animation;
        Assert.Equal(InterfaceContext.Animation, scene.Context);
        Assert.Equal(1, scene.InterfaceContextIndex);
        scene.Context = InterfaceContext.Rigging;
        Assert.Equal(0, scene.InterfaceContextIndex);
        scene.Context = InterfaceContext.Animation;

        AssertReloads(model, reloaded => Assert.Equal(InterfaceContext.Animation, reloaded.Scene.Context));
    }

    [SceneTheory, SceneSamples]
    public void HideAndLock_SurviveSaveAndReload(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.Joints.Count > 0);
        if (character == null || !character.Meshes.Any(m => m.Skinned)) return;
        Outliner.Hide([character]);
        Outliner.SetCharacterMeshesLocked(character, true);
        var rowsBefore = Outliner.Rows(model.Scene).Count;

        AssertReloads(model, reloaded =>
        {
            var again = reloaded.Scene.Characters.First(c => c.Name == character.Name);
            Assert.False(again.IsShown);
            Assert.All(again.Joints, j => Assert.False(j.IsShownInScene));
            var mesh = again.Meshes.First(m => m.Skinned);
            Assert.True(mesh.SkinnedMeshIsLocked);
            // Hiding does not remove rows from the panel.
            Assert.Equal(rowsBefore, Outliner.Rows(reloaded.Scene).Count);
        });
    }

    [SceneTheory, SceneSamples]
    public void Session_HideIsOneUndoStep(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.Joints.Count > 0);
        if (character == null) return;
        // Some samples save their characters hidden (z_Citro_Knight in 1_InitialImport, 2_NewModel
        // and 3_ExampleSkeleton); this test needs a drawn start to count what Hide changes.
        Outliner.ShowAll(model.Scene);
        var session = new PoseEditSession(new ScenePose(model), character);

        int count = session.Hide([character]);

        Assert.True(count > 0);
        Assert.False(character.IsShown);
        Assert.True(session.Undo());
        Assert.True(character.IsShown);
        Assert.True(session.Redo());
        Assert.False(character.IsShown);

        Assert.Equal(1, session.ToggleShown([character]));
        Assert.True(character.IsShown);
        Assert.True(session.Undo());
        Assert.False(character.IsShown);
    }

    [SceneTheory, SceneSamples]
    public void HiddenObjectsAreNotDrawn(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.Joints.Count > 0 && c.Meshes.Any(m => m.Skinned));
        if (character == null) return;
        // The samples that save their characters hidden would otherwise draw nothing to compare
        // with, so measure the drop from a fully shown scene.
        Outliner.ShowAll(model.Scene);
        var pose = new ScenePose(model);
        pose.Update(character, null, 0);
        int ranges = pose.VisibleRanges.Count;
        int points = pose.SkeletonPoints.Length / ScenePose.LineVertexFloats;

        Outliner.Hide([character]);
        pose.Update(character, null, 0);

        Assert.True(ranges > 0);
        Assert.True(pose.VisibleRanges.Count < ranges);
        Assert.True(pose.SkeletonPoints.Length / ScenePose.LineVertexFloats < points);
    }

    [Fact]
    public void Rows_CoverTheShippedResourceScene()
    {
        if (TestFiles.Resource3D == null) return;
        var model = TestFiles.LoadScene(TestFiles.Resource3D);
        var rows = Outliner.Rows(model.Scene);

        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Object is Mesh);
        Assert.DoesNotContain(rows, r => r.Object.IsInternal);
        Assert.All(rows, r => Assert.True(Outliner.IsListed(r.Object)));
    }

    /// <summary>Saves the model, reads it back, and checks the second save is byte-exact.</summary>
    private static void AssertReloads(SceneModel model, Action<SceneModel>? verify = null)
    {
        string first = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-outliner-{Guid.NewGuid():N}.akt");
        string second = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-outliner-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(first);
            var reloaded = TestFiles.LoadScene(first);
            verify?.Invoke(reloaded);
            reloaded.Save(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }
}
