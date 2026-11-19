// The Skin Workshop panel: mesh/joint/mode pickers, the weight table container, the joint rows and
// the mesh selection that feeds them.
//
// Painting and the weight writer are in MainWindow.SkinWorkshop.Weights.cs; the session itself is
// SloppyAnimatsu.Core SkinPaintSession.
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
    /// The Rigging context's PAINTER panel (<c>CPainterDialog</c>, above SKIN ATELIER;
    ///): the paint mode, the brush switches and the brush
    /// radius/strength/value. The Skin Workshop keeps the mesh, joint and weight table.
    /// </summary>
    private Control PainterBody()
    {
        foreach (Control control in new Control[] { _skinModes, _skinPaint, _skinHard, _skinRigid, _skinScanner, _skinFrontBack, _skinRadius, _skinStrength, _skinValue })
            (control.Parent as Panel)?.Children.Remove(control);
        Grid brush = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("64,*"),
            RowSpacing = 2.0,
            ColumnSpacing = 4.0
        };
        BrushRow(0, "Radius", _skinRadius);
        BrushRow(1, "Strength", _skinStrength);
        BrushRow(2, "Value", _skinValue);
        WrapPanel modes = new WrapPanel
        {
            Children = { _skinModes, _skinPaint, _skinHard, _skinRigid, _skinScanner, _skinFrontBack }
        };
        return new StackPanel
        {
            Margin = new Thickness(8.0),
            Spacing = 3.0,
            Children = { modes, brush }
        };
        void BrushRow(int row, string label, Control field)
        {
            TextBlock textBlock = new TextBlock
            {
                Text = label,
                FontSize = 11.0,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(textBlock, row);
            brush.Children.Add(textBlock);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            brush.Children.Add(field);
        }
    }

    private readonly List<(Button Button, MenuCommand Command)> _rigAtelierButtons = new();

    /// <summary>
    /// The Rigging context's RIG WORKSHOP panel (the RIG ATELIER, <c>CSkeletonDialog</c>, above
    /// TRANSFORM): the Rig menu's commands as buttons, enabled as
    /// the menu enables them.
    /// </summary>
    private Control RigWorkshopBody()
    {
        _rigAtelierButtons.Clear();
        WrapPanel panel = new WrapPanel { Margin = new Thickness(3.0, 2.0) };
        foreach (MenuEntry entry in MenuModel.RigItems(CurrentMenuState()))
        {
            if (entry.Command is not { } command || entry.Kind != MenuEntryKind.Item) continue;
            string text = entry.Header.Replace("_", "").Replace("…", "");
            int paren = text.IndexOf(" (", StringComparison.Ordinal);
            Button button = new Button
            {
                Content = paren > 0 ? text[..paren] : text,
                FontSize = 10.0,
                Padding = new Thickness(5.0, 1.0),
                Margin = new Thickness(1.0),
                MinHeight = 0.0
            };
            ToolTip.SetTip(button, entry.Header.Replace("_", ""));
            string? argument = entry.Argument;
            button.Click += delegate { RunMenuCommand(command, argument); };
            _rigAtelierButtons.Add((button, command));
            panel.Children.Add(button);
        }
        RefreshRigAtelier();
        return panel;
    }

    private void RefreshRigAtelier()
    {
        if (_rigAtelierButtons.Count == 0) return;
        Dictionary<MenuCommand, bool> enabled = new();
        foreach (MenuEntry entry in MenuModel.RigItems(CurrentMenuState()))
            if (entry.Command is { } command) enabled[command] = entry.Enabled;
        foreach (var (button, command) in _rigAtelierButtons)
            button.IsEnabled = enabled.TryGetValue(command, out bool on) && on;
    }

    private Control SkinWorkshopBody()
    {
        WrapPanel control = new WrapPanel
        {
            Children = 
            {
                Command("Set", SetSkinWeight),
                Command("Prune", delegate
                {
                    RunSkinEdit("Pruned the weights", (SkinPaintSession s) => s.Prune());
                }),
                Command("Fix", delegate
                {
                    RunSkinEdit("Fixed the selected weights", (SkinPaintSession s) => s.Fix());
                }),
                Command("Flood", delegate
                {
                    RunSkinEdit("Flooded the selection", (SkinPaintSession s) => s.Flood());
                }),
                Command("Mirror", delegate
                {
                    RunSkinEdit("Mirrored the skin", (SkinPaintSession s) => s.Mirror());
                }),
                Command("Copy", CopySkinWeights),
                Command("Paste", PasteSkinWeights),
                Command("Hide", HideSkinVertices),
                Command("Show all", ShowAllSkinVertices)
            }
        };
        Grid header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,38,76,48,26")
        };
        Header("Joint", 0, TextAlignment.Left);
        Header("Sel", 1, TextAlignment.Right);
        Header("Weight", 2, TextAlignment.Right);
        Header("Mesh", 3, TextAlignment.Right);
        Header("Lock", 4, TextAlignment.Center);
        Grid grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        grid.Children.Add(header);
        Grid.SetRow(_skinWeights, 1);
        grid.Children.Add(_skinWeights);
        Grid grid2 = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
            Margin = new Thickness(8.0),
            RowSpacing = 3.0
        };
        // No mesh or joint pickers (the SKIN ATELIER has none): the mesh is the skinned mesh
        // the brush or the vertex selection last hit, and the list is the selected joints plus the
        // joints weighting the selected control points.
        Add(control, 0);
        Add(_skinInfo, 1);
        Add(Heading("Weights"), 2);
        Add(grid, 3);
        return grid2;
        void Add(Control control3, int row)
        {
            Grid.SetRow(control3, row);
            grid2.Children.Add(control3);
        }
        static Control Command(string text, Action run)
        {
            Button button = new Button
            {
                Content = text,
                Padding = new Thickness(6.0, 2.0),
                FontSize = 11.0
            };
            button.Click += delegate
            {
                run();
            };
            return button;
        }
        void Header(string text, int column, TextAlignment align)
        {
            TextBlock textBlock = new TextBlock
            {
                Text = text,
                FontSize = 10.0,
                Opacity = 0.6,
                TextAlignment = align,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(textBlock, column);
            header.Children.Add(textBlock);
        }
    }

    private void RefreshSkinPanel()
    {
        if (_pose == null || _character == null)
        {
            _skin = null;
            _skinRows.Children.Clear();
            _skinWeightFields.Clear();
            _skinMinWeights.Clear();
            _skinMaxWeights.Clear();
            _skinLockBoxes.Clear();
            _skinInfo.Text = "Open a scene with a skinned character.";
            return;
        }
        List<Mesh> source = _character.Meshes.Where((Mesh m) => m.Skinned).ToList();
        Mesh previous = _skin?.Mesh;
        Mesh shown = source.FirstOrDefault((Mesh m) => m == previous) ?? source.FirstOrDefault();
        SelectSkinMesh(shown);
    }

    private void SelectSkinMesh(Mesh? mesh)
    {
        if (_pose == null || _character == null || mesh == null)
        {
            _skin = null;
            _skinRows.Children.Clear();
            _skinWeightFields.Clear();
            _skinMinWeights.Clear();
            _skinMaxWeights.Clear();
            _skinLockBoxes.Clear();
            _skinInfo.Text = "Bind skin, then pick a skinned mesh to paint its weights.";
            return;
        }
        if (_skin == null || _skin.Mesh != mesh)
        {
            _skin = new SkinPaintSession(_character, mesh, _pose.Model.Scene.Context == InterfaceContext.Rigging);
            bool syncing = _syncing;
            _syncing = true;
            _skinRigid.IsChecked = _skin.Rigid;
            _syncing = syncing;
            int selected = LastSelectedJointIndex();
            if (selected >= 0)
            {
                _skin.Joint = selected;
            }
        }
        _skin.Rigid = _skinRigid.IsChecked == true;
        _skin.Hard = _skinHard.IsChecked == true;
        _skin.Scanner = _skinScanner.IsChecked == true;
        _skin.FrontBackPaint = _skinFrontBack.IsChecked == true;
        _skin.FrontBackSelect = _skin.FrontBackPaint;
        _skinRadius.Show(_skinRadiusPixels);
        _skinStrength.Show(_skin.Strength);
        _skinValue.Show(_skin.Value);
        if (_skin.Joint < 0 && _character.Joints.Count > 0)
        {
            _skin.Joint = 0;
        }
        RebuildSkinJoints();
    }

    private void SelectPaintedJoint(int? index)
    {
        SkinPaintSession skin = _skin;
        if (skin != null && index.HasValue)
        {
            int valueOrDefault = index.GetValueOrDefault();
            skin.Joint = valueOrDefault;
            RebuildSkinJoints();
        }
    }

    private void RebuildSkinJoints()
    {
        if (_rebuildingSkin)
        {
            return;
        }
        SkinPaintSession session = _skin;
        if (session == null || _pose == null)
        {
            _skinRows.Children.Clear();
            _skinWeightFields.Clear();
            _skinMinWeights.Clear();
            _skinMaxWeights.Clear();
            _skinLockBoxes.Clear();
            return;
        }
        _rebuildingSkin = true;
        try
        {
            // The weight list: only the selected joints and the joints that weight the selected
            // control points (SkinWorkshop.Row remarks); the painted joint stays listed so its row
            // can be read while painting.
            HashSet<int> selectedJoints = SelectedJointIndices();
            IReadOnlyList<SkinWorkshop.Row> readOnlyList = session.WeightList()
                .Where((SkinWorkshop.Row r) => r.Editable || selectedJoints.Contains(r.Joint) || r.Joint == session.Joint)
                .ToList();
            bool syncing = _syncing;
            _syncing = true;
            _skinRows.Children.Clear();
            _skinWeightFields.Clear();
            _skinMinWeights.Clear();
            _skinMaxWeights.Clear();
            _skinLockBoxes.Clear();
            foreach (SkinWorkshop.Row item in readOnlyList)
            {
                _skinRows.Children.Add(BuildSkinRow(session, item));
            }
            _syncing = syncing;
            _skinInfo.Text = $"{session.Mesh.Name}: {session.Selection.SelectedCount} control point(s) selected, painted joint '{session.JointObject?.Name}'"
                + ((session.LockedJoints.Count > 0) ? $", {session.LockedJoints.Count} locked." : ".")
                + ((selectedJoints.Count == 0 && session.Selection.SelectedCount == 0) ? " Select joints or control points to list their weights." : "");
        }
        finally
        {
            _rebuildingSkin = false;
        }
    }

    /// <summary>Indices into the current character's joints of the selected joints.</summary>
    private HashSet<int> SelectedJointIndices()
    {
        HashSet<int> result = new HashSet<int>();
        if (_character == null)
        {
            return result;
        }
        IReadOnlyList<Joint> joints = _character.Joints;
        foreach (Joint joint in _selectedAll.OfType<Joint>())
        {
            for (int i = 0; i < joints.Count; i++)
            {
                if (ReferenceEquals(joints[i], joint))
                {
                    result.Add(i);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>The index of the last selected joint of the current character, or -1.</summary>
    private int LastSelectedJointIndex()
    {
        if (_character == null)
        {
            return -1;
        }
        IReadOnlyList<Joint> joints = _character.Joints;
        for (int s = _selectedAll.Count - 1; s >= 0; s--)
        {
            if (_selectedAll[s] is Joint joint)
            {
                for (int i = 0; i < joints.Count; i++)
                {
                    if (ReferenceEquals(joints[i], joint))
                    {
                        return i;
                    }
                }
            }
        }
        return -1;
    }

    /// <summary>
    /// Follows the scene selection: the last selected joint becomes the painted joint and the
    /// weight list is rebuilt for the new selection.
    /// </summary>
    private void SyncSkinToSelection()
    {
        SkinPaintSession skin = _skin;
        if (skin == null || _character == null)
        {
            return;
        }
        int selected = LastSelectedJointIndex();
        if (selected >= 0)
        {
            skin.Joint = selected;
        }
        RebuildSkinJoints();
    }

    /// <summary>
    /// The brush or vertex-selection press when it misses the active mesh: picks across every
    /// skinned mesh of the current character and makes the hit mesh the Skin Workshop's mesh.
    /// </summary>
    private SceneHit? PickSkinMesh(IViewCamera view, Vector2 at)
    {
        if (_pose == null || _character == null)
        {
            return null;
        }
        SceneHit? best = null;
        Mesh? bestMesh = null;
        foreach (Mesh mesh in _character.Meshes.Where((Mesh m) => m.Skinned))
        {
            SceneHit? hit = ScenePicking.Pick(_pose, view, _viewport.EditSize, at, mesh);
            if (hit.HasValue && (!best.HasValue || hit.Value.Distance < best.Value.Distance))
            {
                best = hit;
                bestMesh = mesh;
            }
        }
        if (bestMesh != null && _skin?.Mesh != bestMesh)
        {
            SelectSkinMesh(bestMesh);
        }
        return best;
    }
}
