// Duplicate hierarchy, the TREE (scene hierarchy) panel's way to copy objects. Ctrl+C / Ctrl+V in
// the tree copy and paste keys (poses), as everywhere else in the window.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    private const string SceneDuplicateStep = "DUPLICATE OBJECTS: ";

    /// <summary>
    /// Duplicate hierarchy, in either context: whole objects (meshes, characters, groups) are copied
    /// with everything under them as one scene step; otherwise the selected joints are copied with
    /// every joint below them, their poses and their tracks, as one rig step.
    /// </summary>
    private void DuplicateSelectedHierarchy()
    {
        if (_pose == null || _document == null || _drag != null) return;
        var selection = _outliner.SelectedObjects.Concat(_selectedAll)
            .DistinctBy(o => (object)o.Source, ReferenceEqualityComparer.Instance).ToList();
        var wholeObjects = Outliner.TreeSceneObjects(selection);
        if (wholeObjects.Count > 0)
        {
            DuplicateSceneObjects(wholeObjects);
            return;
        }
        var joints = selection.OfType<Joint>().ToList();
        if (joints.Count == 0)
        {
            _status.Text = selection.Count == 0
                ? "Select the objects to duplicate in the tree or the viewport."
                : "Duplicate hierarchy copies joints, meshes, characters and groups; IK, reverse-foot and camera rigs cannot be duplicated.";
            return;
        }
        string description = joints.Count == 1
            ? $"Duplicated the hierarchy of '{joints[0].Name}'"
            : $"Duplicated {joints.Count} joint hierarchies";
        IReadOnlyList<Joint> copies = [];
        RunRigEdit(description, e =>
        {
            copies = e.DuplicateHierarchy(joints);
            return copies.Count > 0 ? copies[^1] : null;
        }, action: "duplicate joints", anyContext: true);
        if (copies.Count > 1)
        {
            // Select every copied root, the last one current.
            _selectedAll.Clear();
            _selectedAll.AddRange(copies);
            SyncSelection(copies[^1]);
        }
    }

    /// <summary>
    /// Copies whole objects (<see cref="Outliner.DuplicateSceneObjects"/>) as one undo step holding
    /// the whole document, then rebuilds the scene view as a scene delete does.
    /// </summary>
    private void DuplicateSceneObjects(IReadOnlyList<Object3D> objects)
    {
        Scene scene = _pose!.Model.Scene;
        EditSnapshot before = EditSnapshot.TakeScene(_character, scene);
        IReadOnlyList<Object3D> copies;
        try
        {
            copies = Outliner.DuplicateSceneObjects(scene, objects);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            before.Restore();
            _status.Text = "Duplicate failed: " + ex.Message;
            return;
        }
        string names = string.Join(", ", copies.Select(o => o.Name ?? "(unnamed)"));
        _history.Push(new EditRecord(SceneDuplicateStep + names, before, EditSnapshot.TakeScene(_character, scene)));
        _document!.Document.Dirty = true;
        RebuildAfterSceneDelete();
        _status.Text = $"Duplicated {copies.Count} object(s) as {names}.";
        MemoryLog.Mark("duplicate " + names, _history.Count);
    }
}
