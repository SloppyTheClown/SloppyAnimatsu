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
        // The list also holds the SKIN WORKSHOP's Bind/Unbind buttons; rebuild only the rig ones.
        _rigAtelierButtons.RemoveAll(b => !IsSkinBindCommand(b.Command));
        StackPanel panel = new StackPanel { Margin = new Thickness(3.0, 2.0), Spacing = 3.0 };
        WrapPanel buttons = new WrapPanel();
        MenuEntry? orient = null;
        foreach (MenuEntry entry in MenuModel.RigItems(CurrentMenuState()))
        {
            if (entry.Command is not { } command || entry.Kind != MenuEntryKind.Item) continue;
            // Orient sits beside the axes it uses (JointAxesRow), not among the other commands.
            if (command == MenuCommand.OrientJoints) { orient = entry; continue; }
            buttons.Children.Add(WorkshopButton(entry, command));
        }
        panel.Children.Add(buttons);
        panel.Children.Add(JointAxesRow(orient));
        RefreshRigAtelier();
        return panel;
    }

    /// <summary>A RIG/SKIN WORKSHOP button for a menu row, with the row's hover text.</summary>
    private Button WorkshopButton(MenuEntry entry, MenuCommand command)
    {
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
        string baseTip = entry.ToolTip ?? entry.Header.Replace("_", "");
        _workshopRequirements[button] = (baseTip, entry.Requires);
        ToolTip.SetTip(button, entry.Enabled ? baseTip : MenuEntry.WithRequirement(baseTip, entry.Requires));
        string? argument = entry.Argument;
        button.Click += delegate { RunMenuCommand(command, argument); };
        _rigAtelierButtons.Add((button, command));
        return button;
    }

    /// <summary>The joint-axis choices of the workshop's combo boxes, in the Joint axes menu's order.</summary>
    private static readonly (JointAxis Axis, bool Negative)[] JointAxisChoices =
    [
        (JointAxis.X, false), (JointAxis.X, true),
        (JointAxis.Y, false), (JointAxis.Y, true),
        (JointAxis.Z, false), (JointAxis.Z, true)
    ];

    private ComboBox? _rigAimAxis, _rigUpAxis;
    private bool _syncingRigAxes;

    /// <summary>Each workshop button's own hover text and what it needs, to compose the disabled tooltip.</summary>
    private readonly Dictionary<Button, (string Tip, string? Requires)> _workshopRequirements = new();

    /// <summary>
    /// The RIG WORKSHOP's joint-axis controls (the same settings as Rig > Joint axes): the Aim axis
    /// that Orient / Create joint point at the child joint above the 2nd axis that points up, with
    /// the Orient Joint button beside them. Auto-orient on creation stays in Rig > Joint axes.
    /// </summary>
    private Control JointAxesRow(MenuEntry? orient)
    {
        _rigAimAxis = AxisCombo(aim: true,
            "Aim axis: the joint's local axis that Orient selected joints (and Create joint with Auto-orient) " +
            "points at the child joint, i.e. the axis running along the bone.");
        _rigUpAxis = AxisCombo(aim: false,
            "2nd axis: the joint's local axis that Orient keeps pointing up (as close as possible to its previous " +
            "direction), which fixes the twist around the bone. It must differ from the Aim axis.");
        // Two rows (Aim above 2nd) with the labels in one column so both combos line up.
        Grid grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 4.0,
            RowSpacing = 2.0,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        Place(Label("Aim"), 0, 0);
        Place(_rigAimAxis, 0, 1);
        Place(Label("2nd"), 1, 0);
        Place(_rigUpAxis, 1, 1);
        if (orient is { Command: { } command })
        {
            Button button = WorkshopButton(orient, command);
            button.Content = "Orient Joint";
            button.VerticalAlignment = VerticalAlignment.Stretch;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            button.Margin = new Thickness(4.0, 0, 0, 0);
            Grid.SetRowSpan(button, 2);
            Place(button, 0, 2);
        }
        return grid;

        void Place(Control control, int row, int column)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            grid.Children.Add(control);
        }
        static TextBlock Label(string text) => new TextBlock
        {
            Text = text, FontSize = 10.0, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2.0, 0, 0, 0)
        };
        ComboBox AxisCombo(bool aim, string tip)
        {
            ComboBox combo = new ComboBox { FontSize = 10.0, MinHeight = 0.0, Padding = new Thickness(4.0, 0) };
            foreach (var (axis, negative) in JointAxisChoices)
                combo.Items.Add((negative ? "-" : "+") + JointAlignment.NameOf(axis));
            ToolTip.SetTip(combo, tip);
            combo.SelectionChanged += delegate
            {
                if (_syncingRigAxes || combo.SelectedIndex < 0) return;
                var (axis, negative) = JointAxisChoices[combo.SelectedIndex];
                SetJointAxis(aim, axis, negative);
            };
            return combo;
        }
    }

    private void RefreshRigAtelier()
    {
        if (_rigAtelierButtons.Count == 0) return;
        MenuState state = CurrentMenuState();
        // Panels are rebuilt; drop the hover texts of buttons that are gone.
        foreach (Button stale in _workshopRequirements.Keys.Where(b => !_rigAtelierButtons.Exists(r => r.Button == b)).ToList())
            _workshopRequirements.Remove(stale);
        Dictionary<MenuCommand, MenuEntry> entries = new();
        foreach (MenuEntry entry in MenuModel.RigItems(state).Concat(MenuModel.SkinItems(state)))
            if (entry.Command is { } command) entries[command] = entry;
        foreach (var (button, command) in _rigAtelierButtons)
        {
            entries.TryGetValue(command, out MenuEntry? entry);
            button.IsEnabled = entry?.Enabled == true;
            if (_workshopRequirements.TryGetValue(button, out var tip))
                ToolTip.SetTip(button, button.IsEnabled ? tip.Tip : MenuEntry.WithRequirement(tip.Tip, entry?.Requires ?? tip.Requires));
        }
        _syncingRigAxes = true;
        try
        {
            if (_rigAimAxis != null)
                _rigAimAxis.SelectedIndex = Array.IndexOf(JointAxisChoices, (_jointAlignment.Aim, _jointAlignment.AimNegative));
            if (_rigUpAxis != null)
                _rigUpAxis.SelectedIndex = Array.IndexOf(JointAxisChoices, (_jointAlignment.Up, _jointAlignment.UpNegative));
        }
        finally
        {
            _syncingRigAxes = false;
        }
    }

    private static bool IsSkinBindCommand(MenuCommand command) =>
        command is MenuCommand.BindSkin or MenuCommand.UnbindSkin;

    private Control SkinWorkshopBody()
    {
        _rigAtelierButtons.RemoveAll(b => IsSkinBindCommand(b.Command));
        WrapPanel bind = new WrapPanel();
        foreach (MenuEntry entry in MenuModel.SkinItems(CurrentMenuState()))
            if (entry.Command is { } command && entry.Kind == MenuEntryKind.Item)
                bind.Children.Add(WorkshopButton(entry, command));
        WrapPanel control = new WrapPanel
        {
            Children =
            {
                Command("Set", SetSkinWeight,
                    "Give the selected control points the weight in the value box for the current joint; the other " +
                    "joints' weights are scaled so each point still sums to 1. Hidden points and locked joints are not changed."),
                Command("Prune", delegate
                {
                    RunSkinEdit("Pruned the weights", (SkinPaintSession s) => s.Prune());
                }, "Remove every tiny influence (below 0.001) from the whole mesh and give its share to the joints " +
                   "that remain on that point. Hidden control points are left alone."),
                Command("Fix", delegate
                {
                    RunSkinEdit("Fixed the selected weights", (SkinPaintSession s) => s.Fix());
                }, "Smooth the selected control points: each takes the average weights of its selected neighbours " +
                   "(it needs at least two), then is normalized to 1. Use it to blend out sharp seams."),
                Command("Flood", delegate
                {
                    RunSkinEdit("Flooded the selection", (SkinPaintSession s) => s.Flood());
                }, "Grow the control-point selection to the whole connected piece of mesh it touches (stopping at " +
                   "hidden points). Handy for selecting a separate part such as an eye or a button."),
                Command("Mirror", delegate
                {
                    RunSkinEdit("Mirrored the skin", (SkinPaintSession s) => s.Mirror());
                }, "Copy the current joint's weights to the matching joint on the other side (left to right, " +
                   "e.g. L_arm to R_arm), on the control points at the mirrored position."),
                Command("Copy", CopySkinWeights,
                    "Copy the weights of the selected control points (at least two) so they can be pasted onto other points, even on another mesh."),
                Command("Paste", PasteSkinWeights,
                    "Paste the copied weights onto the selected control points, pairing them in point order. Joints are matched by name; " +
                    "a joint missing on this character takes the target of its nearest matched parent joint."),
                Command("Hide", HideSkinVertices,
                    "Hide the selected control points so painting, Prune, Fix and Flood ignore them. " +
                    "Useful to protect an area while you work next to it."),
                Command("Show all", ShowAllSkinVertices,
                    "Show every hidden control point again.")
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
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"),
            Margin = new Thickness(8.0),
            RowSpacing = 3.0
        };
        // No mesh or joint pickers (the SKIN ATELIER has none): the mesh is the skinned mesh
        // the brush or the vertex selection last hit, and the list is the selected joints plus the
        // joints weighting the selected control points.
        Add(bind, 0);
        Add(control, 1);
        Add(_skinInfo, 2);
        Add(Heading("Weights"), 3);
        Add(grid, 4);
        return grid2;
        void Add(Control control3, int row)
        {
            Grid.SetRow(control3, row);
            grid2.Children.Add(control3);
        }
        static Control Command(string text, Action run, string toolTip)
        {
            Button button = new Button
            {
                Content = text,
                Padding = new Thickness(6.0, 2.0),
                FontSize = 11.0
            };
            ToolTip.SetTip(button, toolTip);
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
