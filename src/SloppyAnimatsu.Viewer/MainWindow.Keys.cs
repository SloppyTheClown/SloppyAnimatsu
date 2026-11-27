// Rest-pose resolution and the core key operations: CreateKey and Hammer, delete/move/mirror, cycle
// step and cycle side, and the keep-children-fixed switch.
//
// A key edit runs against a PoseEditSession (SloppyAnimatsu.Core) and goes through RunKeyEdit, so a
// failure lands in the status line and the document is only marked dirty on success. The clipboard,
// gap and space operations are in MainWindow.Keys.Clipboard.cs.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SloppyAnimatsu.Core;
using SloppyAnimatsu.Core.Aknm;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    /// <summary>
    /// Called before the playhead or a drag leaves the rigging context: when a ref-pose edit is
    /// still pending, asks how to resolve it first. False means the caller must not continue.
    /// </summary>
    private async Task<bool> LeaveRestPose()
    {
        if (_animation != null || _drag != null || Session is not { } session || !session.RestPose.IsResolutionPending)
        {
            return _drag == null;
        }

        var peers = SharedAnimBanks.PeersOf(session.Character);
        var (answer, follow) = await RestPoseDialog.Ask(this, session.RestPose.ChangedSlots.Count,
            peers.Select(p => p.Name ?? "(unnamed)").ToList());
        if (answer is not { } resolution)
        {
            _status.Text = "The ref pose change is still pending.";
            return false;
        }

        ResolveRestPose(session, resolution, follow ? peers : null);
        return true;
    }

    private void ResolveRestPose(PoseEditSession session, RestPoseResolution resolution, IReadOnlyList<Character>? followers = null)
    {
        try
        {
            session.ResolveRestPose(resolution, followers);
            _status.Text = _history.NextUndo.Description + ".";
            _dirty = true;
            UpdateTitle();
        }
        catch (Exception ex)
        {
            _status.Text = "Could not resolve the ref pose change: " + ex.Message;
        }
        AfterKeyChange();
    }

    private void CreateKey(int? at = null)
    {
        if (_drag != null || _timeline.IsDragging)
        {
            return;
        }
        AnimStack stack = _timeline.ActiveStack;
        if (stack == null)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session != null)
        {
            _play.IsChecked = false;
            int frame = at ?? ((int)MathF.Round(_time));
            RunKeyEdit(delegate
            {
                int num = session.CreateKey(stack, frame);
                _timeline.Select(stack, [num]);
                SetTime(stack.Keys[num].Time);
            });
        }
    }

    private void Hammer()
    {
        if (_drag != null || _timeline.IsDragging)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session == null)
        {
            return;
        }
        AnimStack stack = _timeline.ActiveStack;
        if (stack == null)
        {
            _status.Text = "Pick a stack on the timeline first.";
            return;
        }
        Object3D selected = _selected;
        if (selected == null)
        {
            _status.Text = "Select a controller to add it to the stack or remove it.";
            return;
        }
        Object3D[] selection = _selectedAll.Where((Object3D c) => session.Evaluator.IndexOf(c) >= 0).ToArray();
        if (stack.TrackFor(selected) != null)
        {
            RunKeyEdit(delegate
            {
                session.RemoveFromStack(stack, (IReadOnlyCollection<Object3D>)(object)selection, selected);
            });
        }
        else
        {
            if (!KeyEditor.CanAddToStack(selected, out string reason))
            {
                _status.Text = reason;
                return;
            }
            string reason2;
            Object3D[] targets = selection.Where((Object3D c) => KeyEditor.CanAddToStack(c, out reason2)).ToArray();
            RunKeyEdit(delegate
            {
                session.AddToStack(stack, (IReadOnlyCollection<Object3D>)(object)targets);
            });
        }
        UpdateSelection();
    }

    private void DeleteKeys(bool closeGaps)
    {
        if (_drag != null || _timeline.IsDragging)
        {
            return;
        }
        AnimStack stack = _timeline.ActiveStack;
        if (stack == null)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session == null)
        {
            return;
        }
        int[] selected = _timeline.SelectedKeys.ToArray();
        if (selected.Length == 0)
        {
            _status.Text = "Select keys on the timeline to delete them.";
            return;
        }
        RunKeyEdit(delegate
        {
            if (session.DeleteKeys(stack, (IReadOnlyCollection<int>)(object)selected, closeGaps) == 0)
            {
                _status.Text = "The end key cannot be deleted.";
            }
            _timeline.Select(stack, Array.Empty<int>());
        });
    }

    private void MoveKeys(int delta)
    {
        if (_drag != null)
        {
            return;
        }
        AnimStack stack = _timeline.ActiveStack;
        if (stack == null)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session == null)
        {
            return;
        }
        int[] selected = _timeline.SelectedKeys.ToArray();
        int cursorKey = PoseEditSession.KeyAt(stack, (int)MathF.Round(_time));
        if (_time != MathF.Round(_time))
        {
            cursorKey = -1;
        }
        RunKeyEdit(delegate
        {
            int[] moved = session.MoveKeys(stack, (IReadOnlyCollection<int>)(object)selected, delta);
            _timeline.Select(stack, from k in selected
                where k < moved.Length
                select moved[k]);
            if (cursorKey >= 0 && cursorKey < moved.Length && selected.Contains(cursorKey))
            {
                SetTime(stack.Keys[moved[cursorKey]].Time);
            }
        });
    }

    private void MirrorKeys()
    {
        if (_drag != null || _timeline.IsDragging)
        {
            return;
        }
        AnimStack stack = _timeline.ActiveStack;
        if (stack == null)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session == null)
        {
            return;
        }
        int[] keys = _timeline.SelectedKeys.Where((int k) => k < stack.Keys.Count - 1).ToArray();
        if (keys.Length == 0)
        {
            _status.Text = "Select the keys to mirror (not the end key).";
            return;
        }
        if (_selectedAll.Count == 0)
        {
            _status.Text = "Select the controllers whose keys to mirror.";
            return;
        }
        Object3D[] sources = _selectedAll.ToArray();
        RunKeyEdit(delegate
        {
            int num = session.MirrorKeys(stack, (IReadOnlyCollection<int>)(object)keys, (IReadOnlyCollection<Object3D>)(object)sources, null, KeyChannelFilter);
            _status.Text = ((num == 0) ? "Nothing to mirror: no counterpart has a track with the same channels here." : $"Mirrored {num} key(s) onto the other side.");
        });
    }

    /// <summary>
    /// The TRANSFORM window's Mirror: mirrors the selected timeline keys when an animation has
    /// some selected, otherwise the pose on screen (the animation at the playhead, Paint &amp;
    /// Check, or the rest pose).
    /// </summary>
    private void MirrorTicked()
    {
        if (_animation != null && _timeline.ActiveStack is { } stack
            && _timeline.SelectedKeys.Any(k => k < stack.Keys.Count - 1))
        {
            MirrorKeys();
            return;
        }
        MirrorPose(KeyChannelFilter);
    }

    /// <summary>
    /// Mirrors the selected controllers onto their other-side counterparts in the pose on screen;
    /// central controllers turn as if reflected onto themselves. One undo step.
    /// </summary>
    private void MirrorPose() => MirrorPose(KeyEditor.AllChannels);

    private void MirrorPose(AnimChannels channels)
    {
        if (_drag != null || _timeline.IsDragging)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session == null)
        {
            return;
        }
        if (_selectedAll.Count == 0)
        {
            _status.Text = "Select the controllers to mirror.";
            return;
        }
        Object3D[] sources = _selectedAll.ToArray();
        bool checkPose = InPaintAndCheck;
        Animation animation = checkPose ? null : _animation;
        _play.IsChecked = false;
        int frame = (int)MathF.Round(_time);
        RunKeyEdit(delegate
        {
            int num = session.MirrorPoseAt(animation, sources, frame, checkPose, out string reason, null, channels);
            string where = checkPose ? "in Paint & Check" : animation == null ? "in the rest pose" : $"at frame {frame}";
            _status.Text = num == 0 ? reason ?? "Nothing to mirror." : $"Mirrored {num} controller(s) {where}.";
        });
        UpdateSelection(forceSpinner: true);
    }

    private void CycleStep()
    {
        if (_drag != null || _timeline.IsDragging)
        {
            return;
        }
        AnimStack stack = _timeline.ActiveStack;
        if (stack == null)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session == null)
        {
            return;
        }
        if (!KeyEditor.CanCycleStep(stack, out string reason))
        {
            _status.Text = reason;
            return;
        }
        session.SetCurrentStack(stack);
        int written = 0;
        int count = stack.Keys.Count;
        RunKeyEdit(delegate
        {
            written = session.CycleStep(stack);
        });
        _timeline.Select(stack, Array.Empty<int>());
        _status.Text = ((written > 0) ? $"Cycle Step: wrote {written} key(s) of the second step." : ((stack.Keys.Count != count) ? "Cycle Step: extended the stack by one step; no track had keys to copy." : "Nothing to step: the stack has no keys."));
    }

    private void CycleSide()
    {
        if (_drag != null || _timeline.IsDragging)
        {
            return;
        }
        AnimStack stack = _timeline.ActiveStack;
        if (stack == null)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session == null)
        {
            return;
        }
        if (!KeyEditor.CanCycleSide(stack, out string reason))
        {
            _status.Text = reason;
            return;
        }
        if (_selectedAll.Count == 0)
        {
            _status.Text = "Select the controllers to mirror to the other side.";
            return;
        }
        Object3D[] sources = _selectedAll.ToArray();
        session.SetCurrentStack(stack);
        int written = 0;
        RunKeyEdit(delegate
        {
            written = session.CycleSide(stack, (IReadOnlyCollection<Object3D>)(object)sources);
        });
        _timeline.Select(stack, Array.Empty<int>());
        _status.Text = ((written == 0) ? "Nothing to mirror: no selected controller has a track here." : $"Cycle Side: mirrored {written} key(s) of the cycle.");
    }

    private void SetKeepChildrenFixed(bool value)
    {
        _keepChildrenFixed = value;
        foreach (PoseEditSession value2 in _sessions.Values)
        {
            value2.KeepChildrenFixed = value;
        }
        PoseEditSession session = Session;
        if (session != null)
        {
            session.KeepChildrenFixed = value;
        }
        _status.Text = (value ? "Drags and Mirror Pose keep the children fixed." : "Drags carry the children along.");
        RefreshMenu();
    }
}
