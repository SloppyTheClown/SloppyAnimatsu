using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Tests;

/// <summary>MeshSkin and the viewer's ScenePose on the user's samples.</summary>
public class SkinningTests
{
    /// <summary>Posed at its own bind pose, a skinned mesh shows exactly as stored.</summary>
    [SceneTheory]
    [SceneSamples]
    public void BindPoseReproducesStoredMesh(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var mesh in model.Scene.Meshes)
        {
            var skin = new MeshSkin(mesh);
            if (!skin.IsSkinned || skin.Character == null) continue;
            var bindWorlds = new PoseEvaluator(skin.Character, restPose: mesh.BindPose).WorldMatrices(null, 0);
            var posed = skin.Positions(bindWorlds);
            float scale = Math.Max(1, skin.ControlPoints.Max(p => p.Length()));
            for (int i = 0; i < posed.Length; i++)
            {
                var stored = Vector3.Transform(skin.ControlPoints[i], skin.MeshWorld);
                Assert.True(Vector3.Distance(stored, posed[i]) < 1e-3f * scale,
                    $"{mesh.Name} point {i}: {posed[i]} vs {stored}");
            }
        }
    }

    [SceneTheory]
    [SceneSamples]
    public void ScenePoseProducesFiniteGeometry(string path)
    {
        var pose = new ScenePose(TestFiles.LoadScene(path));
        // Three of the samples save their first character hidden; a hidden object draws nothing,
        // so start from a shown scene before measuring what the pose produced.
        Outliner.ShowAll(pose.Model.Scene);
        var character = pose.Model.Scene.CurrentCharacter ?? pose.Characters.FirstOrDefault();
        var animation = character?.CurrentAnimation;
        pose.Update(character, animation, animation?.TimeRange.End ?? 0);

        Assert.Equal(0, pose.MeshVertices.Length % (3 * ScenePose.MeshVertexFloats));
        Assert.All(pose.MeshVertices, v => Assert.True(float.IsFinite(v)));
        Assert.All(pose.SkeletonLines, v => Assert.True(float.IsFinite(v)));
        // One point per shown controller: a hidden object is not drawn;
        // the shoe locators are drawn as plus signs, and only when ShowShoeLocators is set; a reverse
        // foot is drawn as its sole outline only, with no dot at its origin.
        Assert.Equal((character?.Controllers.Count(c => c.IsShown && c is not ReverseFootLocator and not ControllerReverseFoot) ?? 0) * ScenePose.LineVertexFloats,
            pose.SkeletonPoints.Length);

        // a ring is two strips of 121 vertex pairs (480
        // triangles), a sole outline two strips of 123 pairs (488 triangles).
        var ik = character?.IKControllers.ToList() ?? [];
        int soles = ik.Count(c => c is ControllerReverseFoot);
        int triangles = (ik.Count - soles) * 480 + soles * 488;
        Assert.Equal(triangles * 3 * ScenePose.LineVertexFloats, pose.ControllerTriangles.Length);
        Assert.All(pose.ControllerTriangles, v => Assert.True(float.IsFinite(v)));
    }

    /// <summary>
    /// An unskinned mesh moved below a joint (no sample has one, so it is made in memory) keeps
    /// its offset to the joint's evaluated world matrix at every frame.
    /// </summary>
    [SceneTheory]
    [SceneSamples]
    public void UnskinnedMeshUnderJointFollowsIt(string path)
    {
        var model = TestFiles.LoadScene(path);
        // The samples that ship hidden characters would hide the mesh this test moves under a
        // joint, and a hidden mesh is not drawn.
        Outliner.ShowAll(model.Scene);
        var character = model.Scene.Characters.FirstOrDefault(c => c.Animations.Any(a => a.TimeRange.End > a.TimeRange.Start));
        var meshes = model.Scene.Meshes.Where(m => m.Polygons.Count > 0).ToList();
        if (character == null || meshes.Count == 0) return;
        var animation = character.Animations.First(a => a.TimeRange.End > a.TimeRange.Start);
        var (start, end) = animation.TimeRange;

        // The joint that moves most over the animation.
        var evaluator = new PoseEvaluator(character);
        var first = evaluator.WorldMatrices(animation, start);
        var last = evaluator.WorldMatrices(animation, end);
        var joint = character.Joints
            .Select(j => (Joint: j, Index: evaluator.IndexOf(j)))
            .Where(j => j.Index >= 0)
            .MaxBy(j => Vector3.Distance(first[j.Index].Translation, last[j.Index].Translation));
        if (Vector3.Distance(first[joint.Index].Translation, last[joint.Index].Translation) < 1e-4f) return;

        // Keep one mesh, unskinned, as a child of the joint.
        var mesh = meshes[0];
        foreach (var other in model.Scene.Meshes.ToList())
            other.Source.Parent?.Children.Remove(other.Source);
        mesh.Source.Set("Skinned", false);
        joint.Joint.Source.AddChild(mesh.Source);

        var pose = new ScenePose(model);
        var offsets = new List<Vector3[]>();
        foreach (float time in new float[] { start, (start + end) / 2f, end })
        {
            pose.Update(character, animation, time);
            Assert.True(Matrix4x4.Invert(evaluator.WorldMatrices(animation, time)[joint.Index], out var toJoint));
            var vertices = pose.MeshVertices;
            Assert.True(vertices.Length > 0);
            offsets.Add(Enumerable.Range(0, vertices.Length / ScenePose.MeshVertexFloats)
                .Select(v => v * ScenePose.MeshVertexFloats)
                .Select(o => Vector3.Transform(new Vector3(vertices[o], vertices[o + 1], vertices[o + 2]), toJoint))
                .ToArray());
        }
        float size = Math.Max(1, offsets[0].Max(p => p.Length()));
        for (int t = 1; t < offsets.Count; t++)
            for (int i = 0; i < offsets[0].Length; i++)
                Assert.True(Vector3.Distance(offsets[0][i], offsets[t][i]) < 1e-3f * size,
                    $"{mesh.Name} vertex {i} drifts from {joint.Joint.Name}: {offsets[0][i]} vs {offsets[t][i]}");
    }
}
