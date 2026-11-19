using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// Keyed Compensate Scale: per
/// animation, stepped on/off keys of <see cref="Object3D.CompensatesParentScale"/> per object. Before
/// an object's first key, and in an animation without keys for it, the rigging value applies. Stored
/// as three parallel arrays on <c>CAnimation</c> (<see cref="MaterialSchema"/>), which the app skips.
/// </summary>
public static class CompensateScaleKeys
{
    /// <summary>One key: from <paramref name="Frame"/> on, the object compensates its parent's scale when <paramref name="On"/>.</summary>
    public readonly record struct Key(int Frame, bool On);

    /// <summary>Every keyed object's keys, sorted by frame; null when the animation has none.</summary>
    public static Dictionary<AktObject, Key[]>? Table(Animation animation)
    {
        if (animation.Source[MaterialSchema.CompensateTargetsMember] is not List<AktReference> targets || targets.Count == 0
            || animation.Source[MaterialSchema.CompensateFramesMember] is not List<object?> frames
            || animation.Source[MaterialSchema.CompensateValuesMember] is not List<object?> values)
            return null;
        int n = Math.Min(targets.Count, Math.Min(frames.Count, values.Count));
        var lists = new Dictionary<AktObject, List<Key>>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < n; i++)
        {
            if (targets[i].Target is not { } target) continue;
            if (!lists.TryGetValue(target, out var list)) lists[target] = list = new List<Key>();
            list.Add(new Key(Convert.ToInt32(frames[i]), Convert.ToInt32(values[i]) != 0));
        }
        if (lists.Count == 0) return null;
        var table = new Dictionary<AktObject, Key[]>(ReferenceEqualityComparer.Instance);
        foreach (var (target, list) in lists) table[target] = list.OrderBy(k => k.Frame).ToArray();
        return table;
    }

    /// <summary>The keyed value at <paramref name="time"/> (the last key at or before it), or null before the first key.</summary>
    public static bool? KeyedValue(Key[] keys, float time)
    {
        bool? value = null;
        foreach (var key in keys)
        {
            if (key.Frame > time + 1e-4f) break;
            value = key.On;
        }
        return value;
    }

    /// <summary><paramref name="o"/>'s keys in <paramref name="animation"/>, by frame.</summary>
    public static IReadOnlyList<Key> Keys(Animation animation, Object3D o) =>
        Table(animation) is { } table && table.TryGetValue(o.Source, out var keys) ? keys : [];

    /// <summary>Whether <paramref name="o"/> compensates its parent's scale at <paramref name="time"/> (null animation: the rigging value).</summary>
    public static bool ValueAt(Animation? animation, Object3D o, float time)
    {
        if (animation != null && Table(animation) is { } table && table.TryGetValue(o.Source, out var keys)
            && KeyedValue(keys, time) is { } keyed)
            return keyed;
        return o.CompensatesParentScale;
    }

    /// <summary>The key of <paramref name="o"/> at <paramref name="frame"/>, if any.</summary>
    public static bool? KeyAt(Animation animation, Object3D o, int frame) =>
        Keys(animation, o).Where(k => k.Frame == frame).Select(k => (bool?)k.On).FirstOrDefault();

    /// <summary>Keys <paramref name="on"/> at <paramref name="frame"/> (replacing a key there). Returns whether anything changed.</summary>
    public static bool SetKey(Animation animation, Object3D o, int frame, bool on)
    {
        EnsureMembers(animation.Model);
        var (targets, frames, values) = Arrays(animation);
        for (int i = 0; i < targets.Count; i++)
            if (ReferenceEquals(targets[i].Target, o.Source) && Convert.ToInt32(frames[i]) == frame)
            {
                if ((Convert.ToInt32(values[i]) != 0) == on) return false;
                values[i] = on ? 1 : 0;
                return true;
            }
        targets.Add(new AktReference(o.Source));
        frames.Add(frame);
        values.Add(on ? 1 : 0);
        return true;
    }

    /// <summary>Deletes the key of <paramref name="o"/> at <paramref name="frame"/>, if any.</summary>
    public static bool RemoveKey(Animation animation, Object3D o, int frame)
    {
        if (animation.Source[MaterialSchema.CompensateTargetsMember] is not List<AktReference>) return false;
        var (targets, frames, values) = Arrays(animation);
        for (int i = targets.Count - 1; i >= 0; i--)
            if (ReferenceEquals(targets[i].Target, o.Source) && Convert.ToInt32(frames[i]) == frame)
            {
                targets.RemoveAt(i);
                frames.RemoveAt(i);
                values.RemoveAt(i);
                return true;
            }
        return false;
    }

    /// <summary>Deletes every key of <paramref name="o"/> in <paramref name="animation"/>.</summary>
    public static bool ClearKeys(Animation animation, Object3D o)
    {
        if (animation.Source[MaterialSchema.CompensateTargetsMember] is not List<AktReference>) return false;
        var (targets, frames, values) = Arrays(animation);
        bool removed = false;
        for (int i = targets.Count - 1; i >= 0; i--)
            if (ReferenceEquals(targets[i].Target, o.Source))
            {
                targets.RemoveAt(i);
                frames.RemoveAt(i);
                values.RemoveAt(i);
                removed = true;
            }
        return removed;
    }

    /// <summary>Gives the scene's schema, and every material and animation, the SloppyAnimatsu members. Run outside an undo snapshot.</summary>
    public static void EnsureMembers(SceneModel model) => MaterialSchema.EnsureFields(model.Document.Schema, model.Document.Roots);

    private static (List<AktReference> Targets, List<object?> Frames, List<object?> Values) Arrays(Animation animation)
    {
        var s = animation.Source;
        if (s[MaterialSchema.CompensateTargetsMember] is not List<AktReference> targets) s.Set(MaterialSchema.CompensateTargetsMember, targets = []);
        if (s[MaterialSchema.CompensateFramesMember] is not List<object?> frames) s.Set(MaterialSchema.CompensateFramesMember, frames = []);
        if (s[MaterialSchema.CompensateValuesMember] is not List<object?> values) s.Set(MaterialSchema.CompensateValuesMember, values = []);
        int n = Math.Min(targets.Count, Math.Min(frames.Count, values.Count));
        if (targets.Count > n) targets.RemoveRange(n, targets.Count - n);
        if (frames.Count > n) frames.RemoveRange(n, frames.Count - n);
        if (values.Count > n) values.RemoveRange(n, values.Count - n);
        return (targets, frames, values);
    }
}
