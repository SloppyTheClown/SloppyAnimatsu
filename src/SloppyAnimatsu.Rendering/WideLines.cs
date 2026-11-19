namespace SloppyAnimatsu.Rendering;

/// <summary>
/// Turns a line list into screen-space quads for the renderer's wide-line program. A core or ES
/// context only guarantees 1 px <c>glLineWidth</c>, so a line wider than that (the 3 px shoe
/// locators, the thicker bones) is two triangles per segment that the vertex shader pushes half the
/// width to each side of the segment, in pixels.
/// </summary>
public static class WideLines
{
    /// <summary>Floats per output vertex: position (3), the segment's other end (3), side (1), colour (3).</summary>
    public const int VertexFloats = 10;

    /// <summary>Output vertices per segment (two triangles).</summary>
    public const int VerticesPerSegment = 6;

    /// <summary>
    /// The quads of <paramref name="lines"/>, a <c>GL_LINES</c> array of
    /// <see cref="ScenePose.LineVertexFloats"/>-float vertices (position, colour) in pairs. A trailing
    /// unpaired vertex is dropped.
    /// </summary>
    public static float[] Expand(float[] lines)
    {
        const int stride = ScenePose.LineVertexFloats;
        int segments = lines.Length / (2 * stride);
        var output = new float[segments * VerticesPerSegment * VertexFloats];
        int o = 0;
        for (int s = 0; s < segments; s++)
        {
            int p = s * 2 * stride, q = p + stride;
            // The side is measured from each vertex's own end towards the other, so the same edge of
            // the quad is +1 at P and -1 at Q.
            // Triangles: (P+, P-, Q+) and (Q+, P-, Q-), where Q+ is (Q, P, -1).
            Put(lines, p, q, +1f, ref o, output);
            Put(lines, p, q, -1f, ref o, output);
            Put(lines, q, p, -1f, ref o, output);
            Put(lines, q, p, -1f, ref o, output);
            Put(lines, p, q, -1f, ref o, output);
            Put(lines, q, p, +1f, ref o, output);
        }
        return output;
    }

    private static void Put(float[] lines, int self, int other, float side, ref int o, float[] output)
    {
        output[o++] = lines[self];
        output[o++] = lines[self + 1];
        output[o++] = lines[self + 2];
        output[o++] = lines[other];
        output[o++] = lines[other + 1];
        output[o++] = lines[other + 2];
        output[o++] = side;
        output[o++] = lines[self + 3];
        output[o++] = lines[self + 4];
        output[o++] = lines[self + 5];
    }
}
