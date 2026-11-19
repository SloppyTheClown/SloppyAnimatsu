using System.Numerics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The skin weight generator.
/// </summary>
/// <remarks>
/// <para>
/// The rigid array is the seed: the nearest bone segment's <em>start</em> joint, one influence of
/// weight 1. The smooth array is the seed pushed through <see cref="BodyIteration"/> units of
/// Laplacian smoothing over the mesh's own vertex adjacency
/// (<c>w = w + s * (mean(neighbours' w) - w)</c>), the pass widths following <see cref="Passes"/>
/// (whole units, stopping exactly at <see cref="FaceIteration"/>), a joint's strength following its
/// <c>SmoothMethod</c> (0 body, 1 face, 2 rigid). The result is pruned to
/// <see cref="MaxInfluences"/> influential joints above <see cref="AutoSweepValue"/> and
/// renormalised. Every number here is one of the preference defaults (<c>NbMaxJoints</c> 2,
/// <c>AutoSweepValue</c> 0.01, <c>FaceIteration</c> 0.5, <c>BodyIteration</c> 5); the class the bind
/// actually reads is <c>CSkinWorkshopPreferences</c> - the atoms are shared with
/// <c>CSkinningPreferences</c>, whose copies are legacy data.
/// </para>
/// <para>
/// Not reproduced: the candidate propagation across the mesh graph, so a
/// control point mixes only the joints its own neighbourhood already carried.
/// </para>
/// </remarks>
public static class SkinWeightGenerator
{
    /// <summary>The <c>NbMaxJoints</c>: the influences kept per control point.</summary>
    public const int MaxInfluences = 2;

    /// <summary>The <c>AutoSweepValue</c>: a weight at or below this is dropped.</summary>
    public const float AutoSweepValue = 0.01f;

    /// <summary>The <c>FaceIteration</c>: the smoothing strength of a joint with <c>SmoothMethod</c> 1.</summary>
    public const float FaceIteration = 0.5f;

    /// <summary>The <c>BodyIteration</c>: the smoothing strength of a joint with <c>SmoothMethod</c> 0.</summary>
    public const float BodyIteration = 5f;

    /// <summary>A bone segment in the pose's world space, labelled by the joint at its start.</summary>
    public readonly record struct Bone(int Joint, Vector3 Start, Vector3 End);

    /// <summary>
    /// The rigid seed: for every control point the start joint of its nearest bone segment, one
    /// influence of weight 1. This is also what <c>SkinEditor.Bind</c> writes for the same rig.
    /// </summary>
    public static VertexWeight[][] NearestBones(IReadOnlyList<Vector3> points, Matrix4x4 meshWorld,
        IReadOnlyList<Bone> bones)
    {
        if (points.Count == 0) return [];
        if (bones.Count == 0) throw new ArgumentException("No bone segments to weight against.", nameof(bones));
        var result = new VertexWeight[points.Count][];
        for (int p = 0; p < points.Count; p++)
        {
            var world = Vector3.Transform(points[p], meshWorld);
            int nearest = bones[0].Joint;
            float best = float.MaxValue;
            foreach (var bone in bones)
            {
                float distance = DistanceToSegment(world, bone.Start, bone.End);
                if (distance < best)
                {
                    best = distance;
                    nearest = bone.Joint;
                }
            }
            result[p] = [new VertexWeight(nearest, 1f)];
        }
        return result;
    }

    /// <summary>
    /// The smooth array: the <see cref="NearestBones"/> seed smoothed over
    /// <paramref name="neighbours"/> and pruned.
    /// </summary>
    /// <param name="points">Control points in mesh space.</param>
    /// <param name="meshWorld">The mesh's world matrix in the pose being bound.</param>
    /// <param name="bones">Bone segments in that pose, labelled by their start joint.</param>
    /// <param name="smoothMethods">Per joint index: 0 body, 1 face, 2 rigid (a missing index is body).</param>
    /// <param name="neighbours">Per control point, the control points its polygons connect it to.</param>
    public static VertexWeight[][] Solve(IReadOnlyList<Vector3> points, Matrix4x4 meshWorld,
        IReadOnlyList<Bone> bones, IReadOnlyList<int> smoothMethods, IReadOnlyList<int[]> neighbours,
        int maxInfluences = MaxInfluences, float autoSweep = AutoSweepValue,
        float faceIteration = FaceIteration, float bodyIteration = BodyIteration)
    {
        var seed = NearestBones(points, meshWorld, bones);
        int count = points.Count;
        var channels = new Dictionary<int, float[]>();
        for (int p = 0; p < count; p++)
        {
            int joint = seed[p][0].Bone;
            if (!channels.TryGetValue(joint, out var channel))
                channels[joint] = channel = new float[count];
            channel[p] = 1f;
        }

        Smooth(channels, neighbours, smoothMethods, faceIteration, bodyIteration);

        var result = new VertexWeight[count][];
        for (int p = 0; p < count; p++)
        {
            var influences = new List<VertexWeight>();
            foreach (var (joint, channel) in channels)
                if (channel[p] > autoSweep)
                    influences.Add(new VertexWeight(joint, channel[p]));
            influences.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            if (influences.Count > maxInfluences)
                influences.RemoveRange(maxInfluences, influences.Count - maxInfluences);
            if (influences.Count == 0)
            {
                result[p] = seed[p];
                continue;
            }
            float total = 0f;
            foreach (var influence in influences) total += influence.Weight;
            for (int i = 0; i < influences.Count; i++)
                influences[i] = new VertexWeight(influences[i].Bone, influences[i].Weight / total);
            result[p] = influences.ToArray();
        }
        return result;
    }

    /// <summary>
    /// The smoothing passes: the cumulative width walks from 0 towards
    /// <paramref name="body"/>, each step stopping exactly at <paramref name="face"/>; a joint is
    /// smoothed while its cumulative width is within its <c>SmoothMethod</c> strength.
    /// </summary>
    private static void Smooth(Dictionary<int, float[]> channels, IReadOnlyList<int[]> neighbours,
        IReadOnlyList<int> smoothMethods, float face, float body)
    {
        float cumulative = 0f;
        for (int pass = 0; pass < 1024 && cumulative < body; pass++)
        {
            float step;
            if (cumulative + 1f <= face || face <= cumulative) step = MathF.Min(1f, body - cumulative);
            else step = face - cumulative;
            if (step <= 0f) break;
            cumulative += step;
            foreach (var (joint, channel) in channels)
            {
                int method = joint >= 0 && joint < smoothMethods.Count ? smoothMethods[joint] : 0;
                float strength = method switch { 0 => body, 1 => face, _ => 0f };
                if (cumulative > strength) continue;
                LaplacianStep(channel, neighbours, step);
            }
        }
    }

    /// <summary><c>w = w + s * (mean(neighbours' w) - w)</c> over the mesh's edges.</summary>
    private static void LaplacianStep(float[] channel, IReadOnlyList<int[]> neighbours, float step)
    {
        var previous = (float[])channel.Clone();
        for (int v = 0; v < previous.Length; v++)
        {
            var around = v < neighbours.Count ? neighbours[v] : [];
            if (around.Length == 0) continue;
            float sum = 0f;
            foreach (int n in around) sum += previous[n];
            channel[v] = previous[v] + step * (sum / around.Length - previous[v]);
        }
    }

    /// <summary>
    /// Vertex adjacency from a mesh's polygons: every
    /// polygon edge connects its two control points both ways.
    /// </summary>
    public static int[][] PolygonNeighbours(IReadOnlyList<MeshPolygon> polygons, int controlPointCount)
    {
        var edges = new HashSet<long>[controlPointCount];
        foreach (var polygon in polygons)
        {
            var corners = polygon.Corners;
            if (corners.Length < 2) continue;
            for (int i = 0; i < corners.Length; i++)
            {
                int a = (int)corners[i], b = (int)corners[(i + 1) % corners.Length];
                if (a < 0 || b < 0 || a >= controlPointCount || b >= controlPointCount || a == b) continue;
                (edges[a] ??= []).Add(((long)a << 32) | (uint)b);
                (edges[b] ??= []).Add(((long)b << 32) | (uint)a);
            }
        }
        var result = new int[controlPointCount][];
        for (int v = 0; v < controlPointCount; v++)
            result[v] = edges[v] is { Count: > 0 } set
                ? set.Select(key => (int)(key & 0xffffffff)).ToArray()
                : [];
        return result;
    }

    /// <summary>The distance from a point to a segment, the seed's nearest-bone measure.</summary>
    public static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        var delta = end - start;
        float lengthSquared = delta.LengthSquared();
        if (lengthSquared < 1e-12f) return Vector3.Distance(point, start);
        float t = Math.Clamp(Vector3.Dot(point - start, delta) / lengthSquared, 0f, 1f);
        return Vector3.Distance(point, start + delta * t);
    }
}
