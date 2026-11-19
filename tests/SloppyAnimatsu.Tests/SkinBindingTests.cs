using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Bind and Unbind Skin on the user's samples:
/// the bind pose comes from the reference pose, free meshes are attached keeping their world, unbind
/// detaches every unlocked skinned mesh to the scene, and the edited scene round-trips byte-exact.
/// </summary>
public class SkinBindingTests
{
    [SceneTheory, SceneSamples]
    public void Unbind_DetachesEverySkinnedMeshKeepingItsWorld(string path)
    {
        var model = TestFiles.LoadScene(path);
        var meshes = model.Scene.Meshes.Where(m => m.Skinned).ToList();
        if (meshes.Count == 0) return;
        var worlds = meshes.ToDictionary(m => m, m => m.WorldMatrix);
        var bindPoses = meshes.ToDictionary(m => m, m => m.BindPose);
        var akeytsu = meshes.ToDictionary(m => m, m => m.SkinnedWithAkeytsu);
        var weights = meshes.ToDictionary(m => m, m => m.SkinWeights);

        int count = SkinEditor.Unbind(model.Scene);

        Assert.True(count >= meshes.Count);
        foreach (var mesh in meshes)
        {
            Assert.False(mesh.Skinned);
            Assert.Same(model.Scene, mesh.Parent);
            AssertSameWorld(worlds[mesh], mesh.WorldMatrix);
            Assert.Same(bindPoses[mesh], mesh.BindPose);
            Assert.Equal(akeytsu[mesh], mesh.SkinnedWithAkeytsu);
            AssertSameWeights(weights[mesh], mesh.SkinWeights);
        }
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void Bind_AttachesTheFreeMeshesAndUsesTheCharactersBindPose(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = CharacterWithMeshes(model);
        if (character == null) return;
        var meshes = OwnMeshes(character);
        var worlds = meshes.ToDictionary(m => m, m => m.WorldMatrix);
        var weights = meshes.ToDictionary(m => m, m => m.SkinWeights);
        SkinEditor.Unbind(model.Scene);

        var editor = new SkinEditor(character);
        int count = editor.Bind(meshes);

        Assert.Equal(meshes.Count, count);
        var bindPose = character.BindPoses.FirstOrDefault();
        Assert.NotNull(bindPose);
        foreach (var mesh in meshes)
        {
            Assert.Same(character, mesh.Ancestor<Character>());
            Assert.True(mesh.Skinned);
            Assert.True(mesh.SkinnedWithAkeytsu);
            Assert.True(mesh.SmoothSkinningProcessed);
            Assert.Same(bindPose, mesh.BindPose);
            AssertSameWorld(worlds[mesh], mesh.WorldMatrix);
            // The smooth array is the solver output and is carried as it is; the rigid array
            // is rewritten with one influence of weight 1 per control point.
            AssertSameWeights(weights[mesh], mesh.SkinWeights);
            AssertRigidArray(mesh, character);
        }
        if (character.Source.Has("RigidModified"))
            Assert.False(character.Source.Get<bool>("RigidModified"));
        Assert.Null(character.CurrentPose);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void Bind_FillsAnEmptySmoothArrayWithTheRigidSet(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = CharacterWithMeshes(model);
        if (character == null) return;
        var mesh = OwnMeshes(character).FirstOrDefault(m => m.ControlPoints.Length > 0);
        if (mesh == null) return;
        mesh.Source.Set("SkinWeightArray", new List<object?>());
        mesh.Source.Set("RigidSkinWeightArray", new List<object?>());
        SkinEditor.Unbind(model.Scene);

        new SkinEditor(character).Bind([mesh]);

        AssertRigidArray(mesh, character);
        Assert.True(mesh.SmoothSkinningProcessed);
        var smooth = mesh.SkinWeights;
        Assert.Equal(mesh.ControlPoints.Length, smooth.Length);
        for (int p = 0; p < smooth.Length; p++)
        {
            Assert.Single(smooth[p]);
            Assert.Equal(1f, smooth[p][0].Weight);
        }
        Assert.True(new MeshSkin(mesh).IsSkinned);
        AssertRoundtrip(model);
    }

    [Fact]
    public void Bind_WithKeptWeightsAndNoOldRigLeavesTheJointIndicesAlone()
    {
        var path = TestFiles.AllSceneSamples.FirstOrDefault(
            p => Path.GetFileName(p) == "4_CenteredRootAddedHipsRenamedSome.akt");
        if (path == null) return;
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(
            c => c.Name == "Default character" && c.Joints.Count == 0);
        var mesh = model.Scene.Meshes.FirstOrDefault(m => m.Name == "Dup_b");
        if (character == null || mesh == null) return;
        var smooth = mesh.SkinWeights;
        var rigid = mesh.RigidSkinWeights;
        SkinEditor.Unbind(model.Scene);

        // The bind is not told which rig the kept arrays belong to, so it does not renumber them:
        // only the AutoRigger's "Smooth" answer passes the replaced rig's joint names.
        new SkinEditor(character).Bind([mesh], rewriteWeights: false);

        AssertSameWeights(smooth, mesh.SkinWeights);
        AssertSameWeights(rigid, mesh.RigidSkinWeights);
        Assert.True(mesh.Skinned);
        Assert.Same(character, mesh.BindPose?.Character);
    }

    [SceneTheory, SceneSamples]
    public void Bind_CreatesTheBindPoseFromTheReferencePose(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.RefPose != null && OwnMeshes(c).Count > 0);
        if (character == null) return;
        // Drop the whole bind-pose folder: binding must recreate it.
        if (character.Child("__BindPoses") is { } folder) folder.Source.Parent?.RemoveChild(folder.Source);
        Assert.Empty(character.BindPoses);

        var editor = new SkinEditor(character);
        var created = editor.CreateBindPose();

        Assert.Equal("Bind Pose", created.Name);
        Assert.Same(character, created.Character);
        Assert.Contains(created, character.BindPoses);
        var reference = character.RefPose!;
        Assert.Equal(reference.Count, created.Count);
        for (int i = 0; i < reference.Count; i++)
            Assert.True(AktMath.MaxDifference(reference.Matrix(i), created.Matrix(i)) < 1e-5f,
                $"controller {i}: {reference.Matrix(i).Translation} vs {created.Matrix(i).Translation}");

        var mesh = OwnMeshes(character).FirstOrDefault();
        if (mesh != null)
        {
            var world = mesh.WorldMatrix;
            SkinEditor.Unbind(model.Scene);
            editor.Bind([mesh]);
            Assert.Same(created, mesh.BindPose);
            AssertSameWorld(world, mesh.WorldMatrix);
        }
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void Unbind_LeavesLockedMeshesAttached(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = CharacterWithMeshes(model);
        if (character == null) return;
        var mesh = OwnMeshes(character)[0];
        mesh.Source.Set("SkinnedMeshIsLocked", true);
        var parent = mesh.Parent;
        var world = mesh.WorldMatrix;

        SkinEditor.Unbind(model.Scene);

        Assert.True(mesh.Skinned);
        if (parent != null) Assert.Same(parent, mesh.Parent);
        AssertSameWorld(world, mesh.WorldMatrix);

        mesh.Source.Set("SkinnedMeshIsLocked", false);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void Bind_LeavesAnotherCharactersMeshesAlone(string path)
    {
        var model = TestFiles.LoadScene(path);
        var characters = model.Scene.Characters.ToList();
        if (characters.Count < 2) return;
        var first = characters[0];
        var otherMeshes = characters.Skip(1).SelectMany(OwnMeshes).Where(m => m.Skinned).ToList();
        if (otherMeshes.Count == 0) return;
        var parents = otherMeshes.ToDictionary(m => m, m => m.Parent);

        new SkinEditor(first).Bind();

        foreach (var mesh in otherMeshes)
        {
            Assert.True(mesh.Skinned);
            if (parents[mesh] != null) Assert.Same(parents[mesh], mesh.Parent);
        }
        AssertRoundtrip(model);
    }

    private static Character? CharacterWithMeshes(SceneModel model) =>
        model.Scene.Characters.FirstOrDefault(c => c.RefPose != null && OwnMeshes(c).Any(m => m.Skinned));

    private static List<Mesh> OwnMeshes(Character character) =>
        character.Model.Scene.Meshes.Where(m => ReferenceEquals(m.BindPose?.Character, character)).ToList();

    private static void AssertSameWorld(Matrix4x4 expected, Matrix4x4 actual)
    {
        float scale = Math.Max(1f, new Vector3(expected.M41, expected.M42, expected.M43).Length());
        Assert.True(AktMath.MaxDifference(expected, actual) < 1e-4f * scale,
            $"{expected.Translation} vs {actual.Translation}");
    }

    private static void AssertSameWeights(VertexWeight[][] expected, VertexWeight[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int p = 0; p < expected.Length; p++)
            Assert.Equal(expected[p], actual[p]);
    }

    /// <summary>
    /// One influence of weight 1 per control point, its bone index inside the skinning order
    /// </summary>
    private static void AssertRigidArray(Mesh mesh, Character character)
    {
        var rigid = mesh.RigidSkinWeights;
        Assert.Equal(mesh.ControlPoints.Length, rigid.Length);
        for (int p = 0; p < rigid.Length; p++)
        {
            Assert.Single(rigid[p]);
            Assert.Equal(1f, rigid[p][0].Weight);
            Assert.InRange(rigid[p][0].Bone, 0, character.Joints.Count - 1);
        }
    }

    /// <summary>The edited document writes a file that reads and writes back byte-exact.</summary>
    private static void AssertRoundtrip(SceneModel model)
    {
        string first = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-skin-{Guid.NewGuid():N}.akt");
        string second = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-skin-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(first);
            TestFiles.LoadScene(first).Save(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }
}