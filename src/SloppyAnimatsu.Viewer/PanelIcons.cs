using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The panels' button icons, drawn on the same 12 px grid and in the same colours as the Mixer's
/// toolbar: the subject in gray, an identifier in the bottom-right quarter (centred at 9,9) in
/// yellow when the button adds, builds or changes something (plus, gear, loop, arrow, pencil,
/// wrench) and in red when it removes (cross, minus). Buttons whose subject already says what they
/// do (stepping, copy, show) stay all gray.
/// </summary>
internal static class PanelIcons
{
    public static readonly IBrush Gray = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8));
    public static readonly IBrush Yellow = new SolidColorBrush(Color.FromRgb(0xF0, 0xC8, 0x30));
    public static readonly IBrush Red = new SolidColorBrush(Color.FromRgb(0xE0, 0x48, 0x40));

    // ---- Making a button show an icon ---------------------------------------------------------

    /// <summary>
    /// Turns a button into an icon button: the icon replaces its text, the name leads the tooltip
    /// and becomes the accessible name, and the icon fades while the button is disabled (its
    /// paths carry their own colours, which the theme's disabled look does not touch).
    /// <paramref name="size"/> scales the 12 px drawing (the CHAR BANK's tall buttons use 18).
    /// </summary>
    public static T Apply<T>(T button, string name, Control icon, double size = 12.0) where T : Button
    {
        Control content = icon;
        if (Math.Abs(size - 12.0) > 0.01)
            content = new Viewbox { Width = size, Height = size, Child = icon, Stretch = Stretch.Uniform };
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        button.Content = content;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        Avalonia.Automation.AutomationProperties.SetName(button, name);
        SetTip(button, name, ToolTip.GetTip(button) as string);
        button.PropertyChanged += delegate(object? _, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Avalonia.Input.InputElement.IsEffectivelyEnabledProperty)
                content.Opacity = button.IsEffectivelyEnabled ? 1.0 : 0.3;
        };
        content.Opacity = button.IsEffectivelyEnabled ? 1.0 : 0.3;
        return button;
    }

    /// <summary>Sets "Name: tip" (or just the name) unless the tip already starts with the name.</summary>
    public static void SetTip(Control control, string name, string? tip)
    {
        if (string.IsNullOrEmpty(tip)) ToolTip.SetTip(control, name);
        else if (tip.StartsWith(name, StringComparison.OrdinalIgnoreCase)) ToolTip.SetTip(control, tip);
        else ToolTip.SetTip(control, name + ": " + tip);
    }

    // ---- Drawing helpers ----------------------------------------------------------------------

    private static Canvas Icon(params Control[] parts)
    {
        var canvas = new Canvas { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        foreach (var part in parts) canvas.Children.Add(part);
        return canvas;
    }

    /// <summary>Several parts as one (an identifier made of a stroke and a filled head).</summary>
    private static Control Group(params Control[] parts)
    {
        var canvas = new Canvas { Width = 12, Height = 12 };
        foreach (var part in parts) canvas.Children.Add(part);
        return canvas;
    }

    /// <summary>A filled shape; start the data with "F0" for even-odd holes.</summary>
    private static ShapePath Fill(string data, IBrush brush) => new ShapePath { Data = Geometry.Parse(data), Fill = brush };

    private static ShapePath Stroke(string data, IBrush brush, double thickness = 1.0) =>
        new ShapePath { Data = Geometry.Parse(data), Stroke = brush, StrokeThickness = thickness };

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Circle(double cx, double cy, double r) =>
        $"M{F(cx - r)},{F(cy)} A{F(r)},{F(r)} 0 1 0 {F(cx + r)},{F(cy)} A{F(r)},{F(r)} 0 1 0 {F(cx - r)},{F(cy)} Z";

    private static string Diamond(double cx, double cy, double r) =>
        $"M{F(cx)},{F(cy - r)} L{F(cx + r)},{F(cy)} L{F(cx)},{F(cy + r)} L{F(cx - r)},{F(cy)} Z";

    // ---- Identifiers (bottom-right quarter, centred at 9,9) -----------------------------------

    private static Control Plus() => Stroke("M9,6 V12 M6,9 H12", Yellow, 2.0);
    private static Control Cross() => Stroke("M6.5,6.5 L11.5,11.5 M11.5,6.5 L6.5,11.5", Red, 2.0);
    private static Control Minus() => Stroke("M6,9 H12", Red, 2.0);

    /// <summary>A yellow gear: "done for you" (the AutoRigger).</summary>
    private static Control Gear()
    {
        string teeth = "";
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4.0;
            teeth += $"M{F(9 + 2.0 * Math.Cos(a))},{F(9 + 2.0 * Math.Sin(a))} L{F(9 + 3.2 * Math.Cos(a))},{F(9 + 3.2 * Math.Sin(a))} ";
        }
        return Group(Stroke(teeth, Yellow, 1.3), Fill("F0 " + Circle(9, 9, 2.3) + " " + Circle(9, 9, 0.9), Yellow));
    }

    /// <summary>A circular arrow: reset/normalize, refresh, cycle.</summary>
    private static Control Loop(double cx = 9, double cy = 9, double r = 2.3, IBrush? brush = null)
    {
        brush ??= Yellow;
        // Counter-clockwise from the top round to the right side, arrowhead pointing up there.
        string arc = $"M{F(cx)},{F(cy - r)} A{F(r)},{F(r)} 0 1 0 {F(cx + r)},{F(cy)}";
        double h = r * 0.55;
        string head = $"M{F(cx + r - h)},{F(cy + 0.2)} L{F(cx + r + h)},{F(cy + 0.2)} L{F(cx + r)},{F(cy - h * 1.3)} Z";
        return Group(Stroke(arc, brush, 1.3), Fill(head, brush));
    }

    private static Control ArrowRight(IBrush? b = null) => Group(Stroke("M5.5,9 H10", b ?? Yellow, 1.5), Fill("M9,6.6 L12,9 L9,11.4 Z", b ?? Yellow));
    private static Control ArrowLeft(IBrush? b = null) => Group(Stroke("M7.5,9 H12", b ?? Yellow, 1.5), Fill("M9,6.6 L6,9 L9,11.4 Z", b ?? Yellow));
    private static Control ArrowUp(IBrush? b = null) => Group(Stroke("M9,7.5 V12", b ?? Yellow, 1.5), Fill("M6.6,9 L9,6 L11.4,9 Z", b ?? Yellow));
    private static Control ArrowDown(IBrush? b = null) => Group(Stroke("M9,5.5 V10", b ?? Yellow, 1.5), Fill("M6.6,9 L9,12 L11.4,9 Z", b ?? Yellow));

    /// <summary>A left-right double arrow along the bottom edge (mirror, swap sides).</summary>
    private static Control DoubleArrow() => Group(Stroke("M2.5,10.5 H9.5", Yellow, 1.2),
        Fill("M3,8.6 L0.5,10.5 L3,12.4 Z M9,8.6 L11.5,10.5 L9,12.4 Z", Yellow));

    private static Control Pencil() => Group(Stroke("M7.6,10.4 L11.2,6.8", Yellow, 2.0), Fill("M6,12 L6.4,9.6 L8.4,11.6 Z", Yellow));

    private static Control Wrench() => Group(Stroke("M6.5,11.5 L9.4,8.6", Yellow, 1.8),
        Stroke("M9.4,8.6 A1.7,1.7 0 1 1 11.6,6.4", Yellow, 1.4));

    // ---- Subjects -----------------------------------------------------------------------------

    /// <summary>A bone with two knobs at each end, in the top-left 9 px.</summary>
    private static Control Bone() => Group(
        Stroke("M2.25,7.75 L6.75,3.25", Gray, 2.0),
        Fill(Circle(1.5, 7.0, 1.15) + " " + Circle(3.0, 8.5, 1.15) + " " + Circle(6.0, 2.5, 1.15) + " " + Circle(7.5, 4.0, 1.15), Gray));

    private static Control Joint(double cx, double cy, double r = 1.6) => Fill(Circle(cx, cy, r), Gray);

    private static Control Skull() => Fill(
        "F0 M1,4.5 A3.75,3.75 0 1 1 8.5,4.5 V6.6 L7,7.2 V8.8 H2.5 V7.2 L1,6.6 Z " +
        Circle(3.3, 4.5, 1.05) + " " + Circle(6.2, 4.5, 1.05) + " M4.75,5.9 L4.2,6.9 H5.3 Z", Gray);

    /// <summary>A head-and-shoulders character.</summary>
    private static Control Character() => Fill(Circle(4.5, 2.4, 2.0) + " M0.8,9.5 V8 A3.7,3.3 0 0 1 8.2,8 V9.5 Z", Gray);

    /// <summary>A stick figure in a T-pose: the reference pose.</summary>
    private static Control TPose() => Group(Fill(Circle(4.5, 1.7, 1.4), Gray),
        Stroke("M0.5,4.4 H8.5 M4.5,3.2 V7.6 M4.5,7.4 L2.6,10.8 M4.5,7.4 L6.4,10.8", Gray, 1.3));

    /// <summary>A strip of film: an animation.</summary>
    private static Control Clip() => Fill("F0 M0.5,1 H9.5 V7.5 H0.5 Z M1.5,2 h1.2 v1.2 h-1.2 Z M1.5,5.3 h1.2 v1.2 h-1.2 Z " +
                                          "M7.3,2 h1.2 v1.2 h-1.2 Z M7.3,5.3 h1.2 v1.2 h-1.2 Z", Gray);

    private static Control Folder() => Fill("M0.5,1.5 H3.8 L4.8,2.7 H9.5 V8.5 H0.5 Z", Gray);

    private static Control Page() => Group(Stroke("M1,0.5 H5.8 L8.5,3.2 V10.5 H1 Z", Gray), Stroke("M5.8,0.5 V3.2 H8.5", Gray, 0.8),
        Stroke("M2.6,5 H6.6 M2.6,7 H6.6", Gray, 0.8));

    private static Control Camera() => Fill("M0.5,3 H6.4 V8.2 H0.5 Z M6.4,4.6 L9.2,3 V8.2 L6.4,6.6 Z " + Circle(2.0, 1.6, 1.1) + " " + Circle(4.8, 1.6, 1.1), Gray);

    private static Control Eye(double cy = 4.5) => Group(
        Stroke($"M0.5,{F(cy)} Q5,{F(cy - 4.6)} 9.5,{F(cy)} Q5,{F(cy + 4.6)} 0.5,{F(cy)} Z", Gray),
        Fill(Circle(5, cy, 1.6), Gray));

    private static Control KeyDiamond(double cx = 4.5, double cy = 4.5, double r = 4.0) => Fill(Diamond(cx, cy, r), Gray);

    /// <summary>A two-layer stack (the Mixer's layer bars).</summary>
    private static Control Layers() => Fill("M0.5,0.5 H9 V3.5 H0.5 Z M0.5,5 H9 V8 H0.5 Z", Gray);

    private static Control Ghost(double dx = 0) => Fill(
        $"F0 M{F(1.5 + dx)},10.5 V5 A3.5,3.5 0 0 1 {F(8.5 + dx)},5 V10.5 L{F(7.3 + dx)},9.3 L{F(6.1 + dx)},10.5 L{F(5 + dx)},9.3 " +
        $"L{F(3.9 + dx)},10.5 L{F(2.7 + dx)},9.3 Z " + Circle(3.8 + dx, 4.8, 0.85) + " " + Circle(6.2 + dx, 4.8, 0.85), Gray);

    private static Control Footprint(double cx, double cy) => Fill(
        Circle(cx, cy - 2.6, 0.9) + $" M{F(cx - 1.4)},{F(cy + 0.4)} A1.4,2.2 0 1 1 {F(cx + 1.4)},{F(cy + 0.4)} A1.4,2.2 0 1 1 {F(cx - 1.4)},{F(cy + 0.4)} Z", Gray);

    /// <summary>A triangle mesh.</summary>
    private static Control Mesh() => Stroke("M0.5,8.5 L4.5,0.5 L8.5,8.5 Z M2.5,4.5 H6.5 L4.5,8.5 Z", Gray);

    /// <summary>A mesh with a bone through it: a skinned mesh.</summary>
    private static Control SkinnedMesh() => Group(Mesh(), Stroke("M4.5,2.6 V7.2", Gray, 1.8),
        Fill(Circle(4.5, 2.6, 1.1) + " " + Circle(4.5, 7.2, 1.1), Gray));

    /// <summary>A half-filled bar: a skin weight.</summary>
    private static Control WeightBar() => Group(Stroke("M0.5,2 H10 V6 H0.5 Z", Gray), Fill("M0.5,2 H6 V6 H0.5 Z", Gray));

    private static Control Picture() => Group(Stroke("M0.5,0.5 H9.5 V8 H0.5 Z", Gray), Fill("M1,7.5 L3.8,3.6 L6,6 L7.2,4.8 L9,7.5 Z", Gray),
        Fill(Circle(7.2, 2.4, 0.9), Gray));

    // ---- Anim Bank ----------------------------------------------------------------------------

    public static Control NewAnimation() => Icon(Clip(), Plus());
    public static Control NewFolder() => Icon(Folder(), Plus());
    public static Control DeleteAnimation() => Icon(Clip(), Cross());
    public static Control Retarget() => Icon(Character(), ArrowRight());

    /// <summary>A character with two interlocked yellow links: share its Anim Bank.</summary>
    public static Control LinkBanks() => Icon(Character(),
        Stroke("M6,7.5 H8.5 A1.5,1.5 0 0 1 8.5,10.5 H6 A1.5,1.5 0 0 1 6,7.5 Z " +
               "M9.5,7.5 H12 A1.5,1.5 0 0 1 12,10.5 H9.5 A1.5,1.5 0 0 1 9.5,7.5 Z", Yellow, 1.1));

    /// <summary>A character with two red links pulled apart: stop sharing its Anim Bank.</summary>
    public static Control UnlinkBank() => Icon(Character(),
        Stroke("M5.5,7.5 H6.8 A1.5,1.5 0 0 1 6.8,10.5 H5.5 A1.5,1.5 0 0 1 5.5,7.5 Z " +
               "M11.2,7.5 H12.5 A1.5,1.5 0 0 1 12.5,10.5 H11.2 A1.5,1.5 0 0 1 11.2,7.5 Z", Red, 1.1));

    // ---- CHAR BANK ----------------------------------------------------------------------------

    public static Control CreateCharacter() => Icon(Character(), Plus());
    public static Control CreateRefPose() => Icon(TPose(), Plus());
    public static Control DeleteRefPose() => Icon(TPose(), Cross());

    // ---- RIG WORKSHOP -------------------------------------------------------------------------

    public static Control AutoRigger() => Icon(Skull(), Gear());
    public static Control CreateJoint() => Icon(Bone(), Plus());
    public static Control DeleteJoints() => Icon(Bone(), Cross());

    /// <summary>A chain of two joints with a new yellow joint in the middle.</summary>
    public static Control InsertJoint() => Icon(Stroke("M1.8,4 H10.2", Gray, 1.3), Joint(1.8, 4), Joint(10.2, 4),
        Fill(Circle(6, 4, 1.7), Yellow), Stroke("M6,7.5 V11.5 M4,9.5 H8", Yellow, 1.5));

    /// <summary>A child joint and a yellow arrow up to its new parent.</summary>
    public static Control ParentJoints() => Icon(Joint(9.5, 2.5, 2.0), Joint(2.5, 9.5, 2.0),
        Stroke("M4,8 L7.2,4.8", Yellow, 1.5), Fill("M8.5,3.5 L5.6,4.2 L7.8,6.4 Z", Yellow));

    /// <summary>Two joints whose link is broken, and a red cross.</summary>
    public static Control UnparentJoints() => Icon(Joint(1.8, 2.5), Joint(8.2, 2.5), Stroke("M1.8,2.5 H3.8 M6.2,2.5 H8.2", Gray, 1.3), Cross());

    /// <summary>A joint and a yellow arrow along its aim axis.</summary>
    public static Control OrientJoint() => Icon(Joint(3.5, 3.5, 2.6), Stroke("M5,5 L9.6,9.6", Yellow, 1.5), Fill("M11.5,11.5 L8,10.6 L10.6,8 Z", Yellow));

    /// <summary>A bent limb chain and a plus.</summary>
    public static Control AddIk() => Icon(Stroke("M2,1 L6,4.8 L2.5,9", Gray, 1.3), Joint(2, 1, 1.2), Joint(6, 4.8, 1.2), Joint(2.5, 9, 1.2), Plus());

    public static Control AddReverseFoot() => Icon(Fill("M1.5,0.5 H4 V5.5 L8.5,7 V9 H1.5 Z", Gray), Plus());
    public static Control AddCamera() => Icon(Camera(), Plus());

    public static Control NormalizeScale() => Icon(Stroke("M0.5,0.5 H8.5 V8.5 H0.5 Z", Gray), Fill("M0.5,4.5 H4.5 V8.5 H0.5 Z", Gray), Loop());
    public static Control NormalizeTranslation() => Icon(Stroke("M4.5,0.5 V8.5 M0.5,4.5 H8.5", Gray, 1.2),
        Fill("M4.5,0 L6,1.8 H3 Z M4.5,9 L6,7.2 H3 Z M0,4.5 L1.8,3 V6 Z M9,4.5 L7.2,3 V6 Z", Gray), Loop());
    public static Control NormalizeRotation() => Icon(Stroke(Circle(4.5, 4.5, 3.8), Gray), Stroke("M4.5,4.5 L7.2,1.8", Gray, 1.2), Fill(Circle(4.5, 4.5, 1.0), Gray), Loop());

    /// <summary>The icon of a RIG/SKIN WORKSHOP command, or null when it has none yet.</summary>
    public static Control? ForRigCommand(SloppyAnimatsu.Core.Model.MenuCommand command) => command switch
    {
        SloppyAnimatsu.Core.Model.MenuCommand.AutoRigger => AutoRigger(),
        SloppyAnimatsu.Core.Model.MenuCommand.NewJoint => CreateJoint(),
        SloppyAnimatsu.Core.Model.MenuCommand.InsertJoint => InsertJoint(),
        SloppyAnimatsu.Core.Model.MenuCommand.DeleteJoints => DeleteJoints(),
        SloppyAnimatsu.Core.Model.MenuCommand.ParentJoints => ParentJoints(),
        SloppyAnimatsu.Core.Model.MenuCommand.UnparentJoints => UnparentJoints(),
        SloppyAnimatsu.Core.Model.MenuCommand.OrientJoints => OrientJoint(),
        SloppyAnimatsu.Core.Model.MenuCommand.AddIk => AddIk(),
        SloppyAnimatsu.Core.Model.MenuCommand.AddReverseFoot => AddReverseFoot(),
        SloppyAnimatsu.Core.Model.MenuCommand.AddAnimatedCamera => AddCamera(),
        SloppyAnimatsu.Core.Model.MenuCommand.NormalizeScale => NormalizeScale(),
        SloppyAnimatsu.Core.Model.MenuCommand.NormalizeTranslation => NormalizeTranslation(),
        SloppyAnimatsu.Core.Model.MenuCommand.NormalizeRotation => NormalizeRotation(),
        SloppyAnimatsu.Core.Model.MenuCommand.BindSkin => BindSkin(),
        SloppyAnimatsu.Core.Model.MenuCommand.UnbindSkin => UnbindSkin(),
        _ => null
    };

    // ---- SKIN WORKSHOP ------------------------------------------------------------------------

    public static Control BindSkin() => Icon(SkinnedMesh(), Plus());
    public static Control UnbindSkin() => Icon(SkinnedMesh(), Cross());
    public static Control SetWeight() => Icon(WeightBar(), Pencil());
    public static Control PruneWeights() => Icon(WeightBar(), Minus());
    public static Control FixWeights() => Icon(Stroke("M0.5,6 L2.5,2 L4.5,7 L6.5,2.5 L8.5,6", Gray, 1.2), Wrench());

    /// <summary>A tipped paint bucket and a yellow drop: fill the connected piece.</summary>
    public static Control FloodSelection() => Icon(Fill("M0.8,4.2 L5,0.8 L9.2,5.8 L5,9.2 Z", Gray), Stroke("M2.6,2.6 A2,2 0 0 1 6,1.4", Gray, 0.9),
        Fill("M9.5,6.5 Q11.8,9.6 9.5,11.4 Q7.2,9.6 9.5,6.5 Z", Yellow));

    public static Control MirrorWeights() => Icon(Fill("M5,0.5 L1,7.5 H5 Z", Gray), Stroke("M6,0 V8", Gray, 0.8), Stroke("M7,0.5 L11,7.5 H7 Z", Gray), DoubleArrow());

    /// <summary>Two overlapping pages, all gray (the Mixer's Copy says it with its shape).</summary>
    public static Control CopyWeights() => Icon(Stroke("M0.5,0.5 H6.5 V8 H0.5 Z", Gray), Fill("M4,3.5 H11 V11.5 H4 Z", Gray));

    /// <summary>A clipboard and a yellow arrow into it.</summary>
    public static Control PasteWeights() => Icon(Stroke("M0.5,1.5 H7.5 V11 H0.5 Z", Gray), Fill("M2.3,0.3 H5.7 V2.6 H2.3 Z", Gray), ArrowRight());

    public static Control HideVertices() => Icon(Eye(), Cross());
    public static Control ShowAllVertices() => Icon(Eye(6.0));

    // ---- Timeline, STACKER, CYCLEMAKER ---------------------------------------------------------

    public static Control Play() => Icon(Fill("M3,1.5 L10.5,6 L3,10.5 Z", Gray));
    public static Control Pause() => Icon(Fill("M2.5,1.5 H5 V10.5 H2.5 Z M7,1.5 H9.5 V10.5 H7 Z", Gray));
    public static Control PreviousFrame() => Icon(Fill("M8.5,1.5 L3,6 L8.5,10.5 Z", Gray));
    public static Control NextFrame() => Icon(Fill("M3.5,1.5 L9,6 L3.5,10.5 Z", Gray));
    public static Control PreviousKey() => Icon(Fill("M10,1.5 L4.5,6 L10,10.5 Z", Gray), Fill(Diamond(2.2, 6, 2.0), Gray));
    public static Control NextKey() => Icon(Fill("M2,1.5 L7.5,6 L2,10.5 Z", Gray), Fill(Diamond(9.8, 6, 2.0), Gray));
    public static Control CreateKey() => Icon(KeyDiamond(), Plus());
    public static Control DeleteKey() => Icon(KeyDiamond(), Cross());

    /// <summary>Three keys inside a yellow selection box.</summary>
    public static Control SelectAllKeys() => Icon(Fill(Diamond(2.8, 6, 1.8) + " " + Diamond(6, 6, 1.8) + " " + Diamond(9.2, 6, 1.8), Gray),
        Stroke("M0.5,2.5 H11.5 V9.5 H0.5 Z", Yellow, 0.9));

    public static Control PreviousStack() => Icon(Layers(), ArrowUp());
    public static Control NextStack() => Icon(Layers(), ArrowDown());

    /// <summary>A gray layer bar under a yellow loop: the stack cycles.</summary>
    public static Control LoopStack() => Icon(Fill("M0.5,0.5 H9 V3.5 H0.5 Z", Gray), Loop(7, 7.8, 3.2));

    public static Control PreviousGhosts() => Icon(Ghost(-0.5), ArrowLeft());
    public static Control NextGhosts() => Icon(Ghost(-0.5), ArrowRight());

    public static Control CycleStep() => Icon(Footprint(3.5, 4.5), Loop());
    public static Control CycleSide() => Icon(Footprint(3, 4.2), Footprint(8.5, 3.4), DoubleArrow());

    // ---- MATERIAL -----------------------------------------------------------------------------

    public static Control AddTexture() => Icon(Picture(), Plus());
    public static Control RemoveTexture() => Icon(Picture(), Cross());

    // ---- Blendshapes --------------------------------------------------------------------------

    public static Control SetPointOfView() => Icon(Eye(), Pencil());
    public static Control BlendshapeCamera() => Icon(Camera());
    public static Control ExportSetUp() => Icon(Page(), ArrowUp());
    public static Control ImportSetUp() => Icon(Page(), ArrowDown());

    // ---- Curves -------------------------------------------------------------------------------

    /// <summary>A key with one straight tangent through it.</summary>
    public static Control UnifyTangents() => Icon(Stroke("M0.5,9 L11.5,3", Gray, 1.2), Fill(Diamond(6, 6, 2.2), Gray));

    /// <summary>A key whose two tangents meet at an angle.</summary>
    public static Control BreakTangents() => Icon(Stroke("M0.5,2 L6,6 L11.5,2", Gray, 1.2), Fill(Diamond(6, 6, 2.2), Gray), Fill(Diamond(6, 6, 0.9), Red));

    /// <summary>A curve inside yellow frame corners.</summary>
    public static Control FitCurves() => Icon(Stroke("M2,9 C4,1 7,11 10,3", Gray, 1.2),
        Stroke("M0.5,3 V0.5 H3 M9,0.5 H11.5 V3 M11.5,9 V11.5 H9 M3,11.5 H0.5 V9", Yellow, 1.0));

    // ---- TRANSFORM window ---------------------------------------------------------------------

    // The channel-group toggles sit on the accent colour when ticked, so they draw in a near-white.
    private static readonly IBrush Light = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));

    /// <summary>Translate: a cross with four arrow heads.</summary>
    public static Control TranslateGroup() => Icon(Stroke("M6,2.5 V9.5 M2.5,6 H9.5", Light, 1.4),
        Fill("M6,0 L9,3 H3 Z M6,12 L9,9 H3 Z M0,6 L3,3 V9 Z M12,6 L9,3 V9 Z", Light));

    /// <summary>Rotation: an open circle with an arrow head.</summary>
    public static Control RotateGroup() => Icon(Stroke("M10.4,6 A4.4,4.4 0 1 1 6,1.6", Light, 1.5),
        Fill("M5.4,-0.8 L9.4,1.6 L5.4,4 Z", Light));

    /// <summary>Scale: a small filled box in a big one, with a diagonal arrow.</summary>
    public static Control ScaleGroup() => Icon(Stroke("M0.7,4.5 H7.5 V11.3 H0.7 Z", Light, 1.1), Fill("M0.7,7.5 H4.5 V11.3 H0.7 Z", Light),
        Stroke("M5,7 L10,2", Light, 1.4), Fill("M12,0 L11.6,5.4 L6.6,0.4 Z", Light));

    /// <summary>IK pole: a target.</summary>
    public static Control PoleGroup() => Icon(Stroke(Circle(6, 6, 5.2), Light, 1.1), Stroke(Circle(6, 6, 2.7), Light, 1.1), Fill(Circle(6, 6, 1.1), Light));

    /// <summary>Reverse foot: a foot seen from the side, heel to toe, with the roll arc above it.</summary>
    public static Control FootGroup() => Icon(Fill("M2,3 H4.6 V7.4 L10.6,8.6 Q11.8,9 11.6,11.4 H1.4 Q1,8 2,7 Z", Light),
        Stroke("M5,3.6 A4,3.2 0 0 1 11,4.6", Light, 1.0), Fill("M11.8,3 L11.6,6 L9.4,4.4 Z", Light));

    /// <summary>Blendshape: a round shape morphing into a square one.</summary>
    public static Control BlendshapeGroup() => Icon(Stroke(Circle(3.8, 6, 3.2), Light, 1.2), Fill("M6.5,2.8 H11.5 V9.2 H6.5 Z", Light));

    /// <summary>Reset to ref pose: the T pose with a return loop.</summary>
    public static Control ResetToRefPose() => Icon(TPose(), Loop());

    /// <summary>Copy: a key outline over a filled key.</summary>
    public static Control CopyKeys() => Icon(Stroke(Diamond(4, 4, 3.4), Gray, 1.1), Fill(Diamond(8, 8, 3.4), Gray));

    /// <summary>Paste: a key on a clipboard.</summary>
    public static Control PasteKeys() => Icon(Stroke("M0.5,1.5 H9.5 V11.5 H0.5 Z", Gray), Fill("M3,0.3 H7 V2.8 H3 Z", Gray),
        Fill(Diamond(5, 7, 2.6), Yellow));

    /// <summary>Insert after key: a key, an arrow and the place after it.</summary>
    public static Control InsertAfterKey() => Icon(Fill(Diamond(3, 6, 2.6), Gray), Stroke("M5.8,6 H8.3", Yellow, 1.4),
        Fill("M7.8,3.8 L10.6,6 L7.8,8.2 Z", Yellow), Stroke("M11.5,1 V11", Gray, 1.0));

    /// <summary>Mirror: a figure's half and its outline reflected across a dashed axis.</summary>
    public static Control MirrorPose() => Icon(
        Fill(Circle(3.6, 2.4, 1.6) + " M3.6,4 L4.6,4 L4.6,11.5 L2.6,11.5 L2.6,7 L0.5,8.2 L0.5,6.6 L3.6,4 Z", Gray),
        Stroke(Circle(8.4, 2.4, 1.6) + " M8.4,4 L7.4,4 L7.4,11.5 L9.4,11.5 L9.4,7 L11.5,8.2 L11.5,6.6 L8.4,4 Z", Gray, 0.8),
        new ShapePath { Data = Geometry.Parse("M6,0 V12"), Stroke = Yellow, StrokeThickness = 0.9, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(1.5, 1.2) });

    /// <summary>Reset &amp; Freeze: a mesh with a frost star.</summary>
    public static Control ResetAndFreeze() => Icon(Mesh(),
        Stroke("M9,5.6 V12 M6.2,7.2 L11.8,10.4 M6.2,10.4 L11.8,7.2", Yellow, 1.3));

    // ---- LOG ----------------------------------------------------------------------------------

    public static Control Refresh() => Icon(Page(), Loop());
    public static Control OpenFolder() => Icon(Folder(), ArrowRight());
}
