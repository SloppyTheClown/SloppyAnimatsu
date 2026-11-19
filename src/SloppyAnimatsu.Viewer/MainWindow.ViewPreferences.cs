// The View & Preferences button and its window: the round
// button in the bottom-left corner opens an in-window menu with the shading modes, the viewport
// decorators, the four view layouts, Hide UI, the Shortcut Manager and the Preferences. SloppyAnimatsu
// draws its own glyphs; the artwork is not copied.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    // Shading.
    private readonly CheckBox _vpWireframe = ViewOption("Wireframe", false, "Gray edges over the polygons (NUMPAD1)");
    private readonly CheckBox _vpTextured = ViewOption("Textured", true, "Show material textures; off shows the flat colours (NUMPAD2 / NUMPAD3)");
    private readonly CheckBox _vpLit = ViewOption("Lit", true, "Shade the meshes with the headlight; off draws them unlit (NUMPAD0)");
    // Decorators.
    private readonly CheckBox _vpJoints = ViewOption("Display joints", true, "The dot at each joint and controller");
    private readonly CheckBox _vpHideBones = ViewOption("Hide bones", false, "Hide the bone lines between joints (NUMPAD5)");
    private readonly CheckBox _vpPivots = ViewOption("Display pivots", false, "An axis triad at each controller's pivot (NUMPAD6)");
    private readonly CheckBox _vpSkinning = ViewOption("Display skinning", false, "Tint the meshes with the selected joint's skin weights (NUMPAD7)");
    private readonly CheckBox _vpBlendFaces = ViewOption("Display blendshapes interactive faces", false, "Outline the triangles of every blendshape face zone (NUMPAD8)");
    private readonly CheckBox _vpGrid = ViewOption("Display grid", true, "The ground grid (G)");

    private Button? _viewPrefsButton;
    private Flyout? _viewPrefsFlyout;
    private readonly List<(SceneLayout Layout, Button Button)> _vpLayoutButtons = [];
    private Control? _viewPrefsIcon;
    private Control? _viewPrefsBackLabel;

    /// <summary>Hide UI: every control but the View &amp; Preferences button is hidden.</summary>
    private bool _hudHidden;
    private readonly List<(Control Control, bool WasVisible)> _hudSaved = [];

    /// <summary>The tab strip and the status bar, so Hide UI can hide them (set in Layout).</summary>
    private Control? _tabStrip;
    private Panel? _statusBar;

    private static CheckBox ViewOption(string label, bool on, string tip)
    {
        var box = new CheckBox { Content = label, IsChecked = on, MinHeight = 22, Padding = new Thickness(4, 0, 0, 0) };
        ToolTip.SetTip(box, tip);
        return box;
    }

    /// <summary>The round bottom-left button; built once and docked left in the status bar.</summary>
    private Button ViewPrefsButton()
    {
        if (_viewPrefsButton != null) return _viewPrefsButton;
        _viewPrefsIcon = ViewPrefsGlyph();
        _viewPrefsBackLabel = new TextBlock { Text = "◀ Back", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        var button = new Button
        {
            Content = new Panel { Children = { _viewPrefsIcon, _viewPrefsBackLabel } },
            Padding = new Thickness(5, 2),
            MinHeight = 0,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, "View & Preferences");
        _viewPrefsFlyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedLeft, Content = ViewPrefsContent() };
        _viewPrefsFlyout.Opening += (_, _) => SyncViewPrefs();
        button.Click += (_, _) =>
        {
            if (_hudHidden) { SetHudHidden(false); return; }
            _viewPrefsFlyout.ShowAt(button);
        };
        foreach (var box in new[] { _vpWireframe, _vpTextured, _vpLit, _vpJoints, _vpHideBones, _vpPivots, _vpSkinning, _vpBlendFaces })
            box.IsCheckedChanged += (_, _) => Apply();
        // "Display grid" is the same switch as the G shortcut and the Grid box of the display options.
        _vpGrid.IsChecked = _showGrid.IsChecked;
        _vpGrid.IsCheckedChanged += (_, _) =>
        {
            if (_showGrid.IsChecked != _vpGrid.IsChecked) _showGrid.IsChecked = _vpGrid.IsChecked;
        };
        _showGrid.IsCheckedChanged += (_, _) =>
        {
            if (_vpGrid.IsChecked != _showGrid.IsChecked) _vpGrid.IsChecked = _showGrid.IsChecked;
        };
        return _viewPrefsButton = button;
    }

    private Control ViewPrefsContent()
    {
        var panel = new StackPanel { Spacing = 2, MinWidth = 250 };
        panel.Children.Add(new TextBlock { Text = "VIEW & PREFERENCES", FontWeight = FontWeight.Bold, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(SectionHeader("Shading"));
        panel.Children.Add(_vpWireframe);
        panel.Children.Add(_vpTextured);
        panel.Children.Add(_vpLit);
        panel.Children.Add(SectionHeader("Decorators"));
        foreach (var box in new[] { _vpJoints, _vpHideBones, _vpPivots, _vpSkinning, _vpBlendFaces, _vpGrid }) panel.Children.Add(box);
        panel.Children.Add(SectionHeader("Layout"));
        var layouts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (SceneLayout layout in new[] { SceneLayout.SingleView, SceneLayout.TwoViews, SceneLayout.TwoViewsCurveboard, SceneLayout.FourViews })
        {
            var chosen = layout;
            var b = new Button { Content = LayoutGlyph(layout), Padding = new Thickness(4), MinHeight = 0 };
            ToolTip.SetTip(b, ViewLayouts.Name(layout));
            b.Click += (_, _) => { SetLayout(chosen); SyncViewPrefs(); };
            _vpLayoutButtons.Add((layout, b));
            layouts.Children.Add(b);
        }
        panel.Children.Add(layouts);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 4) });
        panel.Children.Add(MenuRow("Hide UI", "Hide every panel and bar; the Back button brings them back (NUMPAD9)", () => SetHudHidden(true)));
        panel.Children.Add(MenuRow("Shortcut Manager", "Edit the keyboard shortcuts", () => _ = OpenShortcutDialog()));
        panel.Children.Add(MenuRow("Panel keys", "Edit the keys the viewport, TREE, timeline and curveboard handle", () => _ = OpenPanelKeysDialog()));
        panel.Children.Add(MenuRow("Preferences", "Open the preferences", () => _ = OpenPreferencesDialog()));
        return panel;

        static TextBlock SectionHeader(string text) => new()
        {
            Text = text, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 6, 0, 1),
        };
    }

    private Button MenuRow(string text, string tip, Action action)
    {
        var b = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 3),
            MinHeight = 0,
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => { _viewPrefsFlyout?.Hide(); action(); };
        return b;
    }

    /// <summary>Marks the current layout's button and mirrors the grid switch.</summary>
    private void SyncViewPrefs()
    {
        _vpGrid.IsChecked = _showGrid.IsChecked;
        var accent = new SolidColorBrush(Color.FromArgb(200, 240, 160, 50));
        foreach (var (layout, button) in _vpLayoutButtons)
        {
            bool current = layout == _layout;
            button.BorderBrush = current ? accent : Brushes.Transparent;
            button.BorderThickness = new Thickness(current ? 2 : 0);
        }
    }

    /// <summary>
    /// The skin-weight tint is drawn: the switch is on, a joint is selected, and the CHAR BANK's
    /// current row is not a Bind (or Build) Pose - the one mode the app hides the decorator in
    /// </summary>
    private bool SkinTintShown() =>
        _vpSkinning.IsChecked == true && _selected is Joint
        && !(_animation == null && _character != null && CharacterBank.IsBindPoseCurrent(_character));

    /// <summary>Hands the View &amp; Preferences switches to the renderer and the pose (called from Apply, before the pose update).</summary>
    private void ApplyViewPrefs(SceneRenderer renderer)
    {
        bool skinning = SkinTintShown();
        renderer.Wireframe = _vpWireframe.IsChecked == true;
        renderer.Textured = _vpTextured.IsChecked == true && !skinning;
        renderer.Lit = _vpLit.IsChecked == true;
        renderer.ShowJoints = _vpJoints.IsChecked == true;
        renderer.ShowBones = _vpHideBones.IsChecked != true;
        renderer.ShowPivots = _vpPivots.IsChecked == true;
        if (_pose != null) _pose.SkinDisplayJoint = skinning ? _selected : null;
    }

    /// <summary>After the pose update: the blendshape face zones' outlines, when they are shown.</summary>
    private void ApplyViewPrefDecorators(SceneRenderer renderer)
    {
        renderer.DecoratorLines = _vpBlendFaces.IsChecked == true && _pose != null && _character != null
            ? BlendshapeFaceLines(_pose, _character)
            : [];
    }

    private static readonly Vector3 BlendFaceColour = new(0.85f, 0.4f, 0.95f);

    private static float[] BlendshapeFaceLines(ScenePose pose, Character character)
    {
        var controllers = character.Controllers.OfType<BlendshapeFaceController>().ToList();
        if (controllers.Count == 0) return [];
        var lines = new List<float>();
        float[] v = pose.MeshVertices;
        const int F = ScenePose.MeshVertexFloats;
        foreach (var part in pose.MeshParts)
        {
            if (!part.Visible || part.SourceTriangles is not { } source) continue;
            var own = controllers.Where(c => ReferenceEquals(c.Mesh, part.Mesh)).ToList();
            if (own.Count == 0) continue;
            // Source triangle index -> drawn triangle index of this part.
            var drawn = new Dictionary<int, int>(source.Count);
            for (int i = 0; i < source.Count; i++) drawn.TryAdd(source[i], i);
            foreach (var controller in own)
            foreach (uint triangle in controller.InteractiveFaces)
            {
                if (!drawn.TryGetValue((int)triangle, out int d)) continue;
                int first = part.First + d * 3;
                for (int k = 0; k < 3; k++)
                {
                    Add((first + k) * F);
                    Add((first + (k + 1) % 3) * F);
                }
            }
        }
        return lines.ToArray();

        void Add(int o)
        {
            if (o + 2 >= v.Length) return;
            lines.Add(v[o]); lines.Add(v[o + 1]); lines.Add(v[o + 2]);
            lines.Add(BlendFaceColour.X); lines.Add(BlendFaceColour.Y); lines.Add(BlendFaceColour.Z);
        }
    }

    /// <summary>Hide UI: hides the menu, the tabs, the panels and the status bar, leaving the button as "Back".</summary>
    private void SetHudHidden(bool hidden)
    {
        if (hidden == _hudHidden) return;
        _hudHidden = hidden;
        if (hidden)
        {
            _hudSaved.Clear();
            var targets = new List<Control?> { _menuBar, _tabStrip, _floatHost };
            if (_statusBar != null) targets.AddRange(_statusBar.Children.Where(c => !ReferenceEquals(c, _viewPrefsButton)));
            foreach (var c in targets.OfType<Control>())
            {
                _hudSaved.Add((c, c.IsVisible));
                c.IsVisible = false;
            }
        }
        else
        {
            foreach (var (c, was) in _hudSaved) c.IsVisible = was;
            _hudSaved.Clear();
        }
        if (_viewPrefsIcon != null) _viewPrefsIcon.IsVisible = !hidden;
        if (_viewPrefsBackLabel != null) _viewPrefsBackLabel.IsVisible = hidden;
        if (_viewPrefsButton != null) ToolTip.SetTip(_viewPrefsButton, hidden ? "Show the UI again" : "View & Preferences");
        _status.Text = hidden ? "UI hidden: Back (bottom-left) or NUMPAD9 shows it again." : "UI shown.";
        _viewport.RequestNextFrameRendering();
    }

    /// <summary>The shading/decorator shortcuts of the "Shading &amp; Display modes" group.</summary>
    private bool RunViewPrefsAction(string action)
    {
        switch (action)
        {
            case "ToggleUnlit": Flip(_vpLit); return true;
            case "ToggleWireframe": Flip(_vpWireframe); return true;
            case "ToggleTextured": _vpTextured.IsChecked = true; return true;
            case "ToggleUntextured": _vpTextured.IsChecked = false; return true;
            case "ToggleHiddenBones": Flip(_vpHideBones); return true;
            case "TogglePivots": Flip(_vpPivots); return true;
            case "ToggleSkinning": Flip(_vpSkinning); return true;
            case "ToggleBlendshapeInteractiveZones": Flip(_vpBlendFaces); return true;
            case "ToggleHUD": SetHudHidden(!_hudHidden); return true;
            default: return false;
        }
        static void Flip(CheckBox box) => box.IsChecked = box.IsChecked != true;
    }

    private static readonly IBrush GlyphBrush = new SolidColorBrush(Color.FromRgb(220, 222, 228));
    private static readonly IBrush GlyphCut = new SolidColorBrush(Color.FromRgb(40, 42, 48));

    /// <summary>A layout frame (two panes) with a gear over its bottom-right corner.</summary>
    private static Control ViewPrefsGlyph()
    {
        var canvas = new Canvas { Width = 22, Height = 16 };
        var frame = new Rectangle { Width = 15, Height = 12, Stroke = GlyphBrush, StrokeThickness = 1.2, RadiusX = 1.5, RadiusY = 1.5 };
        Canvas.SetLeft(frame, 0.5); Canvas.SetTop(frame, 1.5);
        canvas.Children.Add(frame);
        canvas.Children.Add(new Line { StartPoint = new Point(6, 1.5), EndPoint = new Point(6, 13.5), Stroke = GlyphBrush, StrokeThickness = 1.2 });
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(GearPath(16, 10.5, 5.3, 3.9, 8)),
            Fill = GlyphBrush,
            Stroke = GlyphCut,
            StrokeThickness = 1,
        });
        var hole = new Ellipse { Width = 3.4, Height = 3.4, Fill = GlyphCut };
        Canvas.SetLeft(hole, 16 - 1.7); Canvas.SetTop(hole, 10.5 - 1.7);
        canvas.Children.Add(hole);
        return canvas;
    }

    /// <summary>Path data for a gear of <paramref name="teeth"/> teeth around (cx, cy).</summary>
    private static string GearPath(double cx, double cy, double outer, double inner, int teeth)
    {
        var points = new List<string>();
        int steps = teeth * 4;
        for (int i = 0; i < steps; i++)
        {
            double r = (i % 4) is 0 or 1 ? outer : inner;
            double a = 2 * Math.PI * (i + 0.5) / steps;
            points.Add(FormattableString.Invariant($"{cx + r * Math.Cos(a):0.###},{cy + r * Math.Sin(a):0.###}"));
        }
        return "M" + points[0] + " L" + string.Join(" ", points.Skip(1)) + " Z";
    }

    /// <summary>A small picture of a view layout: its views as boxes, the curveboard as a band.</summary>
    private static Control LayoutGlyph(SceneLayout layout)
    {
        const double W = 30, H = 20;
        var canvas = new Canvas { Width = W, Height = H };
        void Box(double x, double y, double w, double h, bool curves = false)
        {
            var r = new Rectangle
            {
                Width = w, Height = h, Stroke = GlyphBrush, StrokeThickness = 1,
                Fill = curves ? new SolidColorBrush(Color.FromArgb(90, 240, 160, 50)) : null,
            };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            canvas.Children.Add(r);
        }
        switch (layout)
        {
            case SceneLayout.SingleView: Box(0.5, 0.5, W - 1, H - 1); break;
            case SceneLayout.TwoViews: Box(0.5, 0.5, W / 2 - 1, H - 1); Box(W / 2 + 0.5, 0.5, W / 2 - 1, H - 1); break;
            case SceneLayout.TwoViewsCurveboard:
                Box(0.5, 0.5, W / 2 - 1, H * 0.6 - 1); Box(W / 2 + 0.5, 0.5, W / 2 - 1, H * 0.6 - 1);
                Box(0.5, H * 0.6 + 0.5, W - 1, H * 0.4 - 1, curves: true); break;
            default:
                Box(0.5, 0.5, W / 2 - 1, H / 2 - 1); Box(W / 2 + 0.5, 0.5, W / 2 - 1, H / 2 - 1);
                Box(0.5, H / 2 + 0.5, W / 2 - 1, H / 2 - 1); Box(W / 2 + 0.5, H / 2 + 0.5, W / 2 - 1, H / 2 - 1); break;
        }
        return canvas;
    }
}
