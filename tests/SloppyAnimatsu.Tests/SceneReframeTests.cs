using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Normalize translation / rotation zero the position or rotation of characters, meshes and
/// groups while every joint, controller and mesh point stays where it was in world space.
/// </summary>
public class SceneReframeTests
{
    private static string? Sample() =>
        TestFiles.SceneSamples.FirstOrDefault(p => Path.GetFileName(p).StartsWith("7_AddingIK", StringComparison.Ordinal))
        ?? TestFiles.SceneSamples.FirstOrDefault(p => new FileInfo(p).Length < 20_000_000);

    private static readonly float[] Times = [0f, 7f, 15f];

    /// <summary>Joint and controller world positions per animation and time, then mesh points in world space.</summary>
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
        for (int i = 0; i < expected.Count; i++)
            Assert.True(Vector3.Distance(expected[i], actual[i]) <= tolerance,
                $"Point {i} moved: {expected[i]} -> {actual[i]} (tolerance {tolerance})");
    }

    /// <summary>Moves and turns the first character so there is something to normalize.</summary>
    private static Character Offset(SceneModel model)
    {
        var character = model.Scene.Descendants<Character>().First();
        var offset = Matrix4x4.CreateFromYawPitchRoll(0.6f, 0.2f, -0.3f) * Matrix4x4.CreateTranslation(120f, -40f, 75f);
        character.Source.Set("LTM", AktMath.ToArray(character.LocalMatrix * offset));
        return character;
    }

    [Theory]
    [InlineData(ReframeParts.Translation)]
    [InlineData(ReframeParts.Rotation)]
    [InlineData(ReframeParts.Translation | ReframeParts.Rotation)]
    public void Normalize_KeepsEveryWorldPosition(ReframeParts parts)
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var character = Offset(model);
        Assert.Contains(character, SceneReframe.Candidates(model, parts));
        var before = WorldPoints(model);

        var reframe = SceneReframe.Normalize(model, parts);

        Assert.True(reframe.Changed);
        Assert.Contains(character, reframe.Normalized);
        Assert.False(SceneReframe.HasPartsToNormalize(character, parts), $"{character.Name} still has {parts}");
        AssertClose(before, WorldPoints(model));
    }

    [Fact]
    public void Normalize_Undo_RestoresTheScene()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        Offset(model);
        var ltms = model.Scene.Descendants<Object3D>().Select(o => o.LocalMatrix).ToArray();
        var before = WorldPoints(model);

        var reframe = SceneReframe.Normalize(model, ReframeParts.Translation | ReframeParts.Rotation);
        reframe.Undo();

        Assert.Equal(ltms, model.Scene.Descendants<Object3D>().Select(o => o.LocalMatrix).ToArray());
        AssertClose(before, WorldPoints(model));
    }

    [Fact]
    public void Normalize_LeavesJointsAndControllersAlone()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        Offset(model);
        var controllers = model.Scene.Descendants<Character>().SelectMany(c => c.Controllers).ToHashSet();

        var reframe = SceneReframe.Normalize(model, ReframeParts.Translation | ReframeParts.Rotation);

        Assert.DoesNotContain(reframe.Normalized, o => controllers.Contains(o));
    }
}
