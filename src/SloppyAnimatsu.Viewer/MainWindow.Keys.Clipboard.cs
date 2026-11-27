// Key clipboard and the rest of key editing: copy/paste (and paste-after), ripple and reorder, gap
// changes, marks, Change Space, fade in/out, and the RunKeyEdit transaction that turns an edit into
// one undo record.
//
// The clipboard stores resolved key values, not references, so a paste survives a rename or a stack
// change. Create/hammer/delete/move/mirror/cycle are in MainWindow.Keys.cs.
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
    /// <summary>The objects a copy or paste works on: the selected controllers, else every track of the stack.</summary>
    private Object3D[] KeyTargets(AnimStack stack) =>
        _selectedAll.Count > 0
            ? _selectedAll.ToArray()
            : stack.Tracks.Select(t => t.Target).OfType<Object3D>().Distinct().ToArray();

    /// <summary>
    /// Copies the selected keys whole: every channel of the selected controllers (every track of the
    /// stack when none is selected) and the keys' flags and fades, so a paste overwrites a key entirely.
    /// </summary>
    private void CopyKeys()
    {
        if (_drag != null || _timeline.IsDragging)
        {
            return;
        }
        AnimStack stack = _timeline.ActiveStack;
        PoseEditSession session = Session;
        if (stack == null || session == null || _animation == null)
        {
            _status.Text = "Copy works on keys: open an animation and select keys on the timeline.";
            return;
        }
        int[] keys = _timeline.SelectedKeys.ToArray();
        if (keys.Length == 0)
        {
            _status.Text = "Select the keys to copy.";
            return;
        }
        KeyClipboard clipboard = session.Editor.CopyKeys(stack, keys, KeyTargets(stack), KeyChannelFilter);
        if (clipboard.IsEmpty)
        {
            _status.Text = "None of the selected controllers has a track in '" + stack.Name + "'.";
            return;
        }
        _clipboard = clipboard;
        _status.Text = $"Copied {clipboard.Gaps.Count} key(s) of {clipboard.Entries.Count} track target(s).";
        UpdateKeyCommands();
        RefreshMenu();
    }

    /// <summary>
    /// The clipboard and the objects to paste it on in <paramref name="stack"/>. With controllers
    /// selected they are the targets; otherwise the copied objects that have a track here. When every
    /// target has its own copied entry, the clipboard is cut to those entries so they match by identity.
    /// </summary>
    private (KeyClipboard Clipboard, Object3D[] Targets)? PasteSource(AnimStack stack)
    {
        KeyClipboard clipboard = _clipboard;
        if (clipboard == null)
        {
            _status.Text = "Copy keys first (Ctrl+C).";
            return null;
        }
        Object3D[] targets = _selectedAll.Count > 0
            ? _selectedAll.ToArray()
            : clipboard.Entries.Select(e => e.Target).Where(t => stack.TrackFor(t) != null).Distinct().ToArray();
        if (targets.Length == 0)
        {
            _status.Text = "None of the copied objects has a track in '" + stack.Name + "'; select controllers to paste onto.";
            return null;
        }
        List<ClipboardEntry> own = targets
            .Select(t => clipboard.Entries.FirstOrDefault(e => ReferenceEquals(e.Target, t)))
            .Where(e => e != null).Select(e => e!).ToList();
        return (own.Count == targets.Length ? clipboard.With(own) : clipboard, targets);
    }

    /// <summary>
    /// Ctrl+V: onto the selected keys (overwriting values, flags and fades), or - with one key or no
    /// key selected - at that key or the playhead, creating keys where none is (Ctrl+Shift+V inserts after).
    /// </summary>
    private void PasteKeys(bool after)
    {
        if (_drag != null || _timeline.IsDragging)
        {
            return;
        }
        AnimStack stack = _timeline.ActiveStack;
        PoseEditSession session = Session;
        if (stack == null || session == null || _animation == null)
        {
            _status.Text = "Paste works on keys: open an animation and pick a stack on the timeline.";
            return;
        }
        int[] selected = _timeline.SelectedKeys.Where((int k) => k < stack.Keys.Count - 1).ToArray();
        if (!after && selected.Length <= 1)
        {
            PasteKeysAt(selected.Length == 1 ? stack.Keys[selected[0]].Time : (int)MathF.Round(_time));
            return;
        }
        if (selected.Length == 0)
        {
            _status.Text = "Select the keys to paste after (not the end key).";
            return;
        }
        if (PasteSource(stack) is not var (clipboard, targets))
        {
            return;
        }
        RunKeyEdit(delegate
        {
            if (after)
            {
                int num = session.PasteKeysAfter(stack, clipboard, selected, targets);
                if (num >= 0)
                {
                    _timeline.Select(stack, Enumerable.Range(num, clipboard.Gaps.Count));
                    SetTime(stack.Keys[num].Time);
                }
            }
            else if (session.PasteKeys(stack, clipboard, selected, targets, withKeyFlags: true) == 0)
            {
                _status.Text = "Nothing to paste: select controllers with a track in this stack.";
            }
        });
    }

    /// <summary>Pastes the clipboard at animation frame <paramref name="frame"/> (the timeline's "Paste Key(s)").</summary>
    private void PasteKeysAt(int frame)
    {
        if (_drag != null || _timeline.IsDragging)
        {
            return;
        }
        AnimStack stack = _timeline.ActiveStack;
        PoseEditSession session = Session;
        if (stack == null || session == null || PasteSource(stack) is not var (clipboard, targets))
        {
            return;
        }
        _play.IsChecked = false;
        RunKeyEdit(delegate
        {
            int first = session.PasteKeysAt(stack, clipboard, frame, targets);
            if (first < 0)
            {
                _status.Text = "Nothing pasted: the frame is at or past the end key.";
                return;
            }
            _timeline.Select(stack, [first]);
            SetTime(stack.Keys[first].Time);
        });
    }

    private void RippleKey(int key, int delta)
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
        if (session == null || key < 0 || key >= stack.Keys.Count)
        {
            return;
        }
        int cursorKey = ((_time == MathF.Round(_time)) ? PoseEditSession.KeyAt(stack, (int)_time) : (-1));
        RunKeyEdit(delegate
        {
            session.SetKeyTime(stack, key, stack.Keys[key].Time + delta);
            _timeline.Select(stack, [key]);
            if (cursorKey >= key)
            {
                SetTime(stack.Keys[cursorKey].Time);
            }
        });
    }

    private void ReorderKeys(int direction)
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
        int n = stack.Keys.Count;
        int[] selected = _timeline.SelectedKeys.Where((int k) => k < n - 1).ToArray();
        if (selected.Length == 0)
        {
            _status.Text = "The end key stays last; select other keys to move them.";
            return;
        }
        int destination = ((direction < 0) ? (selected.Min() - 1) : (selected.Max() + 2));
        if (destination < 0 || destination > n - 1)
        {
            _status.Text = ((direction < 0) ? "The keys are already first." : "The keys are already last before the end key.");
            return;
        }
        int cursorKey = ((_time == MathF.Round(_time)) ? PoseEditSession.KeyAt(stack, (int)_time) : (-1));
        RunKeyEdit(delegate
        {
            int[] order = session.ReorderKeys(stack, (IReadOnlyCollection<int>)(object)selected, destination);
            _timeline.Select(stack, selected.Select((int k) => order[k]));
            if (cursorKey >= 0 && cursorKey < order.Length && selected.Contains(cursorKey))
            {
                SetTime(stack.Keys[order[cursorKey]].Time);
            }
        });
    }

    private void ChangeGap(int step)
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
        if (!selected.Any((int k) => k > 0))
        {
            _status.Text = "Key 0 has no gap before it; select a later key.";
            return;
        }
        RunKeyEdit(delegate
        {
            if (!session.ChangeKeyGap(stack, (IReadOnlyCollection<int>)(object)selected, step))
            {
                _status.Text = "A gap cannot drop below one frame.";
            }
        });
    }

    private void SetGap(int gap)
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
        int[] selected = _timeline.SelectedKeys.Where((int k) => k > 0).ToArray();
        if (selected.Length != 0)
        {
            RunKeyEdit(delegate
            {
                session.SetKeyGap(stack, (IReadOnlyCollection<int>)(object)selected, gap);
            });
        }
    }

    private void MarkKeys(uint mark, bool all)
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
        int n = stack.Keys.Count;
        int[] keys = (all ? Enumerable.Range(0, n).ToArray() : _timeline.SelectedKeys.ToArray());
        if (!keys.Any((int k) => k < n - 1))
        {
            _status.Text = "The end key cannot be marked.";
            return;
        }
        RunKeyEdit(delegate
        {
            if (session.SetKeyMarks(stack, (IReadOnlyCollection<int>)(object)keys, mark) == 0)
            {
                _status.Text = "The marks are already set.";
            }
        });
    }

    private async Task ChangeSpace()
    {
        if (_drag != null || _timeline.IsDragging || _timeline.ActiveStack == null || _character == null)
        {
            return;
        }
        List<Object3D> spaces = KeyEditor.SpaceableObjects(_character.Model.Scene).ToList();
        if (spaces.Count == 0)
        {
            _status.Text = "Foremost you need to set some joints as available spaces in Rigging mode. Please turn on the space switch icons displayed into the Picker.";
            return;
        }
        Object3D? lead = _selected;
        Object3D? current = lead != null && _animation != null && Session is { } s && s.Evaluator.IndexOf(lead) >= 0
            ? s.Evaluator.SpaceOf(_animation, lead, _time) : null;
        if (lead != null && ReferenceEquals(current, lead.Parent3D)) current = null;
        var (chosen, space) = await SpaceSwitchDialog.Ask(this, _character.Model.Scene.Characters.ToList(), spaces, current, lead,
            "New space for the selected objects at the selected keys");
        if (chosen)
        {
            ChangeSpace(space);
        }
    }

    private void ChangeSpace(Object3D? space)
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
            _status.Text = "Select the keys to change (not the end key).";
            return;
        }
        if (_selectedAll.Count == 0)
        {
            _status.Text = "Select the controllers whose space to change.";
            return;
        }
        foreach (Object3D item in _selectedAll)
        {
            if (!KeyEditor.CanChangeSpace(item, space, out string reason))
            {
                _status.Text = item.Name + ": " + reason;
                return;
            }
        }
        Object3D[] targets = _selectedAll.ToArray();
        RunKeyEdit(delegate
        {
            int num = session.ChangeSpace(stack, (IReadOnlyCollection<int>)(object)keys, (IReadOnlyCollection<Object3D>)(object)targets, space);
            _status.Text = ((num == 0) ? "None of the selected controllers has a key there." : $"Changed the space of {num} key(s) to {((space == null) ? "the parent" : space.Name)}.");
        });
    }

    private void ToggleSpaceable()
    {
        Object3D clicked = _selected;
        if (clicked == null)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session != null)
        {
            Object3D[] objects = _selectedAll.ToArray();
            RunKeyEdit(delegate
            {
                bool flag = session.ToggleSpaceable(clicked, (IReadOnlyCollection<Object3D>)(object)objects);
                _status.Text = $"{objects.Length} object(s) {(flag ? "are now" : "are no longer")} spaces.";
            });
        }
    }

    private void FadeKeys(bool fadeIn, ushort frames)
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
        int[] keys = _timeline.SelectedKeys.ToArray();
        if (keys.Length == 0)
        {
            return;
        }
        RunKeyEdit(delegate
        {
            if (session.SetKeyFades(stack, (IReadOnlyCollection<int>)(object)keys, fadeIn, frames) == 0)
            {
                _status.Text = ((frames == 0) ? "The selected keys have no such fade." : "The fades are already set.");
            }
        });
    }

    private void BeginFadeDrag(int key, bool fadeIn)
    {
        _fadeDrag = null;
        if (_drag != null)
        {
            return;
        }
        AnimStack activeStack = _timeline.ActiveStack;
        if (activeStack != null)
        {
            PoseEditSession session = Session;
            if (session != null && key < activeStack.Keys.Count)
            {
                _play.IsChecked = false;
                _fadeDrag = session.BeginFadeDrag(activeStack, key, fadeIn);
            }
        }
    }

    private void EndFadeDrag(bool keep)
    {
        FadeDrag drag = _fadeDrag;
        if (drag == null)
        {
            return;
        }
        _fadeDrag = null;
        if (keep)
        {
            RunKeyEdit(delegate
            {
                drag.Commit();
            });
        }
        else
        {
            drag.Cancel();
            _status.Text = "Fade drag cancelled.";
            AfterKeyChange();
        }
    }

    /// <summary>
    /// Runs one key edit as a single undo step: a thrown edit is reported in the status line and
    /// leaves the history untouched, a successful one marks the document dirty and refreshes the
    /// key commands. Every key operation goes through here.
    /// </summary>
    private void RunKeyEdit(Action edit, string failure = "Key edit failed: ")
    {
        EditRecord nextUndo = _history.NextUndo;
        try
        {
            edit();
        }
        catch (Exception ex)
        {
            _status.Text = failure + ex.Message;
        }
        if ((object)_history.NextUndo != nextUndo)
        {
            _status.Text = _history.NextUndo.Description;
            _dirty = true;
            UpdateTitle();
        }
        AfterKeyChange();
    }

    private void AfterKeyChange()
    {
        RefreshAnimations();
        _timeline.Refresh();
        UpdateRange();
        SetTime(_time);
        UpdateKeyCommands();
        RefreshPicker();
        RefreshProperties();
        RefreshMaterial();
    }

    private void UpdateKeyCommands()
    {
        SyncTransformKeyActions();
        AnimStack stack = _timeline.ActiveStack;
        int count = _timeline.SelectedKeys.Count;
        _syncing = true;
        DisabledHint.SetEnabled(_stackBlend, stack != null && stack is not AudioStack, "an active pose stack");
        _stackBlend.SelectedIndex = stack?.IsAdditive == true ? 1 : 0;
        _syncing = false;
        int num = ((stack == null) ? (-1) : _timeline.SelectedKeys.Where((int k) => k > 0 && k < stack.Keys.Count).DefaultIfEmpty(-1).Min());
        _syncing = true;
        DisabledHint.SetEnabled(_gap, num > 0, "a selected key after the first key of the active stack");
        _gap.Value = ((num > 0) ? new decimal?(stack.Keys[num].Time - stack.Keys[num - 1].Time) : ((decimal?)null));
        _syncing = false;
        _keyInfo.Text = ((stack == null) ? "" : (count switch
        {
            1 => $"Stack '{stack.Name}', key {_timeline.SelectedKeys.First()}", 
            0 => "Stack '" + stack.Name + "'", 
            _ => $"Stack '{stack.Name}', {count} keys", 
        }));
        RefreshBlendshapePanel();
        RefreshMixer();
        RefreshAnimationPanels();
        UpdateCurveboard();
        RefreshMenu();
    }
}
