using System.Numerics;
using System.Text;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The engine presets of the FBX export: the footer triple,
/// the plain-hierarchy bake, its decomposition, continuous Euler keys and unique stack names.
/// </summary>
public sealed class FbxEngineExportTests
{
    private static readonly DateTime Timestamp = new(2026, 1, 1, 12, 0, 0);

    [Fact]
    public void Presets_default_their_options()
    {
        var plain = new FbxExportOptions();
        Assert.False(plain.Baked);
        Assert.False(plain.PlainHierarchy);
        Assert.False(plain.LinearBakedKeys);
        Assert.False(plain.UniqueAnimationNames);
        Assert.Equal(0f, plain.MinimumScale);
        foreach (var preset in new[] { FbxExportPreset.Unity, FbxExportPreset.Godot, FbxExportPreset.Blender })
        {
            var options = new FbxExportOptions { Preset = preset };
            Assert.True(options.Baked && options.PlainHierarchy && options.LinearBakedKeys && options.UniqueAnimationNames);
            Assert.Equal(1e-3f, options.MinimumScale);
        }
        Assert.False(new FbxExportOptions { Preset = FbxExportPreset.Unity, Baked = false }.Baked);
    }

    [Fact]
    public void The_file_carries_the_matching_footer_triple()
    {
        if (Load("7_AddingIK.akt") is not { } model) return;
        var bytes = FbxExporter.Export(model, new FbxExportOptions { Timestamp = Timestamp });
        Assert.True(Contains(bytes, FbxWriter.FileId), "FileId");
        Assert.True(Contains(bytes, Encoding.ASCII.GetBytes(FbxWriter.CreationTime)), "CreationTime");
        byte[] footerId = Convert.FromHexString("FABCAB09D0C8D466B176FB831CF7267E");
        Assert.True(Contains(bytes, footerId), "footer id");
        Assert.NotNull(FbxDocument.Read(bytes));
    }

    [Fact]
    public void DecomposeLoose_recovers_a_plain_transform_and_survives_degenerate_ones()
    {
        var t = new Transform(new Vector3(1, 2, 3), Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)), new Vector3(2, 0.5f, 1.5f));
        var back = FbxExporter.DecomposeLoose(t.ToMatrix(), Transform.Identity, 0);
        Assert.True(Vector3.Distance(t.Translation, back.Translation) < 1e-5f);
        Assert.True(Vector3.Distance(t.Scale, back.Scale) < 1e-5f);
        Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(t.Rotation, back.Rotation)) - 1) < 1e-5f);

        // A collapsed axis keeps the fallback's rotation and is raised to the floor.
        var zero = Matrix4x4.CreateScale(0f) * Matrix4x4.CreateTranslation(4, 5, 6);
        var fallback = t with { Translation = Vector3.Zero };
        var floored = FbxExporter.DecomposeLoose(zero, fallback, 1e-3f);
        Assert.Equal(new Vector3(4, 5, 6), floored.Translation);
        Assert.All(new[] { floored.Scale.X, floored.Scale.Y, floored.Scale.Z }, s => Assert.True(MathF.Abs(s) >= 1e-3f));
        Assert.True(float.IsFinite(floored.Rotation.X) && MathF.Abs(floored.Rotation.Length() - 1) < 1e-4f);

        // A mirrored matrix gives a negative scale and a proper rotation.
        var mirrored = FbxExporter.DecomposeLoose(Matrix4x4.CreateScale(-1, 1, 1), Transform.Identity, 0);
        Assert.Equal(-1f, mirrored.Scale.X * mirrored.Scale.Y * mirrored.Scale.Z, 4);
    }

    [Fact]
    public void ContinuousEuler_never_jumps_across_the_wrap()
    {
        Assert.Equal(new Vector3(0, 0, 181), FbxExporter.ContinuousEuler(new Vector3(0, 0, 179), new Vector3(0, 0, -179)));
        Assert.Equal(new Vector3(10, 20, 30), FbxExporter.ContinuousEuler(new Vector3(9, 19, 29), new Vector3(10, 20, 30)));
        // The equivalent triple (x+180, 180-y, z+180) is taken when it is closer.
        var picked = FbxExporter.ContinuousEuler(new Vector3(180, 100, 180), new Vector3(0, 80, 0));
        Assert.Equal(new Vector3(180, 100, 180), picked);
    }

    [Fact]
    public void UniqueNames_numbers_later_duplicates_and_skips_taken_names()
    {
        if (Load("19_run.akt") is not { } model) return;
        var animations = model.Scene.Characters.SelectMany(c => c.Animations).Take(3).ToList();
        if (animations.Count < 3) return;
        animations[0].Name = "A";
        animations[1].Name = "A";
        animations[2].Name = "A_2";
        var names = FbxExporter.UniqueNames(animations);
        Assert.Equal(["A", "A_3", "A_2"], animations.Select(a => names[a]));
    }

    [Fact]
    public void An_engine_bake_puts_every_joint_where_the_evaluator_does_and_writes_no_nan()
    {
        if (Load("19_run.akt") is not { } model) return;
        var character = model.Scene.Characters.First(c => c.Animations.Any(a => a.Stacks.Count > 0));
        var animation = character.Animations.First(a => a.Stacks.Count > 0);
        var bytes = FbxExporter.Export(model, new FbxExportOptions
        {
            Preset = FbxExportPreset.Unity, Timestamp = Timestamp, Animations = [animation],
        });
        var fbx = new FbxScene(FbxDocument.Read(bytes));
        var stack = fbx.Stacks.Single(s => s.Name == animation.Name);

        foreach (var node in stack.Layers.SelectMany(l => l.CurveNodes))
            foreach (var curve in node.Curves)
                if (curve != null) Assert.All(curve.Values, v => Assert.True(float.IsFinite(v), $"{node.Model?.Name} {node.Property}"));
        Assert.All(fbx.Models, m => Assert.Equal(1, m.InheritType));

        var evaluator = new PoseEvaluator(character);
        var joints = character.Joints.Where(j => evaluator.IndexOf(j) >= 0).ToList();
        var root = joints.First(j => !joints.Contains(j.Parent3D as Joint));
        var characterModel = fbx.Models.First(m => m.Name == character.Name && m.Kind == "Null");
        var models = characterModel.Descendants().Where(m => m.Kind == "LimbNode").GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First());
        int stop = FbxExporter.StopOf(animation);
        foreach (int frame in new[] { 0, stop / 2, stop })
        {
            long ticks = FbxScene.ToTicks(frame / fbx.FrameRate);
            Matrix4x4 FbxWorld(FbxModel m) => m.Parent is { } p ? stack.LocalMatrix(m, ticks) * FbxWorld(p) : stack.LocalMatrix(m, ticks);
            var worlds = evaluator.WorldMatrices(animation, frame);
            Matrix4x4.Invert(worlds[evaluator.IndexOf(root)], out var evalRoot);
            Matrix4x4.Invert(FbxWorld(models[root.Name!]), out var fbxRoot);
            var expected = joints.ToDictionary(j => j, j => (worlds[evaluator.IndexOf(j)] * evalRoot).Translation);
            float tolerance = 1e-4f * MathF.Max(1f, expected.Values.Max(v => v.Length()));
            foreach (var joint in joints)
            {
                var got = (FbxWorld(models[joint.Name!]) * fbxRoot).Translation;
                Assert.True(Vector3.Distance(expected[joint], got) <= tolerance,
                    $"{joint.Name} frame {frame}: {got} != {expected[joint]}");
            }
        }
    }

    [Fact]
    public void An_engine_bake_gives_a_one_frame_pose_a_one_frame_take()
    {
        if (Load("22_bombFix_MK8.akt") is not { } model) return;
        var animation = model.Scene.Characters.SelectMany(c => c.Animations)
            .First(a => a.Stacks.Count > 0 && FbxExporter.StopOf(a) == 0);
        var fbx = new FbxScene(FbxDocument.Read(FbxExporter.Export(model, new FbxExportOptions
        {
            Preset = FbxExportPreset.Unity, Timestamp = Timestamp, Animations = [animation],
        })));
        var stack = fbx.Stacks.Single();
        Assert.Equal(FbxScene.ToTicks(1 / fbx.FrameRate), stack.Stop);
        foreach (var node in stack.Layers.SelectMany(l => l.CurveNodes))
            foreach (var curve in node.Curves)
                if (curve != null) Assert.Equal(curve.Values[0], curve.Values[^1]);

        var plain = new FbxScene(FbxDocument.Read(FbxExporter.Export(model, new FbxExportOptions
        {
            Timestamp = Timestamp, Animations = [animation], Baked = true,
        })));
        Assert.Equal(0, plain.Stacks.Single().Stop);
    }

    private static SceneModel? Load(string fileName) =>
        TestFiles.AllSceneSamples.FirstOrDefault(p => Path.GetFileName(p) == fileName) is { } path
            ? TestFiles.LoadScene(path)
            : null;

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;
}
