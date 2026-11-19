using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// One copied key: the track's value and <c>Space</c> at that key, plus the IK fields
/// the pole angle, a
/// reverse-foot key's roll, twist and bank, and whether the key is an IK key.
/// </summary>
public sealed record ClipboardKey(Transform Value, Object3D? Space, float PoleAngle = 0,
    float RollAngle = 0, float TwistAngle = 0, float BankAngle = 0, bool IkKey = false);

/// <summary>One copied object: its channel mask and one <see cref="ClipboardKey"/> per copied key.</summary>
public sealed record ClipboardEntry(Object3D Target, AnimChannels Mask, IReadOnlyList<ClipboardKey> Keys);

/// <summary>
/// The key clipboard: one gap per copied key
/// and one entry per copied object. Made by <see cref="KeyEditor.CopyKeys"/>.
/// </summary>
public sealed class KeyClipboard(IReadOnlyList<int> gaps, IReadOnlyList<ClipboardEntry> entries,
    IReadOnlyList<StackKey>? stackKeys = null)
{
    /// <summary>The copied keys' gaps: 10 for key 0, else the frames since the stack's previous key.</summary>
    public IReadOnlyList<int> Gaps { get; } = gaps;

    public IReadOnlyList<ClipboardEntry> Entries { get; } = entries;

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>
    /// The copied stack keys (flags and fades), one per gap, or null. A paste that overwrites keys
    /// "entirely" (the viewer's Ctrl+V) writes them too; the paste does not.
    /// </summary>
    public IReadOnlyList<StackKey>? StackKeys { get; } = stackKeys;

    /// <summary>This clipboard with only <paramref name="entries"/> (same gaps and stack keys).</summary>
    public KeyClipboard With(IReadOnlyList<ClipboardEntry> entries) => new(Gaps, entries, StackKeys);
}

public sealed partial class KeyEditor
{
    /// <summary>The 9 MRS channels: translation, rotation and scale.</summary>
    public const AnimChannels MrsChannels = AnimChannels.Translate | AnimChannels.Rotate | AnimChannels.Scale;

    /// <summary>
    /// Every channel a key can hold: the 9 MRS channels plus the IK pole angle and the reverse-foot
    /// angles. The default channel filter of copy and paste
    /// is every channel (the angle bits have to
    /// be in the filter for its reverse-foot angles to be copied).
    /// </summary>
    public const AnimChannels AllChannels = MrsChannels | AnimChannels.PoleAngle | AnimChannels.Roll
        | AnimChannels.Twist | AnimChannels.Bank;

    /// <summary>
    /// Copy with an active stack: keys
    /// <paramref name="keyIndices"/> of <paramref name="stack"/> (key 0 when none; never the end
    /// key) of each of <paramref name="objects"/> that has a track there. Each entry's mask is the
    /// track's channels &amp; <paramref name="channelFilter"/>. Without
    /// <paramref name="keepSpaces"/> the values are converted into the object's parent space and
    /// carry no <c>Space</c>. IK and reverse-foot keys carry their angles and IK flag; the
    /// <c>RefArray</c> is never copied.
    /// </summary>
    public KeyClipboard CopyKeys(AnimStack stack, IEnumerable<int> keyIndices, IEnumerable<Object3D> objects,
        AnimChannels channelFilter = AllChannels, bool keepSpaces = false)
    {
        var keys = stack.Keys;
        int n = keys.Count;
        var selected = SelectedKeys(keys.Count, keyIndices);
        if (selected.Length == 0 && n > 1) selected = [0];
        var gaps = selected.Select(i => i == 0 ? 10 : keys[i].Time - keys[i - 1].Time).ToArray();

        KeySpaceConverter? converter = null;
        var entries = new List<ClipboardEntry>();
        foreach (var target in objects.Distinct())
        {
            if (stack.TrackFor(target) is not { } track) continue;
            var copied = new List<ClipboardKey>();
            foreach (int i in selected)
            {
                if (i >= track.Keys.Count || track.Keys[i] is not TransformKey key) continue;
                var value = keepSpaces || key.Space == null ? key.Value
                    : (converter ??= new KeySpaceConverter(Evaluator, stack.Animation ?? throw new InvalidOperationException($"{stack} is not in an animation.")))
                        .Convert(target, key.Value, key.Space, null, keys[i].Time);
                copied.Add(key switch
                {
                    ReverseFootKey foot => new ClipboardKey(value, keepSpaces ? key.Space : null,
                        foot.PoleAngle, foot.RollAngle, foot.TwistAngle, foot.BankAngle, (foot.Flags & 1) != 0),
                    IKKey ik => new ClipboardKey(value, keepSpaces ? key.Space : null,
                        ik.PoleAngle, IkKey: (ik.Flags & 1) != 0),
                    _ => new ClipboardKey(value, keepSpaces ? key.Space : null),
                });
            }
            entries.Add(new ClipboardEntry(target, track.Channels & channelFilter, copied));
        }
        return new KeyClipboard(gaps, entries, selected.Select(i => keys[i]).ToArray());
    }

    /// <summary>
    /// Paste onto keys <paramref name="keyIndices"/> of <paramref name="stack"/>
    /// the entries go to <paramref name="objects"/>
    /// by identity, or dealt out in order; the selected keys take the copied keys in order (one
    /// copied key fills them all). Only the channels of track &amp; entry mask are written; an IK
    /// or reverse-foot key also takes the copied angles and IK flag, and its <c>RefArray</c> is
    /// filled only when it does not have the chain's length. Returns the number of track keys written.
    /// </summary>
    /// <param name="withKeyFlags">Also give each pasted key the copied stack key's flags and fades
    /// (<see cref="KeyClipboard.StackKeys"/>), so the paste overwrites the key entirely.</param>
    public int PasteKeys(AnimStack stack, KeyClipboard clipboard, IEnumerable<int> keyIndices, IEnumerable<Object3D> objects,
        bool withKeyFlags = false)
    {
        var selected = SelectedKeys(stack.Keys.Count, keyIndices);
        if (clipboard.IsEmpty || selected.Length == 0) return 0;
        int written = 0;
        foreach (var (track, entry) in Assign(stack, clipboard, objects))
            for (int j = 0; j < selected.Length; j++)
                if (CopiedKey(entry, j) is { } copied
                    && WriteKey(stack, track, selected[j], copied, entry.Mask))
                    written++;
        if (withKeyFlags && written > 0)
            for (int j = 0; j < selected.Length; j++)
                if (CopiedStackKey(clipboard, j) is { } key) stack.SetKeyFlags(selected[j], key.Flags, key.FadeIn, key.FadeOut);
        if (written > 0) RecomputeAll(stack);
        return written;
    }

    /// <summary>
    /// Paste after: inserts one key per
    /// clipboard gap after the last of keys <paramref name="keyIndices"/>, moves the later keys so
    /// their gaps are kept, and writes the copied keys of each entry into the new keys of its
    /// object's track. Tracks that receive nothing hold the last selected key's value. Returns the
    /// first new key's index, or -1 when nothing was inserted.
    /// </summary>
    public int PasteKeysAfter(AnimStack stack, KeyClipboard clipboard, IEnumerable<int> keyIndices, IEnumerable<Object3D> objects)
    {
        var keys = stack.Keys;
        var selected = SelectedKeys(keys.Count, keyIndices);
        int count = clipboard.Gaps.Count;
        if (selected.Length == 0 || count == 0) return -1;
        int last = selected[^1];
        int time = keys[last].Time;
        int shift = clipboard.Gaps.Sum();

        for (int i = keys.Count - 1; i > last; i--) stack.SetKeyTime(i, keys[i].Time + shift);
        var tracks = stack.Tracks.ToList();
        for (int k = 0; k < count; k++)
        {
            time += clipboard.Gaps[k];
            // A new stack key carries no flags or fades, as for the create-key insert.
            stack.InsertKeyTime(last + 1 + k, last, time);
            stack.SetKeyFlags(last + 1 + k, 0, 0, 0);
            foreach (var track in tracks) track.InsertKeyCopy(last, last + 1 + k);
        }

        foreach (var (track, entry) in Assign(stack, clipboard, objects))
            for (int k = 0; k < count; k++)
                if (CopiedKey(entry, k) is { } copied) WriteKey(stack, track, last + 1 + k, copied, entry.Mask);
        RecomputeAll(stack);
        return last + 1;
    }

    /// <summary>
    /// Paste at a frame (the viewer's Ctrl+V with no key selection, or the timeline's "Paste
    /// Key(s)" on an empty spot): the first copied key goes to local frame
    /// <paramref name="localFrame"/> and the next ones keep the copied gaps. A key already at that
    /// frame is overwritten; elsewhere a key is created first. Each key takes the copied values and,
    /// when the clipboard has them, the copied flags and fades. Stops at the end key. Returns the
    /// first pasted key's index, or -1 when nothing was pasted.
    /// </summary>
    public int PasteKeysAt(AnimStack stack, KeyClipboard clipboard, int localFrame, IEnumerable<Object3D> objects)
    {
        if (clipboard.IsEmpty || clipboard.Gaps.Count == 0 || stack.Keys.Count < 2) return -1;
        var targets = objects.ToList();
        int first = -1, time = localFrame;
        for (int k = 0; k < clipboard.Gaps.Count; k++)
        {
            if (k > 0) time += clipboard.Gaps[k];
            var keys = stack.Keys;
            if (time >= keys[^1].Time) break;
            int index = -1;
            for (int i = 0; i < keys.Count - 1; i++)
                if (keys[i].Time == time) { index = i; break; }
            if (index < 0) index = CreateKey(stack, time);
            foreach (var (track, entry) in Assign(stack, clipboard, targets))
                if (CopiedKey(entry, k) is { } copied) WriteKey(stack, track, index, copied, entry.Mask);
            if (CopiedStackKey(clipboard, k) is { } key) stack.SetKeyFlags(index, key.Flags, key.FadeIn, key.FadeOut);
            if (first < 0) first = index;
        }
        if (first >= 0) RecomputeAll(stack);
        return first;
    }

    private static StackKey? CopiedStackKey(KeyClipboard clipboard, int j) =>
        clipboard.StackKeys is not { Count: > 0 } keys ? null : keys.Count == 1 ? keys[0] : j < keys.Count ? keys[j] : null;

    /// <summary>The distinct selected keys but the end key, in index order.</summary>
    private static int[] SelectedKeys(int n, IEnumerable<int> keyIndices) =>
        keyIndices.Where(i => i >= 0 && i < n - 1).Distinct().Order().ToArray();

    /// <summary>The copied key for the j-th pasted key: one copied key fills every key; null once they run out.</summary>
    private static ClipboardKey? CopiedKey(ClipboardEntry entry, int j) =>
        entry.Keys.Count == 1 ? entry.Keys[0] : j < entry.Keys.Count ? entry.Keys[j] : null;

    /// <summary>
    /// The tracks of <paramref name="stack"/> to paste on and their entries: by identity when
    /// every object has its own entry, else the entries in order, wrapping around.
    /// </summary>
    private static List<(AnimTrack Track, ClipboardEntry Entry)> Assign(AnimStack stack, KeyClipboard clipboard, IEnumerable<Object3D> objects)
    {
        var targets = objects.Distinct().Where(o => stack.TrackFor(o) != null).ToList();
        var entries = clipboard.Entries;
        var byIdentity = entries.Count == targets.Count
            ? targets.Select(t => entries.FirstOrDefault(e => ReferenceEquals(e.Target, t))).ToList()
            : null;
        var result = new List<(AnimTrack, ClipboardEntry)>();
        for (int i = 0; i < targets.Count; i++)
        {
            var entry = byIdentity != null && byIdentity.All(e => e != null) ? byIdentity[i]! : entries[i % entries.Count];
            result.Add((stack.TrackFor(targets[i])!, entry));
        }
        return result;
    }

    /// <summary>
    /// Writes <paramref name="copied"/> into key <paramref name="index"/> of
    /// <paramref name="track"/>: the copied <c>Space</c> (when it is in the same file) and the
    /// channels of track &amp; <paramref name="mask"/>, rotation whole. An IK or reverse-foot key
    /// also takes the copied angles, the IK flag, and a <c>RefArray</c> filled from the evaluated
    /// locals when its length is wrong
    /// Key 0 is mirrored into the end key.
    /// </summary>
    private bool WriteKey(AnimStack stack, AnimTrack track, int index, ClipboardKey copied, AnimChannels mask)
    {
        mask &= track.Channels;
        if (mask == AnimChannels.None || index >= track.Keys.Count || track.Keys[index] is not TransformKey key) return false;
        if (copied.Space == null || ReferenceEquals(copied.Space.Model, track.Model)) key.Space = copied.Space;
        if ((mask & MrsChannels) != AnimChannels.None)
            key.Value = Merge(key.Value, copied.Value, mask & MrsChannels);
        if (key is IKKey ik)
        {
            if ((mask & AnimChannels.PoleAngle) != 0) ik.PoleAngle = copied.PoleAngle;
            if (key is ReverseFootKey foot)
            {
                if ((mask & AnimChannels.Roll) != 0) foot.RollAngle = copied.RollAngle;
                if ((mask & AnimChannels.Twist) != 0) foot.TwistAngle = copied.TwistAngle;
                if ((mask & AnimChannels.Bank) != 0) foot.BankAngle = copied.BankAngle;
            }
            // The IK/FK flag is not a channel: every pasted IK or reverse-foot key takes it.
            ik.SetIkKey(copied.IkKey);
            EnsureChainTransforms(stack, track, ik, index);
        }
        if (index == 0) MirrorLoopKey(track);
        return true;
    }

    /// <summary>
    /// Gives <paramref name="key"/> a <c>RefArray</c> of the chain's length when it has none or the
    /// wrong length, filled from the evaluated locals at <paramref name="index"/>'s time: the four
    /// leg locals plus the controller relative to the ball for a reverse-foot key. An array that
    /// already fits is kept.
    /// </summary>
    private void EnsureChainTransforms(AnimStack stack, AnimTrack track, IKKey key, int index)
    {
        if ((key.Controller ?? track.Target as ControllerIK) is not { } controller || stack.Animation is not { } animation) return;
        var joints = controller.Chain;
        bool foot = controller is ControllerReverseFoot && joints.Count == 4;
        int expected = joints.Count + (foot ? 1 : 0);
        if (expected == 0 || key.ChainTransforms.Count == expected) return;
        if (joints.Any(j => Evaluator.IndexOf(j) < 0) || index >= stack.Keys.Count) return;
        int time = stack.Keys[index].Time;
        var local = Evaluator.LocalTransforms(animation, time);
        var values = joints.Select(j => local[Evaluator.IndexOf(j)]).ToList();
        if (foot)
        {
            var world = Evaluator.WorldMatrices(animation, time);
            Matrix4x4.Invert(world[Evaluator.IndexOf(joints[3])], out var inverseBall);
            values.Add(Transform.FromMatrixLoose(world[Evaluator.IndexOf(controller)] * inverseBall));
        }
        key.InitChainTransforms(values);
    }
}
