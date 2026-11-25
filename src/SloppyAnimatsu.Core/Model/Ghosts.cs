namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The ghost preferences: whether ghosts stay on while playing, how many on each side, whether
/// they stand on keys or on every frame, the two colours (RGB bytes), and whether the ghosts get
/// the wireframe drawn over them.
/// </summary>
public sealed record GhostSettings(bool OnPlaying, int MaxPrevious, int MaxNext, bool PerFrame,
    (byte R, byte G, byte B) PreviousColor, (byte R, byte G, byte B) NextColor, bool Wire = false)
{
    /// <summary>The shipped values, used when there are no preferences.</summary>
    public static GhostSettings Default { get; } = new(true, 10, 10, false, (0x68, 0x68, 0x68), (0xF2, 0x36, 0x29));
}

/// <summary>Which side of the playhead a ghost stands on.</summary>
public enum GhostSide
{
    /// <summary>Before the playhead (toward the start of the animation).</summary>
    Previous,

    /// <summary>After the playhead (toward the end of the animation).</summary>
    Next,
}

/// <summary>
/// One ghost: a see-through, recoloured copy of the character posed at <see cref="Time"/>.
/// <see cref="Distance"/> counts ghosts from the playhead on its side (1 = the nearest).
/// <see cref="SwappedColor"/> is set for the bands that take the other side's colour, and
/// <see cref="Opacity"/> fades with the distance.
/// </summary>
public readonly record struct Ghost(float Time, GhostSide Side, int Distance, bool SwappedColor, float Opacity)
{
    /// <summary>The colour this ghost is drawn with, out of the two ghost colours.</summary>
    public bool UsesNextColor => (Side == GhostSide.Next) != SwappedColor;
}

/// <summary>
/// Onion skinning for the Stacker: which poses around the playhead are drawn as ghosts, in which
/// colour and how see-through. The previous ghosts (before the playhead) take the previous colour
/// (grey by default) and the next ones the next colour (light red). Key ghosts swap the two colours
/// every <see cref="ColorBand"/> ghosts so the keys can be counted at a glance; frame ghosts keep
/// one colour per side. The nearest ghost is the most opaque and the farthest the most transparent,
/// never below <see cref="FarthestOpacity"/>.
/// <para>
/// In a cycle the ghosts wrap around the loop: past the last key the next ghosts continue from the
/// first, and before the first key the previous ones continue from the last. When the loop has
/// fewer ghost times than both sides ask for, the times are shared between the sides in proportion
/// to what each asked for. Outside a cycle each side stops at the end of the animation.
/// </para>
/// </summary>
public static class Ghosts
{
    /// <summary>Ghosts per colour band: after four key ghosts on a side its colour swaps with the other side's.</summary>
    public const int ColorBand = 4;

    /// <summary>Opacity of the ghost next to the playhead.</summary>
    public const float NearestOpacity = 0.5f;

    /// <summary>Opacity of the farthest ghost on a side: the floor every ghost stays above.</summary>
    public const float FarthestOpacity = 0.1f;

    /// <summary>How close two times have to be to count as the same frame.</summary>
    private const float SameTime = 1e-3f;

    /// <summary>
    /// The ghosts to draw for <paramref name="stack"/> around <paramref name="current"/>, with the
    /// sides that are on and the counts, mode and colours of <paramref name="settings"/>.
    /// </summary>
    public static IReadOnlyList<Ghost> Plan(AnimStack stack, float current, bool previous, bool next, GhostSettings settings)
    {
        var (times, period) = Candidates(stack, settings.PerFrame);
        return Plan(times, current, previous, next, settings.MaxPrevious, settings.MaxNext, period,
            swapBands: !settings.PerFrame);
    }

    /// <summary>
    /// The ghosts to draw around <paramref name="current"/>: up to <paramref name="maxPrevious"/> of the
    /// <paramref name="times"/> before it (when <paramref name="previous"/> is on) and up to
    /// <paramref name="maxNext"/> after it (when <paramref name="next"/> is on), nearest first per side.
    /// A time on the playhead itself gets no ghost - the character already stands there. With a
    /// <paramref name="period"/> the times are one loop starting at the first of them, and the
    /// playhead and the ghosts wrap around it. <paramref name="swapBands"/> turns the colour bands on.
    /// </summary>
    public static IReadOnlyList<Ghost> Plan(IEnumerable<float> times, float current, bool previous, bool next,
        int maxPrevious, int maxNext, float? period = null, bool swapBands = true)
    {
        var sorted = times.Distinct().OrderBy(t => t).ToList();
        int wantPrevious = previous ? Math.Max(0, maxPrevious) : 0;
        int wantNext = next ? Math.Max(0, maxNext) : 0;
        var ghosts = new List<Ghost>();
        if (sorted.Count == 0 || wantPrevious + wantNext == 0) return ghosts;

        List<float> before, after;
        if (period is > 0f && sorted[^1] - sorted[0] <= period.Value + SameTime)
        {
            float first = sorted[0], length = period.Value;
            float wrapped = first + PositiveModulo(current - first, length);
            if (wrapped > first + length - SameTime) wrapped = first;
            // The ring of ghost times: everything but the pose under the playhead (on the first
            // key that is also the loop key, which repeats it).
            var ring = sorted.Where(t => !SamePose(t, wrapped, first, length)).ToList();
            int available = ring.Count;
            if (available == 0) return ghosts;
            if (wantPrevious + wantNext > available)
            {
                int share = wantPrevious * available / (wantPrevious + wantNext);
                (wantPrevious, wantNext) = (share, available - share);
            }
            int below = ring.Count(t => t < wrapped);
            before = Enumerable.Range(0, wantPrevious).Select(k => ring[PositiveModulo(below - 1 - k, available)]).ToList();
            after = Enumerable.Range(0, wantNext).Select(k => ring[(below + k) % available]).ToList();
        }
        else
        {
            before = sorted.Where(t => t < current - SameTime).Reverse().Take(wantPrevious).ToList();
            after = sorted.Where(t => t > current + SameTime).Take(wantNext).ToList();
        }
        AddSide(ghosts, before, GhostSide.Previous, swapBands);
        AddSide(ghosts, after, GhostSide.Next, swapBands);
        return ghosts;
    }

    /// <summary>
    /// The times ghosts can stand on: the stack's keys (<paramref name="perFrame"/> false), else every
    /// whole frame between its first and last key. For a cycle the loop's length is returned as the
    /// period (otherwise null). Its loop key stays a key ghost - it is where the next ghosts show the
    /// first pose again past the last key - but a frame ghost on it would repeat the first frame.
    /// </summary>
    public static (IReadOnlyList<float> Times, float? Period) Candidates(AnimStack stack, bool perFrame)
    {
        var keys = stack.Keys;
        int count = stack.ConsideredKeyCount;
        if (count <= 0) return ([], null);
        int first = keys[0].Time, last = keys[count - 1].Time;
        bool cycle = stack.IsCycle && count > 1 && last > first;
        float? period = cycle ? last - first : null;
        if (!perFrame)
        {
            return (keys.Take(count).Select(k => (float)k.Time).ToList(), period);
        }
        var frames = new List<float>();
        for (int t = first; t <= (cycle ? last - 1 : last); t++) frames.Add(t);
        return (frames, period);
    }

    /// <summary>The opacity of the ghost <paramref name="distance"/> steps from the playhead, out of <paramref name="count"/> on its side.</summary>
    public static float OpacityAt(int distance, int count) =>
        count <= 1 ? NearestOpacity
            : NearestOpacity + (FarthestOpacity - NearestOpacity) * (distance - 1) / (count - 1);

    /// <summary>Whether ghost number <paramref name="distance"/> (1-based) on a side is in a swapped colour band.</summary>
    public static bool SwappedAt(int distance) => (distance - 1) / ColorBand % 2 == 1;

    private static void AddSide(List<Ghost> into, IReadOnlyList<float> nearestFirst, GhostSide side, bool swapBands)
    {
        for (int i = 0; i < nearestFirst.Count; i++)
        {
            int distance = i + 1;
            into.Add(new Ghost(nearestFirst[i], side, distance, swapBands && SwappedAt(distance),
                OpacityAt(distance, nearestFirst.Count)));
        }
    }

    private static bool SamePose(float time, float wrapped, float first, float length) =>
        Math.Abs(time - wrapped) <= SameTime || (wrapped <= first + SameTime && Math.Abs(time - (first + length)) <= SameTime);

    private static float PositiveModulo(float value, float length) => ((value % length) + length) % length;

    private static int PositiveModulo(int value, int length) => ((value % length) + length) % length;
}
