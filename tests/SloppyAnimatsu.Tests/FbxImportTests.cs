using System.Numerics;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// FBX import: the exports under samples/fbx are read
/// into a new scene and compared with what the export says. The tests return early when those
/// samples are absent (like the other sample-based tests).
/// </summary>
public sealed class FbxImportTests
{
    private static readonly FbxExportOptions Fixed = new() { Timestamp = new DateTime(2026, 1, 1, 12, 0, 0) };

    [Fact]
    public void The_unbaked_export_imports_as_a_rigged_scene()
    {
        if (ReferenceFbx("unbaked/19_run.fbx") is not { } path) return;
        var model = Import(path);
        var scene = model.Scene;

        // Both characters of 19_run are in the export; Dup is the rig with the animations.
        Assert.Equal(2, scene.Characters.Count());
        var character = scene.Characters.First(c => c.Name == "Dup");
        Assert.Equal(64, character.Joints.Count);
        Assert.Equal(64, character.Controllers.Count);
        Assert.Empty(character.IKControllers);           // FBX has no controller objects, only joints
        Assert.Equal(4, character.Meshes.Count());
        Assert.All(character.Meshes, m => Assert.True(m.Skinned));
        Assert.Equal(105, character.Animations.Count());
        Assert.NotNull(character.RigAnimation);
        Assert.NotNull(character.RefPose);
        Assert.Equal(character.Joints.Count, character.RefPose!.Count);
        Assert.Equal(14, scene.Materials.Count());
        Assert.Equal(10, scene.Textures.Count());
        Assert.All(scene.Textures, t => Assert.NotEmpty(t.Data));
        Assert.Equal((uint)60, scene.FrameRate);
    }

    [Fact]
    public void The_geometry_materials_and_uvs_match_the_export()
    {
        if (ReferenceFbx("unbaked/19_run.fbx") is not { } path) return;
        var model = Import(path);
        var document = FbxDocument.Load(path);
        var fbx = new FbxScene(document);

        foreach (string name in new[] { "bomb", "pie", "Dup_b" })
        {
            var mesh = model.Scene.Meshes.First(m => m.Name == name);
            var fbxModel = fbx.Models.First(m => m.Name == name && m.Kind == "Mesh");
            var geometry = GeometryOf(document, fbxModel.Id)!;
            var vertices = geometry.Value<double[]>("Vertices") ?? [];
            var indices = geometry.Value<int[]>("PolygonVertexIndex") ?? [];

            Assert.Equal(vertices.Length / 3, mesh.ControlPoints.Length);
            for (int i = 0; i < Math.Min(vertices.Length / 3, 24); i++)
                Assert.True(Vector3.Distance(
                    new Vector3((float)vertices[i * 3], (float)vertices[i * 3 + 1], (float)vertices[i * 3 + 2]),
                    mesh.ControlPoints[i]) < 1e-4f);

            // One polygon per negative (last) corner index.
            Assert.Equal(indices.Count(i => i < 0), mesh.Polygons.Count);
            Assert.Equal(indices.Length, mesh.CornerControlPoints.Length);
            var expectedCorners = indices.Select(i => (uint)(i < 0 ? ~i : i)).ToArray();
            Assert.Equal(expectedCorners, mesh.CornerControlPoints);

            // Per-corner normals and UVs are kept with their own value arrays.
            int normalValues = geometry.Child("LayerElementNormal")?.Value<double[]>("Normals")?.Length ?? 0;
            Assert.Equal(normalValues / 3, mesh.Normals.Length);
            Assert.Equal(mesh.CornerControlPoints.Length, mesh.CornerNormals.Length);
            int uvValues = geometry.Child("LayerElementUV")?.Value<double[]>("UV")?.Length ?? 0;
            Assert.Equal(uvValues / 2, mesh.UVs.Length);
            Assert.Equal(mesh.CornerControlPoints.Length, mesh.CornerUVs.Length);

            // The material slots are the materials the model is connected to.
            Assert.Equal(mesh.Materials.Count, document.Node("Connections")!.ChildrenNamed("C")
                .Count(c => c[2] is long parent && parent == fbxModel.Id
                    && c[1] is long child
                    && document.Node("Objects")!.Children.Any(o => o.PropertyCount > 1 && o[0] is long id && id == child && o.Name == "Material")));
            Assert.All(mesh.Polygons, p => Assert.InRange((int)p.MaterialIndex, 0, mesh.Materials.Count - 1));
        }
    }

    [Fact]
    public void The_bind_pose_and_skin_weights_are_read_from_the_export()
    {
        if (ReferenceFbx("unbaked/19_run.fbx") is not { } path) return;
        var model = Import(path);
        var character = model.Scene.Characters.First(c => c.Name == "Dup");

        Assert.All(character.Meshes, mesh =>
        {
            Assert.NotNull(mesh.BindPose);
            Assert.Equal(character.Joints.Count, mesh.BindPose!.Count);
            Assert.Equal(mesh.ControlPoints.Length, mesh.SkinWeights.Length);
            Assert.All(mesh.SkinWeights, weights =>
            {
                Assert.NotEmpty(weights);
                Assert.InRange(weights.Sum(w => w.Weight), 0.99f, 1.01f);
                Assert.All(weights, w => Assert.InRange(w.Bone, 0, character.Joints.Count - 1));
            });
            // The bind pose is a separate pose in __BindPoses (the reference pose is the base the
            // animation deltas are relative to, and may differ from it in a rig edited after binding).
            Assert.Equal("Bind Pose", mesh.BindPose!.Name);
            Assert.Contains(mesh.BindPose, character.BindPoses);
            Assert.Equal(character.Joints.Count, mesh.BindPose.Count);
        });
    }

    [Fact]
    public void An_imported_scene_saves_and_reloads_byte_exact()
    {
        if (ReferenceFbx("unbaked/19_run.fbx") is not { } path) return;
        var model = Import(path);
        string first = TempFile();
        model.Save(first);
        var reloaded = TestFiles.LoadScene(first);
        string second = TempFile();
        reloaded.Save(second);
        Assert.Equal(new FileInfo(first).Length, new FileInfo(second).Length);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void An_additive_layer_is_read_as_deltas()
    {
        if (ReferenceFbx("unbaked/19_run.fbx") is not { } path) return;
        var model = Import(path);
        var character = model.Scene.Characters.First(c => c.Name == "Dup");
        var fbx = new FbxScene(FbxDocument.Load(path));

        // "Run/All_2" and "DamageFlyTumbleBounce/All" are additive layers: the FBX curve values
        // are already the layer's own deltas, the value an .akt additive key stores.
        foreach (var (animationName, stackName) in new[] { ("Run", "All_2"), ("DamageFlyTumbleBounce", "All") })
        {
            var stack = character.Animations.First(a => a.Name == animationName).Stacks.First(s => s.Name == stackName);
            Assert.True(stack.IsAdditive, $"{animationName}/{stackName} should be additive");
            var layer = fbx.Stacks.First(s => s.Name == animationName).Layers.First(l => l.Name == stackName);
            Assert.Equal(0, layer.BlendMode);

            var joint = character.Joints.First(j => j.Name == "Pelvis");
            var track = stack.Tracks.First(t => t.Target?.Name == "Pelvis");
            var node = layer.CurveNodes.First(cn => cn.Property == "Lcl Translation" && cn.Model?.Name == "Pelvis");
            var curve = node.Curves[0]!;
            var value = (TransformKey)track.Keys[0]!;
            Assert.True(Vector3.Distance(value.Value.Translation, node.Evaluate(curve.Times[0])) < 1e-3f,
                $"{animationName}/{stackName}: key translation {value.Value.Translation} is not the layer delta {node.Evaluate(curve.Times[0])}");
            // A translated delta is small next to the Pelvis's place in the rig (a double-converted
            // delta would be an absolute local transform, hundreds of units away).
            Assert.True(value.Value.Translation.Length() < 200f,
                $"{animationName}/{stackName}: {value.Value.Translation} looks like an absolute transform");
        }
    }

    [Fact]
    public void Tangent_types_come_from_the_key_flags()
    {
        if (ReferenceFbx("unbaked/19_run.fbx") is not { } path) return;
        var model = Import(path);
        var character = model.Scene.Characters.First(c => c.Name == "Dup");
        var fbx = new FbxScene(FbxDocument.Load(path));

        var animation = character.Animations.First(a => a.Name == "WpnBombSpecialDashFinishWall");
        var stack = animation.Stacks.Single();
        var layer = fbx.Stacks.First(s => s.Name == animation.Name).Layers.Single();
        int compared = 0;
        foreach (var track in stack.Tracks)
        {
            if (track.Target is not Joint joint) continue;
            var node = layer.CurveNodes.FirstOrDefault(cn => cn.Property == "Lcl Translation" && cn.Model?.Name == joint.Name);
            if (node?.Curves[0] is not { } curve) continue;
            var modes = track.TangentModes;
            for (int k = 0; k < Math.Min(modes.Count, curve.KeyFlags.Length); k++)
            {
                uint expected = (curve.KeyFlags[k] & 0xE) switch { 2 => 5u, 4 => 1u, _ => 0u };
                Assert.Equal(expected, modes[k][0].LowMode);
                compared++;
            }
        }
        Assert.True(compared > 100, $"only {compared} tangent modes compared");
    }

    [Fact]
    public void A_stacks_keys_evaluate_to_the_exported_local_transforms()
    {
        if (ReferenceFbx("unbaked/19_run.fbx") is not { } path) return;
        var model = Import(path);
        var character = model.Scene.Characters.First(c => c.Name == "Dup");
        var fbx = new FbxScene(FbxDocument.Load(path));

        var animation = character.Animations.First(a => a.Name == "WpnBombWeakStart");
        var stack = animation.Stacks.Single();
        var fbxStack = fbx.Stacks.First(s => s.Name == "WpnBombWeakStart");
        Assert.Single(fbxStack.Layers);

        // Compare at a key time: between keys the .akt curve uses its own tangents while the FBX
        // curve is sampled linearly by the comparison reader.
        int frame = stack.Keys[1].Time;
        var evaluator = new PoseEvaluator(character);
        var locals = evaluator.StackLocalTransforms(stack, frame, out _);
        long ticks = (long)Math.Round(frame / 60.0 * FbxScene.TicksPerSecond);
        for (int i = 0; i < character.Joints.Count; i++)
        {
            var joint = character.Joints[i];
            var fbxModel = fbx.Models.FirstOrDefault(m => m.Name == joint.Name && m.Kind == "LimbNode");
            if (fbxModel == null) continue; // a joint the reference does not export (an IK controller's helper)
            var expected = fbxStack.LocalMatrix(fbxModel, ticks);
            Assert.True(Vector3.Distance(expected.Translation, locals[i].Translation) < 0.01f,
                $"joint '{joint.Name}' translation {locals[i].Translation} != FBX {expected.Translation}");
            if (Matrix4x4.Decompose(expected, out var scale, out var rotation, out _))
            {
                Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(rotation, locals[i].Rotation)) - 1f) < 1e-4f,
                    $"joint '{joint.Name}' rotation differs from the FBX");
            }
        }
    }

    [Fact]
    public void Re_exporting_an_import_matches_the_reference_export()
    {
        if (ReferenceFbx("unbaked/19_run.fbx") is not { } path) return;
        var model = Import(path);
        var bytes = FbxExporter.Export(model, Fixed);
        var comparison = FbxCompare.Compare(FbxDocument.Read(bytes), FbxDocument.Load(path), new FbxCompareOptions { MaxReported = 100000 });

        // Structure, geometry, skins, bind poses, key times and tangent types all match; what
        // remains is float precision in the absolute/delta round trip.
        Assert.DoesNotContain(comparison.Differences, d => d.Contains("model '"));
        Assert.DoesNotContain(comparison.Differences, d => d.Contains("geometry"));
        Assert.DoesNotContain(comparison.Differences, d => d.Contains("skin of"));
        Assert.DoesNotContain(comparison.Differences, d => d.Contains("bind pose"));
        Assert.DoesNotContain(comparison.Differences, d => d.Contains("key flags"));
        Assert.DoesNotContain(comparison.Differences, d => d.Contains("key counts"));
        Assert.DoesNotContain(comparison.Differences, d => d.Contains("key times"));
        Assert.DoesNotContain(comparison.Differences, d => d.Contains("materials"));
        Assert.True(comparison.DifferenceCount <= 600,
            $"{comparison.DifferenceCount} differences:{Environment.NewLine}{comparison}");
        // The largest residual is a few tenths of a unit on a key whose stored translation is
        // thousands of units long (a retargeted scene); everything else is below 0.1.
        var values = comparison.Differences
            .Select(d => d[(d.LastIndexOf("by ", StringComparison.Ordinal) + 3)..])
            .Select(v => v.TrimEnd('°'))
            .Where(v => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        Assert.All(values, v => Assert.True(v < 0.25, $"a {v} difference is larger than expected"));
    }

    [Fact]
    public void A_baked_export_imports_one_key_per_frame()
    {
        if (ReferenceFbx("baked/19_run_baked.fbx") is not { } path) return;
        var model = Import(path);
        var character = model.Scene.Characters.First(c => c.Name == "Dup");
        var animation = character.Animations.First(a => a.Name == "WpnBombWeakStart");
        var stack = animation.Stacks.Single();

        var fbx = new FbxScene(FbxDocument.Load(path));
        var fbxStack = fbx.Stacks.First(s => s.Name == "WpnBombWeakStart");
        int layerKeys = fbxStack.Layers[0].CurveNodes.Select(cn => cn.Curves[0]?.Times.Length ?? 0).Max();
        // Baked curves have one key per frame; the import appends the stack's last (loop) key.
        Assert.Equal(layerKeys + 1, stack.Keys.Count);
        for (int i = 1; i < stack.Keys.Count; i++)
            Assert.Equal(1, stack.Keys[i].Time - stack.Keys[i - 1].Time);
    }

    private static SceneModel Import(string path) =>
        FbxImporter.Import(path, new FbxImportOptions { TextureDirectory = Path.GetDirectoryName(path) });

    private static FbxNode? GeometryOf(FbxDocument document, long modelId) =>
        document.Node("Connections")!.ChildrenNamed("C")
            .Where(c => c[2] is long parent && parent == modelId)
            .Select(c => c[1] is long child ? document.Node("Objects")!.Children.FirstOrDefault(o => o.PropertyCount > 1 && o[0] is long id && id == child) : null)
            .FirstOrDefault(n => n?.Name == "Geometry");

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-fbx-import-{Guid.NewGuid():N}.akt");

    /// <summary>The FBX under the workspace's samples/fbx, when it is there.</summary>
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
