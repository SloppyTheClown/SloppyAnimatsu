using System.Numerics;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// FBX export. The written file must read back with
/// <see cref="FbxDocument"/>; where reference exports are present (samples/fbx, which is not
/// in the repository) the export is compared with them. Those tests return early when the
/// samples are absent, like the other sample-based tests (set SLOPPYANIMATSU_SAMPLES).
/// </summary>
public sealed class FbxExportTests
{
    private static readonly FbxExportOptions Fixed = new() { Timestamp = new DateTime(2026, 1, 1, 12, 0, 0) };

    [Fact]
    public void Exporting_the_shipped_scene_reads_back()
    {
        if (TestFiles.Resource3D is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var bytes = FbxExporter.Export(model, Fixed);
        var document = FbxDocument.Read(bytes);
        Assert.Equal(FbxWriter.Version, document.Version);

        var objects = document.Node("Objects")!.Children;
        var meshes = model.Scene.Meshes.ToList();
        Assert.Equal(meshes.Count, objects.Count(o => o.Name == "Model"));
        Assert.Equal(meshes.Count, objects.Count(o => o.Name == "Geometry"));

        foreach (var mesh in meshes)
        {
            var geometry = objects.First(o => o.Name == "Geometry" && FbxScene.ShortName(o) == mesh.Name);
            var vertices = (double[])geometry.Child("Vertices")!.Properties[0];
            Assert.Equal(mesh.ControlPoints.Length * 3, vertices.Length);
            var indices = (int[])geometry.Child("PolygonVertexIndex")!.Properties[0];
            Assert.Equal(mesh.CornerControlPoints.Length, indices.Length);
        }

        var definitions = document.Node("Definitions")!;
        int count = (int)definitions.Child("Count")!.Properties[0];
        Assert.Equal(1 + 1 + objects.Count, count);
    }

    [Fact]
    public void Exporting_twice_gives_the_same_bytes()
    {
        if (TestFiles.Resource3D is not { } path) return;
        var model = TestFiles.LoadScene(path);
        Assert.Equal(FbxExporter.Export(model, Fixed), FbxExporter.Export(model, Fixed));
    }

    [Fact]
    public void A_rig_with_animations_exports_models_skins_and_curves()
    {
        if (Load("19_run.akt") is not { } model) return;
        var character = model.Scene.Characters.First(c => c.Animations.Any());
        var animation = character.Animations.First();
        var bytes = FbxExporter.Export(model, new FbxExportOptions { Timestamp = Fixed.Timestamp, Animations = [animation] });
        var fbx = new FbxScene(FbxDocument.Read(bytes));

        // The character root, its joints and its meshes are models; the node attributes are limbs.
        Assert.Contains(fbx.Models, m => m.Name == character.Name && m.Kind == "Null");
        foreach (var joint in character.Joints)
            Assert.Contains(fbx.Models, m => m.Name == joint.Name && m.Kind == "LimbNode");
        foreach (var mesh in character.Meshes)
            Assert.Contains(fbx.Models, m => m.Name == mesh.Name && m.Kind == "Mesh");

        var stack = fbx.Stacks.Single(s => s.Name == animation.Name);
        var layer = stack.Layers.Single();
        int tracked = animation.Stacks.Sum(s => s.Tracks.Count(t => t.Target is Joint));
        Assert.Equal(tracked * 3, layer.CurveNodes.Count);

        // Every curve node holds the stack's keys (the end marker is dropped).
        foreach (var node in layer.CurveNodes)
        {
            var curve = node.Curves[0]!;
            Assert.NotEmpty(curve.Times);
            Assert.Equal(curve.Times.Length, curve.Values.Length);
        }
    }

    [Fact]
    public void The_unbaked_export_matches_the_reference_fbx()
    {
        if (Load("19_run.akt") is not { } model || ReferenceFbx("unbaked/19_run.fbx") is not { } reference) return;
        var bytes = FbxExporter.Export(model, Fixed);
        var comparison = FbxCompare.Compare(FbxDocument.Read(bytes), FbxDocument.Load(reference));
        Assert.True(comparison.DifferenceCount <= 40,
            $"{comparison.DifferenceCount} differences:{Environment.NewLine}{comparison}");
        // The only known mismatch is one cycle layer at the end of the range:
        // its last wrapped key is written, the reference stops one key earlier.
        Assert.All(comparison.Differences, d => Assert.Contains("AttackHoldMove", d));
    }

    [Fact]
    public void The_baked_export_matches_the_reference_fbx_up_to_the_known_bake_artefacts()
    {
        if (Load("19_run.akt") is not { } model || ReferenceFbx("baked/19_run_baked.fbx") is not { } reference) return;
        var bytes = FbxExporter.Export(model, new FbxExportOptions { Baked = true, Timestamp = Fixed.Timestamp });
        var comparison = FbxCompare.Compare(FbxDocument.Read(bytes), FbxDocument.Load(reference));
        // Structure (models, geometry, skins, poses, stacks, layers) and most values match; what
        // remains is the reference's own bake: the 180 degree flip
        // in one sample animation and joints below non-uniform scale, where the numbers differ by
        // less than 0.03 except that joint's scale (0.72).
        Assert.DoesNotContain(comparison.Differences, d => d.Contains("model '"));
        Assert.DoesNotContain(comparison.Differences, d => d.Contains("geometry"));
        Assert.DoesNotContain(comparison.Differences, d => d.Contains("skin of"));
        Assert.DoesNotContain(comparison.Differences, d => d.Contains("bind pose"));
        Assert.True(comparison.DifferenceCount < 600, $"{comparison.DifferenceCount} differences:{Environment.NewLine}{comparison}");
    }

    [Fact]
    public void The_exported_models_use_the_reference_pose()
    {
        if (Load("19_run.akt") is not { } model) return;
        var bytes = FbxExporter.Export(model, Fixed);
        var fbx = new FbxScene(FbxDocument.Read(bytes));
        var character = model.Scene.Characters.First(c => c.Name == "Dup");
        // Ankle_L is constrained by a reverse-foot controller: its stored LTM is the solved pose,
        // while its model transform must be the animation's base (fbx-export.md "Models").
        var ankle = character.Controllers.First(c => c.Name == "Ankle_L");
        var evaluator = new PoseEvaluator(character);
        var rest = evaluator.RestTransforms[evaluator.IndexOf(ankle)];
        var ankleModel = fbx.Models.First(m => m.Name == "Ankle_L");
        Assert.True(Vector3.Distance(rest.Translation, ankleModel.Translation) < 1e-3f,
            $"model translation {ankleModel.Translation} != reference pose {rest.Translation}");
        Assert.True(Vector3.Distance(ankle.LocalMatrix.Translation, ankleModel.Translation) > 1f,
            "the model transform must not come from the stored LTM");
    }

    /// <summary>A scene from SLOPPYANIMATSU_SAMPLES by file name.</summary>
    private static SceneModel? Load(string fileName) =>
        TestFiles.AllSceneSamples.FirstOrDefault(p => Path.GetFileName(p) == fileName) is { } path
            ? TestFiles.LoadScene(path): null;

    /// <summary>The reference FBX under the workspace's samples/fbx, when it is there.</summary>
    private static string? ReferenceFbx(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "fbx", relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}


