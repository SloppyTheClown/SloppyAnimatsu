// Skin Workshop weights: the per-joint weight rows and their lock/min/max columns, the weight writer
// (Set, Spinner, per-row), hide/show, copy/paste, and the viewport painting (DabAt, the Scanner
// readout in ShowScan).
//
// A stroke is one undo record: RunSkinEdit takes the snapshot before the edit and pushes it after, so
// painting cannot leave partial undo state. The panel that shows the rows is MainWindow.SkinWorkshop.cs.
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
    private Control BuildSkinRow(SkinPaintSession session, SkinWorkshop.Row row)
    {
        Grid grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,38,76,48,26"),
            Height = 22.0,
            Background = Brushes.Transparent
        };
        TextBlock textBlock = new TextBlock
        {
            Text = row.Name,
            FontSize = 11.0,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        textBlock.PointerPressed += delegate
        {
            if (!_rebuildingSkin)
            {
                SelectPaintedJoint(row.Joint);
            }
        };
        grid.Children.Add(textBlock);
        TextBlock textBlock2 = new TextBlock
        {
            Text = FormatFraction(row.MinWeight),
            FontSize = 11.0,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.8
        };
        Grid.SetColumn(textBlock2, 1);
        grid.Children.Add(textBlock2);
        _skinMinWeights[row.Joint] = textBlock2;
        SpinnerField field = new SpinnerField
        {
            Step = 0.1f
        };
        field.Show(row.Weight, row.Editable);
        field.Committed += delegate(float value)
        {
            _skinWeightFields.TryAdd(row.Joint, field);
            SetSkinRowWeight(row.Joint, value);
        };
        Grid.SetColumn(field, 2);
        grid.Children.Add(field);
        _skinWeightFields[row.Joint] = field;
        TextBlock textBlock3 = new TextBlock
        {
            Text = FormatFraction(row.MaxWeight),
            FontSize = 11.0,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.8
        };
        Grid.SetColumn(textBlock3, 3);
        grid.Children.Add(textBlock3);
        _skinMaxWeights[row.Joint] = textBlock3;
        CheckBox lockBox = new CheckBox
        {
            IsChecked = row.Locked,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 18.0,
            Padding = new Thickness(0.0)
        };
        lockBox.IsCheckedChanged += delegate
        {
            if (!_rebuildingSkin && !_syncing)
            {
                session.SetLock([row.Joint], lockBox.IsChecked == true);
                RebuildSkinJoints();
            }
        };
        Grid.SetColumn(lockBox, 4);
        grid.Children.Add(lockBox);
        _skinLockBoxes[row.Joint] = lockBox;
        return grid;
    }

    private void RefreshSkinWeightColumn()
    {
        SkinPaintSession skin = _skin;
        if (skin == null)
        {
            return;
        }
        foreach (SkinWorkshop.Row item in skin.WeightList())
        {
            if (_skinWeightFields.TryGetValue(item.Joint, out SpinnerField value))
            {
                value.Show(item.Weight, item.Editable);
            }
            if (_skinMinWeights.TryGetValue(item.Joint, out TextBlock value2))
            {
                value2.Text = FormatFraction(item.MinWeight);
            }
            if (_skinMaxWeights.TryGetValue(item.Joint, out TextBlock value3))
            {
                value3.Text = FormatFraction(item.MaxWeight);
            }
        }
    }

    private static string FormatFraction(float value)
    {
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private Vector3[] SkinMeshPositions(Mesh mesh)
    {
        return (_pose == null) ? Array.Empty<Vector3>() : new MeshSkin(mesh).Positions(_pose.ControllerWorlds);
    }

    /// <summary>
    /// Runs one weight edit as a single undo step: the weights are snapshotted first, a thrown edit
    /// restores them, and a successful one records the description and repaints.
    /// </summary>
    private void RunSkinEdit(string description, Func<SkinPaintSession, int> edit)
    {
        SkinPaintSession skin = _skin;
        if (skin == null || _pose == null || _character == null)
        {
            _status.Text = "Open a scene and pick a skinned mesh first.";
            return;
        }
        if (SkinEditsBlockedByBindPose())
        {
            return;
        }
        EditSnapshot editSnapshot = EditSnapshot.TakeWeights(_character, [skin.Mesh]);
        int num;
        try
        {
            num = edit(skin);
        }
        catch (Exception ex)
        {
            editSnapshot.Restore();
            _status.Text = "Weight edit failed: " + ex.Message;
            Apply();
            return;
        }
        if (num == 0)
        {
            _status.Text = description + ": nothing to do.";
            return;
        }
        _history.Push(new EditRecord(description, editSnapshot, EditSnapshot.TakeWeights(_character, [skin.Mesh])));
        _dirty = true;
        UpdateTitle();
        _pose.ReloadSkins();
        Apply();
        RebuildSkinJoints();
        _status.Text = $"{description} ({num}).";
    }

    private void SetSkinWeight()
    {
        SetSkinWeight(_skin?.Value ?? 1f);
    }

    private void SetSkinWeight(float weight)
    {
        SkinPaintSession skin = _skin;
        if (skin == null)
        {
            _status.Text = "Pick a mesh and a joint first.";
            return;
        }
        RunSkinEdit($"Set weight {weight:0.###}", delegate(SkinPaintSession s)
        {
            _skinPositions = SkinMeshPositions(s.Mesh);
            _skinJointPositions = SkinJointPositions();
            return s.SetWeight(weight, _skinPositions, _skinJointPositions);
        });
    }

    private void SetSkinRowWeight(int joint, float weight)
    {
        SkinPaintSession skin = _skin;
        if (skin != null && _pose != null && _character != null)
        {
            _skinPositions = SkinMeshPositions(skin.Mesh);
            _skinJointPositions = SkinJointPositions();
            EditSnapshot before = EditSnapshot.TakeWeights(_character, [skin.Mesh]);
            int num = skin.SetJointWeight(joint, weight, _skinPositions, _skinJointPositions);
            if (num == 0)
            {
                _status.Text = (skin.IsLocked(joint) ? ("'" + _character.Joints[joint].Name + "' is locked.") : "Select the control points to write first.");
                RebuildSkinJoints();
                return;
            }
            _history.Push(new EditRecord($"Set '{_character.Joints[joint].Name}' weight {weight:0.###}", before, EditSnapshot.TakeWeights(_character, [skin.Mesh])));
            _dirty = true;
            UpdateTitle();
            Apply();
            RebuildSkinJoints();
            _status.Text = $"Set '{_character.Joints[joint].Name}' weight {weight:0.###} on {num} control point(s).";
        }
    }

    private void HideSkinVertices()
    {
        SkinPaintSession skin = _skin;
        if (skin == null || _pose == null || _character == null)
        {
            _status.Text = "Pick a mesh first.";
            return;
        }
        EditSnapshot before = EditSnapshot.TakeWeights(_character, [skin.Mesh]);
        int num = skin.HideSelection();
        if (num == 0)
        {
            _status.Text = "Hide: the selection is empty or already hidden.";
            return;
        }
        _history.Push(new EditRecord($"Hid {num} control point(s) of {skin.Mesh.Name}", before, EditSnapshot.TakeWeights(_character, [skin.Mesh])));
        _dirty = true;
        UpdateTitle();
        Apply();
        RebuildSkinJoints();
        _status.Text = $"Hid {num} control point(s) of {skin.Mesh.Name}; the brush skips them.";
    }

    private void ShowAllSkinVertices()
    {
        SkinPaintSession skin = _skin;
        if (skin == null || _pose == null || _character == null)
        {
            _status.Text = "Pick a mesh first.";
            return;
        }
        EditSnapshot before = EditSnapshot.TakeWeights(_character, [skin.Mesh]);
        int num = skin.ShowAllVertices();
        if (num == 0)
        {
            _status.Text = "Show all: no control point is hidden.";
            return;
        }
        _history.Push(new EditRecord($"Showed {num} control point(s) of {skin.Mesh.Name}", before, EditSnapshot.TakeWeights(_character, [skin.Mesh])));
        _dirty = true;
        UpdateTitle();
        Apply();
        RebuildSkinJoints();
        _status.Text = $"Showed {num} control point(s) of {skin.Mesh.Name}.";
    }

    private void CopySkinWeights()
    {
        SkinPaintSession skin = _skin;
        if (skin == null)
        {
            _status.Text = "Pick a mesh and a joint first.";
            return;
        }
        _skinClipboard = skin.Copy(out string refusal);
        _status.Text = refusal ?? $"Copied {_skinClipboard.VertexCount} control point(s) of {skin.Mesh.Name}.";
    }

    private void PasteSkinWeights()
    {
        SkinPaintSession skin = _skin;
        if (skin != null)
        {
            SkinWeightClipboard clipboard = _skinClipboard;
            if (clipboard != null)
            {
                string refusal = null;
                SkinSloughResult result = default(SkinSloughResult);
                RunSkinEdit("Pasted the weights", (SkinPaintSession s) => (result = s.Paste(clipboard, out refusal)).Points);
                if (refusal != null)
                {
                    _status.Text = refusal;
                }
                else if (result.Warnings is { Count: > 0 })
                {
                    _status.Text = _status.Text + " " + string.Join(" ", result.Warnings);
                }
                return;
            }
        }
        _status.Text = "Copy some weights first.";
    }

    private void DabAt(SkinPaintSession session, Vector3 point)
    {
        if (session.Mode == SkinPaintMode.Select)
        {
            int value = session.Select(point, _skinPositions, add: true, _skinNormals);
            _status.Text = $"Selecting {session.JointObject?.Name}: {value} control point(s), {session.Selection.SelectedCount} in the selection.";
            RefreshSkinWeightColumn();
        }
        else
        {
            int value2 = session.Dab(point, _skinPositions, _skinJointPositions, _skinNormals);
            _status.Text = $"Painting {session.JointObject?.Name}: {value2} control point(s) within {session.Radius:0.###}";
            RefreshSkinWeightColumn();
        }
    }

    void IViewportEditor.HoverAt(Vector2 at)
    {
        SkinPaintSession skin = _skin;
        if (skin == null || !skin.Scanner || _pose == null || _viewport.EditSize.X <= 0f)
        {
            ShowScan(null, at);
            return;
        }
        IViewCamera activeView = _viewport.Renderer.ActiveView;
        Vector3[] array = SkinMeshPositions(skin.Mesh);
        if (array.Length == 0)
        {
            ShowScan(null, at);
            return;
        }
        Mesh mesh = skin.Mesh;
        bool hasHiddenVertices = mesh.HasHiddenVertices;
        float skinRadiusPixels = _skinRadiusPixels;
        float num = skinRadiusPixels * skinRadiusPixels;
        int num2 = -1;
        Vector2 editSize = _viewport.EditSize;
        for (int i = 0; i < array.Length; i++)
        {
            if (hasHiddenVertices && mesh.IsVertexHidden(i))
            {
                continue;
            }
            Vector2? vector = activeView.Project(array[i], editSize);
            if (!vector.HasValue)
            {
                continue;
            }
            Vector2 valueOrDefault = vector.GetValueOrDefault();
            float num3 = valueOrDefault.X - at.X;
            float num4 = valueOrDefault.Y - at.Y;
            float num5 = num3 * num3 + num4 * num4;
            if (!(num5 >= num))
            {
                num = num5;
                num2 = i;
            }
        }
        ShowScan((num2 < 0) ? ((SkinPaintSession.ScanHit?)null) : skin.Inspect(num2), at);
        RefreshSkinWeightColumn();
    }

    private void ShowScan(SkinPaintSession.ScanHit? hit, Vector2 at)
    {
        if (_scanBadge == null || _scanText == null)
        {
            return;
        }
        if (hit.HasValue)
        {
            SkinPaintSession.ScanHit valueOrDefault = hit.GetValueOrDefault();
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append("Point ").Append(valueOrDefault.Vertex);
            foreach (var weight in valueOrDefault.Weights)
            {
                string item = weight.Name;
                float item2 = weight.Weight;
                stringBuilder.Append('\n').Append(item).Append("  ")
                    .Append(item2.ToString("0.###", CultureInfo.InvariantCulture));
            }
            _scanText.Text = stringBuilder.ToString();
            double num = ((_viewport.Bounds.Width > 0.0) ? _viewport.Bounds.Width : 400.0);
            double num2 = ((_viewport.Bounds.Height > 0.0) ? _viewport.Bounds.Height : 300.0);
            _scanBadge.Margin = new Thickness(Math.Clamp(at.X + 14f, 0.0, Math.Max(0.0, num - 240.0)), Math.Clamp(at.Y + 14f, 0.0, Math.Max(0.0, num2 - 140.0)), 0.0, 0.0);
            _scanBadge.IsVisible = true;
            return;
        }
        _scanBadge.IsVisible = false;
    }

    private float WorldRadius(IViewCamera camera, Vector3 point)
    {
        return _skinRadiusPixels * camera.ViewHeightAt(point) / MathF.Max(_viewport.EditSize.Y, 1f);
    }

    private IReadOnlyList<Vector3> SkinJointPositions()
    {
        if (_pose == null || _character == null)
        {
            return Array.Empty<Vector3>();
        }
        PoseEvaluator poseEvaluator = _pose.Evaluator(_character);
        Matrix4x4[] controllerWorlds = _pose.ControllerWorlds;
        IReadOnlyList<Joint> joints = _character.Joints;
        Vector3[] array = new Vector3[joints.Count];
        for (int i = 0; i < array.Length; i++)
        {
            int num = poseEvaluator.IndexOf(joints[i]);
            array[i] = ((num >= 0 && num < controllerWorlds.Length) ? controllerWorlds[num].Translation : joints[i].WorldMatrix.Translation);
        }
        return array;
    }
}
