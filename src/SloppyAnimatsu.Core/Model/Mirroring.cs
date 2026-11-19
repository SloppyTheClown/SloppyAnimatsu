using System.Numerics;
using System.Text.RegularExpressions;

namespace SloppyAnimatsu.Core.Model;

/// <summary>The side of a name in a mirror pair.</summary>
public enum MirrorSide
{
    /// <summary>The name carries no side marker.</summary>
    None,
    /// <summary>The first alternative of a pair matched: left (or up for the up/down list).</summary>
    Left,
    /// <summary>The second alternative matched: right (or down).</summary>
    Right,
    /// <summary>The first alternative of the up/down list matched.</summary>
    Up,
    /// <summary>The second alternative of the up/down list matched.</summary>
    Down,
}

/// <summary>
/// The name-based symmetry
/// five lists of pairs of regular expressions, parsed from
/// <c>CMirrorPreferences</c>. The defaults are the shipped preference file's lists. A pair
/// <c>a|b</c> means "a name matching a turns into the same name with b in its place, and the other
/// way round"; a suffix list only matches at the end, a prefix list at the start, the other lists
/// anywhere.
/// </summary>
public sealed class MirrorMatch
{
    /// <summary>The shipped lists (<c>CMirrorPreferences</c>, "Default name_14").</summary>
    public const string DefaultSuffixes = @"_L|_R,_l|_r,\.L|\.R,\.l|\.r";
    public const string DefaultPrefixes = @"L_|R_,l_|r_,L\.|R\.,l\.|r\.";
    public const string DefaultPatterns = @"Left|Right,left|right";
    public const string DefaultPatterns2 =
        @"_L_|_R_,_l_|_r_,\.L\.|\.R\.,\.l\.|\.r\.,_L\.|_R\.,_l\.|_r\.,\.L_|\.R_,\.l_|\.r_";
    public const string DefaultUpDown = @"U_|D_,u_|d_";

    /// <summary>The default matcher, with the shipped preference lists.</summary>
    public static MirrorMatch Default { get; } = new();

    // The rules of each list's first alternatives and of its second alternatives, in list order.
    // The app tries every first alternative before any second one.
    private readonly List<Rule> _first = [];
    private readonly List<Rule> _second = [];

    private readonly record struct Rule(Regex Pattern, string Replacement, MirrorSide Side);

    public MirrorMatch()
        : this(DefaultSuffixes, DefaultPrefixes, DefaultPatterns, DefaultPatterns2, DefaultUpDown) { }

    /// <param name="suffixes">Comma-separated <c>a|b</c> pairs matched at the end of the name.</param>
    /// <param name="prefixes">Pairs matched at the start.</param>
    /// <param name="patterns">Pairs matched anywhere.</param>
    /// <param name="patterns2">More pairs matched anywhere.</param>
    /// <param name="upDown">Pairs matched at the start, whose sides are up and down.</param>
    public MirrorMatch(string suffixes, string prefixes, string patterns, string patterns2, string upDown)
    {
        AddList(suffixes, anchoredStart: false, anchoredEnd: true, upDown: false);
        AddList(prefixes, anchoredStart: true, anchoredEnd: false, upDown: false);
        AddList(patterns, anchoredStart: false, anchoredEnd: false, upDown: false);
        AddList(patterns2, anchoredStart: false, anchoredEnd: false, upDown: false);
        AddList(upDown, anchoredStart: true, anchoredEnd: false, upDown: true);
    }

    private void AddList(string list, bool anchoredStart, bool anchoredEnd, bool upDown)
    {
        foreach (var pair in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sides = pair.Split('|');
            if (sides.Length != 2 || sides[0].Length == 0 || sides[1].Length == 0) continue;
            var (first, second) = upDown ? (MirrorSide.Up, MirrorSide.Down) : (MirrorSide.Left, MirrorSide.Right);
            Rule rule(string pattern, string counter, MirrorSide side) =>
                new(new Regex(Anchor(pattern, anchoredStart, anchoredEnd), RegexOptions.CultureInvariant), Literal(counter), side);
            _first.Add(rule(sides[0], sides[1], first));
            _second.Add(rule(sides[1], sides[0], second));
        }
    }

    private static string Anchor(string pattern, bool start, bool end) =>
        (start ? "^" : "") + "(?:" + pattern + ")" + (end ? "$" : "");

    /// <summary>A pattern as the text it stands for: the defaults only escape dots.</summary>
    private static string Literal(string pattern) => pattern.Replace(@"\.", ".");

    /// <summary>
    /// The mirrored name of <paramref name="name"/> and the side it was matched on, if any of the
    /// lists matches.
    /// </summary>
    public bool TryMirror(string name, out string mirrored, out MirrorSide side)
    {
        if (Try(_first, name, out mirrored, out side) || Try(_second, name, out mirrored, out side)) return true;
        mirrored = name;
        side = MirrorSide.None;
        return false;

        static bool Try(List<Rule> rules, string name, out string mirrored, out MirrorSide side)
        {
            foreach (var rule in rules)
            {
                var match = rule.Pattern.Match(name);
                if (!match.Success) continue;
                mirrored = string.Concat(name.AsSpan(0, match.Index), rule.Replacement, name.AsSpan(match.Index + match.Length));
                side = rule.Side;
                return true;
            }
            mirrored = name;
            side = MirrorSide.None;
            return false;
        }
    }

    /// <summary>The side of <paramref name="name"/> in the lists (<see cref="MirrorSide.None"/> when no list matches).</summary>
    public MirrorSide SideOf(string name) => TryMirror(name, out _, out var side) ? side : MirrorSide.None;

    /// <summary>Whether <paramref name="name"/> is on the left of its pair.</summary>
    public bool IsLeft(string name) => SideOf(name) == MirrorSide.Left;

    /// <summary>Whether <paramref name="name"/> is on the right of its pair.</summary>
    public bool IsRight(string name) => SideOf(name) == MirrorSide.Right;

    /// <summary>Whether <paramref name="name"/> is the up side of an up/down pair.</summary>
    public bool IsUp(string name) => SideOf(name) == MirrorSide.Up;

    /// <summary>Whether <paramref name="name"/> is the down side of an up/down pair.</summary>
    public bool IsDown(string name) => SideOf(name) == MirrorSide.Down;

    /// <summary>
    /// The symmetric object of <paramref name="source"/> in <paramref name="character"/>, following
    /// The rule: the same class, the mirrored name compared
    /// case-insensitively, among the character's controllers. A reverse-foot shoe locator goes
    /// through its controller's symmetric shoe. A name without a side marker is its own
    /// counterpart, unless a folder above it is side-tagged, in which case there is none.
    /// </summary>
    public Object3D? Find(Character character, Object3D source)
    {
        if (source is ReverseFootLocator locator && locator.Controller is { } controller
            && Find(character, controller) is ControllerReverseFoot symmetric && !ReferenceEquals(symmetric, controller)
            && symmetric.Shoe.FirstOrDefault(s => s.Index == locator.Index) is { } counterpart)
            return counterpart;

        if (!TryMirror(source.Name ?? "", out string mirrored, out _))
        {
            // The tagged parent: a non-3D ancestor (a folder) with a side marker.
            for (var ancestor = source.Parent; ancestor != null && ancestor is not Object3D; ancestor = ancestor.Parent)
                if (TryMirror(ancestor.Name ?? "", out _, out _)) return null;
            return source;
        }
        return character.Controllers.FirstOrDefault(c =>
            c.GetType() == source.GetType() && string.Equals(c.Name, mirrored, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Mirroring of keys. The mirror is the reflection
/// across the character's local YZ plane: a world matrix W becomes <c>M·W·M</c> with
/// <c>M = C⁻¹·diag(−1,1,1)·C</c>, C the character's world.
/// </summary>
public sealed partial class KeyEditor
{
    /// <summary>
    /// Mirrors the selected keys of <paramref name="sources"/> in <paramref name="stack"/> onto
    /// their symmetric counterparts (<see cref="MirrorMatch.Find"/>). A pair is
    /// skipped when the counterpart has no track there or when its masked channel mask differs;
    /// only the masked channels are written (the rotation whole), key 0 is also written into the
    /// loop key, and the tangents are recomputed. The counterpart's world becomes the source's
    /// world mirrored about the character's plane, expressed relative to the counterpart's
    /// reference pose and key space. IK and reverse-foot keys also mirror their scalar angles, the
    /// IK flag and the stored chain. Returns the number of target keys written.
    /// </summary>
    public int MirrorKeys(AnimStack stack, IEnumerable<int> keyIndices, IEnumerable<Object3D> sources,
        MirrorMatch? match = null, AnimChannels filter = AllChannels)
    {
        var animation = stack.Animation ?? throw new InvalidOperationException($"{stack} is not in an animation.");
        match ??= MirrorMatch.Default;
        var indices = keyIndices.Where(i => i >= 0 && i < stack.Keys.Count - 1).Distinct().OrderBy(i => i).ToArray();
        if (indices.Length == 0) return 0;

        var pairs = new List<(AnimTrack Source, AnimTrack Target)>();
        foreach (var source in sources.Distinct())
        {
            if (Evaluator.IndexOf(source) < 0 || stack.TrackFor(source) is not { } sourceTrack) continue;
            if (match.Find(Character, source) is not { } target || ReferenceEquals(target, source)) continue;
            if (Evaluator.IndexOf(target) < 0 || stack.TrackFor(target) is not { } targetTrack) continue;
            var mask = sourceTrack.Channels & filter;
            if (mask == AnimChannels.None || (targetTrack.Channels & filter) != mask) continue;
            pairs.Add((sourceTrack, targetTrack));
        }
        if (pairs.Count == 0) return 0;

        Evaluator.Invalidate();
        var worlds = new FrameWorlds(Evaluator, animation);

        // Two phases, as the app collects the source data before applying it: selecting both sides
        // must not mirror an already mirrored key.
        var writes = new List<MirrorWrite>();
        foreach (var (sourceTrack, targetTrack) in pairs)
        {
            var sourceObject = sourceTrack.Target!;
            var targetObject = targetTrack.Target!;
            var mask = sourceTrack.Channels & filter;
            foreach (int i in indices)
            {
                if (i >= sourceTrack.Keys.Count || sourceTrack.Keys[i] is not TransformKey sourceKey) continue;
                if (i >= targetTrack.Keys.Count || targetTrack.Keys[i] is not TransformKey targetKey) continue;
                int frame = stack.Keys[i].Time;
                var sourceFrame = sourceKey.Space ?? sourceObject.Parent3D;
                var sourceWorld = PoseWorld(sourceObject, sourceKey.Value, sourceFrame, frame, worlds);
                var mirror = MirrorMatrix(frame, worlds);
                var targetWorld = mirror * sourceWorld * mirror;
                var targetSpace = MirroredSpace(match, Character, sourceKey.Space, targetObject);
                var targetFrame = targetSpace ?? targetObject.Parent3D;
                var targetLocal = LocalUnder(targetObject, targetWorld, targetFrame, frame, worlds);
                var value = PoseEvaluator.Decompose(RestOf(targetObject), Transform.FromMatrixLoose(targetLocal));
                var extras = sourceKey is IKKey sourceIk && targetKey is IKKey
                    ? MirrorKeyExtras(sourceIk, mask, sourceObject, targetObject, frame, worlds)
                    : null;
                writes.Add(new MirrorWrite(targetTrack, i, Merge(targetKey.Value, value, mask), targetSpace, extras));
            }
        }
        foreach (var write in writes)
        {
            if (write.Index >= write.Track.Keys.Count || write.Track.Keys[write.Index] is not TransformKey key) continue;
            key.Value = write.Value;
            key.Space = write.Space;
            if (write.Extras is { } extras && key is IKKey ik) ApplyMirrorExtras(ik, extras);
        }
        foreach (var track in writes.Where(w => w.Index == 0).Select(w => w.Track).Distinct()) MirrorLoopKey(track);
        RecomputeAll(stack);
        return writes.Count;
    }

    /// <summary>One mirrored key value, collected before anything is written.</summary>
    private sealed record MirrorWrite(AnimTrack Track, int Index, Transform Value, Object3D? Space, MirrorExtras? Extras);

    /// <summary>The IK and reverse-foot parts of a mirrored key, collected before anything is written.</summary>
    private sealed record MirrorExtras(float PoleAngle, uint Flags, float? Roll, float? Twist, float? Bank, Transform[]? Chain);

    /// <summary>
    /// Keeps the direct children of a posed object where they were in the world
    /// every child in <paramref name="children"/> with a track takes the
    /// local that cancels the parent's change from <paramref name="before"/> to
    /// <paramref name="after"/>, and is keyed at the same key. Returns the number of children keyed.
    /// </summary>
    public int KeepChildrenFixed(AnimStack stack, int keyIndex, IReadOnlyList<(Object3D Child, Transform Local)> children,
        Transform before, Transform after)
    {
        if (children.Count == 0 || !Matrix4x4.Invert(after.ToMatrix(), out _)) return 0;
        // Row-vector terms: childLocal' = childLocal * (before * after⁻¹).
        var delta = before.ToMatrix() * Inverse(after.ToMatrix());
        int count = 0;
        foreach (var (child, local) in children)
        {
            if (stack.TrackFor(child) is not { } track) continue;
            SetKey(stack, keyIndex, child, Transform.FromMatrixLoose(local.ToMatrix() * delta), track.Channels);
            count++;
        }
        return count;
    }

    /// <summary>The rest transform of <paramref name="o"/>, or identity when it is not a controller.</summary>
    private Transform RestOf(Object3D o)
    {
        int i = Evaluator.IndexOf(o);
        return i >= 0 ? Evaluator.RestTransforms[i] : Transform.Identity;
    }

    /// <summary>The world of a key's posed local, under <paramref name="frame"/> (null: under the world).</summary>
    private static Matrix4x4 PoseWorld(Object3D target, Transform value, Object3D? frame, float time, FrameWorlds worlds)
    {
        var local = PoseEvaluator.Compose(worlds.RestOf(target), value).ToMatrix();
        return frame is null ? local
            : WorldComposition.Compose(local, worlds.World(frame, time), worlds.ScaleOf(frame, time),
                worlds.ScaleCompensated(target, time));
    }

    /// <summary>The local matrix that puts <paramref name="world"/> under <paramref name="frame"/> (null: the world).</summary>
    private static Matrix4x4 LocalUnder(Object3D target, Matrix4x4 world, Object3D? frame, float time, FrameWorlds worlds)
    {
        if (frame is null || !WorldComposition.TryDecompose(world, worlds.World(frame, time), worlds.ScaleOf(frame, time),
                worlds.ScaleCompensated(target, time), out var local))
            return world;
        return local;
    }

    /// <summary>The reflection across the character's local YZ plane, <c>C⁻¹·diag(−1,1,1)·C</c> (an involution).</summary>
    private Matrix4x4 MirrorMatrix(float time, FrameWorlds worlds)
    {
        var characterWorld = worlds.World(Character, time);
        return Inverse(characterWorld) * Matrix4x4.CreateScale(-1, 1, 1) * characterWorld;
    }

    internal static Matrix4x4 Inverse(Matrix4x4 m) => Matrix4x4.Invert(m, out var inverse) ? inverse : Matrix4x4.Identity;

    /// <summary>The counterpart of a key's space, normalised to null when it is the target's parent.</summary>
    private static Object3D? MirroredSpace(MirrorMatch match, Character character, Object3D? space, Object3D target)
    {
        if (space is null) return null;
        var mirrored = match.Find(character, space) ?? space;
        return ReferenceEquals(mirrored, target.Parent3D) ? null : mirrored;
    }

    /// <summary>
    /// The IK and reverse-foot extras of a mirrored key: the pole angle, the reverse-foot angles
    /// whose channel bits are in the mask, the IK flag, and the stored chain mirrored joint by
    /// joint.
    /// </summary>
    private MirrorExtras MirrorKeyExtras(IKKey source, AnimChannels mask, Object3D sourceObject, Object3D targetObject,
        float time, FrameWorlds worlds)
    {
        float pole = (mask & AnimChannels.PoleAngle) != 0 ? source.PoleAngle : float.NaN;
        float? roll = null, twist = null, bank = null;
        if (source is ReverseFootKey sourceFoot)
        {
            if ((mask & AnimChannels.Roll) != 0) roll = sourceFoot.RollAngle;
            if ((mask & AnimChannels.Twist) != 0) twist = sourceFoot.TwistAngle;
            if ((mask & AnimChannels.Bank) != 0) bank = sourceFoot.BankAngle;
        }

        Transform[]? chain = null;
        if (source.ChainTransforms is { Count: > 0 } stored && sourceObject is ControllerIK sourceController
            && targetObject is ControllerIK targetController
            && stored.Count == sourceController.Chain.Count && stored.Count == targetController.Chain.Count)
            chain = MirrorChain(sourceController, targetController, stored, time, worlds);
        return new MirrorExtras(pole, source.Flags, roll, twist, bank, chain);
    }

    /// <summary>Writes the collected IK and reverse-foot extras into a mirrored key.</summary>
    private static void ApplyMirrorExtras(IKKey target, MirrorExtras extras)
    {
        if (!float.IsNaN(extras.PoleAngle)) target.PoleAngle = extras.PoleAngle;
        if (target is ReverseFootKey targetFoot)
        {
            if (extras.Roll is { } roll) targetFoot.RollAngle = roll;
            if (extras.Twist is { } twist) targetFoot.TwistAngle = twist;
            if (extras.Bank is { } bank) targetFoot.BankAngle = bank;
        }
        target.SetIkKey((extras.Flags & 1) != 0);
        if (extras.Chain is { } chain && target.ChainTransforms.Count == chain.Length) target.SetChainTransforms(chain);
    }

    /// <summary>
    /// The source chain's stored locals, mirrored: the source joints' worlds built down the chain
    /// and reflected about the character's plane, then expressed as the target chain's locals under
    /// the target's actual parent frames.
    /// </summary>
    private Transform[] MirrorChain(ControllerIK source, ControllerIK target, IReadOnlyList<Transform> chain, float time,
        FrameWorlds worlds)
    {
        int n = chain.Count;
        var mirror = MirrorMatrix(time, worlds);
        var sourceWorld = new Matrix4x4[n];
        for (int j = 0; j < n; j++)
        {
            var parentWorld = j == 0
                ? (source.Chain[0].Parent3D is { } root ? worlds.World(root, time) : Matrix4x4.Identity)
                : ReferenceEquals(source.Chain[j].Parent3D, source.Chain[j - 1]) ? sourceWorld[j - 1]
                : source.Chain[j].Parent3D is { } above ? worlds.World(above, time) : Matrix4x4.Identity;
            sourceWorld[j] = chain[j].ToMatrix() * parentWorld;
        }
        var mirrored = new Matrix4x4[n];
        for (int j = 0; j < n; j++) mirrored[j] = mirror * sourceWorld[j] * mirror;

        var result = new Transform[n];
        for (int j = 0; j < n; j++)
        {
            var parentWorld = j == 0
                ? (target.Chain[0].Parent3D is { } root ? worlds.World(root, time) : Matrix4x4.Identity)
                : ReferenceEquals(target.Chain[j].Parent3D, target.Chain[j - 1]) ? mirrored[j - 1]
                : target.Chain[j].Parent3D is { } above ? worlds.World(above, time) : Matrix4x4.Identity;
            result[j] = Transform.FromMatrixLoose(mirrored[j] * Inverse(parentWorld));
        }
        return result;
    }

    /// <summary>
    /// World matrices and local scales of any object at a frame, from the evaluator for the
    /// character's controllers and from the stored matrices above them. The cache keeps the
    /// pre-edit state, which is what a mirror reads.
    /// </summary>
    private sealed class FrameWorlds(PoseEvaluator evaluator, Animation? animation)
    {
        private readonly Dictionary<float, (Matrix4x4[] World, Transform[] Local)> _frames = [];
        private readonly Dictionary<(Object3D, float), (Matrix4x4 World, Vector3 Scale)> _others = [];

        public bool ScaleCompensated(Object3D o, float time) => evaluator.Compensates(o, animation, time);

        public Transform RestOf(Object3D o)
        {
            int i = evaluator.IndexOf(o);
            return i >= 0 ? evaluator.RestTransforms[i] : Transform.Identity;
        }

        public Matrix4x4 World(Object3D o, float time)
        {
            int i = evaluator.IndexOf(o);
            if (i >= 0) return Frame(time).World[i];
            if (_others.TryGetValue((o, time), out var cached)) return cached.World;
            var world = o.Parent3D is { } p
                ? WorldComposition.Compose(o.LocalMatrix, World(p, time), ScaleOf(p, time), ScaleCompensated(o, time))
                : o.LocalMatrix;
            _others[(o, time)] = (world, Object3D.LocalScale(o.LocalMatrix));
            return world;
        }

        public Vector3 ScaleOf(Object3D o, float time)
        {
            int i = evaluator.IndexOf(o);
            return i >= 0 ? Frame(time).Local[i].Scale : Object3D.LocalScale(o.LocalMatrix);
        }

        private (Matrix4x4[] World, Transform[] Local) Frame(float time)
        {
            if (!_frames.TryGetValue(time, out var frame))
                _frames[time] = frame = (evaluator.WorldMatrices(animation, time), evaluator.LocalTransforms(animation, time));
            return frame;
        }
    }
}

/// <summary>
/// Mirroring of the rest pose. The
/// pose is the rigging context's pose; its worlds come from the pose's matrices.
/// </summary>
public sealed partial class RestPoseEditor
{
    /// <summary>
    /// Mirrors the given controllers' slots of the rest pose onto their symmetric counterparts
    /// (<see cref="MirrorMatch.Find"/>), ancestors first. A counterpart's world becomes the
    /// source's world reflected about the character's plane; with <paramref name="keepChildrenFixed"/>
    /// the counterpart's direct children keep their world. Returns the number of slots written.
    /// </summary>
    public int MirrorPose(IEnumerable<Object3D> sources, MirrorMatch? match = null, bool keepChildrenFixed = false)
    {
        match ??= MirrorMatch.Default;
        var objects = sources.Distinct().Where(o => SlotOf(o) >= 0).OrderBy(Depth).ToList();
        if (objects.Count == 0) return 0;
        var frames = new RestFrames(this);
        var mirror = frames.Mirror;

        // Read every source and target from the state before the edit, then write.
        var writes = new List<(Object3D Target, Transform Before, Transform After)>();
        foreach (var source in objects)
        {
            if (match.Find(Character, source) is not { } target || ReferenceEquals(target, source)) continue;
            if (!CanEdit(target, out _)) continue;
            var world = mirror * frames.World(source) * mirror;
            var local = Transform.FromMatrixLoose(world * Inverse(frames.World(target.Parent3D)));
            writes.Add((target, GetLocal(target), local));
        }
        foreach (var (target, before, after) in writes)
        {
            SetLocal(target, after);
            if (keepChildrenFixed) KeepChildrenFixed(target, before, after);
        }
        return writes.Count;
    }

    /// <summary>
    /// The rest-pose form of <see cref="KeyEditor.KeepChildrenFixed"/>: each direct child with an
    /// editable slot takes the local that cancels its parent's change from
    /// <paramref name="before"/> to <paramref name="after"/>. With <paramref name="children"/> the
    /// child locals come from that list. Returns the number of children written.
    /// </summary>
    public int KeepChildrenFixed(Object3D parent, Transform before, Transform after,
        IReadOnlyList<(Object3D Child, Transform Local)>? children = null)
    {
        if (!Matrix4x4.Invert(after.ToMatrix(), out _)) return 0;
        var delta = before.ToMatrix() * Inverse(after.ToMatrix());
        var todo = children ?? parent.Children.OfType<Object3D>()
            .Select(c => (Child: c, Local: SlotOf(c) >= 0 ? GetLocal(c) : Transform.Identity)).ToArray();
        int count = 0;
        foreach (var (child, local) in todo)
        {
            if (SlotOf(child) < 0 || !CanEdit(child, out _)) continue;
            SetLocal(child, Transform.FromMatrixLoose(local.ToMatrix() * delta));
            count++;
        }
        return count;
    }

    private static int Depth(Object3D o)
    {
        int depth = 0;
        for (var p = o.Parent3D; p != null; p = p.Parent3D) depth++;
        return depth;
    }

    private static Matrix4x4 Inverse(Matrix4x4 m) => Matrix4x4.Invert(m, out var inverse) ? inverse : Matrix4x4.Identity;

    /// <summary>Worlds of the objects in the rest pose's matrices.</summary>
    private sealed class RestFrames(RestPoseEditor editor)
    {
        /// <summary>The reflection across the character's plane, <c>C⁻¹·diag(−1,1,1)·C</c>.</summary>
        public Matrix4x4 Mirror
        {
            get
            {
                var world = World(editor.Character);
                return Inverse(world) * Matrix4x4.CreateScale(-1, 1, 1) * world;
            }
        }

        public Matrix4x4 World(Object3D? o)
        {
            if (o is null) return Matrix4x4.Identity;
            var local = LocalOf(o);
            return o.Parent3D is { } parent
                ? WorldComposition.Compose(local, World(parent), Object3D.LocalScale(LocalOf(parent)), o.CompensatesParentScale)
                : local;
        }

        private Matrix4x4 LocalOf(Object3D o)
        {
            int slot = editor.SlotOf(o);
            return slot >= 0 && editor.EditedPose is { } pose && slot < pose.Count ? pose.Matrix(slot) : o.LocalMatrix;
        }

        private static Matrix4x4 Inverse(Matrix4x4 m) => Matrix4x4.Invert(m, out var inverse) ? inverse : Matrix4x4.Identity;
    }
}
