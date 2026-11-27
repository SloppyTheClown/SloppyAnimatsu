using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Normalize Scale keeps every world position while resetting object scales to 1, and a uniform
/// scale (the export factor) multiplies every world position, skinned geometry included.
/// </summary>
public class SceneRescaleTests
{
    private static string? Sample() =>
        TestFiles.SceneSamples.FirstOrDefault(p => Path.GetFileName(p).StartsWith("7_AddingIK", StringComparison.Ordinal))
        ?? TestFiles.SceneSamples.FirstOrDefault(p => new FileInfo(p).Length < 20_000_000);

    private static readonly float[] Times = [0f, 7f, 15f];

    /// <summary>Joint and controller world positions for each animation and time, then every mesh point in world space.</summary>
    private static List<Vector3> WorldPoints(SceneModel model)
    {
        var points = new List<Vector3>();
        foreach (var character in model.Scene.Descendants<Character>())
        {
            var evaluator = new PoseEvaluator(character);
            var animations = character.Animations.Take(2).Cast<Animation?>().Prepend(null).ToList();
            foreach (var animation in animations)
                foreach (float t in Times)
                    points.AddRange(evaluator.WorldMatrices(animation, t).Select(m => m.Translation));
        }
        foreach (var mesh in model.Scene.Meshes)
        {
            var skin = new MeshSkin(mesh);
            if (skin.IsSkinned && skin.Character is { } character)
            {
                var evaluator = new PoseEvaluator(character);
                var animation = character.Animations.FirstOrDefault();
                points.AddRange(skin.Positions(evaluator.WorldMatrices(animation, 7f)).Where((_, i) => i % 7 == 0));
            }
            else
                points.AddRange(mesh.ControlPoints.Where((_, i) => i % 7 == 0).Select(p => Vector3.Transform(p, mesh.WorldMatrix)));
        }
        return points;
    }

    private static void AssertClose(IReadOnlyList<Vector3> expected, IReadOnlyList<Vector3> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        float extent = expected.Select(p => p.Length()).DefaultIfEmpty(1f).Max();
        float tolerance = MathF.Max(extent, 1f) * 2e-4f;
        int worst = -1;
        float worstDistance = 0f;
        for (int i = 0; i < expected.Count; i++)
        {
            float d = Vector3.Distance(expected[i], actual[i]);
            if (d > worstDistance) { worstDistance = d; worst = i; }
        }
        Assert.True(worstDistance <= tolerance,
            $"Point {worst} moved by {worstDistance} (tolerance {tolerance}): {(worst >= 0 ? expected[worst] : default)} -> {(worst >= 0 ? actual[worst] : default)}");
    }

    [Fact]
    public void Normalize_KeepsEveryWorldPosition_AndSetsScalesToOne()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var before = WorldPoints(model);

        var rescale = SceneRescale.Normalize(model);

        Assert.NotEmpty(rescale.Normalized);
        foreach (var o in rescale.Normalized)
        {
            var s = Object3D.LocalScale(o.LocalMatrix);
            Assert.True(MathF.Abs(s.X - 1f) < 1e-4f && MathF.Abs(s.Y - 1f) < 1e-4f && MathF.Abs(s.Z - 1f) < 1e-4f, $"{o} has scale {s}");
        }
        Assert.False(SceneRescale.SceneNeedsNormalizing(model));
        AssertClose(before, WorldPoints(model));
    }

    /// <summary>Every joint/controller scale the evaluator gives, per character, animation and time.</summary>
    private static List<(string Where, Vector3 Scale)> ControllerScales(SceneModel model)
    {
        var scales = new List<(string, Vector3)>();
        foreach (var character in model.Scene.Descendants<Character>())
        {
            var evaluator = new PoseEvaluator(character);
            foreach (var (rest, i) in evaluator.RestTransforms.Select((t, i) => (t, i)))
                scales.Add(($"{character.Name} rest #{i}", rest.Scale));
            foreach (var animation in character.Animations.Where(a => a.Stacks.Count > 0).Take(3))
                foreach (float t in Times)
                    foreach (var (local, i) in evaluator.LocalTransforms(animation, t).Select((l, i) => (l, i)))
                        scales.Add(($"{character.Name} {animation.Name}@{t} #{i}", local.Scale));
        }
        return scales;
    }

    [Fact]
    public void Normalize_SetsDriftedControllerScalesToExactlyOne_AndKeepsRealScales()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var before = ControllerScales(model);

        SceneRescale.Normalize(model);

        Assert.Equal(0, SceneRescale.ControllerScaleDrift(model));
        var after = ControllerScales(model);
        Assert.Equal(before.Count, after.Count);
        static float Off(Vector3 s) => MathF.Max(MathF.Abs(s.X - 1), MathF.Max(MathF.Abs(s.Y - 1), MathF.Abs(s.Z - 1)));
        for (int i = 0; i < before.Count; i++)
        {
            var (where, was) = before[i];
            var now = after[i].Scale;
            if (Off(was) > SceneRescale.KeyDriftTolerance)
                // A real scale moves at most by the drift removed from the keys it is interpolated from.
                Assert.True(Vector3.Distance(was, now) <= SceneRescale.KeyDriftTolerance * 1.7321f + 1e-4f * was.Length(), $"{where}: real scale {was} became {now}");
            else if (where.Contains(" rest "))
                Assert.True(Off(now) <= Transform.ScaleRoundOff, $"{where}: rest scale {was} became {now:R}, not 1");
            else
                Assert.True(Off(now) <= Off(was) + 1e-6f, $"{where}: scale {was} drifted further to {now}");
        }
    }

    /// <summary>Every scale float of a track's value keys, in order.</summary>
    private static List<float> KeyScales(AnimTrack track)
    {
        var seen = new List<float>();
        SceneRescale.MapScales(SceneValues.Items(track.Source, "KeyList"), v => { seen.Add(v); return v; });
        return seen;
    }

    [Fact]
    public void Normalize_CleansDriftedKeys_InTracksThatAlsoAnimateScale_AndKeepsNaN()
    {
        // A track holding drift (1.002, 0.9985), a real scale (1.5) and a NaN (a cached render
        // copy) is cleaned value by value: drift becomes 1, the rest is kept as it was.
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var controllers = model.Scene.Descendants<Character>().SelectMany(c => c.Controllers).Select(c => c.Source).ToHashSet();
        var track = model.Scene.Descendants<AnimTrack>()
            .First(t => t.Target != null && controllers.Contains(t.Target.Source) && KeyScales(t).Count >= 6);
        int n = 0;
        float[] planted = [1.002f, 1.5f, float.NaN, 0.9985f];
        var edited = SceneRescale.MapScales(SceneValues.Items(track.Source, "KeyList"), _ => n < planted.Length ? planted[n++] : 1.0005f);
        track.Source.Set("KeyList", edited);
        Assert.True(SceneRescale.ControllerScaleDrift(model) > 0);

        SceneRescale.Normalize(model);

        var after = KeyScales(track);
        Assert.True(after.Count >= 4, "too few scale floats");
        string got = $"{track.Target?.Name}: " + string.Join(" ", after.Take(8).Select(v => v.ToString("R")));
        Assert.True(after[0] == 1f && after[1] == 1.5f && float.IsNaN(after[2]) && after[3] == 1f, got);
        Assert.Equal(1f, after[0]);
        Assert.Equal(1.5f, after[1]);
        Assert.True(float.IsNaN(after[2]));
        Assert.All(after.Skip(3), v => Assert.Equal(1f, v));
        Assert.Equal(0, SceneRescale.ControllerScaleDrift(model));
    }

    [Fact]
    public void Normalize_Undo_RestoresTheScene()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var ltms = model.Scene.Descendants<Object3D>().Select(o => o.LocalMatrix).ToArray();
        var before = WorldPoints(model);

        var rescale = SceneRescale.Normalize(model);
        rescale.Undo();

        Assert.Equal(ltms, model.Scene.Descendants<Object3D>().Select(o => o.LocalMatrix).ToArray());
        AssertClose(before, WorldPoints(model));
    }

    [Fact]
    public void ScaleUniformly_MultipliesEveryWorldPosition()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var expected = WorldPoints(model).Select(p => p * 2f).ToList();

        var rescale = SceneRescale.ScaleUniformly(model, 2f);

        Assert.True(rescale.Changed);
        AssertClose(expected, WorldPoints(model));
    }

    [Fact]
    public void PlacementScale_ScalesTheCharacterAboutItsOrigin_InRiggingOnly()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Descendants<Character>().First();
        var session = new PoseEditSession(new ScenePose(model), character);
        var origin = character.WorldMatrix.Translation;
        var before = new PoseEvaluator(character).WorldMatrices(null, 0f).Select(m => m.Translation - origin).ToList();
        float scale = Object3D.LocalScale(character.LocalMatrix).X;

        Assert.False(session.SetPlacementValue(character, 6, scale * 2f, allowScale: false, out var refused));
        Assert.NotNull(refused);
        for (int channel = 6; channel <= 8; channel++)
            Assert.True(session.SetPlacementValue(character, channel, scale * 2f, allowScale: true, out _));

        Assert.True(MathF.Abs(Object3D.LocalScale(character.LocalMatrix).X / (scale * 2f) - 1f) < 1e-3f);
        var after = new PoseEvaluator(character).WorldMatrices(null, 0f).Select(m => m.Translation - origin).ToList();
        AssertClose(before.Select(p => p * 2f).ToList(), after);
    }

    [Fact]
    public void ExportScaleFactor_ChangesTheFile_AndLeavesTheSceneAlone()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var ltms = model.Scene.Descendants<Object3D>().Select(o => o.LocalMatrix).ToArray();
        var points = WorldPoints(model);
        var stamp = new DateTime(2026, 1, 1);

        var plain = Core.Fbx.FbxExporter.Export(model, new Core.Fbx.FbxExportOptions { Timestamp = stamp });
        var doubled = Core.Fbx.FbxExporter.Export(model, new Core.Fbx.FbxExportOptions { Timestamp = stamp, ScaleFactor = 2f });
        var again = Core.Fbx.FbxExporter.Export(model, new Core.Fbx.FbxExportOptions { Timestamp = stamp });

        Assert.NotEqual(plain, doubled);
        Assert.Equal(plain, again);
        Assert.Equal(ltms, model.Scene.Descendants<Object3D>().Select(o => o.LocalMatrix).ToArray());
        AssertClose(points, WorldPoints(model));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Core.Fbx.FbxExporter.Export(model, new Core.Fbx.FbxExportOptions { ScaleFactor = 0f }));
    }

    [Fact]
    public void ExportScaleFactor_IsKeptInThePreferences()
    {
        if (TestFiles.Preferences is not { } file) return;
        var prefs = Preferences.Load(file);
        Assert.Equal(1f, prefs.ExportSettings.ScaleFactor);
        prefs.ExportSettings = prefs.ExportSettings with { ScaleFactor = 2.5f };
        Assert.Equal(2.5f, prefs.ExportSettings.ScaleFactor);
    }

    [Fact]
    public void ScaleUniformly_RefusesANonPositiveFactor()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        Assert.Throws<ArgumentOutOfRangeException>(() => SceneRescale.ScaleUniformly(model, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => SceneRescale.ScaleUniformly(model, -1f));
    }
}
