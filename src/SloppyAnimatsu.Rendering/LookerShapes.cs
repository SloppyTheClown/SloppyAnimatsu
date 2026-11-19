using System.Numerics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Rendering;

/// <summary>
/// Triangles for the marker of an animated camera's looker target
/// a view-facing disc of radius half the <c>TargetRenderSize * TargetRenderSizeRatio * 0.3</c> scale
/// The app draws it with, in the colour <c>0xffb8b8b8</c> - white while
/// selected - at alpha <c>0x7f</c>.
/// </summary>
public static class LookerShapes
{
    /// <summary>Rim steps per disc; the app walks the circle in 121 steps (0x79).</summary>
    public const int Steps = 120;

    /// <summary>The marker's alpha, 0x7f.</summary>
    public const float Alpha = 0x7f / 255f;

    private static readonly Vector3 Color = new(0xB8 / 255f);

    /// <summary>
    /// One view-facing disc per marker, as triangles (<see cref="ScenePose.LineVertexFloats"/> floats
    /// per vertex). A marker exactly on <paramref name="eye"/> is left out: it has no facing.
    /// </summary>
    public static float[] Discs(IReadOnlyList<LookerMarker> markers, Vector3 eye, Object3D? selected)
    {
        var vertices = new List<float>(markers.Count * Steps * 3 * ScenePose.LineVertexFloats);
        foreach (var marker in markers)
        {
            var forward = marker.Position - eye;
            if (forward.LengthSquared() < 1e-12f) continue;
            forward = Vector3.Normalize(forward);
            var up = MathF.Abs(forward.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
            var u = Vector3.Normalize(Vector3.Cross(up, forward));
            var v = Vector3.Cross(forward, u);
            var color = ReferenceEquals(marker.Target, selected) ? Vector3.One : Color;
            for (int k = 0; k < Steps; k++)
            {
                float a = k * 2 * MathF.PI / Steps;
                float b = (k + 1) * 2 * MathF.PI / Steps;
                Add(vertices, marker.Position, color);
                Add(vertices, marker.Position + (MathF.Cos(a) * u + MathF.Sin(a) * v) * marker.Radius, color);
                Add(vertices, marker.Position + (MathF.Cos(b) * u + MathF.Sin(b) * v) * marker.Radius, color);
            }
        }
        return vertices.ToArray();
    }

    private static void Add(List<float> vertices, Vector3 position, Vector3 color)
    {
        vertices.Add(position.X); vertices.Add(position.Y); vertices.Add(position.Z);
        vertices.Add(color.X); vertices.Add(color.Y); vertices.Add(color.Z);
    }
}