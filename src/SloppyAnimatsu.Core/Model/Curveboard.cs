using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>Tangent types and their names.</summary>
public static class TangentTypes
{
    public const uint Spline = 0, Linear = 1, Auto = 2, Clamped = 3, IK = 4, Step = 5, Custom = 0xF;

    /// <summary>The types a button can set, in toolbar order.</summary>
    public static readonly uint[] Settable = [Step, Linear, Spline, Auto, Clamped, IK];

    public static string Name(uint type) => type switch
    {
        Spline => "Spline", Linear => "Linear", Auto => "Auto", Clamped => "Clamped",
        IK => "IK", Step => "Step", Custom => "Custom", _ => "",
    };

    /// <summary>The tooltip form "in | out Unified/Broken".</summary>
    public static string Describe(TangentMode mode) => $"{Name(mode.In)} | {Name(mode.Out)} {(mode.Unified ? "Unified" : "Broken")}";

    /// <summary>
    /// The types that may be set on <paramref name="target"/>, one bit per type: 0x2F (0, 1, 2, 3
    /// and 5), plus type 4 on IK and reverse-foot controllers.
    /// </summary>
    public static uint AllowedMask(Object3D? target) => target is ControllerIK ? 0x3Fu : 0x2Fu;

    public static bool IsAllowed(Object3D? target, uint type) => type < 32 && (AllowedMask(target) & (1u << (int)type)) != 0;
}

public enum TangentSide { In, Out }

/// <summary>
/// The Curveboard selection of one key of one track:
/// the channels whose in handle, key value and out handle are selected. <see cref="Key"/> is a
/// stack key index; the end key stands for key 0.
/// </summary>
public readonly record struct CurveKeySelection(AnimTrack Track, int Key, AnimChannels In, AnimChannels Value, AnimChannels Out)
{
    public AnimChannels Any => In | Value | Out;
}

/// <summary>
/// Channel values of keys and curves as the Curveboard shows them
/// translation and scale as stored, rotation as ZYX Euler angles in
/// degrees, and the IK pole and reverse-foot angles as stored.
/// </summary>
public static class Curves
{
    public const int ChannelCount = 13;

    private const float Degrees = 57.29578f;

    public static readonly string[] ChannelNames =
        ["Translate X", "Translate Y", "Translate Z", "Rotate X", "Rotate Y", "Rotate Z",
         "Scale X", "Scale Y", "Scale Z", "Pole", "Roll", "Twist", "Bank"];

    public static bool IsRotation(int channel) => channel is >= 3 and <= 5;
    public static bool IsScalar(int channel) => channel >= 9;

    public static AnimChannels Bit(int channel) => (AnimChannels)(1u << channel);

    /// <summary>The channels in <paramref name="mask"/>, lowest first.</summary>
    public static IEnumerable<int> Channels(AnimChannels mask) =>
        Enumerable.Range(0, ChannelCount).Where(c => (mask & Bit(c)) != 0);

    /// <summary>ZYX Euler angles in degrees (X, Y, Z), with the rotation Rz·Ry·Rx.</summary>
    public static Vector3 ToEulerDegrees(Quaternion q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float cz = 1 - 2 * (y * y + z * z), sz = 2 * (w * z + x * y);
        float sinB = 2 * (w * y - z * x);
        float cosB = MathF.Sqrt(sz * sz + cz * cz);
        float b = MathF.Atan2(sinB, cosB);
        float a, c;
        if (MathF.Abs(cosB) < 1e-5f)
        {
            c = 0;
            a = MathF.Sign(sinB) * MathF.Atan2(2 * (x * y - w * z), 1 - 2 * (x * x + z * z));
        }
        else
        {
            a = MathF.Atan2(2 * (z * y + w * x), 1 - 2 * (x * x + y * y));
            c = MathF.Atan2(sz, cz);
        }
        return new Vector3(a, b, c) * Degrees;
    }

    /// <summary>The inverse of <see cref="ToEulerDegrees"/>.</summary>
    public static Quaternion FromEulerDegrees(Vector3 e)
    {
        var h = e / Degrees * 0.5f;
        float ca = MathF.Cos(h.X), sa = MathF.Sin(h.X), cb = MathF.Cos(h.Y), sb = MathF.Sin(h.Y), cc = MathF.Cos(h.Z), sc = MathF.Sin(h.Z);
        return new Quaternion(
            sa * cb * cc - ca * sb * sc,
            ca * sb * cc + sa * cb * sc,
            ca * cb * sc - sa * sb * cc,
            ca * cb * cc + sa * sb * sc);
    }

    /// <summary>Channel <paramref name="channel"/> of a transform (rotation in Euler degrees).</summary>
    public static float Value(Transform t, int channel) => channel switch
    {
        < 3 => t.Translation[channel],
        < 6 => ToEulerDegrees(t.Rotation)[channel - 3],
        < 9 => t.Scale[channel - 6],
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    /// <summary>The value of key <paramref name="key"/> on <paramref name="channel"/>, or null if it has none.</summary>
    public static float? KeyValue(AnimKey? key, int channel) =>
        IsScalar(channel) ? Scalar(key, channel) : key is TransformKey t ? Value(t.Value, channel) : null;

    /// <summary>An IK pole or reverse-foot angle of a key or tangent key, or null if the key has none.</summary>
    public static float? Scalar(AnimKey? key, int channel) => (channel, key) switch
    {
        (9, IKKey k) => k.PoleAngle,
        (10, ReverseFootKey k) => k.RollAngle,
        (11, ReverseFootKey k) => k.TwistAngle,
        (12, ReverseFootKey k) => k.BankAngle,
        _ => null,
    };

    internal static void SetScalar(AnimKey key, int channel, float value)
    {
        switch (channel, key)
        {
            case (9, IKKey k): k.PoleAngle = value; break;
            case (10, ReverseFootKey k): k.RollAngle = value; break;
            case (11, ReverseFootKey k): k.TwistAngle = value; break;
            case (12, ReverseFootKey k): k.BankAngle = value; break;
        }
    }

    /// <summary>
    /// The value of <paramref name="track"/> on <paramref name="channel"/> at each integer frame from
    /// <paramref name="first"/> to <paramref name="last"/>, as the track alone evaluates (no layer
    /// mixing). Rotation curves are unwrapped from frame to frame (a SloppyAnimatsu choice).
    /// </summary>
    public static float[] Sample(PoseEvaluator evaluator, AnimStack stack, AnimTrack track, int channel, int first, int last)
    {
        int count = Math.Max(0, last - first + 1);
        var values = new float[count];
        if (IsScalar(channel))
        {
            for (int i = 0; i < count; i++) values[i] = ScalarAt(stack, track, channel, first + i);
            return values;
        }
        for (int i = 0; i < count; i++) values[i] = Value(evaluator.LayerValue(stack, track, first + i, out _), channel);
        if (IsRotation(channel))
            for (int i = 1; i < count; i++) values[i] = Unwrap(values[i], values[i - 1]);
        return values;
    }

    /// <summary><paramref name="angle"/> moved by whole turns to within 180° of <paramref name="near"/>.</summary>
    public static float Unwrap(float angle, float near) => near + WrapDegrees(angle - near);

    private static float WrapDegrees(float d) => d - 360 * MathF.Round(d / 360);

    /// <summary>A scalar channel at stack time <paramref name="time"/>: Hermite between the track's keys, held at a step key.</summary>
    public static float ScalarAt(AnimStack stack, AnimTrack track, int channel, float time)
    {
        var stackKeys = stack.Keys;
        var keys = track.Keys;
        var idx = Enumerable.Range(0, Math.Min(stackKeys.Count, keys.Count)).Where(i => Scalar(keys[i], channel) != null).ToArray();
        if (idx.Length == 0) return 0;
        time = PoseEvaluator.LocalTime(stackKeys, stack.Flags, time);
        float Value(int j) => Scalar(keys[idx[j == idx.Length - 1 && idx.Length > 1 ? 0 : j]], channel)!.Value;
        int next = 0;
        while (next < idx.Length && stackKeys[idx[next]].Time <= time) next++;
        if (next == 0) return Value(0);
        if (next == idx.Length) return Value(idx.Length - 1);
        int prev = next - 1;
        var modes = track.TangentModes;
        if (idx[prev] < modes.Count && channel < modes[idx[prev]].Length && modes[idx[prev]][channel].Out == TangentTypes.Step) return Value(prev);
        float t0 = stackKeys[idx[prev]].Time, t1 = stackKeys[idx[next]].Time;
        float u = t1 > t0 ? (time - t0) / (t1 - t0) : 1;
        float m0 = Scalar(track.TangentsOut.ElementAtOrDefault(idx[prev]), channel) ?? 0;
        float m1 = Scalar(track.TangentsIn.ElementAtOrDefault(idx[next]), channel) ?? 0;
        float u2 = u * u, u3 = u2 * u;
        return (2 * u3 - 3 * u2 + 1) * Value(prev) + (u3 - 2 * u2 + u) * m0 + (-2 * u3 + 3 * u2) * Value(next) + (u3 - u2) * m1;
    }

    /// <summary>
    /// The length in frames of the segment a key's <paramref name="side"/> belongs to
    /// the in side uses the previous segment
    /// (key 0 wraps to the last), the out side the next. 0 when the track has fewer than two keys.
    /// </summary>
    public static float SegmentLength(AnimStack stack, AnimTrack track, int key, TangentSide side)
    {
        var present = PresentKeys(stack, track);
        int n = present.Length;
        if (n < 2) return 0;
        int k = Array.IndexOf(present, key == stack.Keys.Count - 1 ? present[0] : key);
        if (k < 0) return 0;
        if (k == n - 1) k = 0;
        int segment = side == TangentSide.Out ? k : k == 0 ? n - 2 : k - 1;
        return stack.Keys[present[segment + 1]].Time - stack.Keys[present[segment]].Time;
    }

    /// <summary>The stack keys at which <paramref name="track"/> has a key.</summary>
    public static int[] PresentKeys(AnimStack stack, AnimTrack track)
    {
        var keys = track.Keys;
        return Enumerable.Range(0, Math.Min(stack.Keys.Count, keys.Count)).Where(i => keys[i] != null).ToArray();
    }

    /// <summary>
    /// The slope of a key's handle on <paramref name="channel"/>, in channel units per frame. For
    /// rotation this is the Euler slope along the stored log tangent (a SloppyAnimatsu choice,
    ///).
    /// </summary>
    public static float TangentSlope(AnimStack stack, AnimTrack track, int key, int channel, TangentSide side)
    {
        float length = SegmentLength(stack, track, key, side);
        if (length <= 0) return 0;
        var tangent = (side == TangentSide.In ? track.TangentsIn : track.TangentsOut).ElementAtOrDefault(key);
        if (IsScalar(channel)) return (Scalar(tangent, channel) ?? 0) / length;
        if (tangent is not TransformKey t) return 0;
        var stored = t.Value;
        if (!IsRotation(channel)) return Value(stored, channel) / length;
        if (track.Keys.ElementAtOrDefault(key) is not TransformKey k) return 0;
        var slopes = EulerJacobian(k.Value.Rotation) * Log(stored.Rotation) / length;
        return slopes[channel - 3];
    }

    internal static Vector3 Log(Quaternion tangent) => new(tangent.X, tangent.Y, tangent.Z);

    /// <summary>
    /// d(Euler degrees)/dv of q·exp(v) at v = 0, by central differences. Each column is the
    /// derivative along one axis of v.
    /// </summary>
    internal static Matrix3 EulerJacobian(Quaternion q)
    {
        const float h = 1e-3f;
        var e0 = ToEulerDegrees(q);
        Vector3 Column(Vector3 axis)
        {
            var plus = ToEulerDegrees(q * HermiteKeyInterpolator.Exp(axis * h));
            var minus = ToEulerDegrees(q * HermiteKeyInterpolator.Exp(-axis * h));
            Vector3 d = default;
            for (int i = 0; i < 3; i++) d[i] = (Unwrap(plus[i], e0[i]) - Unwrap(minus[i], e0[i])) / (2 * h);
            return d;
        }
        return new Matrix3(Column(Vector3.UnitX), Column(Vector3.UnitY), Column(Vector3.UnitZ));
    }
}

/// <summary>A 3×3 matrix stored by columns.</summary>
internal readonly record struct Matrix3(Vector3 C0, Vector3 C1, Vector3 C2)
{
    public static Vector3 operator *(Matrix3 m, Vector3 v) => m.C0 * v.X + m.C1 * v.Y + m.C2 * v.Z;

    public float Determinant => Vector3.Dot(C0, Vector3.Cross(C1, C2));

    /// <summary>The solution x of m·x = <paramref name="b"/>, or null when m is (nearly) singular.</summary>
    public Vector3? Solve(Vector3 b)
    {
        float det = Determinant;
        if (MathF.Abs(det) < 1e-6f) return null;
        // Cramer's rule.
        return new Vector3(
            Vector3.Dot(b, Vector3.Cross(C1, C2)),
            Vector3.Dot(C0, Vector3.Cross(b, C2)),
            Vector3.Dot(C0, Vector3.Cross(C1, b))) / det;
    }
}

/// <summary>
/// The Curveboard's edits: moving and scaling key values, setting
/// handle slopes, changing tangent types and the unified flag. Tangents are recomputed after each.
/// </summary>
public sealed partial class KeyEditor
{
    /// <summary>
    /// Adds <paramref name="delta"/> to the selected key values. Rotation
    /// channels move in Euler degrees. Returns the number of keys changed.
    /// </summary>
    public int MoveKeyValues(IEnumerable<CurveKeySelection> selection, float delta) =>
        EditKeyValues(selection, _ => delta == 0 ? null : v => v + delta);

    /// <summary>Scales the selected key values about <paramref name="pivot"/>.</summary>
    public int ScaleKeyValues(IEnumerable<CurveKeySelection> selection, float factor, float pivot) =>
        EditKeyValues(selection, _ => factor == 1 ? null : v => (v - pivot) * factor + pivot);

    /// <summary>
    /// Writes the selected key values from their current values: <paramref name="change"/> gives,
    /// per channel, the new value from the old (null leaves the channel).
    /// </summary>
    public int EditKeyValues(IEnumerable<CurveKeySelection> selection, Func<int, Func<float, float>?> change)
    {
        var touched = new HashSet<AnimTrack>(ReferenceEqualityComparer.Instance);
        int count = 0;
        foreach (var item in Merge(selection))
        {
            var mask = item.Value & item.Track.Channels;
            if (mask == AnimChannels.None || item.Track.Stack is not { } stack) continue;
            int key = LoopToFirst(stack, item.Key);
            if (item.Track.Keys.ElementAtOrDefault(key) is not { } animKey) continue;
            bool changed = false;
            foreach (int c in Curves.Channels(mask).Where(Curves.IsScalar))
                if (Curves.Scalar(animKey, c) is { } v && change(c) is { } f)
                {
                    Curves.SetScalar(animKey, c, f(v));
                    changed = true;
                }
            if (animKey is TransformKey t && (mask & ~(AnimChannels)0x1E00) != 0)
            {
                var value = t.Value;
                var translation = value.Translation;
                var scale = value.Scale;
                var euler = Curves.ToEulerDegrees(value.Rotation);
                bool rotated = false;
                foreach (int c in Curves.Channels(mask).Where(c => c < 9))
                {
                    if (change(c) is not { } f) continue;
                    changed = true;
                    if (c < 3) translation[c] = f(translation[c]);
                    else if (c < 6) { euler[c - 3] = f(euler[c - 3]); rotated = true; }
                    else scale[c - 6] = f(scale[c - 6]);
                }
                var rotation = value.Rotation;
                if (rotated)
                {
                    rotation = Curves.FromEulerDegrees(euler);
                    if (Quaternion.Dot(rotation, value.Rotation) < 0) rotation = -rotation;
                }
                t.Value = new Transform(translation, rotation, scale);
            }
            if (!changed) continue;
            MirrorLoopKey(stack, item.Track, key);
            touched.Add(item.Track);
            count++;
        }
        Finish(touched);
        return count;
    }

    /// <summary>
    /// Sets one handle's slope (channel units per frame) on <paramref name="channel"/> and makes
    /// that side Custom. A unified key gets the same slope on its other side. Rotation keeps the
    /// other two Euler slopes. Returns false
    /// when nothing could be set (no segment, or a rotation at gimbal lock).
    /// </summary>
    public bool SetTangentSlope(AnimTrack track, int key, int channel, TangentSide side, float slope)
    {
        if (track.Stack is not { } stack || (track.Channels & Curves.Bit(channel)) == 0) return false;
        key = LoopToFirst(stack, key);
        var modes = track.TangentModes;
        bool unified = key < modes.Count && channel < modes[key].Length && modes[key][channel].Unified;
        bool set = SetSlope(stack, track, key, channel, side, slope);
        if (set && unified) SetSlope(stack, track, key, channel, side == TangentSide.In ? TangentSide.Out : TangentSide.In, slope);
        if (set) Finish([track]);
        return set;
    }

    /// <summary>
    /// The handle drag: turns each selected handle by
    /// <paramref name="angle"/> radians in the curve view where a slope is value / (frames·0.001),
    /// and makes it Custom. Selecting either handle of a unified key turns both.
    /// </summary>
    public int RotateTangents(IEnumerable<CurveKeySelection> selection, float angle)
    {
        var touched = new HashSet<AnimTrack>(ReferenceEqualityComparer.Instance);
        int count = 0;
        foreach (var item in Merge(selection))
        {
            if (item.Track.Stack is not { } stack) continue;
            int key = LoopToFirst(stack, item.Key);
            var modes = item.Track.TangentModes;
            foreach (int c in Curves.Channels((item.In | item.Out) & item.Track.Channels))
            {
                bool unified = key < modes.Count && c < modes[key].Length && modes[key][c].Unified;
                foreach (var side in new[] { TangentSide.In, TangentSide.Out })
                {
                    var mask = side == TangentSide.In ? item.In : item.Out;
                    if ((mask & Curves.Bit(c)) == 0 && !unified) continue;
                    float length = Curves.SegmentLength(stack, item.Track, key, side);
                    if (length <= 0) continue;
                    float slope = Curves.TangentSlope(stack, item.Track, key, c, side) / 0.001f;
                    float turned = Math.Clamp(MathF.Atan(slope) + angle, -1.5707864f, 1.5707864f);
                    if (SetSlope(stack, item.Track, key, c, side, MathF.Tan(turned) * 0.001f)) count++;
                }
            }
            touched.Add(item.Track);
        }
        Finish(touched);
        return count;
    }

    /// <summary>
    /// Sets the selected keys' tangent type (ChangeTangentType) on the displayed channels the
    /// track animates. Tracks whose target does not allow the type are skipped. A key whose value
    /// is selected changes both sides; otherwise only the selected handles' sides change. Rotation
    /// channels change together. Returns the number of channel modes changed.
    /// </summary>
    public int ChangeTangentType(IEnumerable<CurveKeySelection> selection, uint type, AnimChannels displayed)
    {
        var touched = new HashSet<AnimTrack>(ReferenceEqualityComparer.Instance);
        int count = 0;
        foreach (var item in Merge(selection))
        {
            if (!TangentTypes.IsAllowed(item.Track.Target, type) || item.Track.Stack is not { } stack) continue;
            int key = LoopToFirst(stack, item.Key);
            var both = WithRotation(item.Value & displayed & item.Track.Channels);
            var inOnly = WithRotation(item.In & displayed & item.Track.Channels) & ~both;
            var outOnly = WithRotation(item.Out & displayed & item.Track.Channels) & ~both;
            count += ChangeModes(stack, item.Track, key, both | inOnly | outOnly, m =>
            {
                var bit = m.Channel;
                var mode = m.Mode;
                if ((both & bit) != 0 || (inOnly & bit) != 0) mode = mode.WithIn(type);
                if ((both & bit) != 0 || (outOnly & bit) != 0) mode = mode.WithOut(type);
                return mode;
            });
            touched.Add(item.Track);
        }
        Finish(touched);
        return count;
    }

    /// <summary>Sets or clears Unified on the displayed channels of the selected keys and handles.</summary>
    public int SetTangentsUnified(IEnumerable<CurveKeySelection> selection, bool unified, AnimChannels displayed)
    {
        var touched = new HashSet<AnimTrack>(ReferenceEqualityComparer.Instance);
        int count = 0;
        foreach (var item in Merge(selection))
        {
            if (item.Track.Stack is not { } stack) continue;
            count += ChangeModes(stack, item.Track, LoopToFirst(stack, item.Key), WithRotation(item.Any & displayed & item.Track.Channels),
                m => m.Mode.WithUnified(unified));
            touched.Add(item.Track);
        }
        Finish(touched);
        return count;
    }

    private static AnimChannels WithRotation(AnimChannels mask) => (mask & AnimChannels.Rotate) != 0 ? mask | AnimChannels.Rotate : mask;

    /// <summary>Rewrites the modes of <paramref name="channels"/> at a key (and the end key for key 0). Returns how many changed.</summary>
    private static int ChangeModes(AnimStack stack, AnimTrack track, int key, AnimChannels channels,
        Func<(AnimChannels Channel, TangentMode Mode), TangentMode> change)
    {
        var modes = track.TangentModes;
        int count = 0;
        foreach (int k in KeyAndLoop(stack, key))
        {
            if (k >= modes.Count) continue;
            foreach (int c in Curves.Channels(channels))
            {
                if (c >= modes[k].Length) continue;
                var mode = change((Curves.Bit(c), modes[k][c]));
                if (mode == modes[k][c]) continue;
                track.SetTangentMode(k, c, mode);
                if (k == key) count++;
            }
        }
        return count;
    }

    /// <summary>Writes one side's tangent for a slope in channel units per frame and makes the side Custom.</summary>
    private static bool SetSlope(AnimStack stack, AnimTrack track, int key, int channel, TangentSide side, float slope)
    {
        float length = Curves.SegmentLength(stack, track, key, side);
        if (length <= 0) return false;
        var tangent = (side == TangentSide.In ? track.TangentsIn : track.TangentsOut).ElementAtOrDefault(key);
        if (tangent == null) return false;
        if (Curves.IsScalar(channel))
        {
            if (Curves.Scalar(tangent, channel) == null) return false;
            Curves.SetScalar(tangent, channel, slope * length);
        }
        else
        {
            if (tangent is not TransformKey t) return false;
            var stored = t.Value;
            if (Curves.IsRotation(channel))
            {
                if (track.Keys.ElementAtOrDefault(key) is not TransformKey k) return false;
                var jacobian = Curves.EulerJacobian(k.Value.Rotation);
                var slopes = jacobian * Curves.Log(stored.Rotation) / length;
                slopes[channel - 3] = slope;
                if (jacobian.Solve(slopes * length) is not { } v) return false;
                t.Value = stored with { Rotation = v == Vector3.Zero ? Quaternion.Identity : new Quaternion(v, 0) };
            }
            else
            {
                var translation = stored.Translation;
                var scale = stored.Scale;
                if (channel < 3) translation[channel] = slope * length;
                else scale[channel - 6] = slope * length;
                t.Value = stored with { Translation = translation, Scale = scale };
            }
        }
        var bits = Curves.IsRotation(channel) ? AnimChannels.Rotate : Curves.Bit(channel);
        ChangeModes(stack, track, key, bits, m => side == TangentSide.In ? m.Mode.WithIn(TangentTypes.Custom) : m.Mode.WithOut(TangentTypes.Custom));
        return true;
    }

    /// <summary>Selections of the same track key combined into one.</summary>
    private static IEnumerable<CurveKeySelection> Merge(IEnumerable<CurveKeySelection> selection) =>
        selection.Where(s => s.Track.Stack != null)
            .GroupBy(s => (s.Track.Source, LoopToFirst(s.Track.Stack!, s.Key)))
            .Select(g => new CurveKeySelection(g.First().Track, g.Key.Item2,
                g.Aggregate(AnimChannels.None, (m, s) => m | s.In),
                g.Aggregate(AnimChannels.None, (m, s) => m | s.Value),
                g.Aggregate(AnimChannels.None, (m, s) => m | s.Out)));

    /// <summary>The end key stands for key 0.</summary>
    private static int LoopToFirst(AnimStack stack, int key) => stack.Keys.Count > 1 && key == stack.Keys.Count - 1 ? 0 : key;

    private static IEnumerable<int> KeyAndLoop(AnimStack stack, int key) =>
        key == 0 && stack.Keys.Count > 1 ? [0, stack.Keys.Count - 1] : [key];

    /// <summary>Key 0's value is mirrored into the end key.</summary>
    private static void MirrorLoopKey(AnimStack stack, AnimTrack track, int key)
    {
        int n = stack.Keys.Count;
        var keys = track.Keys;
        if (key != 0 || n < 2 || n - 1 >= keys.Count || keys[0] is not { } first || keys[n - 1] is not { } loop) return;
        if (first is TransformKey a && loop is TransformKey b) b.Value = a.Value;
        for (int c = 9; c < Curves.ChannelCount; c++)
            if (Curves.Scalar(first, c) is { } v) Curves.SetScalar(loop, c, v);
    }

    private void Finish(IEnumerable<AnimTrack> tracks)
    {
        Evaluator.Invalidate();
        foreach (var track in tracks) TangentRecompute.Recompute(track, Evaluator);
        Evaluator.Invalidate();
    }
}
