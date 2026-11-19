using Avalonia.Controls;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

// The scene tree's inline rename and clickable toggle, and the Rename Tool window
// Each change is one undo step of the character's session.
public partial class MainWindow
{
    private RenameToolDialog? _renameTool;

    /// <summary>
    /// The pointer button of a tree row: sets the object clickable or not. A row inside the tree's
    /// multi-selection changes the whole selection, like the Set as unpickable.
    /// </summary>
    private void ChangePickable(Object3D o, bool pickable)
    {
        if (_pose == null) return;
        var selected = _outliner.SelectedObjects;
        IReadOnlyList<Object3D> objects = selected.Contains(o) ? selected : [o];
        if (Session is { } session)
        {
            RunKeyEdit(() => session.SetPickable(objects, pickable));
        }
        else
        {
            int changed = Outliner.SetPickable(objects, pickable);
            _status.Text = $"{changed} object(s) set {(pickable ? "clickable" : "unclickable")}.";
            _dirty = true;
            UpdateTitle();
        }
        // An object that can no longer be clicked leaves the viewport selection.
        if (!pickable && _selectedAll.RemoveAll(x => !Outliner.IsPickable(x)) > 0) SelectionChanged();
        _outliner.Build(_pose.Model.Scene);
        RefreshProperties();
    }

    /// <summary>An inline rename committed in the tree.</summary>
    private void RenameFromOutliner(Object3D o, string name) => RenameTreeObjects([(o, name)]);

    private void RenameTreeObjects(IReadOnlyList<(Object3D Object, string Name)> renames)
    {
        if (_pose == null || renames.Count == 0) return;
        if (Session is { } session)
        {
            RunKeyEdit(() => session.RenameTreeObjects(renames));
        }
        else
        {
            foreach (var (o, name) in renames) RenameTool.RenameTreeObject(o, name);
            _status.Text = $"Renamed {renames.Count} object(s).";
            _dirty = true;
            UpdateTitle();
        }
        _outliner.Build(_pose.Model.Scene);
        RefreshProperties();
        _renameTool?.Refresh();
    }

    /// <summary>Opens the Rename Tool, or brings it forward when it is already open.</summary>
    private void OpenRenameTool()
    {
        if (_renameTool != null)
        {
            _renameTool.Activate();
            _renameTool.Refresh();
            return;
        }
        _renameTool = new RenameToolDialog(RenameTargets, ApplyRenameTool);
        _renameTool.Closed += (_, _) => _renameTool = null;
        _renameTool.Show(this);
    }

    /// <summary>The panel the Rename Tool works on (the one clicked last) and its selection's names.</summary>
    private (string Panel, IReadOnlyList<string> Names) RenameTargets() =>
        _renameFromBank
            ? ("Anim Bank", RenameBankTargets().Select(i => i.Name ?? "").ToList())
            : ("Scene tree", _outliner.SelectedObjects.Select(o => o.Name ?? "").ToList());

    /// <summary>The bank's selection, or the current folder / animation when nothing is selected.</summary>
    private List<SceneObject> RenameBankTargets()
    {
        var items = SelectedBankItems();
        if (items.Count == 0 && ((SceneObject?)_bankFolder ?? _animation) is { } current) items.Add(current);
        return items;
    }

    private void ApplyRenameTool(RenameToolOptions options)
    {
        if (_renameFromBank)
        {
            var renames = RenameBankTargets()
                .Select((item, i) => (Item: item, Name: RenameTool.Apply(item.Name ?? "", options, i)))
                .Where(r => r.Name != r.Item.Name).ToList();
            if (renames.Count == 0) return;
            RunBankEdit(session => session.RenameBankItems(renames));
            _renameTool?.Refresh();
            return;
        }
        var tree = _outliner.SelectedObjects
            .Select((o, i) => (Object: o, Name: RenameTool.Apply(o.Name ?? "", options, i)))
            .Where(r => r.Name != r.Object.Name).ToList();
        if (tree.Count == 0)
        {
            _status.Text = "Rename Tool: nothing in the scene tree's selection changes.";
            return;
        }
        RenameTreeObjects(tree);
    }
}
