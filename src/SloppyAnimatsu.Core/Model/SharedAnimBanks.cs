using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Plugins;

namespace SloppyAnimatsu.Core.Model;

/// <summary>Whether a character can share an Anim Bank with a reference character, and why not.</summary>
public sealed record SharedBankEligibility(Character Reference, Character Other,
    IReadOnlyList<string> UnmatchedReference, IReadOnlyList<string> UnmatchedOther, string? Problem)
{
    public bool Eligible => Problem == null && UnmatchedReference.Count == 0 && UnmatchedOther.Count == 0;

    /// <summary>One line for a dialog: "compatible skeleton", or what does not match.</summary>
    public string Summary
    {
        get
        {
            if (Problem != null) return Problem;
            if (Eligible) return "compatible skeleton";
            var parts = new List<string>();
            if (UnmatchedOther.Count > 0)
                parts.Add($"{UnmatchedOther.Count} of its controllers have no counterpart on {Reference.Name}: {Sample(UnmatchedOther)}");
            if (UnmatchedReference.Count > 0)
                parts.Add($"{UnmatchedReference.Count} controllers of {Reference.Name} have no counterpart: {Sample(UnmatchedReference)}");
            return string.Join("; ", parts);
        }
    }

    private static string Sample(IReadOnlyList<string> names) =>
        string.Join(", ", names.Take(5)) + (names.Count > 5 ? ", ..." : "");
}

/// <summary>Two animations that hold the same motion; linking keeps one and drops the other.</summary>
public sealed record BankDuplicate(Animation Kept, Character KeptOwner, Animation Dropped, Character DroppedOwner)
{
    public string KeptPath => SharedAnimBanks.PathOf(Kept);
    public string DroppedPath => SharedAnimBanks.PathOf(Dropped);
}

/// <summary>What a link did.</summary>
public sealed record SharedBankLinkResult(IReadOnlyList<Character> Members, IReadOnlyList<BankDuplicate> Dropped,
    int MergedAnimations, int ConformedControllers);

/// <summary>
/// Shared Anim Banks: characters with compatible skeletons linked so that they play the same
/// animations. Each linked character keeps its own full copy of the bank, whose tracks drive its
/// own controllers, so a scene stays an ordinary scene to any reader; the link itself is a list of
/// groups on <c>CScene</c>, plus rows that pair each bank folder and animation with its copy in
/// every other member. An edit in one member's bank is mirrored into the others by
/// <see cref="Sync"/>; unlinking forgets the link and leaves every character its full copy.
/// </summary>
/// <remarks>
/// Storage: <see cref="MaterialSchema.SharedBankCharactersMember"/> with the group number of each in
/// <see cref="MaterialSchema.SharedBankGroupsMember"/>; <see cref="MaterialSchema.SharedBankItemsMember"/>
/// with the row number of each in <see cref="MaterialSchema.SharedBankItemRowsMember"/>. Rows refer to
/// objects, so renames and moves need nothing; a row entry whose object left the scene is dropped
/// the next time the rows are written.
/// </remarks>
public static partial class SharedAnimBanks
{
    /// <summary>Largest rotation difference, in degrees, of two keys still counted as the same motion.</summary>
    public const float DuplicateRotationDegrees = 0.5f;
    /// <summary>Largest translation or scale difference of two keys still counted as the same motion.</summary>
    public const float DuplicateValueTolerance = 0.05f;
    /// <summary>
    /// Ref-pose rotation (degrees) and scale differences below this are left alone by the conform
    /// step: a matrix decompose/compose round trip alone moves a rotation by ~1e-4 degrees.
    /// </summary>
    private const float ConformTolerance = 1e-3f;

    private const string RetargetFolderPrefix = "Retargeted From";

    // ---- groups ------------------------------------------------------------------------------

    /// <summary>Every group of linked characters (two or more members each), in stored order.</summary>
    public static IReadOnlyList<IReadOnlyList<Character>> Groups(SceneModel model) =>
        ReadGroups(model).Values.Where(g => g.Count >= 2).Select(g => (IReadOnlyList<Character>)g).ToList();

    /// <summary>The group <paramref name="character"/> belongs to (itself included), or empty.</summary>
    public static IReadOnlyList<Character> GroupOf(Character character) =>
        Groups(character.Model).FirstOrDefault(g => g.Contains(character)) ?? [];

    /// <summary>The other members of <paramref name="character"/>'s group.</summary>
    public static IReadOnlyList<Character> PeersOf(Character character) =>
        GroupOf(character).Where(c => c != character).ToList();

    public static bool IsLinked(Character character) => GroupOf(character).Count > 0;

    // ---- eligibility -------------------------------------------------------------------------

    /// <summary>
    /// Whether <paramref name="other"/> can share <paramref name="reference"/>'s bank: every joint
    /// and IK/reverse-foot controller of each has a counterpart of the same name in the other.
    /// </summary>
    public static SharedBankEligibility Eligibility(Character reference, Character other)
    {
        if (reference == other)
            return new(reference, other, [], [], "it is the reference character");
        if (reference.AnimBank == null || other.AnimBank == null)
            return new(reference, other, [], [], "a character without an Anim Bank");
        var map = RetargetingMap.Build(reference, other);
        return new(reference, other,
            map.UnmatchedSources.Select(o => o.Name ?? "?").ToList(),
            map.UnmatchedTargets.Select(o => o.Name ?? "?").ToList(), null);
    }

    // ---- link / unlink -----------------------------------------------------------------------

    /// <summary>
    /// The animations that hold the same motion in the banks that a link of <paramref name="reference"/>
    /// with <paramref name="others"/> would merge (pairs from different banks only), and which of each
    /// pair is kept: the copy outside a "Retargeted From" folder, else the one met first (the
    /// reference character's bank comes first).
    /// </summary>
    public static IReadOnlyList<BankDuplicate> FindDuplicates(Character reference, IEnumerable<Character> others)
    {
        var units = Representatives(reference, others);
        var entries = new List<(Animation Animation, Character Owner, int Unit, string Signature, string Path)>();
        for (int u = 0; u < units.Count; u++)
            foreach (var animation in AnimationsOf(units[u]))
                entries.Add((animation, units[u], u, CheapSignature(animation), PathOf(animation)));

        var result = new List<BankDuplicate>();
        var dropped = new HashSet<Animation>();
        // Two passes: copies at the same path pair up first, so two banks holding the same motion
        // twice (e.g. once in a "Retargeted From" folder) pair each copy with its own counterpart.
        foreach (bool samePath in new[] { true, false })
        foreach (var bucket in entries.GroupBy(e => e.Signature))
        {
            var list = bucket.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                if (dropped.Contains(list[i].Animation)) continue;
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = list[i];
                    var b = list[j];
                    if (a.Unit == b.Unit || dropped.Contains(b.Animation)) continue;
                    if (samePath && a.Path != b.Path) continue;
                    if (!SameMotion(a.Animation, a.Owner, b.Animation, b.Owner)) continue;
                    bool aRetargeted = InRetargetFolder(a.Animation);
                    bool bRetargeted = InRetargetFolder(b.Animation);
                    bool keepA = aRetargeted == bRetargeted ? a.Unit <= b.Unit : bRetargeted;
                    var (kept, drop) = keepA ? (a, b) : (b, a);
                    result.Add(new BankDuplicate(kept.Animation, kept.Owner, drop.Animation, drop.Owner));
                    dropped.Add(drop.Animation);
                    if (!keepA) break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Links <paramref name="others"/> to <paramref name="reference"/>: each other character's
    /// ref pose takes the reference's joint rotations and scales (its own bone lengths stay; its
    /// animations are converted so they keep their motion), every bank is merged into one by folder
    /// path, the <paramref name="drop"/> duplicates (null: every one <see cref="FindDuplicates"/>
    /// reports) are left out, and every member gets a full copy. A character already in a group
    /// brings its whole group.
    /// </summary>
    public static SharedBankLinkResult Link(SceneModel model, Character reference, IReadOnlyList<Character> others,
        IReadOnlyCollection<BankDuplicate>? drop = null)
    {
        if (reference.AnimBank == null) throw new InvalidOperationException($"{reference.Name} has no Anim Bank.");
        var units = Representatives(reference, others);
        if (units.Count < 2 && others.All(o => GroupOf(reference).Contains(o)))
            return new SharedBankLinkResult(GroupOf(reference), [], 0, 0);
        drop ??= FindDuplicates(reference, others);
        var dropSet = new HashSet<AktObject>(drop.Select(d => d.Dropped.Source), ReferenceEqualityComparer.Instance);

        var groups = ReadGroups(model);
        var members = new List<Character>();
        void AddMember(Character c)
        {
            if (!members.Contains(c)) members.Add(c);
        }
        foreach (var unit in units)
        {
            AddMember(unit);
            foreach (var g in groups.Values)
                if (g.Contains(unit)) foreach (var c in g) AddMember(c);
        }
        foreach (var member in members) member.AnimBank?.ToString(); // members without a bank fail below

        // 1. Ref poses follow the reference (members already linked to it already do).
        var alreadyLinked = GroupOf(reference);
        int conformed = 0;
        foreach (var member in members)
            if (member != reference && !alreadyLinked.Contains(member))
                conformed += Conform(reference, member);

        // 2. Merge every other bank into the reference's; the merged copies are rows with their originals.
        var rows = ReadRows(model);
        var referenceBank = reference.AnimBank.Source;
        var emptied = new HashSet<AktObject>(ReferenceEqualityComparer.Instance);
        int merged = 0;
        foreach (var unit in units.Skip(1))
        {
            var bank = unit.AnimBank ?? throw new InvalidOperationException($"{unit.Name} has no Anim Bank.");
            var map = ObjectMap(unit, reference);
            merged += MergeFolder(model, bank.Source, referenceBank, unit, reference, map, rows, dropSet, emptied);
        }
        foreach (var animation in AnimationsOf(reference).Where(a => dropSet.Contains(a.Source)))
        {
            if (animation.Source.Parent is { } parent) emptied.Add(parent);
            animation.Source.Parent?.RemoveChild(animation.Source);
            if (reference.CurrentAnimation == animation) reference.CurrentAnimation = null;
        }
        RemoveEmptiedFolders(emptied, referenceBank);

        // 3. One group, then every member mirrors the merged bank.
        foreach (var key in groups.Keys.ToList())
            if (groups[key].Any(members.Contains)) groups.Remove(key);
        groups[groups.Count == 0 ? 0 : groups.Keys.Max() + 1] = members;
        WriteGroups(model, groups);
        WriteRows(model, rows);
        Sync(model, reference, dirty: null);
        return new SharedBankLinkResult(members, drop.ToList(), merged, conformed);
    }

    /// <summary>
    /// Removes <paramref name="character"/> from its group; it keeps its full copy of the bank. A
    /// group left with one member is dissolved. Returns whether it was linked.
    /// </summary>
    public static bool Unlink(SceneModel model, Character character)
    {
        var groups = ReadGroups(model);
        var key = groups.FirstOrDefault(g => g.Value.Contains(character)).Key;
        if (!groups.TryGetValue(key, out var group) || !group.Contains(character)) return false;
        group.Remove(character);
        if (group.Count < 2) groups.Remove(key);
        WriteGroups(model, groups);

        var stillLinked = groups.Values.SelectMany(g => g).ToHashSet();
        var rows = ReadRows(model);
        foreach (var row in rows.List)
            row.RemoveAll(item => OwnerOf(model, item) is not { } owner || !stillLinked.Contains(owner));
        WriteRows(model, rows);
        return true;
    }

    // ---- mirroring ---------------------------------------------------------------------------

    /// <summary>
    /// Mirrors <paramref name="source"/>'s bank into every other member of its group: folders and
    /// animations are created, moved, renamed and deleted to match, and the animations in
    /// <paramref name="dirty"/> (the source's <c>CAnimation</c>s whose content changed; null: none)
    /// are copied again. A copied animation's tracks drive the peer's controller of the same
    /// skeleton name; a track whose controller the peer lacks is left out. The peer's own objects
    /// (its current animation, references to its animations) keep their identity.
    /// </summary>
    /// <returns>The number of peers updated.</returns>
    public static int Sync(SceneModel model, Character source, IReadOnlySet<AktObject>? dirty)
    {
        var peers = PeersOf(source);
        if (peers.Count == 0 || source.AnimBank is not { } bank) return 0;
        var rows = ReadRows(model);
        foreach (var peer in peers)
        {
            var peerBank = peer.AnimBank?.Source ?? CreateBank(model, peer);
            var context = new MirrorContext(model, source, peer, rows, dirty, ObjectMap(source, peer));
            MirrorFolder(bank.Source, peerBank, context);
            if (peer.CurrentAnimation is { } current && !PluginSceneData.IsAttached(model, current.Source))
                peer.CurrentAnimation = null;
        }
        WriteRows(model, rows);
        return peers.Count;
    }

    /// <summary>
    /// Every <c>CAnimation</c> of <paramref name="character"/>'s bank in <paramref name="objects"/>
    /// or above one of them: the animations an edit of those objects changed.
    /// </summary>
    public static HashSet<AktObject> AnimationsAbove(Character character, IEnumerable<AktObject> objects)
    {
        var result = new HashSet<AktObject>(ReferenceEqualityComparer.Instance);
        var bank = character.AnimBank?.Source;
        if (bank == null) return result;
        foreach (var o in objects)
        {
            AktObject? animation = null;
            for (var p = o; p != null; p = p.Parent)
            {
                if (ReferenceEquals(p, bank)) { if (animation != null) result.Add(animation); break; }
                if (animation == null && character.Model.Wrap(p) is Animation) animation = p;
            }
        }
        return result;
    }

    /// <summary>The character whose Anim Bank holds <paramref name="obj"/>, or null.</summary>
    public static Character? BankOwnerOf(SceneModel model, AktObject obj) => OwnerOf(model, obj);

    /// <summary>
    /// For an edit of <paramref name="objects"/>: every linked character whose Anim Bank holds one
    /// of them (the bank itself included), with the animations of that bank the edit changed. An
    /// entry with no animations is a structural edit (a folder or the bank's own list).
    /// </summary>
    public static IReadOnlyList<(Character Owner, HashSet<AktObject> Animations)> EditedBanks(
        SceneModel model, IEnumerable<AktObject> objects)
    {
        var banks = new Dictionary<AktObject, Character>(ReferenceEqualityComparer.Instance);
        foreach (var group in Groups(model))
            foreach (var member in group)
                if (member.AnimBank is { } b) banks[b.Source] = member;
        var result = new List<(Character, HashSet<AktObject>)>();
        if (banks.Count == 0) return result;
        var sets = new Dictionary<AktObject, HashSet<AktObject>>(ReferenceEqualityComparer.Instance);
        // What each visited object resolves to: the bank above it and the animation at or above it.
        var cache = new Dictionary<AktObject, (AktObject? Bank, AktObject? Animation)>(ReferenceEqualityComparer.Instance);
        var chain = new List<AktObject>();
        foreach (var o in objects)
        {
            chain.Clear();
            (AktObject? Bank, AktObject? Animation) found = (null, null);
            for (var p = o; p != null; p = p.Parent)
            {
                if (cache.TryGetValue(p, out found)) break;
                if (banks.ContainsKey(p)) { found = (p, null); cache[p] = found; break; }
                chain.Add(p);
            }
            // Walk back down: the nearest animation on the way wins.
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                if (found.Bank != null && found.Animation == null && model.Wrap(chain[i]) is Animation)
                    found = (found.Bank, chain[i]);
                cache[chain[i]] = found;
            }
            if (found.Bank == null) continue;
            if (!sets.TryGetValue(found.Bank, out var set))
            {
                set = new HashSet<AktObject>(ReferenceEqualityComparer.Instance);
                sets[found.Bank] = set;
                result.Add((banks[found.Bank], set));
            }
            if (found.Animation != null) set.Add(found.Animation);
        }
        return result;
    }

    // ---- ref poses ---------------------------------------------------------------------------

    /// <summary>
    /// Makes <paramref name="peer"/>'s ref pose take <paramref name="reference"/>'s local rotation
    /// and scale on every matched controller, keeping its own translations (bone lengths), and
    /// converts the peer's animations so they keep their motion. Returns the controllers changed.
    /// </summary>
    public static int Conform(Character reference, Character peer)
    {
        if (reference.RefPose is not { } wanted || peer.RefPose == null) return 0;
        var map = RetargetingMap.Build(reference, peer);
        var from = new RestPoseEditor(reference);
        return EditPeerRefPose(peer, to =>
        {
            int changed = 0;
            foreach (var pair in map.Pairs)
            {
                int slot = from.SlotOf(pair.Source);
                if (slot < 0 || slot >= wanted.Count || to.SlotOf(pair.Target) < 0 || !to.CanEdit(pair.Target, out _)) continue;
                var want = Transform.FromMatrixLoose(wanted.Matrix(slot));
                var have = to.GetLocal(pair.Target);
                if (RotationDegrees(want.Rotation, have.Rotation) <= ConformTolerance
                    && Vector3.Distance(want.Scale, have.Scale) <= ConformTolerance) continue;
                to.SetLocal(pair.Target, have with { Rotation = want.Rotation, Scale = want.Scale });
                changed++;
            }
            if (changed > 0) to.Resolve(RestPoseResolution.ConvertAnimations);
            return changed;
        });
    }

    /// <summary>
    /// Runs <paramref name="edit"/> with <paramref name="peer"/>'s ref pose as the pose a
    /// <see cref="RestPoseEditor"/> edits (the rigging context may be showing a bind pose), then
    /// puts the shown pose back unless it was the ref pose itself.
    /// </summary>
    private static int EditPeerRefPose(Character peer, Func<RestPoseEditor, int> edit)
    {
        var shown = peer.CurrentPose;
        bool showedRefPose = ReferenceEquals(shown, peer.RefPose);
        peer.CurrentPose = peer.RefPose;
        try
        {
            return edit(new RestPoseEditor(peer));
        }
        finally
        {
            if (!showedRefPose) peer.CurrentPose = shown;
        }
    }

    /// <summary>
    /// After <paramref name="source"/>'s ref pose was edited and before it is resolved: gives every
    /// peer the same change on its matched controllers - the new rotation and scale, and the
    /// translation moved by the same amount - and accepts it without converting the peer's keys
    /// (they come from the source's bank). Returns the controllers changed over all peers.
    /// </summary>
    public static int FollowRefPose(Character source, IEnumerable<Character> peers)
    {
        var editor = new RestPoseEditor(source);
        if (source.RefPose is not { } current || source.RefPoseOld is not { } old) return 0;
        var slots = editor.ChangedSlots;
        int changed = 0;
        foreach (var peer in peers)
        {
            if (peer.RefPose == null) continue;
            var map = RetargetingMap.Build(source, peer);
            changed += EditPeerRefPose(peer, to =>
            {
                to.EnsureEditableRefPose();
                var locals = new Dictionary<Object3D, Transform>(ReferenceEqualityComparer.Instance);
                foreach (int slot in slots)
                {
                    if (slot >= editor.Controllers.Count || map.TargetOf(editor.Controllers[slot]) is not { } target) continue;
                    // Joints driven by an IK chain (every limb) are written too; IK controllers and
                    // reverse feet are not copied but carried with their chain by SetLocalsCarryingIK.
                    if (to.SlotOf(target) < 0 || (target is not Joint && !to.CanEdit(target, out _))) continue;
                    var before = Transform.FromMatrixLoose(old.Matrix(slot));
                    var after = Transform.FromMatrixLoose(current.Matrix(slot));
                    var have = to.GetLocal(target);
                    locals[target] = new Transform(have.Translation + after.Translation - before.Translation, after.Rotation, after.Scale);
                }
                int mine = to.SetLocalsCarryingIK(locals);
                if (mine > 0) to.Resolve(RestPoseResolution.KeepAnimations);
                return mine;
            });
        }
        return changed;
    }

    // ---- paths and comparison ----------------------------------------------------------------

    /// <summary>The animation's full path in its Anim Bank, e.g. <c>Common/Locomotion/Run</c>.</summary>
    public static string PathOf(SceneObject item)
    {
        var names = new List<string>();
        for (var o = item.Source; o != null && o.Name != "__Anims"; o = o.Parent) names.Add(o.Name ?? "");
        names.Reverse();
        return string.Join("/", names);
    }

    private static bool InRetargetFolder(Animation animation)
    {
        for (var o = animation.Source.Parent; o != null && o.Name != "__Anims"; o = o.Parent)
            if (o.Name?.StartsWith(RetargetFolderPrefix, StringComparison.OrdinalIgnoreCase) == true) return true;
        return false;
    }

    // Stack count, key times and track counts: equal for any two animations SameMotion accepts.
    private static string CheapSignature(Animation animation) =>
        string.Join(";", animation.Stacks.Select(s =>
            string.Join(",", s.Keys.Select(k => k.Time)) + "#" + s.Tracks.Count(t => t.Target != null)));

    /// <summary>
    /// Same stacks (count, key times), same tracks by the skeleton name of their target, same key
    /// counts and transform keys within <see cref="DuplicateRotationDegrees"/> and
    /// <see cref="DuplicateValueTolerance"/>.
    /// </summary>
    internal static bool SameMotion(Animation a, Character aOwner, Animation b, Character bOwner)
    {
        var aStacks = a.Stacks;
        var bStacks = b.Stacks;
        if (aStacks.Count != bStacks.Count) return false;
        for (int s = 0; s < aStacks.Count; s++)
        {
            if (!aStacks[s].Keys.Select(k => k.Time).SequenceEqual(bStacks[s].Keys.Select(k => k.Time))) return false;
            var aTracks = TracksByName(aStacks[s], aOwner);
            var bTracks = TracksByName(bStacks[s], bOwner);
            if (aTracks.Count != bTracks.Count) return false;
            foreach (var (name, aTrack) in aTracks)
            {
                if (!bTracks.TryGetValue(name, out var bTrack)) return false;
                var aKeys = aTrack.Keys;
                var bKeys = bTrack.Keys;
                if (aKeys.Count != bKeys.Count) return false;
                for (int k = 0; k < aKeys.Count; k++)
                {
                    if (aKeys[k] is TransformKey ak && bKeys[k] is TransformKey bk)
                    {
                        var x = ak.Value;
                        var y = bk.Value;
                        if (Vector3.Distance(x.Translation, y.Translation) > DuplicateValueTolerance
                            || Vector3.Distance(x.Scale, y.Scale) > DuplicateValueTolerance
                            || RotationDegrees(x.Rotation, y.Rotation) > DuplicateRotationDegrees) return false;
                    }
                    else if ((aKeys[k] == null) != (bKeys[k] == null) || aKeys[k]?.KeyType != bKeys[k]?.KeyType) return false;
                }
            }
        }
        return true;
    }

    private static Dictionary<string, AnimTrack> TracksByName(AnimStack stack, Character owner)
    {
        var result = new Dictionary<string, AnimTrack>(StringComparer.Ordinal);
        foreach (var track in stack.Tracks)
            if (track.Target is { } target)
                result.TryAdd(track.Source.ClassKey + "|" + RetargetNames.SkeletonName(owner, target), track);
        return result;
    }

    private static float RotationDegrees(Quaternion a, Quaternion b)
    {
        // atan2 of the relative rotation's vector and scalar parts: an acos of the dot product
        // rounds hundredths of a degree to zero in single precision.
        var d = Quaternion.Normalize(Quaternion.Conjugate(Quaternion.Normalize(a)) * Quaternion.Normalize(b));
        double vector = Math.Sqrt((double)d.X * d.X + (double)d.Y * d.Y + (double)d.Z * d.Z);
        return (float)(2 * Math.Atan2(vector, Math.Abs((double)d.W)) * 180 / Math.PI);
    }

    // ---- merge helpers -----------------------------------------------------------------------

    /// <summary>The reference, then one member per other group (or lone character) among <paramref name="others"/>.</summary>
    private static List<Character> Representatives(Character reference, IEnumerable<Character> others)
    {
        var units = new List<Character> { reference };
        var covered = new HashSet<Character>(GroupOf(reference)) { reference };
        foreach (var other in others)
        {
            if (covered.Contains(other)) continue;
            units.Add(other);
            covered.Add(other);
            foreach (var c in GroupOf(other)) covered.Add(c);
        }
        return units;
    }

    private static IEnumerable<Animation> AnimationsOf(Character character) =>
        character.AnimBank is { } bank
            ? bank.Source.PreOrder().Skip(1).Select(character.Model.Wrap).OfType<Animation>().ToList()
            : [];

    private static int MergeFolder(SceneModel model, AktObject from, AktObject into, Character unit, Character reference,
        Dictionary<AktObject, AktObject> map, Rows rows, HashSet<AktObject> dropSet, HashSet<AktObject> emptied)
    {
        int merged = 0;
        foreach (var child in from.Children.ToList())
        {
            var wrapped = model.Wrap(child);
            if (wrapped is Folder)
            {
                var target = into.Children.FirstOrDefault(c => model.Wrap(c) is Folder && c.Name == child.Name);
                if (target == null)
                {
                    target = CopyFolder(child);
                    into.AddChild(target);
                }
                rows.Pair(child, target);
                merged += MergeFolder(model, child, target, unit, reference, map, rows, dropSet, emptied);
            }
            else if (wrapped is Animation)
            {
                if (dropSet.Contains(child))
                {
                    emptied.Add(into);
                    continue;
                }
                var copy = CloneAnimation(model, child, unit, map, rows, reference, into: null);
                copy.Set("Name", UniqueNames.For(into, child.Name ?? "New animation"));
                into.AddChild(copy);
                rows.Pair(child, copy);
                merged++;
            }
        }
        return merged;
    }

    /// <summary>Removes the folders in <paramref name="emptied"/> that are now empty, then their emptied parents.</summary>
    private static void RemoveEmptiedFolders(HashSet<AktObject> emptied, AktObject bank)
    {
        foreach (var folder in emptied)
        {
            var f = folder;
            while (f != null && !ReferenceEquals(f, bank) && f.Children.Count == 0 && f.Parent is { } parent)
            {
                parent.RemoveChild(f);
                f = parent;
            }
        }
    }

    // ---- mirror helpers ----------------------------------------------------------------------

    private sealed class MirrorContext(SceneModel model, Character source, Character peer, Rows rows,
        IReadOnlySet<AktObject>? dirty, Dictionary<AktObject, AktObject> map)
    {
        public SceneModel Model { get; } = model;
        public Character Source { get; } = source;
        public Character Peer { get; } = peer;
        public Rows Rows { get; } = rows;
        public IReadOnlySet<AktObject>? Dirty { get; } = dirty;
        public Dictionary<AktObject, AktObject> Map { get; } = map;
    }

    private static void MirrorFolder(AktObject sourceFolder, AktObject peerFolder, MirrorContext context)
    {
        var model = context.Model;
        var desired = new List<AktObject>();
        foreach (var child in sourceFolder.Children)
        {
            var wrapped = model.Wrap(child);
            bool isFolder = wrapped is Folder;
            if (!isFolder && wrapped is not Animation) continue;
            var counterpart = context.Rows.CounterpartIn(model, child, context.Peer);
            if (counterpart == null)
            {
                counterpart = isFolder
                    ? CopyFolder(child)
                    : CloneAnimation(model, child, context.Source, context.Map, context.Rows, context.Peer, into: null);
                context.Rows.Pair(child, counterpart);
            }
            else if (!isFolder && context.Dirty?.Contains(child) == true)
                CloneAnimation(model, child, context.Source, context.Map, context.Rows, context.Peer, into: counterpart);
            if (counterpart.Name != child.Name) counterpart.Set("Name", child.Name);
            if (counterpart.Parent is { } oldParent && !ReferenceEquals(oldParent, peerFolder)) oldParent.RemoveChild(counterpart);
            desired.Add(counterpart);
            if (isFolder) MirrorFolder(child, counterpart, context);
        }
        var keep = peerFolder.Children.Where(c => model.Wrap(c) is not (Folder or Animation)).ToList();
        if (peerFolder.Children.Count == keep.Count + desired.Count
            && peerFolder.Children.Skip(keep.Count).SequenceEqual(desired, ReferenceEqualityComparer.Instance)) return;
        peerFolder.SetChildren(keep.Concat(desired));
    }

    private static AktObject CopyFolder(AktObject folder)
    {
        var (layout, values) = folder.CopyFields(deep: true, shareLargeArrays: false);
        return new AktObject(folder.TypeName, layout, values);
    }

    private static AktObject CreateBank(SceneModel model, Character character)
    {
        var bank = model.Document.Schema.NewObject("CDummy");
        bank.Set("Name", "__Anims");
        character.Source.AddChild(bank);
        return bank;
    }

    /// <summary>
    /// A copy of <paramref name="animation"/> (of <paramref name="from"/>'s bank) for
    /// <paramref name="to"/>: references inside it point at the copy, references to
    /// <paramref name="from"/>'s objects at <paramref name="to"/>'s counterparts (null when there is
    /// none), references to other bank items at their row counterpart. Tracks left without a target
    /// are removed. With <paramref name="into"/>, the copy's fields and children replace that
    /// object's, which is returned (its identity is kept).
    /// </summary>
    private static AktObject CloneAnimation(SceneModel model, AktObject animation, Character from,
        Dictionary<AktObject, AktObject> map, Rows rows, Character to, AktObject? into)
    {
        var copy = animation.CloneHierarchy();
        var inside = new HashSet<AktObject>(copy.PreOrder(), ReferenceEqualityComparer.Instance);
        var fromObjects = FromObjects(from);
        var targeted = copy.PreOrder().Where(o => model.Wrap(o) is AnimTrack t && t.Target != null).ToList();
        AktObject? Lookup(AktObject target)
        {
            if (inside.Contains(target)) return ReferenceEquals(target, copy) && into != null ? into : target;
            if (map.TryGetValue(target, out var mapped)) return mapped;
            if (rows.RowOf(target) >= 0) return rows.CounterpartIn(model, target, to);
            return fromObjects.Contains(target) ? null : target;
        }
        foreach (var o in copy.PreOrder())
            for (int i = 0; i < o.FieldCount; i++) AktRefs.Remap(o.ValueAt(i), Lookup);

        // Tracks whose controller the peer lacks.
        var removed = new HashSet<AktObject>(ReferenceEqualityComparer.Instance);
        foreach (var track in targeted)
            if (track["Target"] is AktReference { Target: null } && track.Parent is { } stack)
            {
                stack.RemoveChild(track);
                removed.Add(track);
            }
        if (removed.Count > 0)
            foreach (var o in copy.PreOrder())
                for (int i = 0; i < o.FieldCount; i++)
                    AktRefs.Remap(o.ValueAt(i), t => removed.Contains(t) ? null : t);

        if (into == null) return copy;
        var (layout, values) = copy.CopyFields(deep: false, shareLargeArrays: true);
        into.ResetFields(layout, values);
        into.SetChildren(copy.Children.ToList());
        return into;
    }

    // The source character's own objects outside its bank (what a copy must not keep pointing at).
    private static HashSet<AktObject> FromObjects(Character from)
    {
        var bank = from.AnimBank?.Source;
        return new HashSet<AktObject>(OutsideBank(from.Source, bank), ReferenceEqualityComparer.Instance);
    }

    private static IEnumerable<AktObject> OutsideBank(AktObject root, AktObject? bank)
    {
        var stack = new Stack<AktObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            if (ReferenceEquals(o, bank)) continue;
            yield return o;
            for (int i = o.Children.Count - 1; i >= 0; i--) stack.Push(o.Children[i]);
        }
    }

    /// <summary>
    /// <paramref name="from"/>'s objects (outside its bank) to <paramref name="to"/>'s: the
    /// character itself, matched controllers, then any object of the same class and skeleton name.
    /// </summary>
    private static Dictionary<AktObject, AktObject> ObjectMap(Character from, Character to)
    {
        var map = new Dictionary<AktObject, AktObject>(ReferenceEqualityComparer.Instance) { [from.Source] = to.Source };
        if (from != to)
            foreach (var pair in RetargetingMap.Build(from, to).Pairs) map[pair.Source.Source] = pair.Target.Source;
        var index = new Dictionary<string, AktObject>(StringComparer.Ordinal);
        foreach (var o in OutsideBank(to.Source, to.AnimBank?.Source)) index.TryAdd(KeyOf(to, o), o);
        foreach (var o in OutsideBank(from.Source, from.AnimBank?.Source))
            if (!map.ContainsKey(o) && index.TryGetValue(KeyOf(from, o), out var target)) map[o] = target;
        return map;
    }

    private static string KeyOf(Character owner, AktObject o) =>
        o.ClassKey + "|" + (owner.Model.Wrap(o) is Object3D obj ? RetargetNames.SkeletonName(owner, obj) : o.Name ?? "");

    private static Character? OwnerOf(SceneModel model, AktObject item)
    {
        for (var o = item; o != null; o = o.Parent)
            if (o.Name == "__Anims" && o.Parent is { } owner && model.Wrap(owner) is Character character)
                return character;
        return null;
    }

    // ---- storage -----------------------------------------------------------------------------

    private static Dictionary<int, List<Character>> ReadGroups(SceneModel model)
    {
        var scene = model.Scene.Source;
        var characters = Values(scene[MaterialSchema.SharedBankCharactersMember]);
        var numbers = Values(scene[MaterialSchema.SharedBankGroupsMember]);
        var groups = new Dictionary<int, List<Character>>();
        for (int i = 0; i < characters.Count && i < numbers.Count; i++)
        {
            if (characters[i] is not AktReference { Target: { } target } || numbers[i] is not int number) continue;
            if (!PluginSceneData.IsAttached(model, target) || model.Wrap(target) is not Character character) continue;
            if (!groups.TryGetValue(number, out var group)) groups[number] = group = [];
            if (!group.Contains(character)) group.Add(character);
        }
        return groups;
    }

    private static void WriteGroups(SceneModel model, Dictionary<int, List<Character>> groups)
    {
        EnsureMembers(model);
        var refs = new List<AktReference>();
        var numbers = new List<object?>();
        int n = 0;
        foreach (var group in groups.Values.Where(g => g.Count >= 2))
        {
            foreach (var c in group)
            {
                refs.Add(new AktReference(c.Source));
                numbers.Add(n);
            }
            n++;
        }
        var scene = model.Scene.Source;
        scene.Set(MaterialSchema.SharedBankCharactersMember, refs);
        scene.Set(MaterialSchema.SharedBankGroupsMember, numbers);
    }

    /// <summary>The item rows: each row is one folder or animation and its copies.</summary>
    private sealed class Rows
    {
        public List<List<AktObject>> List { get; } = [];
        private readonly Dictionary<AktObject, int> _rowOf = new(ReferenceEqualityComparer.Instance);

        public int RowOf(AktObject item) => _rowOf.TryGetValue(item, out int row) ? row : -1;

        public void Add(int row, AktObject item)
        {
            while (List.Count <= row) List.Add([]);
            if (_rowOf.ContainsKey(item)) return;
            List[row].Add(item);
            _rowOf[item] = row;
        }

        /// <summary>Puts <paramref name="copy"/> in <paramref name="original"/>'s row (a new row when it has none).</summary>
        public void Pair(AktObject original, AktObject copy)
        {
            int row = RowOf(original);
            if (row < 0)
            {
                row = List.Count;
                Add(row, original);
            }
            Add(row, copy);
        }

        public AktObject? CounterpartIn(SceneModel model, AktObject item, Character character)
        {
            int row = RowOf(item);
            if (row < 0) return null;
            foreach (var other in List[row])
                if (!ReferenceEquals(other, item) && PluginSceneData.IsAttached(model, other) && OwnerOf(model, other) == character)
                    return other;
            return null;
        }
    }

    private static Rows ReadRows(SceneModel model)
    {
        var scene = model.Scene.Source;
        var items = Values(scene[MaterialSchema.SharedBankItemsMember]);
        var numbers = Values(scene[MaterialSchema.SharedBankItemRowsMember]);
        var rows = new Rows();
        for (int i = 0; i < items.Count && i < numbers.Count; i++)
            if (items[i] is AktReference { Target: { } target } && numbers[i] is int row && row >= 0)
                rows.Add(row, target);
        return rows;
    }

    private static void WriteRows(SceneModel model, Rows rows)
    {
        EnsureMembers(model);
        var refs = new List<AktReference>();
        var numbers = new List<object?>();
        int n = 0;
        foreach (var row in rows.List)
        {
            var live = row.Where(o => PluginSceneData.IsAttached(model, o)).ToList();
            if (live.Count < 2) continue;
            foreach (var o in live)
            {
                refs.Add(new AktReference(o));
                numbers.Add(n);
            }
            n++;
        }
        var scene = model.Scene.Source;
        scene.Set(MaterialSchema.SharedBankItemsMember, refs);
        scene.Set(MaterialSchema.SharedBankItemRowsMember, numbers);
    }

    private static void EnsureMembers(SceneModel model) =>
        MaterialSchema.EnsureFields(model.Document.Schema, model.Document.Roots);

    private static List<object?> Values(object? value) => value switch
    {
        List<AktReference> refs => refs.Cast<object?>().ToList(),
        IEnumerable<object?> list => list.ToList(),
        _ => [],
    };
}
