using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>What the Curveboard is doing between a press and its release.</summary>
public enum CurveboardGesture
{
    None,
    /// <summary>A key was pressed; it becomes <see cref="KeyDrag"/> once the pointer leaves the click.</summary>
    Press,
    /// <summary>Selected key values are being dragged.</summary>
    KeyDrag,
    /// <summary>A tangent handle's end is being dragged.</summary>
    HandleDrag,
    /// <summary>Empty space is pressed: a selection box, or a click that sets the time.</summary>
    Box,
}

/// <summary>
/// The Curveboard's geometry, sampling, selection and pointer rules without a window
/// the frames lined up with the timeline,
/// the sampled curve of every shown channel, the key squares and tangent-handle ends drawn for
/// them, what a press selects, how far a drag moves a value and what slope a dragged handle gets.
/// The viewer draws from these numbers and presses through this class, so the pointer rules can be
/// tested and printed from the command line; the layout itself is a viewer design, not the.
/// </summary>
public sealed class CurveboardLayout
{
    /// <summary>The width (DIPs) left of the curves for the value labels; the timeline's gutter.</summary>
    public const double Gutter = TimelineLayout.Gutter;

    /// <summary>The padding after the last frame, as on the timeline.</summary>
    public const double RightPad = TimelineLayout.RightPad;

    /// <summary>The half-size (DIPs) of a key's square.</summary>
    public const double KeyRadius = 4;

    /// <summary>How far (DIPs) a press may be from a key or a handle end and still hit it.</summary>
    public const double HitRadius = 7;

    /// <summary>The length (DIPs) of a tangent handle's line.</summary>
    public const double HandleLength = 40;

    /// <summary>How far (DIPs) a press must move before it becomes a drag or a selection box.</summary>
    public const double DragSlop = 3;

    /// <summary>The default shown channels: every channel the board knows.</summary>
    public static readonly AnimChannels EveryChannel = (AnimChannels)((1u << Curves.ChannelCount) - 1);

    /// <summary>A sampled curve: a track's channel, from <see cref="First"/> on.</summary>
    public readonly record struct CurveboardCurve(AnimTrack Track, int Channel, float[] Values);

    /// <summary>A key drawn on screen: its track, key, channel and position.</summary>
    public readonly record struct CurveboardPoint(AnimTrack Track, int Key, int Channel, Vector2 Point);

    /// <summary>One side of a selected key's tangent handle: the key's position and the handle's end.</summary>
    public readonly record struct CurveboardHandle(AnimTrack Track, int Key, int Channel, TangentSide Side, Vector2 KeyPoint, Vector2 End);

    /// <summary>What a press did: the gesture to run, and whether the selection it changed should be announced.</summary>
    public readonly record struct PressResult(CurveboardGesture Gesture, bool SelectionTouched);

    private readonly List<CurveboardCurve> _curves = [];
    private readonly Dictionary<(AnimTrack Track, int Key), (AnimChannels In, AnimChannels Value, AnimChannels Out)> _selection = [];

    /// <summary>The control's width in DIPs.</summary>
    public double Width { get; set; } = 800;

    /// <summary>The control's height in DIPs.</summary>
    public double Height { get; set; } = 200;

    private Animation? _animation;

    /// <summary>The animation whose frame range is shown.</summary>
    public Animation? Animation
    {
        get => _animation;
        set
        {
            if (ReferenceEquals(_animation, value)) return;
            _animation = value;
            FitPending = true;
        }
    }

    /// <summary>The stack whose tracks are shown, or null for none.</summary>
    public AnimStack? Stack { get; private set; }

    /// <summary>The tracks of <see cref="Stack"/> to show.</summary>
    public IReadOnlyList<AnimTrack> Tracks { get; private set; } = [];

    /// <summary>The evaluator the curves are sampled with.</summary>
    public PoseEvaluator? Evaluator { get; set; }

    /// <summary>The channels shown and selectable.</summary>
    public AnimChannels Displayed { get; set; } = EveryChannel;

    /// <summary>The playhead's frame, drawn as the cursor.</summary>
    public float Time { get; set; }

    /// <summary>The value at the bottom of the board.</summary>
    public double YMin { get; set; } = -1;

    /// <summary>The value at the top of the board.</summary>
    public double YMax { get; set; } = 1;

    /// <summary>The frame of each sample array's index 0.</summary>
    public int First { get; private set; }

    /// <summary>Set when the shown curves should be fitted into the view on the next refresh.</summary>
    public bool FitPending { get; set; } = true;

    /// <summary>Whether a refresh may fit the view; a drag holds it still.</summary>
    public bool AllowFit { get; set; } = true;

    /// <summary>The sampled curves on screen.</summary>
    public IReadOnlyList<CurveboardCurve> CurvesOnScreen => _curves;

    /// <summary>The handle the last press picked, or null when it picked a key or empty space.</summary>
    public CurveboardHandle? PressedHandle { get; private set; }

    /// <summary>The key the last press picked, or null when it picked a handle or empty space.</summary>
    public CurveboardPoint? PressedPoint { get; private set; }

    /// <summary>The selection as the Core edits take it.</summary>
    public IReadOnlyCollection<CurveKeySelection> Selection =>
        _selection.Select(p => new CurveKeySelection(p.Key.Track, p.Key.Key, p.Value.In, p.Value.Value, p.Value.Out)).ToArray();

    /// <summary>
    /// The frames the range bar shows, or null to span the animation's
    /// <see cref="Animation.KeyRange"/> - the same window as <see cref="TimelineLayout.ViewRange"/>.
    /// </summary>
    public (int Start, int End)? ViewRange { get; set; }

    /// <summary>The frames shown, at least two so that a frame has a width (the timeline's range).</summary>
    public (int Start, int End) Range
    {
        get
        {
            var (start, end) = ViewRange ?? Animation?.KeyRange ?? (0, 0);
            return (start, Math.Max(end, start + 1));
        }
    }

    /// <summary>The width of one frame in DIPs.</summary>
    public double FrameWidth
    {
        get
        {
            double width = Width - Gutter - RightPad;
            int frames = Range.End - Range.Start + 1;
            return width > 0 && frames > 0 ? width / frames : 1;
        }
    }

    /// <summary>The centre of a frame's cell, where its keys are drawn.</summary>
    public double X(float frame) => Gutter + (frame - Range.Start + 0.5) * FrameWidth;

    /// <summary>The (fractional) frame under an x.</summary>
    public double FrameAt(double x) => Range.Start - 0.5 + (x - Gutter) / FrameWidth;

    /// <summary>The whole frame under an x, clamped to the range: where a click on empty space goes.</summary>
    public int FrameUnder(double x) => (int)MathF.Round(Math.Clamp((float)FrameAt(x), Range.Start, Range.End));

    /// <summary>DIPs per value unit.</summary>
    public double UnitHeight
    {
        get
        {
            double span = YMax - YMin;
            return Height > 0 && span > 0 ? Height / span : 0;
        }
    }

    /// <summary>The y of a value (the top is the largest value).</summary>
    public double Y(double value) => (YMax - value) * UnitHeight;

    /// <summary>The value at a y.</summary>
    public double ValueAt(double y) => UnitHeight > 0 ? YMax - y / UnitHeight : YMax;

    /// <summary>The value change of a drag from <paramref name="fromY"/> to <paramref name="y"/>.</summary>
    public float MovedValue(double fromY, double y) => (float)((fromY - y) / UnitHeight);

    /// <summary>The tracks of <paramref name="stack"/> to show: the given ones, or every track with keys.</summary>
    public static IReadOnlyList<AnimTrack> TracksOf(AnimStack? stack, IEnumerable<AnimTrack>? wanted = null) =>
        stack == null ? [] : (wanted ?? stack.Tracks).Where(t => t.Keys.Count > 0).ToArray();

    /// <summary>Shows a stack's tracks, refitting the view when what is shown changes.</summary>
    public void Show(AnimStack? stack, IReadOnlyList<AnimTrack> tracks)
    {
        bool same = ReferenceEquals(stack, Stack) && tracks.Count == Tracks.Count &&
                    tracks.Zip(Tracks).All(p => ReferenceEquals(p.First, p.Second));
        Stack = stack;
        Tracks = stack == null ? [] : tracks;
        if (!same) FitPending = true;
        Refresh();
    }

    /// <summary>Resamples the curves, drops selections of keys that are gone, and fits when asked.</summary>
    /// <returns>Whether the selection changed.</returns>
    public bool Refresh()
    {
        _curves.Clear();
        int before = _selection.Count;
        foreach (var key in _selection.Keys.ToArray())
            if (!Tracks.Contains(key.Track) || key.Key >= key.Track.Keys.Count || key.Track.Keys[key.Key] == null)
                _selection.Remove(key);

        if (Stack is { } stack && Evaluator is { } evaluator && stack.Keys.Count > 0)
        {
            var (start, end) = Range;
            First = start;
            foreach (var track in Tracks)
                foreach (int c in Curves.Channels(track.Channels & Displayed))
                {
                    try
                    {
                        _curves.Add(new CurveboardCurve(track, c, Curves.Sample(evaluator, stack, track, c, start, end)));
                    }
                    catch (InvalidOperationException) { }
                }
        }
        if (FitPending && AllowFit) Fit();
        return _selection.Count != before;
    }

    /// <summary>Fits the shown curves' values into the view.</summary>
    public void Fit()
    {
        FitPending = false;
        var values = _curves.SelectMany(c => c.Values).Where(float.IsFinite).ToArray();
        double min = values.Length > 0 ? values.Min() : -1, max = values.Length > 0 ? values.Max() : 1;
        if (max - min < 1e-3) { min -= 1; max += 1; }
        double pad = (max - min) * 0.08;
        YMin = min - pad;
        YMax = max + pad;
    }

    /// <summary>The keys of each curve on screen: track, key, channel and position.</summary>
    public IReadOnlyList<CurveboardPoint> KeyPoints()
    {
        var points = new List<CurveboardPoint>();
        if (Stack is not { } stack) return points;
        var stackKeys = stack.Keys;
        foreach (var (track, c, values) in _curves)
            foreach (int k in Curves.PresentKeys(stack, track))
            {
                int i = stackKeys[k].Time - First;
                if (i < 0 || i >= values.Length) continue;
                points.Add(new CurveboardPoint(track, k, c, new Vector2((float)X(stackKeys[k].Time), (float)Y(values[i]))));
            }
        return points;
    }

    /// <summary>The handles of the selected keys on screen: the key's end point on each side.</summary>
    public IReadOnlyList<CurveboardHandle> Handles()
    {
        var handles = new List<CurveboardHandle>();
        if (Stack is not { } stack) return handles;
        foreach (var point in KeyPoints())
        {
            if (!_selection.TryGetValue((point.Track, point.Key), out var s) ||
                ((s.In | s.Value | s.Out) & Curves.Bit(point.Channel)) == 0) continue;
            foreach (var side in new[] { TangentSide.In, TangentSide.Out })
            {
                if (Curves.SegmentLength(stack, point.Track, point.Key, side) <= 0) continue;
                float slope = Curves.TangentSlope(stack, point.Track, point.Key, point.Channel, side);
                double dx = FrameWidth, dy = -slope * UnitHeight;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-9 || !double.IsFinite(length)) continue;
                double sign = side == TangentSide.In ? -1 : 1;
                handles.Add(new CurveboardHandle(point.Track, point.Key, point.Channel, side, point.Point,
                    new Vector2((float)(point.Point.X + sign * dx / length * HandleLength),
                                (float)(point.Point.Y + sign * dy / length * HandleLength))));
            }
        }
        return handles;
    }

    /// <summary>The key nearest to <paramref name="p"/> within <see cref="HitRadius"/>, or null.</summary>
    public CurveboardPoint? KeyAt(Vector2 p)
    {
        if (p.X < Gutter || Stack == null) return null;
        CurveboardPoint? best = null;
        float bestDistance = float.PositiveInfinity;
        // The first key at the nearest distance wins, as it is drawn under the later ones.
        foreach (var point in KeyPoints())
        {
            float d = Vector2.Distance(point.Point, p);
            if (d <= HitRadius && d < bestDistance) { bestDistance = d; best = point; }
        }
        return best;
    }

    /// <summary>The tangent-handle end nearest to <paramref name="p"/> within <see cref="HitRadius"/>, or null.</summary>
    public CurveboardHandle? HandleAt(Vector2 p)
    {
        if (p.X < Gutter || Stack == null) return null;
        CurveboardHandle? best = null;
        float bestDistance = float.PositiveInfinity;
        foreach (var handle in Handles())
        {
            float d = Vector2.Distance(handle.End, p);
            if (d <= HitRadius && d < bestDistance) { bestDistance = d; best = handle; }
        }
        return best;
    }

    /// <summary>
    /// A left press at <paramref name="p"/>: a handle end (select its side and drag it), a key (Shift
    /// adds, Ctrl toggles, a plain press on an unselected key clears the rest) or empty space (a
    /// selection box, or a click that sets the time).
    /// </summary>
    public PressResult Press(Vector2 p, bool add, bool control)
    {
        PressedHandle = null;
        PressedPoint = null;
        if (p.X < Gutter) return new PressResult(CurveboardGesture.None, false);

        if (HandleAt(p) is { } handle)
        {
            PressedHandle = handle;
            var s = _selection.GetValueOrDefault((handle.Track, handle.Key));
            var bit = Curves.Bit(handle.Channel);
            if (handle.Side == TangentSide.In) s.In |= bit; else s.Out |= bit;
            _selection[(handle.Track, handle.Key)] = s;
            return new PressResult(CurveboardGesture.HandleDrag, true);
        }

        if (KeyAt(p) is { } point)
        {
            PressedPoint = point;
            var bit = Curves.Bit(point.Channel);
            bool was = _selection.TryGetValue((point.Track, point.Key), out var s) && (s.Value & bit) != 0;
            if (control && was) Deselect(point.Track, point.Key, bit);
            else
            {
                if (!add && !control && !was) _selection.Clear();
                s = _selection.GetValueOrDefault((point.Track, point.Key));
                s.Value |= bit;
                _selection[(point.Track, point.Key)] = s;
            }
            // A Ctrl press on a selected key only removes it; there is nothing to drag.
            return new PressResult(control && was ? CurveboardGesture.None : CurveboardGesture.Press, true);
        }

        PressedPoint = null;
        return new PressResult(CurveboardGesture.Box, false);
    }

    /// <summary>Whether two points are the same press: no drag and no selection box.</summary>
    public static bool IsClick(Vector2 from, Vector2 to) => Vector2.Distance(from, to) <= DragSlop;

    /// <summary>
    /// The slope a dragged handle of <paramref name="handle"/> gives its tangent: the pointer's
    /// value change over its frame change from the key, with the handle kept on its own side.
    /// </summary>
    public float Slope(CurveboardHandle handle, Vector2 p)
    {
        var origin = KeyPoints().FirstOrDefault(k =>
            ReferenceEquals(k.Track, handle.Track) && k.Key == handle.Key && k.Channel == handle.Channel);
        if (origin.Track == null) return 0;
        double frames = (p.X - origin.Point.X) / FrameWidth;
        // A handle stays on its own side of the key.
        frames = handle.Side == TangentSide.Out ? Math.Max(frames, 1e-3) : Math.Min(frames, -1e-3);
        return (float)((origin.Point.Y - p.Y) / UnitHeight / frames);
    }

    /// <summary>
    /// Selects every key of the shown curves inside the box <paramref name="a"/>..<paramref name="b"/>
    /// (a drag from empty space), adding to the selection when <paramref name="add"/> is set.
    /// Returns how many key values are selected afterwards.
    /// </summary>
    public int BoxSelect(Vector2 a, Vector2 b, bool add)
    {
        if (!add) _selection.Clear();
        var min = Vector2.Min(a, b);
        var max = Vector2.Max(a, b);
        foreach (var point in KeyPoints())
        {
            if (point.Point.X < min.X || point.Point.X > max.X || point.Point.Y < min.Y || point.Point.Y > max.Y) continue;
            var s = _selection.GetValueOrDefault((point.Track, point.Key));
            s.Value |= Curves.Bit(point.Channel);
            _selection[(point.Track, point.Key)] = s;
        }
        return _selection.Values.Count(s => s.Value != AnimChannels.None);
    }

    /// <summary>Whether a key's value on a channel is selected.</summary>
    public bool IsSelected(CurveboardPoint point) =>
        _selection.TryGetValue((point.Track, point.Key), out var s) && (s.Value & Curves.Bit(point.Channel)) != 0;

    /// <summary>Whether a key's tangent handle on one side and channel is selected.</summary>
    public bool IsSelected(CurveboardHandle handle) =>
        _selection.TryGetValue((handle.Track, handle.Key), out var s) &&
        ((handle.Side == TangentSide.In ? s.In : s.Out) & Curves.Bit(handle.Channel)) != 0;

    /// <summary>Selects the values of every key of the shown curves.</summary>
    public void SelectAll()
    {
        foreach (var point in KeyPoints())
        {
            var s = _selection.GetValueOrDefault((point.Track, point.Key));
            s.Value |= Curves.Bit(point.Channel);
            _selection[(point.Track, point.Key)] = s;
        }
    }

    /// <summary>Drops the whole selection.</summary>
    public void ClearSelection() => _selection.Clear();

    /// <summary>Zooms the values around the pointer's y.</summary>
    public void ZoomAt(double y, double wheel)
    {
        double at = ValueAt(y);
        double factor = Math.Pow(0.85, wheel);
        YMin = at + (YMin - at) * factor;
        YMax = at + (YMax - at) * factor;
    }

    /// <summary>The value range a middle drag from <paramref name="fromY"/> to <paramref name="y"/> pans.</summary>
    public (double Min, double Max) Panned(double fromY, double y, double panMin, double panMax)
    {
        double shift = (y - fromY) / UnitHeight;
        return (panMin + shift, panMax + shift);
    }

    private void Deselect(AnimTrack track, int key, AnimChannels bit)
    {
        var s = _selection[(track, key)];
        s.Value &= ~bit;
        if ((s.In | s.Value | s.Out) == 0) _selection.Remove((track, key));
        else _selection[(track, key)] = s;
    }
}
