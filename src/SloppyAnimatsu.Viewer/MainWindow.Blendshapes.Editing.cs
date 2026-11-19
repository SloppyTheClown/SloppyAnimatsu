// Blendshape editing, state and SetUp: refreshing the panel, writing a value or a face, the
// point-of-view camera toggle, and the SetUp import/export that swaps in a one-blendshape scene.
//
// These methods only read and write SloppyAnimatsu.Core own BlendshapeSession; the widgets they update are
// built in MainWindow.Blendshapes.cs.
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
    private void RefreshBlendshapePanel(bool rebuild = false)
    {
        if (_blendshapeStatus == null || _blendshapeControllers == null || _blendshapeFaces == null)
        {
            return;
        }
        if (_character == null || _pose == null)
        {
            if (rebuild)
            {
                _blendshapeControllers.Children.Clear();
                _blendshapeFaces.Children.Clear();
                _blendshapeFields.Clear();
            }
            _blendshapeStatus.Text = "Show a character to list its blendshapes.";
            return;
        }
        if (_blendshapes == null || _blendshapes.Character != _character)
        {
            _blendshapes = new BlendshapeSession(_character);
            _pointOfViewBefore = null;
            rebuild = true;
        }
        BlendshapeSession blendshapes = _blendshapes;
        blendshapes.Animation = _animation;
        blendshapes.Stack = _timeline.ActiveStack;
        blendshapes.Time = _time;
        if (rebuild)
        {
            BuildBlendshapeControllerRows(blendshapes);
            BuildBlendshapeFaceCells(blendshapes);
        }
        IReadOnlyList<BlendshapeControllerRow> controllerRows = blendshapes.ControllerRows;
        _syncing = true;
        for (int i = 0; i < _blendshapeFields.Count && i < controllerRows.Count; i++)
        {
            (BlendshapeController Controller, NumericUpDown X, NumericUpDown Y, TextBlock Info) tuple = _blendshapeFields[i];
            NumericUpDown item = tuple.X;
            NumericUpDown item2 = tuple.Y;
            TextBlock item3 = tuple.Info;
            float[] array = blendshapes.Values(controllerRows[i].Controller);
            (float MinX, float MaxX, float MinY, float MaxY) tuple2 = blendshapes.Range(controllerRows[i].Controller);
            float item4 = tuple2.MinX;
            float item5 = tuple2.MaxX;
            float item6 = tuple2.MinY;
            float item7 = tuple2.MaxY;
            item.Minimum = (decimal)item4;
            item.Maximum = (decimal)item5;
            item2.Minimum = (decimal)item6;
            item2.Maximum = (decimal)item7;
            item.Value = (decimal)Math.Round(array[0], 3);
            item2.Value = (decimal)Math.Round(array[1], 3);
            item3.Text = ((controllerRows[i].KeyIndex >= 0) ? $"key {controllerRows[i].KeyIndex} of the current stack" : "no key at the playhead: a value edit adds one");
        }
        _syncing = false;
        AnimStack currentStack = blendshapes.CurrentStack;
        _blendshapeStatus.Text = $"{blendshapes.Controllers.Count} controller(s), {blendshapes.Faces.Count} face(s), {blendshapes.Sights.Count} sight marker(s). " + ((currentStack == null) ? "No stack: show an animation to key values." : $"Editing '{currentStack.Name}' at frame {blendshapes.Frame}.");
    }

    private void ApplyBlendshapeValue(BlendshapeController controller, float x, float y)
    {
        BlendshapeSession blendshapes = _blendshapes;
        if (blendshapes == null || _pose == null)
        {
            return;
        }
        blendshapes.Animation = _animation;
        blendshapes.Stack = _timeline.ActiveStack;
        blendshapes.Time = _time;
        AnimStack currentStack = blendshapes.CurrentStack;
        if (currentStack == null)
        {
            _status.Text = "Pick an animation and a stack on the timeline before setting a blendshape value.";
            RefreshBlendshapePanel();
            return;
        }
        EditSnapshot editSnapshot = EditSnapshot.Take(currentStack);
        BlendshapeValueWrite blendshapeValueWrite;
        try
        {
            blendshapeValueWrite = blendshapes.SetValues(controller, x, y);
        }
        catch (Exception ex)
        {
            editSnapshot.Restore();
            _status.Text = "Could not set the blendshape value: " + ex.Message;
            RefreshBlendshapePanel();
            return;
        }
        _history.Push(new EditRecord($"Set blendshape '{controller.Name}' to {blendshapeValueWrite.X:0.##}/{blendshapeValueWrite.Y:0.##}", editSnapshot, EditSnapshot.Take(currentStack)));
        _dirty = true;
        UpdateTitle();
        _status.Text = $"Blendshape '{controller.Name}' = {blendshapeValueWrite.X:0.##}/{blendshapeValueWrite.Y:0.##} at key {blendshapeValueWrite.KeyIndex} of '{currentStack.Name}'" + (blendshapeValueWrite.Created ? " (new key)." : ".");
        AfterKeyChange();
    }

    private void ApplyBlendshapeFace(BlendshapeFace face)
    {
        BlendshapeSession blendshapes = _blendshapes;
        if (blendshapes == null || _pose == null)
        {
            return;
        }
        blendshapes.Animation = _animation;
        blendshapes.Stack = _timeline.ActiveStack;
        blendshapes.Time = _time;
        AnimStack currentStack = blendshapes.CurrentStack;
        if (currentStack == null)
        {
            _status.Text = "Pick an animation and a stack on the timeline before applying a face.";
            return;
        }
        EditSnapshot editSnapshot = EditSnapshot.Take(currentStack);
        int num;
        try
        {
            num = blendshapes.ApplyFace(face);
        }
        catch (Exception ex)
        {
            editSnapshot.Restore();
            _status.Text = "Could not apply the blendshape face: " + ex.Message;
            return;
        }
        if (num == 0)
        {
            editSnapshot.Restore();
            _status.Text = $"Face '{face.Name}' references no controller of {_character?.Name} in the current stack.";
        }
        else
        {
            _history.Push(new EditRecord("Apply blendshape face '" + face.Name + "'", editSnapshot, EditSnapshot.Take(currentStack)));
            _dirty = true;
            UpdateTitle();
            _status.Text = $"Applied face '{face.Name}' to {num} controller(s) at the keys of '{currentStack.Name}'.";
            AfterKeyChange();
        }
    }

    private void SetBlendshapePointOfView()
    {
        if (_blendshapeStatus == null)
        {
            return;
        }
        BlendshapeSession blendshapes = _blendshapes;
        if (blendshapes == null || _pose == null || _character == null)
        {
            _blendshapeStatus.Text = "Show a character to store a blendshape point of view.";
            return;
        }
        List<Mesh> list = blendshapes.Controllers.Select((BlendshapeController controller) => controller.Mesh).OfType<Mesh>().Distinct()
            .ToList();
        if (list.Count == 0)
        {
            _blendshapeStatus.Text = "No blendshape controller on this character: there is no mesh to store the view on.";
            return;
        }
        OrbitCamera camera = _viewport.Renderer.Camera;
        Matrix4x4 worldMatrix = camera.WorldMatrix;
        float distance = camera.Distance;
        EditSnapshot editSnapshot = EditSnapshot.TakePointOfView(_character, list);
        try
        {
            foreach (Mesh item in list)
            {
                blendshapes.SetPointOfView(item, worldMatrix, distance);
            }
        }
        catch (Exception ex)
        {
            editSnapshot.Restore();
            _blendshapeStatus.Text = "Could not store the point of view: " + ex.Message;
            return;
        }
        _history.Push(new EditRecord($"Set blendshape point of view ({list.Count} mesh(es))", editSnapshot, EditSnapshot.TakePointOfView(_character, list)));
        _dirty = true;
        UpdateTitle();
        RefreshHotPointMarkers();
        RefreshBlendshapePanel();
        _blendshapeStatus.Text = $"Stored the viewport camera (distance {distance:0.##}) on {list.Count} mesh(es): " + string.Join(", ", list.Select((Mesh mesh) => mesh.Name));
        _status.Text = $"Blendshape point of view stored on {list.Count} mesh(es).";
    }

    private void ToggleBlendshapeCamera()
    {
        if (_blendshapeStatus == null || _pose == null || _character == null)
        {
            return;
        }
        OrbitCamera camera = _viewport.Renderer.Camera;
        (Matrix4x4, float)? pointOfViewBefore = _pointOfViewBefore;
        if (pointOfViewBefore.HasValue)
        {
            (Matrix4x4, float) valueOrDefault = pointOfViewBefore.GetValueOrDefault();
            camera.Restore(valueOrDefault.Item1, valueOrDefault.Item2);
            _pointOfViewBefore = null;
            _viewport.RequestNextFrameRendering();
            _blendshapeStatus.Text = "Blendshape camera: back to the previous view.";
            _status.Text = "Blendshape camera: back to the previous view.";
            return;
        }
        Mesh mesh = null;
        Matrix4x4? matrix4x = null;
        BlendshapeSession blendshapes = _blendshapes;
        if (blendshapes != null)
        {
            foreach (Mesh mesh2 in _character.Meshes)
            {
                Matrix4x4? matrix4x2 = blendshapes.PointOfView(mesh2);
                if (matrix4x2.HasValue)
                {
                    Matrix4x4 valueOrDefault2 = matrix4x2.GetValueOrDefault();
                    mesh = mesh2;
                    matrix4x = valueOrDefault2;
                    break;
                }
            }
        }
        if (mesh != null && matrix4x.HasValue)
        {
            Matrix4x4 valueOrDefault3 = matrix4x.GetValueOrDefault();
            _pointOfViewBefore = (camera.WorldMatrix, camera.Distance);
            camera.Restore(valueOrDefault3, mesh.BlendshapeCameraDistanceFromAnchor);
            _viewport.RequestNextFrameRendering();
            RefreshHotPointMarkers();
            _blendshapeStatus.Text = $"Blendshape camera: '{mesh.Name}' at distance {mesh.BlendshapeCameraDistanceFromAnchor:0.##}.";
            _status.Text = $"Blendshape camera: '{mesh.Name}' at distance {mesh.BlendshapeCameraDistanceFromAnchor:0.##}.";
            return;
        }
        _blendshapeStatus.Text = "No mesh of " + _character.Name + " stores a blendshape point of view: use \"Set point of view\" first.";
        _status.Text = "No stored blendshape point of view on this character.";
    }

    private Control SetUpButtons()
    {
        Button button = new Button
        {
            Content = "Export set up...",
            FontSize = 11.0
        };
        button.Click += async delegate
        {
            await ExportBlendshapeSetUp();
        };
        Button button2 = new Button
        {
            Content = "Import set up...",
            FontSize = 11.0
        };
        button2.Click += async delegate
        {
            await ImportBlendshapeSetUp();
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6.0,
            Children = 
            {
                (Control)button,
                (Control)button2
            }
        };
    }

    private Mesh? SetUpMesh(BlendshapeSession session)
    {
        return (_selected as BlendshapeController)?.Mesh ?? session.Controllers.Select((BlendshapeController controller) => controller.Mesh).OfType<Mesh>().FirstOrDefault() ?? _character?.Meshes.FirstOrDefault();
    }

    private async Task ExportBlendshapeSetUp()
    {
        if (_blendshapeStatus == null || _character == null)
        {
            return;
        }
        BlendshapeSession session = _blendshapes;
        if (session == null)
        {
            _blendshapeStatus.Text = "Show a character to export a blendshape set up.";
            return;
        }
        Mesh mesh = SetUpMesh(session);
        if (mesh == null)
        {
            _blendshapeStatus.Text = "'" + _character.Name + "' has no mesh: there is nothing to export.";
            return;
        }
        IReadOnlyList<BlendshapeSetUpController> setup = BlendshapeSetUp.Capture(mesh);
        if (setup.Count == 0)
        {
            _blendshapeStatus.Text = "'" + mesh.Name + "' has no blendshape controller: there is nothing to export.";
            return;
        }
        string path = await TextPromptDialog.Ask(this, "Export blendshape set up", "File (.bsu)", mesh.Name + ".bsu");
        if (path == null)
        {
            _blendshapeStatus.Text = "Export set up cancelled; no file was written.";
            return;
        }
        try
        {
            File.WriteAllBytes(path, BlendshapeSetUp.Write(setup));
        }
        catch (Exception ex)
        {
            Exception e = ex;
            _blendshapeStatus.Text = "Could not write the blendshape set up: " + e.Message;
            return;
        }
        int channels = setup.Sum((BlendshapeSetUpController controller) => controller.Channels.Count);
        int infos = setup.Sum((BlendshapeSetUpController controller) => controller.Channels.Sum((BlendshapeSetUpChannel channel) => channel.Infos.Count));
        _blendshapeStatus.Text = $"Exported '{mesh.Name}' to {path}: {setup.Count} controller(s), {channels} channel(s), {infos} info entr(ies).";
        _status.Text = $"Blendshape set up of '{mesh.Name}' exported to {path}.";
    }

    private async Task ImportBlendshapeSetUp()
    {
        if (_blendshapeStatus == null || _character == null || _pose == null)
        {
            return;
        }
        BlendshapeSession session = _blendshapes;
        if (session == null)
        {
            _blendshapeStatus.Text = "Show a character to import a blendshape set up.";
            return;
        }
        Mesh mesh = SetUpMesh(session);
        if (mesh == null)
        {
            _blendshapeStatus.Text = "'" + _character.Name + "' has no mesh to apply a set up to.";
            return;
        }
        string path = await TextPromptDialog.Ask(this, "Import blendshape set up", "File (.bsu)", "");
        if (path == null)
        {
            _blendshapeStatus.Text = "Import set up cancelled; the scene is unchanged.";
            return;
        }
        IReadOnlyList<BlendshapeSetUpController> setup;
        try
        {
            setup = BlendshapeSetUp.Read(File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            Exception e = ex;
            _blendshapeStatus.Text = "Could not read the blendshape set up: " + e.Message;
            return;
        }
        if (setup.Count == 0)
        {
            _blendshapeStatus.Text = Path.GetFileName(path) + " carries no controller; the scene is unchanged.";
            _status.Text = "The blendshape set up file is empty.";
            return;
        }
        EditSnapshot before = EditSnapshot.TakeRig(_character, _pose.Model.Scene);
        BlendshapeSetUpApplyResult result;
        try
        {
            result = BlendshapeSetUp.Apply(mesh, setup);
        }
        catch (Exception ex)
        {
            Exception e2 = ex;
            before.Restore();
            _pose.ReloadRestPose(_character);
            RefreshRigStructure();
            RefreshBlendshapePanel(rebuild: true);
            _blendshapeStatus.Text = "Could not apply the blendshape set up: " + e2.Message;
            return;
        }
        _pose.ReloadRestPose(_character);
        _history.Push(new EditRecord($"Import blendshape set up '{Path.GetFileName(path)}' onto '{mesh.Name}'", before, EditSnapshot.TakeRig(_character, _pose.Model.Scene)));
        _dirty = true;
        UpdateTitle();
        RefreshRigStructure();
        RefreshBlendshapePanel(rebuild: true);
        _blendshapeStatus.Text = $"Imported {Path.GetFileName(path)} onto '{mesh.Name}': {result}";
        _status.Text = $"Blendshape set up applied to '{mesh.Name}': {result.ControllersCreated + result.ControllersUpdated} controller(s), {result.ChannelsCreated + result.ChannelsUpdated} channel(s).";
    }
}
