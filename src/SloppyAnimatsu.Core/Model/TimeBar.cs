namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The range bar under the timeline
/// the bar's extent <see cref="Min"/>..<see cref="Max"/> and the window it shows,
/// <see cref="ViewStart"/> for <see cref="ViewLength"/> frames. Runtime state only, one per
/// animation; nothing of it is saved in the scene.
/// </summary>
public sealed class TimeBar
{
    public int Min { get; private set; }
    public int Max { get; private set; } = 100;
    public int ViewStart { get; private set; }
    public int ViewLength { get; private set; } = 100;

    /// <summary>The last frame shown.</summary>
    public int ViewEnd => ViewStart + ViewLength;

    /// <summary>Sets the whole state (used when restoring a press).</summary>
    public void Set(int min, int max, int viewStart, int viewLength)
    {
        Min = min;
        Max = Math.Max(max, min + 1);
        ViewLength = Math.Max(1, viewLength);
        ViewStart = viewStart;
    }

    /// <summary>
    /// Fit all: the earliest first key and the latest last considered key over the stacks, and the
    /// current time, become both the bar and the window (100 frames when they span none).
    /// </summary>
    public void FitAll(Animation? animation, int time)
    {
        int start = time, end = time;
        if (animation != null)
        {
            var (s, e) = animation.KeyRange;
            if (animation.Stacks.Any(st => st.ConsideredKeyCount > 0)) { start = Math.Min(s, time); end = Math.Max(e, time); }
        }
        int length = end > start ? end - start : 100;
        Min = start;
        Max = start + length;
        ViewStart = start;
        ViewLength = length;
    }

    /// <summary>
    /// Fit layer: the window spans <paramref name="times"/> (a stack's keys); the bar's extent
    /// grows to hold it but is otherwise kept. Nothing happens when the times span no frames.
    /// Returns whether the window changed.
    /// </summary>
    public bool FitTimes(IEnumerable<int> times)
    {
        var list = times.ToList();
        if (list.Count == 0) return false;
        int start = list.Min(), end = list.Max();
        if (end <= start) return false;
        ViewStart = start;
        ViewLength = end - start;
        Min = Math.Min(Min, start);
        Max = Math.Max(Max, end);
        return true;
    }

    /// <summary>The frame at a fraction of the bar's width (0 = left edge, 1 = right edge).</summary>
    public double FrameAt(double fraction) => Min + (Max - Min) * fraction;

    /// <summary>The bar fraction of a frame.</summary>
    public double FractionOf(double frame) => Max > Min ? (frame - Min) / (Max - Min) : 0;

    /// <summary>A click on the bar's background pages the window one length toward <paramref name="frame"/>.</summary>
    public void Page(double frame)
    {
        int start = frame - ViewLength / 2.0 < ViewStart ? ViewStart - ViewLength : ViewStart + ViewLength;
        ViewStart = ClampStart(start, ViewLength);
    }

    /// <summary>The body drag: the window slides by <paramref name="delta"/> frames from where it was pressed.</summary>
    public void Slide(int pressStart, int delta) => ViewStart = ClampStart(pressStart + delta, ViewLength);

    /// <summary>
    /// The left handle drag. The length shrinks by <paramref name="delta"/> (at least 1); dragged
    /// <c>d</c> frames before the bar's start it grows faster, by <c>d * d / 2</c>, and the bar's
    /// start follows.
    /// </summary>
    public void DragLeft(int pressStart, int pressLength, int pressMin, int delta)
    {
        int start = pressStart + delta;
        int length = pressLength - delta;
        if (length < 1) { length = 1; start = pressStart + pressLength - 1; }
        if (start < pressMin)
        {
            int d = pressMin - start;
            int extra = d * d / 2;
            start -= extra;
            length += extra;
            Min = start;
        }
        else Min = pressMin;
        ViewStart = start;
        ViewLength = length;
    }

    /// <summary>
    /// The right handle drag. The length grows by <paramref name="delta"/> (at least 1); dragged
    /// <c>e</c> frames past the bar's end it grows by <c>e * e / 2</c> more and the bar's end follows.
    /// </summary>
    public void DragRight(int pressStart, int pressLength, int pressMax, int delta)
    {
        int length = Math.Max(1, pressLength + delta);
        int end = pressStart + length;
        if (end > pressMax)
        {
            int e = end - pressMax;
            length += e * e / 2;
            Max = pressStart + length;
        }
        else Max = pressMax;
        ViewStart = pressStart;
        ViewLength = length;
    }

    /// <summary>
    /// The wheel zoom about frame <paramref name="frame"/>: one notch is <paramref name="notches"/>
    /// = 1 (positive zooms in). A window under 5 frames changes by a frame per notch, a longer one
    /// by a factor 1.2 per notch; the length stays in [1, Max - Min].
    /// </summary>
    public void Zoom(double frame, double notches)
    {
        if (notches == 0) return;
        int oldLength = ViewLength, oldStart = ViewStart;
        double length = oldLength < 5
            ? oldLength - Math.Round(notches)
            : oldLength * Math.Pow(1.2, -notches);
        int newLength = (int)Math.Clamp(Math.Round(length), 1, Math.Max(1, Max - Min));
        if (newLength == oldLength && oldLength < Max - Min && notches < 0) newLength++;
        ViewLength = newLength;
        ViewStart = ClampStart((int)Math.Round(frame - newLength * (frame - oldStart) / oldLength), newLength);
    }

    private int ClampStart(int start, int length) => Math.Clamp(start, Min, Math.Max(Min, Max - length));

    /// <summary>
    /// The play loop's wrap: a time past <paramref name="end"/> comes back to
    /// <c>(t - end - 1) mod (end - start + 1) + start</c>.
    /// </summary>
    public static float Wrap(float time, int start, int end)
    {
        if (time <= end + 1 - 1e-6f && time >= start) return time;
        float period = end - start + 1;
        if (period <= 0) return start;
        float t = (time - start) % period;
        if (t < 0) t += period;
        return start + t;
    }
}
