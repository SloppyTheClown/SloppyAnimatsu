// Context and selection: switching a character/animation, toggling the Rigging/Animation context
// (which re-homes the dock and rebuilds the panels), the outliner selection, and Show/Hide/lock on
// the tree.
//
// InterfaceContext decides which UI the window shows, not which data exists. SetContext is the only
// place that switches it.
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
    private void SelectCharacter(Character? character, string? animationName, float? frame)
    {
        if (_autoRigger != null && _autoRigger.Character != character)
        {
            _autoRiggerDialog?.Close();
        }
        // The character left keeps its animation and time, and plays on in the background.
        if (_character != character) RememberCharacterPlayback();
        CharacterPlayback? remembered = character != null && _character != character && animationName == null
            && RememberedPlayback(character) is { Animation: not null } r ? r : null;
        _character = character;
        UpdateSharedBankMarks();
        _placement = null;
        _hotPoint = null;
        _syncing = true;
        _characters.SelectedItem = (_characters.ItemsSource as IEnumerable<Item<Character>>)?.FirstOrDefault((Item<Character> i) => i.Value == character);
        List<Item<SceneObject>> itemsSource = BankItems(character);
        _bankFolder = null;
        _animations.ItemsSource = itemsSource;
        _controllers.ItemsSource = null;
        _syncing = false;
        RefreshPicker();
        _selectedAll.RemoveAll((Object3D c) => character == null || _pose == null || _pose.Evaluator(character).IndexOf(c) < 0);
        List<Animation> source = character?.Animations.ToList() ?? new List<Animation>();
        Animation animation = ((animationName == "(rest pose)") ? null : (source.FirstOrDefault((Animation a) => a.Name == animationName || AnimationPath(a) == animationName) ?? character?.CurrentAnimation ?? source.FirstOrDefault()));
        if (remembered != null)
        {
            animation = remembered.Animation;
            // The playhead is shared by every character: it stays where it is.
            frame ??= _time;
        }
        SelectAnimation(animation, frame);
        RefreshMenu();
        RefreshSkinPanel();
    }

    /// <param name="switchContext">Only the context switch (<see cref="SetContext"/>) passes true.
    /// Everything else - picking another character, a bank row, a new or deleted animation - keeps
    /// the interface context the user chose: in Rigging the picked animation is only remembered as
    /// the character's current one (it opens when the user switches to Animation) and the rest pose
    /// stays on screen; in Animation a character without animations shows its rest pose.</param>
    private void SelectAnimation(Animation? animation, float? frame, bool reveal = true, bool switchContext = false)
    {
        // Each animation keeps its own playhead: the one being left stores the frame it was on
        // (in the scene's per-animation CurrentTime, which a save writes and a reopen reads), so
        // coming back to it - or reopening the file - lands on that frame again. Not an edit.
        if (_animation != null && _animation != animation)
        {
            _animation.Source.Set("CurrentTime", (int)MathF.Round(_time));
        }
        if (!switchContext && animation != null && _pose?.Model.Scene.Context == InterfaceContext.Rigging)
        {
            if (_character != null) _character.CurrentAnimation = animation;
            animation = null;
        }
        _animation = animation;
        _audio.Stop();
        if (_pose != null)
        {
            _pose.RiggingContext = animation == null;
            if (switchContext)
            {
                _pose.Model.Scene.Context = ((animation != null) ? InterfaceContext.Animation : InterfaceContext.Rigging);
            }
        }
        _syncing = true;
        _bankFolder = null;
        if (reveal) RevealInBank(animation);
        _animations.SelectedItem = (_animations.ItemsSource as IEnumerable<Item<SceneObject>>)?.FirstOrDefault((Item<SceneObject> i) => i.Value == animation);
        _syncing = false;
        _timeline.Animation = animation;
        _curveboard.Animation = animation;
        UpdateRange();
        SetTime(frame ?? ((float)(animation?.CurrentTime ?? animation?.TimeRange.Start ?? 0)));
        UpdateKeyCommands();
        RefreshPicker();
        DropHiddenShoeLocators();
        RefreshContextButton();
    }

    private void SetContext(InterfaceContext context)
    {
        if (_pose == null)
        {
            return;
        }
        if (context == InterfaceContext.Rigging)
        {
            CaptureRigOutlines();
            if (_animation != null)
            {
                SelectAnimation(null, null, switchContext: true);
            }
            else
            {
                _pose.Model.Scene.Context = InterfaceContext.Rigging;
                RefreshMenu();
            }
        }
        else if (_animation == null)
        {
            Animation animation = _character?.CurrentAnimation ?? _character?.Animations.FirstOrDefault();
            if (animation != null)
            {
                SelectAnimation(animation, null, switchContext: true);
            }
            else
            {
                _pose.Model.Scene.Context = InterfaceContext.Animation;
                RefreshMenu();
            }
        }
        _status.Text = ((context == InterfaceContext.Rigging) ? "Rigging context: drags edit the rest pose." : "Animation context.");
        _lastContext = context;
        DropHiddenShoeLocators();
        RefreshContextButton();
        ApplyDockLayout();
        Apply();
    }

    private void ToggleContext()
    {
        if (_pose == null)
        {
            _status.Text = "Open a scene to switch the interface context.";
        }
        else
        {
            RequestContext(MenuModel.OtherContext(_pose.Model.Scene.Context));
        }
    }

    /// <summary>
    /// The user's context switch (Tab, the context button, the menu): leaving Rigging with a
    /// reference-pose change the animations were not resolved against raises the modal REFERENCE
    /// POSE dialog first, and closing it without an answer stays in Rigging.
    /// </summary>
    private async void RequestContext(InterfaceContext context)
    {
        if (_pose == null) return;
        if (context == InterfaceContext.Animation && _pose.Model.Scene.Context == InterfaceContext.Rigging
            && !await LeaveRestPose())
        {
            return;
        }
        if (context == InterfaceContext.Animation && _pose.Model.Scene.Context == InterfaceContext.Rigging)
            await OfferRigCopy();
        SetContext(context);
    }

    private void RefreshContextButton()
    {
        bool flag = _pose != null;
        InterfaceContext context = _pose?.Model.Scene.Context ?? _lastContext;
        _contextLabel.Text = (flag ? ("Context: " + MenuModel.ContextName(context)) : "Context: —");
        _contextButton.Content = (flag ? ("Switch to " + MenuModel.ContextName(MenuModel.OtherContext(context))) : "Switch context");
        ToolTip.SetTip(_contextButton, "Swap the Rigging and Animation contexts (Tab).");
        DisabledHint.SetEnabled(_contextButton, flag, "an open scene");
        RefreshCharBank();
    }

    /// <summary>Set while a tree click is being applied, so SyncSelection leaves the tree's rows alone.</summary>
    private bool _outlinerDriving;

    private async void SelectFromOutliner(Object3D o, bool multi)
    {
        SyncFreezeButton();
        if (multi)
        {
            // Ctrl/Shift+click: the controllers among the tree's selected rows become the
            // selection, the clicked row last (the current one) when it is still selected.
            Character? ch = _character;
            if (ch == null || _pose == null) return;
            PoseEvaluator ev = _pose.Evaluator(ch);
            IReadOnlyList<Object3D> rows = _outliner.SelectedObjects;
            _selectedAll.Clear();
            _selectedAll.AddRange(rows.Where(r => ev.IndexOf(r) >= 0 && !ReferenceEquals(r, o)));
            Object3D? current = rows.Contains(o) && ev.IndexOf(o) >= 0 ? o : _selectedAll.LastOrDefault();
            if (current != null && ReferenceEquals(current, o)) _selectedAll.Add(o);
            _outlinerDriving = true;
            try { SyncSelection(current); }
            finally { _outlinerDriving = false; }
            _status.Text = $"{rows.Count} row(s) selected in the tree, {_selectedAll.Count} controller(s).";
            RefreshMenu();
            return;
        }
        // The Anim Bank shows the bank of the character the picked object belongs to.
        Character? owner = (o as Character) ?? (o as Mesh)?.BoundCharacter ?? o.Ancestor<Character>();
        if (owner != null && owner != _character)
        {
            if (!await LeaveRestPose())
            {
                return;
            }
            SelectCharacter(owner, null, null);
            _outliner.Select(o);
        }
        Character? character = _character;
        if (character != null && _pose != null && _pose.Evaluator(character).IndexOf(o) >= 0)
        {
            _outlinerDriving = true;
            try { Select(o); }
            finally { _outlinerDriving = false; }
            return;
        }
        // A row that is not a controller (the character itself, a mesh, a group) selects that
        // object: the TRANSFORM panel shows its own transform, and its gizmo places it (and
        // everything below it) in the scene.
        if (o is not Blendshape)
        {
            var placed = o;
            _selectedAll.Clear();
            _selectedAll.Add(placed);
            _outlinerDriving = true;
            try { SyncSelection(placed); }
            finally { _outlinerDriving = false; }
            _status.Text = placed is Character
                ? $"Character '{placed.Name}': its Anim Bank is shown; drag the gizmo to move or turn the whole character."
                : $"{placed.Name} ({placed.ClassName}): drag the gizmo to move or turn it with everything below it.";
            RefreshProperties();
            RefreshMaterial();
            RefreshMenu();
            return;
        }
        // A row that is not a controller (a mesh, ...) replaces the controller
        // selection; otherwise the previous controller stays _selected and Properties/Material
        // keep showing it instead of the clicked row.
        if (_selectedAll.Count > 0)
        {
            _selectedAll.Clear();
            _outlinerDriving = true;
            try { SyncSelection(null); }
            finally { _outlinerDriving = false; }
        }
        _status.Text = (o is Character)
            ? $"Character '{o.Name}': its Anim Bank is shown."
            : $"{o.Name} ({o.ClassName}) is not a controller of {character?.Name ?? "a character"}.";
        RefreshProperties();
        RefreshMaterial();
        RefreshMenu();
    }

    private void ChangeShown(IReadOnlyList<Object3D> objects, bool shown)
    {
        if (_pose == null)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session != null)
        {
            RunKeyEdit(delegate
            {
                if (shown)
                {
                    session.Show(objects);
                }
                else
                {
                    session.Hide(objects);
                }
            });
        }
        else
        {
            int value = (shown ? Outliner.Show(objects) : Outliner.Hide(objects));
            _status.Text = $"{(shown ? "Showed" : "Hid")} {value} object(s).";
            _dirty = true;
            UpdateTitle();
            Apply();
        }
        _outliner.Build(_pose.Model.Scene);
        RefreshProperties();
        RefreshMaterial();
    }

    private void ChangeLock(IReadOnlyList<Mesh> meshes, bool locked)
    {
        if (_pose == null)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session != null)
        {
            RunKeyEdit(delegate
            {
                session.SetMeshesLocked(meshes, locked);
            });
        }
        else
        {
            int value = Outliner.SetMeshesLocked(meshes, locked);
            _status.Text = $"{(locked ? "Locked" : "Unlocked")} {value} mesh(es).";
            _dirty = true;
            UpdateTitle();
            Apply();
        }
        _outliner.Build(_pose.Model.Scene);
    }

    private void ChangeAllShown(bool shown)
    {
        if (_pose == null)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session != null)
        {
            RunKeyEdit(delegate
            {
                if (shown)
                {
                    session.ShowAll();
                }
                else
                {
                    session.HideAll();
                }
            });
        }
        else
        {
            int value = (shown ? Outliner.ShowAll(_pose.Model.Scene) : Outliner.HideAll(_pose.Model.Scene));
            _status.Text = $"{(shown ? "Showed" : "Hid")} {value} object(s).";
            _dirty = true;
            UpdateTitle();
            Apply();
        }
        _outliner.Build(_pose.Model.Scene);
    }

    /// <summary>
    /// The rows the hierarchy panel's menu acts on: the tree selection (a right-click on a row
    /// outside it has just made that row the selection), the clicked row last.
    /// </summary>
    private List<Object3D> TreeMenuRoots()
    {
        var roots = _outliner.SelectedObjects.ToList();
        if (_outliner.Selected is { } current)
        {
            roots.Remove(current);
            roots.Add(current);
        }
        if (roots.Count == 0) _status.Text = "Pick a row of the hierarchy panel first.";
        return roots;
    }

    /// <summary>Show hierarchy / Hide hierarchy: the picked rows and everything under them.</summary>
    private void ShowTreeHierarchy(bool shown)
    {
        var roots = TreeMenuRoots();
        if (roots.Count == 0) return;
        var objects = roots.SelectMany(Outliner.Subtree).Concat(_outliner.Hierarchy(roots))
            .DistinctBy(o => (object)o.Source, ReferenceEqualityComparer.Instance).ToList();
        ChangeShown(objects, shown);
    }

    /// <summary>Expand hierarchy / Collapse hierarchy: opens or closes the picked rows to their last level.</summary>
    private void ExpandTreeHierarchy(bool expanded)
    {
        foreach (var root in TreeMenuRoots()) _outliner.SetExpanded(root, expanded, deep: true);
    }

    /// <summary>
    /// Select hierarchy: every row under the picked rows joins the tree selection - joints, meshes,
    /// folders, controllers, cameras alike - and the current character's controllers among them
    /// become the viewport selection.
    /// </summary>
    private void SelectTreeHierarchy()
    {
        var roots = TreeMenuRoots();
        if (roots.Count == 0) return;
        var rows = _outliner.Hierarchy(roots);
        _outliner.SetSelection(rows, roots[^1]);
        SyncControllersFromTree(roots[^1], $"Selected the hierarchy of '{roots[^1].Name}'");
    }

    /// <summary>Ctrl+A in the viewport: every bone and handle (controller) of the current character.</summary>
    private void SelectAllCharacterControllers()
    {
        Character? character = _character;
        if (character == null || _pose == null)
        {
            _status.Text = "No current character to select.";
            return;
        }
        PoseEvaluator evaluator = _pose.Evaluator(character);
        Object3D? current = _selectedAll.LastOrDefault(o => evaluator.IndexOf(o) >= 0);
        _selectedAll.Clear();
        _selectedAll.AddRange(evaluator.Controllers);
        if (current != null && _selectedAll.Remove(current)) _selectedAll.Add(current);
        SyncSelection(_selectedAll.LastOrDefault());
        _status.Text = $"Selected every bone and handle of {character.Name}: {_selectedAll.Count} controller(s).";
    }

    /// <summary>Ctrl+A in the hierarchy panel (or its Select all row): every row is selected.</summary>
    private void SelectAllTreeRows() => SyncControllersFromTree(_outliner.Selected, "Selected every row");

    /// <summary>
    /// Makes the current character's controllers among the tree's selected rows the viewport
    /// selection, <paramref name="current"/> last when it is one, and leaves the tree's rows as they are.
    /// </summary>
    private void SyncControllersFromTree(Object3D? current, string what)
    {
        IReadOnlyList<Object3D> rows = _outliner.SelectedObjects;
        Character? character = _character;
        if (character != null && _pose != null)
        {
            PoseEvaluator evaluator = _pose.Evaluator(character);
            _selectedAll.Clear();
            _selectedAll.AddRange(rows.Where(r => evaluator.IndexOf(r) >= 0));
            Object3D? cur = current != null && evaluator.IndexOf(current) >= 0 ? current : _selectedAll.LastOrDefault();
            if (cur != null)
            {
                _selectedAll.Remove(cur);
                _selectedAll.Add(cur);
            }
            _outlinerDriving = true;
            try { SyncSelection(cur); }
            finally { _outlinerDriving = false; }
        }
        _status.Text = $"{what}: {rows.Count} row(s) in the tree, {_selectedAll.Count} controller(s).";
        RefreshMenu();
    }

    private async Task DeleteTreeObject()
    {
        Object3D selected = _outliner.Selected;
        if (selected == null)
        {
            _status.Text = "Pick a row of the hierarchy panel first.";
            return;
        }
        // Meshes, characters and other whole objects go with their subtrees (a character with its
        // Anim Bank and bound meshes); rig rows keep the joint/controller delete below.
        IReadOnlyList<Object3D> wholeObjects = Outliner.TreeSceneObjects(_outliner.SelectedObjects.ToArray());
        if (wholeObjects.Count > 0)
        {
            // A character takes its rig, Anim Bank and meshes with it: ask first (a SloppyAnimatsu
            // safeguard; the scripted --bank delete-objects does not ask).
            List<Character> characters = wholeObjects.OfType<Character>().ToList();
            if (characters.Count > 0 && !await ConfirmDeleteCharacters(characters, wholeObjects.Count - characters.Count))
            {
                _status.Text = "Delete cancelled.";
                return;
            }
            DeleteSceneObjects(wholeObjects);
            return;
        }
        PoseEditSession session = Session;
        if (session == null)
        {
            return;
        }
        // The app deletes tree objects in either context; the edit is one undo step with the
        // character's whole subtree (its animations' tracks included) in the snapshot.
        RigEditor rigEditor = RigEditorFor("delete tree objects", anyContext: true);
        if (rigEditor == null)
        {
            return;
        }
        TreeDeletePlan plan = Outliner.PlanTreeDelete(rigEditor, _outliner.SelectedObjects.ToArray());
        if (!plan.Ok)
        {
            _status.Text = plan.Refusal;
            return;
        }
        RunRigEdit($"Deleted {plan.Count} object(s) from the tree", delegate(RigEditor e)
        {
            e.DeleteControllers(plan.Controllers);
            e.DeleteJoints(plan.Joints);
            return (Object3D?)null;
        }, anyContext: true);
    }

    private const string SceneDeleteStep = "DELETE OBJECTS: ";

    /// <summary>
    /// Deletes whole objects (<see cref="Outliner.DeleteSceneObjects"/>) as one undo step holding the
    /// whole document, then rebuilds the scene view as opening does, since meshes and characters
    /// come and go.
    /// </summary>
    /// <summary>Asks before a character goes: it takes its rig, Anim Bank and bound meshes with it.</summary>
    private async Task<bool> ConfirmDeleteCharacters(IReadOnlyList<Character> characters, int others)
    {
        string what = characters.Count == 1
            ? $"the character '{characters[0].Name}'"
            : $"{characters.Count} characters ({string.Join(", ", characters.Select(c => "'" + c.Name + "'"))})";
        string extra = others > 0 ? $" and {others} other object{(others == 1 ? "" : "s")}" : "";
        Button delete = new Button { Content = "Delete", IsDefault = true };
        Button cancel = new Button { Content = "Cancel", IsCancel = true };
        Window window = new Window
        {
            Title = "Delete character",
            Width = 460.0,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 12.0,
                Margin = new Thickness(18.0),
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Delete {what}{extra}?\n\nIts rig, Anim Bank (every animation) and bound meshes go with it. Edit > Undo (Ctrl+Z) brings it back.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8.0,
                        Children = { delete, cancel }
                    }
                }
            }
        };
        bool confirmed = false;
        delete.Click += delegate { confirmed = true; window.Close(); };
        cancel.Click += delegate { window.Close(); };
        window.Opened += delegate { delete.Focus(); };
        await window.ShowDialog(this);
        return confirmed;
    }

    private void DeleteSceneObjects(IReadOnlyList<Object3D> objects)
    {
        if (_pose == null || _document == null || _drag != null)
        {
            return;
        }
        Scene scene = _pose.Model.Scene;
        bool Deleted(Character c) => objects.Any(o =>
            ReferenceEquals(o.Source, c.Source) || IsUnder(c.Source, o.Source));
        // The character to show afterwards: it has to exist on both sides of the edit, so undo and
        // redo can re-evaluate it.
        Character? owner = (_character != null && !Deleted(_character) ? _character : null)
            ?? scene.Characters.FirstOrDefault(c => !Deleted(c));
        EditSnapshot before = EditSnapshot.TakeScene(owner, scene);
        int count;
        try
        {
            count = Outliner.DeleteSceneObjects(scene, objects);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            before.Restore();
            _status.Text = "Delete failed: " + ex.Message;
            return;
        }
        string names = string.Join(", ", objects.Select(o => o.Name ?? "(unnamed)"));
        _history.Push(new EditRecord(SceneDeleteStep + names, before, EditSnapshot.TakeScene(owner, scene)));
        _document.Document.Dirty = true;
        RebuildAfterSceneDelete();
        _status.Text = $"Deleted {count} object(s): {names}.";
        MemoryLog.Mark("delete " + names, _history.Count);
    }

    private static bool IsUnder(AktObject node, AktObject ancestor)
    {
        for (AktObject? p = node.Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }

    /// <summary>The scene view after objects were deleted, or after such a step was undone or redone.</summary>
    private void RebuildAfterSceneDelete()
    {
        if (_document == null) return;
        ShowDocument(_document.Document);
        if (_pose == null) return;
        _dirty = true;
        UpdateTitle();
        _outliner.Build(_pose.Model.Scene);
        AfterCharBankChange();
    }

    private void AfterSceneDeleteHistoryStep(EditRecord? step)
    {
        if (IsSceneDeleteStep(step)) RebuildAfterSceneDelete();
    }

    /// <summary>A step that holds the whole document: a scene delete or a whole-object duplicate.</summary>
    private static bool IsSceneDeleteStep(EditRecord? step) =>
        step?.Description is { } d &&
        (d.StartsWith(SceneDeleteStep, StringComparison.Ordinal) || d.StartsWith(SceneDuplicateStep, StringComparison.Ordinal));

    /// <summary>
    /// Undoes or redoes the next step when it is a scene delete. Such a step holds the whole
    /// document and the view is rebuilt from it, so it needs no character session: deleting the
    /// last character leaves none, and its undo must still work.
    /// </summary>
    private bool StepSceneDelete(bool undo)
    {
        if (!IsSceneDeleteStep(undo ? _history.NextUndo : _history.NextRedo)) return false;
        EditRecord record = (undo ? _history.Undo() : _history.Redo())!;
        (undo ? record.Before : record.After).Restore();
        if (_document != null) _document.Document.Dirty = true;
        RebuildAfterSceneDelete();
        AfterSharedBankHistoryStep(record);
        _status.Text = (undo ? "Undid: " : "Redid: ") + record.Description;
        MemoryLog.Mark(_status.Text, _history.Count);
        return true;
    }
}
