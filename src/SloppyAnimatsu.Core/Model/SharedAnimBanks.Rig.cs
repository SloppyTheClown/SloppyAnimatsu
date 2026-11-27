using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>One joint or IK controller of a rig, as it was when a rigging visit started.</summary>
/// <param name="Source">The scene object, compared by reference.</param>
/// <param name="Name">Its full name.</param>
/// <param name="SkeletonName">Its name within the skeleton (see <see cref="RetargetNames.SkeletonName"/>).</param>
/// <param name="Parent">Its parent object.</param>
/// <param name="IsJoint">A joint; otherwise an IK or reverse-foot controller.</param>
public sealed record RigOutlineEntry(AktObject Source, string Name, string SkeletonName, AktObject? Parent, bool IsJoint);

/// <summary>A character's joints and IK controllers at one moment, to compare the rig against later.</summary>
public sealed class RigOutline
{
    internal RigOutline(AktObject character, IReadOnlyList<RigOutlineEntry> entries)
    {
        Character = character;
        Entries = entries;
    }

    /// <summary>The character's scene object.</summary>
    public AktObject Character { get; }

    public IReadOnlyList<RigOutlineEntry> Entries { get; }
}

/// <summary>What changed in a rig since a <see cref="RigOutline"/> was taken.</summary>
public sealed class RigChangeSet
{
    internal RigChangeSet(IReadOnlyList<(RigOutlineEntry Before, Object3D Now)> renamed,
        IReadOnlyList<RigOutlineEntry> deleted, IReadOnlyList<Joint> reparented, IReadOnlyList<Object3D> added)
    {
        Renamed = renamed;
        Deleted = deleted;
        Reparented = reparented;
        Added = added;
    }

    public IReadOnlyList<(RigOutlineEntry Before, Object3D Now)> Renamed { get; }
    public IReadOnlyList<RigOutlineEntry> Deleted { get; }
    public IReadOnlyList<Joint> Reparented { get; }

    /// <summary>New joints (parents before children) and new IK or reverse-foot controllers.</summary>
    public IReadOnlyList<Object3D> Added { get; }

    public int Count => Renamed.Count + Deleted.Count + Reparented.Count + Added.Count;
    public bool IsEmpty => Count == 0;

    /// <summary>A short list for a prompt, e.g. "2 joint(s) added, 1 IK added, 1 renamed".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        int joints = Added.Count(o => o is Joint), iks = Added.Count - joints;
        if (joints > 0) parts.Add($"{joints} joint(s) added");
        if (iks > 0) parts.Add($"{iks} IK/reverse foot added");
        if (Deleted.Count > 0) parts.Add($"{Deleted.Count} deleted");
        if (Renamed.Count > 0) parts.Add($"{Renamed.Count} renamed");
        if (Reparented.Count > 0) parts.Add($"{Reparented.Count} reparented");
        return string.Join(", ", parts);
    }
}

/// <summary>What copying a rig change gave: the changes made over all peers, and what could not be matched.</summary>
public sealed record RigCopyResult(int Applied, IReadOnlyList<string> Skipped);

public static partial class SharedAnimBanks
{
    /// <summary>The character's joints and IK/reverse-foot controllers (shoe locators come with their foot).</summary>
    public static RigOutline CaptureRig(Character character) =>
        new(character.Source, RigObjects(character)
            .Select(o => new RigOutlineEntry(o.Source, o.Name ?? "", RetargetNames.SkeletonName(character, o),
                o.Source.Parent, o is Joint))
            .ToList());

    private static IEnumerable<Object3D> RigObjects(Character character) =>
        character.Controllers.Where(o => o is Joint or ControllerIK);

    /// <summary>The joints and IK controllers added, deleted, renamed or reparented since <paramref name="before"/>.</summary>
    public static RigChangeSet RigChanges(RigOutline before, Character now)
    {
        var old = new Dictionary<AktObject, RigOutlineEntry>(ReferenceEqualityComparer.Instance);
        foreach (var entry in before.Entries) old[entry.Source] = entry;
        var current = RigObjects(now).ToList();
        var present = new HashSet<AktObject>(current.Select(o => o.Source), ReferenceEqualityComparer.Instance);

        var renamed = new List<(RigOutlineEntry, Object3D)>();
        var reparented = new List<Joint>();
        var added = new List<Object3D>();
        foreach (var o in current)
        {
            if (!old.TryGetValue(o.Source, out var was))
            {
                added.Add(o);
                continue;
            }
            if (!string.Equals(was.Name, o.Name ?? "", StringComparison.Ordinal)) renamed.Add((was, o));
            if (o is Joint joint && !ReferenceEquals(was.Parent, o.Source.Parent)) reparented.Add(joint);
        }
        // Joints first, parents before children, so each new joint finds its parent on the peer.
        added = added.OrderBy(o => o is Joint ? 0 : 1).ThenBy(Depth).ToList();
        var deleted = before.Entries.Where(e => !present.Contains(e.Source)).ToList();
        return new RigChangeSet(renamed, deleted, reparented, added);
    }

    private static int Depth(Object3D o)
    {
        int depth = 0;
        for (var p = o.Source.Parent; p != null; p = p.Parent) depth++;
        return depth;
    }

    /// <summary>
    /// Replays <paramref name="changes"/>, made on <paramref name="source"/>, on every peer, matching
    /// objects by skeleton name: renames first, then deletions (IK before joints), reparenting, new
    /// joints (the source's local in its rig pose, the offset scaled by the parent bone's length
    /// ratio, named with the peer's prefix) and new IK and reverse-foot controllers on the matched
    /// chain. Anything without a match on a peer is skipped and listed.
    /// </summary>
    public static RigCopyResult CopyRigChanges(Character source, RigChangeSet changes, IEnumerable<Character> peers)
    {
        int applied = 0;
        var skipped = new List<string>();
        var sourceRig = new RigEditor(source);
        var sourcePose = sourceRig.RigPose;
        foreach (var peer in peers)
        {
            var rig = new RigEditor(peer);
            var pose = rig.RigPose;
            Object3D? Find(string skeleton) => RigObjects(peer).FirstOrDefault(o =>
                string.Equals(RetargetNames.SkeletonName(peer, o), skeleton, StringComparison.OrdinalIgnoreCase));
            // The peer's counterpart of a source parent: the peer itself for the character.
            Object3D? PeerParent(AktObject? parent)
            {
                if (parent == null || ReferenceEquals(parent, source.Source)) return peer;
                return source.Model.Wrap(parent) is Object3D o ? Find(RetargetNames.SkeletonName(source, o)) : null;
            }
            void Skip(string what) => skipped.Add($"{peer.Name}: {what}");
            void Try(string what, Func<bool> edit)
            {
                try
                {
                    if (edit()) applied++;
                    else Skip(what);
                }
                catch (InvalidOperationException ex)
                {
                    Skip($"{what} ({ex.Message})");
                }
            }

            foreach (var (before, now) in changes.Renamed)
            {
                string newSkeleton = RetargetNames.SkeletonName(source, now);
                Try($"rename {before.SkeletonName}", () =>
                {
                    if (Find(before.SkeletonName) is not { } target || Find(newSkeleton) != null) return false;
                    target.Name = Renamed(target.Name ?? "", before.SkeletonName, newSkeleton);
                    return true;
                });
            }

            foreach (var entry in changes.Deleted.OrderBy(e => e.IsJoint ? 1 : 0))
            {
                if (Find(entry.SkeletonName) is not { } target)
                {
                    // A deleted joint takes its IK with it, on the source as on the peer.
                    if (entry.IsJoint) Skip($"delete {entry.SkeletonName}");
                    continue;
                }
                Try($"delete {entry.SkeletonName}", () => target is Joint joint
                    ? rig.DeleteJoints([joint]) > 0
                    : rig.DeleteControllers([target]) > 0);
            }

            foreach (var joint in changes.Reparented)
            {
                string skeleton = RetargetNames.SkeletonName(source, joint);
                Try($"reparent {skeleton}", () =>
                {
                    if (Find(skeleton) is not Joint target || PeerParent(joint.Source.Parent) is not { } parent) return false;
                    if (ReferenceEquals(target.Source.Parent, parent.Source)) return true;
                    return rig.Reparent([target], parent) > 0;
                });
            }

            foreach (var o in changes.Added)
            {
                string skeleton = RetargetNames.SkeletonName(source, o);
                if (o is Joint joint)
                {
                    Try($"add joint {skeleton}", () =>
                    {
                        if (Find(skeleton) != null || PeerParent(joint.Source.Parent) is not { } parent) return false;
                        var local = sourceRig.LocalInPose(sourcePose, joint);
                        local.Translation *= BoneRatio(sourceRig, sourcePose, joint.Source.Parent, rig, pose, parent);
                        rig.CreateJoint(parent, PeerName(peer, parent, skeleton), local);
                        return true;
                    });
                }
                else if (o is ControllerIK ik && ik.Chain.Count > 0)
                {
                    string upperName = RetargetNames.SkeletonName(source, ik.Chain[0]);
                    string lowerName = RetargetNames.SkeletonName(source, ik.Chain[^1]);
                    Try($"add {skeleton}", () =>
                    {
                        if (Find(skeleton) != null) return false;
                        if (Find(upperName) is not Joint upper || Find(lowerName) is not Joint lower) return false;
                        // The default name follows the peer's lower joint; a renamed one keeps its name.
                        string prefix = ik is ControllerReverseFoot ? "RF_" : "IK_";
                        string? name = string.Equals(ik.Name, prefix + ik.Chain[^1].Name, StringComparison.Ordinal)
                            ? null : PeerName(peer, lower, skeleton);
                        if (ik is ControllerReverseFoot) rig.AddReverseFoot(upper, lower, name);
                        else rig.AddIK(upper, lower, name);
                        return true;
                    });
                }
            }
        }
        return new RigCopyResult(applied, skipped);
    }

    /// <summary><paramref name="name"/> with its skeleton part <paramref name="from"/> replaced by <paramref name="to"/>.</summary>
    private static string Renamed(string name, string from, string to) =>
        name.EndsWith(from, StringComparison.OrdinalIgnoreCase) ? name[..^from.Length] + to : to;

    /// <summary>
    /// A new object's name on the peer: <paramref name="skeleton"/> with the prefix the peer's own
    /// names carry (read from <paramref name="like"/>, else from its first joint).
    /// </summary>
    private static string PeerName(Character peer, Object3D? like, string skeleton)
    {
        if (like is null or Character) like = peer.Joints.FirstOrDefault();
        if (like?.Name is { } name)
        {
            string tail = RetargetNames.SkeletonName(peer, like);
            if (tail.Length > 0 && name.Length > tail.Length && name.EndsWith(tail, StringComparison.Ordinal))
                return name[..^tail.Length] + skeleton;
        }
        return skeleton;
    }

    /// <summary>
    /// How much longer the peer's parent bone is than the source's (their local offsets in the rig
    /// poses), so a new joint lands at the same place along a differently sized limb. 1 when either
    /// parent has no offset to compare (a root joint, the character).
    /// </summary>
    private static float BoneRatio(RigEditor sourceRig, CharacterPose? sourcePose, AktObject? sourceParent,
        RigEditor peerRig, CharacterPose? peerPose, Object3D peerParent)
    {
        // Below this length (scene units) a parent offset is treated as zero.
        const float minimumLength = 1e-4f;
        if (sourceParent == null || peerParent is Character) return 1f;
        if (sourceRig.Character.Model.Wrap(sourceParent) is not Object3D parent || parent is Character) return 1f;
        float from = sourceRig.LocalInPose(sourcePose, parent).Translation.Length();
        float to = peerRig.LocalInPose(peerPose, peerParent).Translation.Length();
        return from > minimumLength && to > minimumLength ? to / from : 1f;
    }
}
