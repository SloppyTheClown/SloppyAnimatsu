using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

// Shared Anim Banks in the viewer: linking and unlinking characters, mirroring every edit of a
// linked bank into the other members, and the chain marks in the TREE.
public partial class MainWindow
{
    private const string LinkBanksDescription = "Link Anim Banks";
    private const string UnlinkBankDescription = "Unlink Anim Bank";

    // Set while a sync writes the peers' banks, so nothing it triggers syncs again.
    private bool _syncingBanks;

    /// <summary>Makes <paramref name="history"/> the document's history and follows its new steps.</summary>
    private void AttachHistory(EditHistory history)
    {
        _history.Pushed -= SyncSharedBanks;
        _history = history;
        _history.Pushed -= SyncSharedBanks;
        _history.Pushed += SyncSharedBanks;
        CaptureRigOutlines();
    }

    /// <summary>
    /// After a step was pushed, undone or redone: every linked bank the step touched is mirrored
    /// into its group's other members. The step's own character goes first, and a group is synced
    /// once, from the first of its members that was touched. A rig or rest pose step only mirrors
    /// the bank's structure (its animations' keys are not what it changed).
    /// </summary>
    private void SyncSharedBanks(EditRecord? step)
    {
        if (step == null || _pose == null || _syncingBanks) return;
        var model = _pose.Model;
        if (SharedAnimBanks.Groups(model).Count == 0) return;
        var targets = (step.Before?.Targets ?? []).Concat(step.After?.Targets ?? []);
        var edited = SharedAnimBanks.EditedBanks(model, targets);
        if (edited.Count == 0) return;
        bool structureOnly = step.Before?.RestPose == true;
        var stepOwner = step.Before?.Owner?.Source ?? step.After?.Owner?.Source;
        var synced = new HashSet<object>(ReferenceEqualityComparer.Instance);
        _syncingBanks = true;
        try
        {
            foreach (var (owner, animations) in edited.OrderBy(e => ReferenceEquals(e.Owner.Source, stepOwner) ? 0 : 1))
            {
                if (synced.Contains(owner.Source)) continue;
                foreach (var member in SharedAnimBanks.GroupOf(owner)) synced.Add(member.Source);
                SharedAnimBanks.Sync(model, owner, structureOnly ? new HashSet<AktObject>() : animations);
                foreach (var peer in SharedAnimBanks.PeersOf(owner))
                    if (Shown(peer) is { } shown) _pose.Evaluator(shown).Invalidate();
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Could not update the linked Anim Banks: " + ex.Message;
        }
        finally
        {
            _syncingBanks = false;
        }
    }

    /// <summary>The drawn character for <paramref name="character"/> (same scene object), if any.</summary>
    private Character? Shown(Character character) =>
        _pose?.Characters.FirstOrDefault(c => ReferenceEquals(c.Source, character.Source));

    /// <summary>
    /// After undo or redo: mirrors the restored banks, and a link or unlink step reloads every
    /// character's rest pose (linking conforms the members' reference poses).
    /// </summary>
    private void AfterSharedBankHistoryStep(EditRecord? step)
    {
        if (step == null || _pose == null) return;
        if (step.Description.StartsWith(LinkBanksDescription, StringComparison.Ordinal)
            || step.Description.StartsWith(CopyRigDescription, StringComparison.Ordinal))
        {
            foreach (var character in _pose.Characters)
            {
                _pose.ReloadRestPose(character);
                _pose.Evaluator(character).Invalidate();
            }
        }
        else
        {
            SyncSharedBanks(step);
        }
        RefreshBankView();
        UpdateSharedBankMarks();
    }

    /// <summary>Opens the link dialog and links the characters it returns, as one undoable step.</summary>
    private async Task OpenLinkBanks()
    {
        if (_pose == null) return;
        var characters = _pose.Model.Scene.Characters.Where(c => c.AnimBank != null).ToList();
        if (characters.Count < 2)
        {
            _status.Text = "Linking Anim Banks needs two characters with an Anim Bank.";
            return;
        }
        var request = await SharedBankDialog.Ask(this, characters, _character);
        if (request == null) return;
        var scene = _pose.Model.Scene;
        var before = EditSnapshot.TakeRig(request.Reference, scene);
        SharedBankLinkResult result;
        _syncingBanks = true;
        try
        {
            result = SharedAnimBanks.Link(_pose.Model, request.Reference, request.Others, request.Drop);
        }
        catch (Exception ex)
        {
            before.Restore();
            _status.Text = "Link failed: " + ex.Message;
            return;
        }
        finally
        {
            _syncingBanks = false;
        }
        string names = string.Join(", ", result.Members.Select(c => c.Name));
        _syncingBanks = true;
        try
        {
            _history.Push(new EditRecord($"{LinkBanksDescription} ({names})", before, EditSnapshot.TakeRig(request.Reference, scene)));
        }
        finally
        {
            _syncingBanks = false;
        }
        foreach (var member in result.Members)
            if (Shown(member) is { } shown)
            {
                _pose.ReloadRestPose(shown);
                _pose.Evaluator(shown).Invalidate();
            }
        _dirty = true;
        UpdateTitle();
        AfterKeyChange();
        RefreshBankView();
        UpdateSharedBankMarks();
        CaptureRigOutlines();
        _status.Text = $"Linked {names}: {result.MergedAnimations} animation(s) merged, {result.Dropped.Count} duplicate(s) left out, " +
                       $"{result.ConformedControllers} reference pose controller(s) matched to {request.Reference.Name}.";
    }

    /// <summary>Takes the current character out of its shared bank; it keeps its full copy.</summary>
    private void UnlinkBank()
    {
        if (_pose == null || _character == null) return;
        if (!SharedAnimBanks.IsLinked(_character))
        {
            _status.Text = $"{_character.Name} does not share its Anim Bank.";
            return;
        }
        var scene = _pose.Model.Scene;
        var before = EditSnapshot.TakeShallow(_character, [scene]);
        SharedAnimBanks.Unlink(_pose.Model, _character);
        _syncingBanks = true;
        try
        {
            _history.Push(new EditRecord($"{UnlinkBankDescription} ({_character.Name})", before, EditSnapshot.TakeShallow(_character, [scene])));
        }
        finally
        {
            _syncingBanks = false;
        }
        _dirty = true;
        UpdateTitle();
        UpdateSharedBankMarks();
        _status.Text = $"{_character.Name} no longer shares its Anim Bank; it keeps a copy of every animation.";
    }

    private const string CopyRigDescription = "Copy rig changes";

    // The linked characters' rigs as they were when the rigging visit began, by character object.
    private readonly Dictionary<AktObject, RigOutline> _rigOutlines = new(ReferenceEqualityComparer.Instance);

    /// <summary>Remembers every linked character's joints and IK controllers, to compare against on leaving Rigging.</summary>
    private void CaptureRigOutlines()
    {
        _rigOutlines.Clear();
        if (_pose == null) return;
        foreach (var group in SharedAnimBanks.Groups(_pose.Model))
            foreach (var member in group)
                _rigOutlines[member.Source] = SharedAnimBanks.CaptureRig(member);
    }

    /// <summary>
    /// On the way from Rigging to Animation: for each linked character whose joints or IK
    /// controllers changed during the visit, asks whether to make the same changes on the characters
    /// it shares its bank with. Declining keeps them on that character only; either way the visit's
    /// baseline is reset.
    /// </summary>
    private async Task OfferRigCopy()
    {
        if (_pose == null || _rigOutlines.Count == 0) return;
        foreach (var character in _pose.Model.Scene.Characters.ToList())
        {
            if (!_rigOutlines.TryGetValue(character.Source, out var outline)) continue;
            var peers = SharedAnimBanks.PeersOf(character);
            if (peers.Count == 0) continue;
            var changes = SharedAnimBanks.RigChanges(outline, character);
            if (changes.IsEmpty) continue;
            string names = string.Join(", ", peers.Select(p => p.Name));
            string message = $"{character.Name}'s rig changed in Rigging: {changes.Describe()}.\n\n" +
                             $"It shares its Anim Bank with {names}. Make the same changes on them? " +
                             "Joints and controllers are matched by name; anything without a match is skipped.";
            if (await WarningDialog.Ask(this, "COPY RIG CHANGES", message, "Copy to linked characters", "Keep on this character only"))
            {
                CopyRigChanges(character, changes, peers);
                // The copies are not changes of the peers' own to offer back.
                foreach (var peer in peers) _rigOutlines[peer.Source] = SharedAnimBanks.CaptureRig(peer);
            }
        }
        _rigOutlines.Clear();
    }

    /// <summary>Replays <paramref name="changes"/> on <paramref name="peers"/> as one undoable step.</summary>
    private void CopyRigChanges(Character source, RigChangeSet changes, IReadOnlyList<Character> peers)
    {
        if (_pose == null) return;
        var scene = _pose.Model.Scene;
        var before = EditSnapshot.TakeRig(source, scene);
        RigCopyResult result;
        try
        {
            result = SharedAnimBanks.CopyRigChanges(source, changes, peers);
        }
        catch (Exception ex)
        {
            before.Restore();
            foreach (var peer in peers)
                if (Shown(peer) is { } shown) _pose.ReloadRestPose(shown);
            _status.Text = "Could not copy the rig changes: " + ex.Message;
            return;
        }
        foreach (var peer in peers)
            if (Shown(peer) is { } shown)
            {
                _pose.ReloadRestPose(shown);
                _pose.Evaluator(shown).Invalidate();
            }
        _pose.ReloadSkins();
        string names = string.Join(", ", peers.Select(p => p.Name));
        _history.Push(new EditRecord($"{CopyRigDescription} ({source.Name} to {names})", before, EditSnapshot.TakeRig(source, scene)));
        _dirty = true;
        UpdateTitle();
        RefreshRigStructure();
        _status.Text = $"Copied {result.Applied} rig change(s) from {source.Name} to {names}" +
                       (result.Skipped.Count == 0 ? "." : $"; skipped {result.Skipped.Count}: {string.Join("; ", result.Skipped.Take(4))}" +
                                                          (result.Skipped.Count > 4 ? "…" : "."));
    }

    /// <summary>
    /// The characters that share the current character's bank carry a chain mark in the TREE, in
    /// both contexts (the Anim Bank in Animation, the Char Bank in Rigging), so a rig edit on a linked
    /// character is never made unaware of the link; otherwise no row does.
    /// </summary>
    private void UpdateSharedBankMarks()
    {
        if (_character == null)
        {
            _outliner.ShowSharedBank([], "");
            return;
        }
        var group = SharedAnimBanks.GroupOf(_character);
        if (group.Count == 0)
        {
            _outliner.ShowSharedBank([], "");
            return;
        }
        _outliner.ShowSharedBank(group, "Shares its Anim Bank with " +
            string.Join(", ", group.Select(c => c.Name)) + " (edits in one bank are copied to the others)");
    }
}
