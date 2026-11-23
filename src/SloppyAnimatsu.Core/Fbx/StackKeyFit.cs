using System.Numerics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Core.Fbx;

/// <summary>
/// Picks the stack key times for animation sampled once per frame, so that the app's own playback
/// of those keys (spline tangents through the neighbouring keys, SQUAD for rotation) reproduces
/// every sampled frame within a tolerance. Keys are shared by every track of a stack, so a time a
/// track needs is a key of all of them.
/// </summary>
public static class StackKeyFit
{
    /// <summary>
    /// The key frames for <paramref name="tracks"/>, each holding one key value per frame from
    /// <paramref name="first"/> on. Starts from the first and last frame and adds, for every segment
    /// whose worst frame (over all tracks) is out of tolerance, that frame, until none is.
    /// </summary>
    /// <param name="translationTolerance">Largest translation error, in scene units.</param>
    /// <param name="rotationTolerance">Largest rotation error, in radians.</param>
    /// <param name="scaleTolerance">Largest error of a scale factor.</param>
    public static List<int> Fit(int first, IReadOnlyList<Transform[]> tracks,
        float translationTolerance, float rotationTolerance, float scaleTolerance)
    {
        int count = tracks.Count == 0 ? 0 : tracks.Max(t => t.Length);
        if (count <= 1) return [first];
        return Refine([0, count - 1], count, tracks, translationTolerance, rotationTolerance, scaleTolerance)
            .Select(f => f + first).ToList();
    }

    private static List<int> Refine(List<int> frames, int count, IReadOnlyList<Transform[]> tracks,
        float translationTolerance, float rotationTolerance, float scaleTolerance)
    {
        var worstRatio = new List<float>();
        var worstFrame = new List<int>();
        while (frames.Count < count)
        {
            worstRatio.Clear();
            worstFrame.Clear();
            for (int s = 0; s + 1 < frames.Count; s++) { worstRatio.Add(1); worstFrame.Add(-1); }
            foreach (var samples in tracks)
            {
                if (samples.Length < count) continue;
                var (tangentIn, tangentOut) = Tangents(samples, frames);
                for (int s = 0; s + 1 < frames.Count; s++)
                {
                    int a = frames[s], b = frames[s + 1];
                    var ka = samples[a];
                    var kb = samples[b];
                    for (int f = a + 1; f < b; f++)
                    {
                        float u = (f - a) / (float)(b - a);
                        var value = Interpolate(ka, kb, tangentOut[s], tangentIn[s + 1], u);
                        var truth = samples[f];
                        float ratio = MathF.Max(
                            (value.Translation - truth.Translation).Length() / translationTolerance,
                            MathF.Max(Angle(value.Rotation, truth.Rotation) / rotationTolerance,
                                MaxAbs(value.Scale - truth.Scale) / scaleTolerance));
                        if (ratio > worstRatio[s] || float.IsNaN(ratio) && worstFrame[s] < 0)
                        {
                            worstRatio[s] = float.IsNaN(ratio) ? float.MaxValue : ratio;
                            worstFrame[s] = f;
                        }
                    }
                }
            }
            var splits = worstFrame.Where(f => f >= 0).ToList();
            if (splits.Count == 0) break;
            frames.AddRange(splits);
            frames.Sort();
        }
        return frames;
    }

    /// <summary>
    /// The spline tangents of keys at <paramref name="frames"/>: the slope through the neighbouring
    /// keys, scaled by the time to each side; the first key's neighbour before it and the last key's
    /// after it are the key itself. Rotation tangents are logs in the key's own frame.
    /// </summary>
    private static (Transform[] In, Transform[] Out) Tangents(Transform[] samples, List<int> frames)
    {
        int n = frames.Count;
        var tIn = new Transform[n];
        var tOut = new Transform[n];
        for (int k = 0; k < n; k++)
        {
            int prev = Math.Max(k - 1, 0), next = Math.Min(k + 1, n - 1);
            float t = frames[k], tPrev = frames[prev], tNext = frames[next];
            float span = tNext - tPrev;
            var a = samples[frames[prev]];
            var p = samples[frames[k]];
            var b = samples[frames[next]];
            float inScale = span < 1e-5f ? 0 : (t - tPrev) / span;
            float outScale = span < 1e-5f ? 0 : (tNext - t) / span;
            var translation = b.Translation - a.Translation;
            var scale = b.Scale - a.Scale;
            var inverse = Quaternion.Inverse(p.Rotation);
            var rotation = HermiteKeyInterpolator.Log(inverse * b.Rotation) - HermiteKeyInterpolator.Log(inverse * a.Rotation);
            tIn[k] = new Transform(translation * inScale, Tangent(rotation * inScale), scale * inScale);
            tOut[k] = new Transform(translation * outScale, Tangent(rotation * outScale), scale * outScale);
        }
        return (tIn, tOut);
    }

    private static Quaternion Tangent(Vector3 v) => v == Vector3.Zero ? Quaternion.Identity : new Quaternion(v, 0);

    private static Transform Interpolate(Transform a, Transform b, Transform tangentOut, Transform tangentIn, float u)
    {
        float u2 = u * u, u3 = u2 * u;
        float h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
        return new Transform(
            h00 * a.Translation + h10 * tangentOut.Translation + h01 * b.Translation + h11 * tangentIn.Translation,
            HermiteKeyInterpolator.Squad(a.Rotation, b.Rotation, tangentOut.Rotation, tangentIn.Rotation, u),
            Vector3.Max(h00 * a.Scale + h10 * tangentOut.Scale + h01 * b.Scale + h11 * tangentIn.Scale,
                new Vector3(HermiteKeyInterpolator.MinScale)));
    }

    /// <summary>The angle between two rotations, in radians.</summary>
    private static float Angle(Quaternion a, Quaternion b)
    {
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b)));
        return 2 * MathF.Acos(MathF.Min(dot, 1));
    }

    private static float MaxAbs(Vector3 v) => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)));
}
