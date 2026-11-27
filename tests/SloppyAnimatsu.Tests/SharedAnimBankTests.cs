using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Shared Anim Banks: linking characters with the same skeleton, mirroring edits between their
/// banks, unlinking, and keeping the link in the app's own files only. Each test pairs a sample
/// scene's character with a second import of the same scene, so both have the same skeleton and
/// the same bank.
/// </summary>
public sealed class SharedAnimBankTests : IDisposable
{
    private readonly List<string> _temporaryFiles = [];

    public void Dispose()
    {
        foreach (string path in _temporaryFiles)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Identical_skeletons_are_eligible_and_their_banks_are_all_duplicates()
    {
        if (Pair() is not var (_, first, second)) return;
        var eligibility = SharedAnimBanks.Eligibility(first, second);
        Assert.True(eligibility.Eligible, eligibility.Summary);

        var duplicates = SharedAnimBanks.FindDuplicates(first, [second]);
        var missed = first.Animations.Select(a => SharedAnimBanks.PathOf(a))
            .Except(duplicates.Select(d => d.KeptPath)).ToList();
        Assert.True(first.Animations.Count() == duplicates.Count,
            $"{duplicates.Count} duplicates of {first.Animations.Count()}; not kept: {string.Join(" | ", missed.Take(8))}");
        Assert.All(duplicates, d =>
        {
            Assert.Same(first.Source, d.KeptOwner.Source);
            Assert.Same(second.Source, d.DroppedOwner.Source);
            Assert.Equal(d.KeptPath, d.DroppedPath);
        });
    }

    [Fact]
    public void A_character_with_a_different_skeleton_is_not_eligible()
    {
        if (Pair() is not var (_, first, second)) return;
        var joint = second.Descendants<Joint>().Last();
        joint.Name = "NoSuchJointAnywhere";

        var eligibility = SharedAnimBanks.Eligibility(first, second);

        Assert.False(eligibility.Eligible);
        Assert.Contains("NoSuchJointAnywhere", eligibility.UnmatchedOther);
        Assert.False(string.IsNullOrWhiteSpace(eligibility.Summary));
    }

    [Fact]
    public void Linking_merges_the_banks_drops_duplicates_and_retargets_each_copy()
    {
        if (Pair() is not var (model, first, second)) return;
        int count = first.Animations.Count();

        var result = SharedAnimBanks.Link(model, first, [second]);

        Assert.Equal(2, result.Members.Count);
        Assert.Equal(count, result.Dropped.Count);
        Assert.Single(SharedAnimBanks.Groups(model));
        Assert.True(SharedAnimBanks.IsLinked(first));
        Assert.True(SharedAnimBanks.IsLinked(second));
        Assert.Equal(Paths(first), Paths(second));
        Assert.Equal(count, first.Animations.Count());

        // Every copy drives its own character's controllers.
        var firstControllers = first.Descendants<Object3D>().Select(o => o.Source).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var track in second.Animations.SelectMany(a => a.Stacks).SelectMany(s => s.Tracks))
            if (track.Target is { } target)
                Assert.DoesNotContain(target.Source, firstControllers);
    }

    [Fact]
    public void An_edited_animation_is_copied_to_the_linked_characters()
    {
        if (Pair() is not var (model, first, second)) return;
        SharedAnimBanks.Link(model, first, [second]);
        var animation = first.Animations.FirstOrDefault(a => a.Stacks.Any(s => s.Keys.Count > 0));
        if (animation == null) return;
        var stack = animation.Stacks.First(s => s.Keys.Count > 0);
        int stackIndex = animation.Stacks.ToList().IndexOf(stack);
        var key = stack.Keys[0];
        ushort fadeIn = (ushort)(key.FadeIn + 7);
        stack.SetKeyFlags(0, key.Flags, fadeIn, key.FadeOut);

        var edited = SharedAnimBanks.EditedBanks(model, [stack.Source]);
        var entry = Assert.Single(edited);
        Assert.Same(first.Source, entry.Owner.Source);
        Assert.Contains(animation.Source, entry.Animations);

        Assert.Equal(1, SharedAnimBanks.Sync(model, first, entry.Animations));
        string path = SharedAnimBanks.PathOf(animation);
        var copy = second.Animations.Single(a => SharedAnimBanks.PathOf(a) == path);
        Assert.Equal(fadeIn, copy.Stacks[stackIndex].Keys[0].FadeIn);
    }

    [Fact]
    public void A_deleted_animation_is_deleted_from_the_linked_characters()
    {
        if (Pair() is not var (model, first, second)) return;
        SharedAnimBanks.Link(model, first, [second]);
        var animation = first.Animations.FirstOrDefault();
        if (animation == null) return;
        string path = SharedAnimBanks.PathOf(animation);
        animation.Source.Parent!.RemoveChild(animation.Source);

        SharedAnimBanks.Sync(model, first, dirty: null);

        Assert.DoesNotContain(path, Paths(second));
        Assert.Equal(Paths(first), Paths(second));
    }

    [Fact]
    public void Unlinking_leaves_each_character_its_full_copy()
    {
        if (Pair() is not var (model, first, second)) return;
        SharedAnimBanks.Link(model, first, [second]);
        var paths = Paths(first);

        Assert.True(SharedAnimBanks.Unlink(model, second));

        Assert.Empty(SharedAnimBanks.Groups(model));
        Assert.False(SharedAnimBanks.IsLinked(first));
        Assert.False(SharedAnimBanks.IsLinked(second));
        Assert.Equal(paths, Paths(second));
        Assert.Equal(paths, Paths(first));
        Assert.False(SharedAnimBanks.Unlink(model, second));
    }

    [Fact]
    public void Undoing_a_link_restores_both_banks_and_removes_the_group()
    {
        if (Pair() is not var (model, first, second)) return;
        var firstPaths = Paths(first);
        var secondPaths = Paths(second);
        var before = EditSnapshot.TakeRig(first, model.Scene);

        SharedAnimBanks.Link(model, first, [second]);
        before.Restore();

        Assert.Empty(SharedAnimBanks.Groups(model));
        Assert.Equal(firstPaths, Paths(first));
        Assert.Equal(secondPaths, Paths(second));
    }

    [Fact]
    public void The_link_is_kept_in_slopa_files_and_left_out_of_akt_files()
    {
        if (Pair() is not var (model, first, second)) return;
        SharedAnimBanks.Link(model, first, [second]);
        string own = Temporary(".slopa");
        string plainFile = Temporary(".akt");

        model.Save(own, AktFileFormat.SloppyAnimatsu);
        model.Save(plainFile, AktFileFormat.Akeytsu);

        var reopened = SceneModel.Load(own);
        var group = Assert.Single(SharedAnimBanks.Groups(reopened));
        Assert.Equal(new[] { first.Name, second.Name }, group.Select(c => c.Name));

        var plain = SceneModel.Load(plainFile);
        Assert.Empty(SharedAnimBanks.Groups(plain));
        Assert.False(plain.Scene.Source.Has(MaterialSchema.SharedBankGroupsMember));
        // The banks themselves are still there in full.
        Assert.All(plain.Scene.Characters.Where(c => c.Name == first.Name || c.Name == second.Name),
            c => Assert.Equal(Paths(first), Paths(c)));
    }

    [Fact]
    public void Rig_changes_list_added_renamed_and_deleted_joints_and_ik()
    {
        if (Pair() is not var (model, first, second)) return;
        SharedAnimBanks.Link(model, first, [second]);
        var outline = SharedAnimBanks.CaptureRig(first);
        Assert.True(SharedAnimBanks.RigChanges(outline, first).IsEmpty);

        var (renamed, deleted, upper, lower, ik) = EditRig(first);
        var changes = SharedAnimBanks.RigChanges(outline, first);

        Assert.Equal([upper.Source, lower.Source, ik.Source], changes.Added.Select(o => o.Source));
        Assert.Single(changes.Renamed);
        Assert.Same(renamed.Source, changes.Renamed[0].Now.Source);
        Assert.Contains(changes.Deleted, e => e.Name == deleted && e.IsJoint);
        Assert.Contains("2 joint(s) added", changes.Describe());
    }

    [Fact]
    public void Rig_changes_are_copied_to_a_linked_character_by_name()
    {
        if (Pair() is not var (model, first, second)) return;
        SharedAnimBanks.Link(model, first, [second]);
        var outline = SharedAnimBanks.CaptureRig(first);
        var (renamed, deleted, upper, lower, ik) = EditRig(first);
        var changes = SharedAnimBanks.RigChanges(outline, first);

        var result = SharedAnimBanks.CopyRigChanges(first, changes, [second]);

        Assert.True(result.Skipped.Count == 0, string.Join("; ", result.Skipped));
        Assert.Equal(changes.Count, result.Applied);
        Object3D? Find(Character c, string skeleton) => c.Controllers.FirstOrDefault(o =>
            string.Equals(RetargetNames.SkeletonName(c, o), skeleton, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(Find(second, RetargetNames.SkeletonName(first, renamed)));
        Assert.Null(Find(second, changes.Deleted.First(e => e.IsJoint).SkeletonName));
        var peerLower = Assert.IsType<Joint>(Find(second, RetargetNames.SkeletonName(first, lower)));
        var peerUpper = Assert.IsType<Joint>(Find(second, RetargetNames.SkeletonName(first, upper)));
        Assert.Same(peerUpper.Source, peerLower.Source.Parent);
        var peerIk = Assert.IsAssignableFrom<ControllerIK>(Find(second, RetargetNames.SkeletonName(first, ik)));
        Assert.Same(peerLower.Source, peerIk.Chain[^1].Source);
        Assert.EndsWith("_Renamed", Find(second, RetargetNames.SkeletonName(first, renamed))!.Name);
    }

    [Fact]
    public void A_ref_pose_made_from_bent_limbs_is_copied_to_a_linked_character()
    {
        if (Pair() is not var (model, first, second)) return;
        SharedAnimBanks.Link(model, first, [second]);
        var chains = first.IKControllers.Where(k => k.Chain.Count >= 2 && k is not ControllerReverseFoot).ToList();
        if (chains.Count == 0) return;
        Object3D? Peer(Object3D o) => second.Controllers.FirstOrDefault(p =>
            string.Equals(RetargetNames.SkeletonName(second, p), RetargetNames.SkeletonName(first, o), StringComparison.OrdinalIgnoreCase));
        var peerBefore = new RigEditor(second);
        var peerIkWorlds = chains.Select(k => Peer(k)).OfType<ControllerIK>()
            .Select(k => (Key: (Object3D)k, Value: peerBefore.WorldInPose(second.RefPose!, k))).ToList();

        // Bend every limb in Paint & Check (the first joint of each IK chain), then make the ref pose.
        if (CharacterBank.CanDeleteRefPose(first)) Assert.True(CharacterBank.DeleteRefPose(first));
        CharacterBank.Select(first, CharacterBank.Rows(first).First(r => r.Kind == CharacterBankRowKind.PaintAndCheck));
        var pose = new ScenePose(model) { RiggingContext = true };
        var session = new PoseEditSession(pose, first);
        var bent = chains.Select(k => k.Chain[0]).Distinct().ToList();
        foreach (var joint in bent)
        {
            var drag = Assert.IsType<CheckPoseDrag>(session.BeginCheckPose(joint, out _));
            var turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 0.3f, 0.2f)), 40 * MathF.PI / 180);
            drag.Set(drag.BaseLocal with { Rotation = Quaternion.Normalize(drag.BaseLocal.Rotation * turn) }, AnimChannels.Rotate);
            drag.Commit();
        }
        Assert.NotNull(CharacterBank.CreateRefPoseFromCheckPose(first, session.Evaluator));
        var changed = new RestPoseEditor(first).ChangedSlots;
        Assert.True(changed.Count >= bent.Count, $"{changed.Count} changed slots for {bent.Count} bent joints");

        Assert.True(SharedAnimBanks.FollowRefPose(first, [second]) >= bent.Count);

        var source = new RestPoseEditor(first);
        var peer = new RestPoseEditor(second);
        foreach (var joint in bent)
        {
            var target = Assert.IsAssignableFrom<Object3D>(Peer(joint));
            var want = source.GetLocal(joint).Rotation;
            var have = peer.GetLocal(target).Rotation;
            Assert.True(MathF.Abs(Quaternion.Dot(want, have)) > 0.9999f, $"{target.Name} did not get {joint.Name}'s ref-pose rotation");
        }
        var peerAfter = new RigEditor(second);
        Assert.Contains(peerIkWorlds, pair =>
            AktMath.MaxDifference(pair.Value, peerAfter.WorldInPose(second.RefPose!, pair.Key)) > 1e-2f);
        Assert.False(peer.IsResolutionPending, "the copy was left pending on the linked character");
    }

    /// <summary>
    /// On <paramref name="character"/>: renames one leaf joint, deletes another, and adds a
    /// two-joint chain under a third with an IK on it.
    /// </summary>
    private static (Joint Renamed, string DeletedName, Joint Upper, Joint Lower, ControllerIK Ik) EditRig(Character character)
    {
        var rig = new RigEditor(character);
        var leaves = character.Joints.Where(j => !character.Joints.Any(c => ReferenceEquals(c.Source.Parent, j.Source)))
            .Where(j => !character.Controllers.OfType<ControllerIK>().Any(k => k.Chain.Any(c => ReferenceEquals(c.Source, j.Source))))
            .ToList();
        Assert.True(leaves.Count >= 3, $"only {leaves.Count} free leaf joints");
        var renamed = leaves[0];
        renamed.Name = (renamed.Name ?? "") + "_Renamed";
        string deletedName = leaves[1].Name ?? "";
        Assert.Equal(1, rig.DeleteJoints([leaves[1]]));
        var upper = rig.CreateJoint(leaves[2], (leaves[2].Name ?? "") + "_Extra1", Matrix4x4.CreateTranslation(0, 5, 0));
        var lower = rig.CreateJoint(upper, (leaves[2].Name ?? "") + "_Extra2", Matrix4x4.CreateTranslation(0, 5, 0));
        var ik = rig.AddIK(upper, lower);
        return (renamed, deletedName, upper, lower, ik);
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>
    /// 19_run (its character Dup holds the sample's animations) plus a second import of itself:
    /// two characters with one skeleton and the same bank. Null when the sample is not there.
    /// </summary>
    private static (SceneModel Model, Character First, Character Second)? Pair()
    {
        if (Load("19_run.akt") is not { } model || Load("19_run.akt") is not { } copy) return null;
        var first = model.Scene.Characters.First(c => c.AnimBank != null && c.Animations.Any());
        var imported = AktImport.Import(model, copy.Document);
        var second = imported.Characters.Single(c => c.Animations.Any());
        Assert.NotSame(first.Source, second.Source);
        return (model, first, second);
    }

    private static List<string> Paths(Character character) =>
        character.Animations.Select(a => SharedAnimBanks.PathOf(a)).OrderBy(p => p, StringComparer.Ordinal).ToList();

    private string Temporary(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-sharedbank-{Guid.NewGuid():N}{extension}");
        _temporaryFiles.Add(path);
        return path;
    }

    private static SceneModel? Load(string fileName) =>
        TestFiles.AllSceneSamples.FirstOrDefault(p => Path.GetFileName(p) == fileName) is { } path
            ? TestFiles.LoadScene(path)
            : null;
}
