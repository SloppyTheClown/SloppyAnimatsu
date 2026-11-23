namespace SloppyAnimatsu.Core.Fbx;

/// <summary>
/// Turns a curve sampled once per frame into a few keys that reproduce every sample within a
/// tolerance. Cubic fits give each key its own incoming and outgoing slope (cubic Hermite between
/// keys, the curve an FBX reader evaluates for user tangents); linear fits join the keys with straight
/// segments. Keys always sit on frames and carry the sampled value, so the fit is exact at its keys.
/// </summary>
public static class CurveFit
{
    /// <summary>One fitted key: its frame, its value and its slopes in value per frame.</summary>
    public readonly record struct Key(int Frame, float Value, float SlopeIn, float SlopeOut);

    /// <summary>
    /// Fits <paramref name="samples"/> (one per frame, frame 0 first). Starts from the first and
    /// last frame and splits every segment whose worst frame is further than
    /// <paramref name="tolerance"/> from the sample at that frame, until none is.
    /// </summary>
    public static Key[] Fit(ReadOnlySpan<float> samples, float tolerance, bool cubic)
    {
        int n = samples.Length;
        if (n == 0) return [];
        if (n == 1) return [new Key(0, samples[0], 0, 0)];
        var y = samples.ToArray();
        var frames = new List<int> { 0, n - 1 };
        // Segment slopes as tangents scaled to the segment (value change over the segment).
        var segments = new List<(float TOut, float TIn)>();
        while (true)
        {
            segments.Clear();
            var splits = new List<int>();
            for (int s = 0; s + 1 < frames.Count; s++)
            {
                int a = frames[s], b = frames[s + 1];
                var (tOut, tIn) = cubic ? FitSegment(y, a, b) : (y[b] - y[a], y[b] - y[a]);
                segments.Add((tOut, tIn));
                int worst = -1;
                float worstError = tolerance;
                for (int i = a + 1; i < b; i++)
                {
                    float error = MathF.Abs(Hermite(y[a], y[b], tOut, tIn, (i - a) / (float)(b - a)) - y[i]);
                    if (error > worstError || float.IsNaN(error)) { worstError = error; worst = i; }
                }
                if (worst >= 0) splits.Add(worst);
            }
            if (splits.Count == 0) break;
            frames.AddRange(splits);
            frames.Sort();
        }

        var keys = new Key[frames.Count];
        for (int k = 0; k < frames.Count; k++)
        {
            int f = frames[k];
            float slopeIn = k > 0 ? segments[k - 1].TIn / (f - frames[k - 1]) : 0;
            float slopeOut = k + 1 < frames.Count ? segments[k].TOut / (frames[k + 1] - f) : 0;
            if (k == 0) slopeIn = slopeOut;
            if (k == frames.Count - 1) slopeOut = slopeIn;
            keys[k] = new Key(f, y[f], slopeIn, slopeOut);
        }
        return keys;
    }

    /// <summary>Cubic Hermite between <paramref name="a"/> and <paramref name="b"/> with segment-scaled tangents.</summary>
    public static float Hermite(float a, float b, float tangentOut, float tangentIn, float u)
    {
        float u2 = u * u, u3 = u2 * u;
        return (2 * u3 - 3 * u2 + 1) * a + (u3 - 2 * u2 + u) * tangentOut + (-2 * u3 + 3 * u2) * b + (u3 - u2) * tangentIn;
    }

    /// <summary>
    /// The two tangents of segment a..b that fit its inner frames best in the least-squares sense.
    /// A small pull towards the straight line keeps a segment with one inner frame (two unknowns,
    /// one equation) and a flat run well defined; a segment with no inner frame is the straight line.
    /// </summary>
    private static (float TOut, float TIn) FitSegment(float[] y, int a, int b)
    {
        double secant = y[b] - y[a];
        if (b - a < 2) return ((float)secant, (float)secant);
        // Normal equations of  sum (h10*Ta + h11*Tb - r)^2 + w*((Ta-secant)^2 + (Tb-secant)^2).
        const double pull = 1e-3;
        double s11 = pull, s12 = 0, s22 = pull, r1 = pull * secant, r2 = pull * secant;
        for (int i = a + 1; i < b; i++)
        {
            double u = (i - a) / (double)(b - a), u2 = u * u, u3 = u2 * u;
            double h00 = 2 * u3 - 3 * u2 + 1, h01 = -2 * u3 + 3 * u2, h10 = u3 - 2 * u2 + u, h11 = u3 - u2;
            double r = y[i] - h00 * y[a] - h01 * y[b];
            s11 += h10 * h10; s12 += h10 * h11; s22 += h11 * h11;
            r1 += h10 * r; r2 += h11 * r;
        }
        double det = s11 * s22 - s12 * s12;
        if (Math.Abs(det) < 1e-18) return ((float)secant, (float)secant);
        return ((float)((r1 * s22 - r2 * s12) / det), (float)((s11 * r2 - s12 * r1) / det));
    }
}
