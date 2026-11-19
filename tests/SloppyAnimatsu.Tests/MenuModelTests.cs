using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The window's menu bar and context menus without a window (<see cref="MenuModel"/>): the
/// structure, every row's command and argument, and the enabled, checked and gesture state each of
/// them takes from the interface context, the selection, the timeline, the view and the shortcut
/// file. The viewer draws these rows and runs the command a click names, so what a click would do is
/// decided here.
/// </summary>
public class MenuModelTests
{
    private static MenuEntry Menu(MenuState state, string header) =>
        MenuModel.Build(state).Single(entry => entry.Header == header);

    private static MenuEntry Row(MenuEntry menu, string header) =>
        menu.Children.Single(entry => entry.Header == header);

    private static List<string> Headers(IEnumerable<MenuEntry> entries) =>
        entries.Select(entry => entry.Header).ToList();

    [Fact]
    public void MenuBar_HasTheExpectedOrderAndEveryTopLevelIsASubmenu()
    {
        var menu = MenuModel.Build(new MenuState());
        Assert.Equal(
            new[] { "_File", "_Edit", "_Rig", "_Keys", "_Animation", "_Stack", "_Context", "_View", "_Export", "_Import", "_Help" },
            Headers(menu));
        Assert.All(menu, entry => Assert.Equal(MenuEntryKind.Submenu, entry.Kind));
        Assert.All(menu, entry => Assert.Null(entry.Command));
        Assert.All(menu, entry => Assert.NotEmpty(entry.Children));
    }

    [Fact]
    public void FileMenu_HasTheFileCommandsAndTheRecentPlaceholder()
    {
        var file = Menu(new MenuState(), "_File");
        Assert.Equal(
            new[] { "_New scene", "_Open…", "_Close file", "_Save", "Save _As…", "", "_Recent scenes", "", "_Preferences…" },
            Headers(file.Children));
        Assert.Equal(
            new MenuCommand?[]
            {
                MenuCommand.NewScene, MenuCommand.Open, MenuCommand.CloseFile, MenuCommand.Save,
                MenuCommand.SaveAs, null, null, null, MenuCommand.Preferences,
            },
            file.Children.Select(entry => entry.Command).ToArray());
        Assert.Equal(MenuEntryKind.Separator, file.Children[5].Kind);
        Assert.Equal(MenuEntryKind.Separator, file.Children[7].Kind);
        // Close file is only usable while a document is open.
        Assert.False(file.Children[2].Enabled);
        Assert.True(Row(Menu(new MenuState { HasDocument = true }, "_File"), "_Close file").Enabled);

        var recent = Row(file, "_Recent scenes");
        Assert.Equal(MenuEntryKind.Submenu, recent.Kind);
        var empty = Assert.Single(recent.Children);
        Assert.Equal(MenuModel.NoRecentScenes, empty.Header);
        Assert.False(empty.Enabled);
        Assert.Null(empty.Command);
    }

    [Fact]
    public void RecentMenu_ListsTheFilesNewestFirstAndEachRowCarriesItsPath()
    {
        string[] files = [@"C:\scenes\b.akt", @"C:\scenes\a.akt"];
        var recent = Row(Menu(new MenuState { RecentFiles = files }, "_File"), "_Recent scenes");
        Assert.Equal(files, Headers(recent.Children));
        Assert.Equal(files, recent.Children.Select(entry => entry.Argument));
        var underscored = Row(Menu(new MenuState { RecentFiles = [@"C:\s\22_bomb_MK8.akt"] }, "_File"), "_Recent scenes");
        Assert.Equal(@"C:\s\22__bomb__MK8.akt", Assert.Single(underscored.Children).Header);
        Assert.Equal(@"C:\s\22_bomb_MK8.akt", underscored.Children[0].Argument);
        Assert.All(recent.Children, entry => Assert.Equal(MenuCommand.OpenRecent, entry.Command));
        Assert.All(recent.Children, entry => Assert.True(entry.Enabled));
    }

    [Fact]
    public void EditMenu_FollowsTheTimelineAndTheRestPose()
    {
        // Nothing open: the key commands are off, the rest-pose commands are off, Undo/Redo stay on.
        var empty = Menu(new MenuState(), "_Edit");
        Assert.False(Row(empty, "_Create key").Enabled);
        Assert.False(Row(empty, "_Delete keys").Enabled);
        Assert.False(Row(empty, "Delete keys, _closing the gaps").Enabled);
        Assert.False(Row(empty, "_Add selection to active stack").Enabled);
        Assert.False(Row(empty, "_Mirror pose").Enabled);
        Assert.True(Row(empty, "_Undo").Enabled);
        Assert.True(Row(empty, "_Redo").Enabled);

        // An animation with a stack, keys selected and the object already tracked: Add turns into Remove.
        var editing = new MenuState
        {
            HasScene = true,
            HasAnimation = true,
            HasStack = true,
            SelectedKeyCount = 2,
            SelectionCount = 1,
            HammerRemoves = true,
        };
        var menu = Menu(editing, "_Edit");
        Assert.True(Row(menu, "_Create key").Enabled);
        Assert.True(Row(menu, "_Delete keys").Enabled);
        Assert.True(Row(menu, "Delete keys, _closing the gaps").Enabled);
        var hammer = Row(menu, "_Remove selection from active stack");
        Assert.Equal(MenuCommand.Hammer, hammer.Command);
        Assert.True(hammer.Enabled);
        Assert.False(Row(menu, "_Mirror pose").Enabled);
        Assert.DoesNotContain(menu.Children, entry => entry.Header == "_Add selection to active stack");

        // Back in the rest pose with a controller selected: Mirror pose runs; with an animation shown it does not.
        var rest = new MenuState { HasScene = true, Context = InterfaceContext.Rigging, SelectionCount = 1 };
        Assert.True(Row(Menu(rest, "_Edit"), "_Mirror pose").Enabled);
        Assert.False(Row(Menu(rest with { HasAnimation = true }, "_Edit"), "_Mirror pose").Enabled);

        // A stack with no selected key: Create key runs, the key commands do not.
        var stackOnly = new MenuState { HasScene = true, HasAnimation = true, HasStack = true };
        var stackMenu = Menu(stackOnly, "_Edit");
        Assert.True(Row(stackMenu, "_Create key").Enabled);
        Assert.False(Row(stackMenu, "_Delete keys").Enabled);
        Assert.False(Row(stackMenu, "Delete keys, _closing the gaps").Enabled);
    }

    [Fact]
    public void EditMenu_KeepsChildrenFixedShowsTheStoredBit()
    {
        var menu = Menu(new MenuState { KeepChildrenFixed = true }, "_Edit");
        var row = Row(menu, "_Keep children fixed");
        Assert.True(row.Checkable);
        Assert.True(row.Checked);
        Assert.Equal(MenuCommand.KeepChildrenFixed, row.Command);
        Assert.False(Row(Menu(new MenuState(), "_Edit"), "_Keep children fixed").Checked);
    }

    [Fact]
    public void RigMenu_NeedsTheRiggingContextAndTheRightSelection()
    {
        // No scene: every rest-pose command is off.
        var none = Menu(new MenuState { Context = InterfaceContext.Rigging }, "_Rig");
        foreach (string header in new[]
                 {
                     "_New joint…", "_Insert joint above…", "_Delete selected joints", "_Orient selected joints",
                     "_Parent selected joints to the last selected", "_Unparent selected joints",
                     "Add _IK (two selected joints)", "Add _reverse foot (two selected joints)", "_Bind skin",
                     "Unbind _skin", "Snap _LTM from the rig pose",
                 })
            Assert.False(Row(none, header).Enabled, header);

        // The Rigging context with the rest pose shown: the bind commands run, nothing that needs a joint.
        var rigging = new MenuState { HasScene = true, Context = InterfaceContext.Rigging };
        var rig = Menu(rigging, "_Rig");
        Assert.True(Row(rig, "_New joint…").Enabled);
        Assert.True(Row(rig, "_Bind skin").Enabled);
        Assert.True(Row(rig, "Unbind _skin").Enabled);
        Assert.True(Row(rig, "Snap _LTM from the rig pose").Enabled);
        // A CHAR BANK pose row current (Bind/Build/Ref Pose): Bind Skin needs Paint & Check
        // Unbind stays available.
        var poseRow = Menu(rigging with { PoseRowCurrent = true }, "_Rig");
        Assert.False(Row(poseRow, "_Bind skin").Enabled);
        Assert.True(Row(poseRow, "Unbind _skin").Enabled);
        Assert.False(Row(rig, "_Insert joint above…").Enabled);
        Assert.False(Row(rig, "_Delete selected joints").Enabled);
        Assert.False(Row(rig, "Add _IK (two selected joints)").Enabled);

        // Two joints with the last being a joint: parent, IK and the reverse foot run.
        var pair = rigging with { JointsSelected = 2, SelectionIsJoint = true, SelectionCount = 2 };
        var pairMenu = Menu(pair, "_Rig");
        Assert.True(Row(pairMenu, "_Parent selected joints to the last selected").Enabled);
        Assert.True(Row(pairMenu, "Add _IK (two selected joints)").Enabled);
        Assert.True(Row(pairMenu, "Add _reverse foot (two selected joints)").Enabled);
        Assert.True(Row(pairMenu, "_Delete selected joints").Enabled);
        Assert.True(Row(pairMenu, "_Insert joint above…").Enabled);

        // A CHAR BANK row other than the Bind/Build Pose (the transform modes 2 and 4): the
        // joint tools are off, Delete and the animated camera stay
        var blocked = Menu(pair with { JointEditBlocked = true }, "_Rig");
        foreach (string header in new[]
                 {
                     "_New joint…", "_Insert joint above…", "_Orient selected joints",
                     "_Parent selected joints to the last selected", "_Unparent selected joints",
                     "Add _IK (two selected joints)", "Add _reverse foot (two selected joints)",
                 })
            Assert.False(Row(blocked, header).Enabled, header);
        Assert.True(Row(blocked, "_Delete selected joints").Enabled);
        Assert.True(Row(blocked, "Add animated _camera").Enabled);

        // Three joints: IK and the reverse foot need exactly two.
        var three = rigging with { JointsSelected = 3, SelectionIsJoint = true, SelectionCount = 3 };
        Assert.False(Row(Menu(three, "_Rig"), "Add _IK (two selected joints)").Enabled);

        // A selected mesh is not a joint: nothing that needs one runs.
        var mesh = rigging with { JointsSelected = 0, SelectionIsJoint = false, SelectionCount = 1 };
        var meshMenu = Menu(mesh, "_Rig");
        Assert.False(Row(meshMenu, "_Insert joint above…").Enabled);
        Assert.False(Row(meshMenu, "_Parent selected joints to the last selected").Enabled);

        // With an animation shown, the rest pose is left and every rest-pose command is off; the
        // animated camera (always available, as in the View menu and the RIG ATELIER) stays.
        var animated = rigging with { HasAnimation = true, JointsSelected = 2, SelectionIsJoint = true };
        var animatedMenu = Menu(animated, "_Rig");
        Assert.All(animatedMenu.Children.Where(entry => entry.Kind == MenuEntryKind.Item
                                                        && entry.Command != MenuCommand.AddAnimatedCamera),
            entry => Assert.False(entry.Enabled, entry.Header));
        Assert.True(Row(animatedMenu, "Add animated _camera").Enabled);

        // The Animation context has no rest-pose commands either.
        var context = new MenuState
        {
            HasScene = true,
            Context = InterfaceContext.Animation,
            JointsSelected = 2,
            SelectionIsJoint = true,
        };
        Assert.False(Row(Menu(context, "_Rig"), "_Bind skin").Enabled);
    }

    [Fact]
    public void TimelineContextMenus_EmptySpotAndKey()
    {
        var empty = MenuModel.TimelineEmptyItems(new MenuState { HasStack = true });
        Assert.Equal(new[] { "Create Key", "Paste Key(s)" }, Headers(empty));
        Assert.True(empty[0].Enabled);
        Assert.False(empty[1].Enabled);
        Assert.True(MenuModel.TimelineEmptyItems(new MenuState { HasStack = true, HasClipboard = true })[1].Enabled);
        Assert.Equal(new[] { MenuCommand.TimelineCreateKey, MenuCommand.TimelinePasteKeys }, empty.Select(e => e.Command!.Value));

        var onKey = MenuModel.TimelineKeyItems(new MenuState { HasStack = true, SelectedKeyCount = 1 });
        Assert.Equal(new[] { "Remove Key(s)", "Copy Key(s)", "", "Create Fade-in", "Create Fade-out" }, Headers(onKey));
        Assert.Equal(new[] { MenuCommand.DeleteKeys, MenuCommand.CopyKeys, MenuCommand.FadeIn, MenuCommand.FadeOut },
            onKey.Where(e => e.Kind == MenuEntryKind.Item).Select(e => e.Command!.Value));
        Assert.All(onKey.Where(e => e.Kind == MenuEntryKind.Item), e => Assert.True(e.Enabled, e.Header));
    }

    [Fact]
    public void KeysMenu_NamesTheTimelineCommandsAndTheirSelectionRules()
    {
        var keys = MenuModel.KeyItems(new MenuState());
        Assert.Equal(
            new[]
            {
                "_Copy keys", "_Paste keys", "Paste keys _after selection", "_Mirror keys", "",
                "Mark selected as _extreme", "Mark selected as _breakdown", "_Remove marks on selected",
                "Remove marks on _all keys", "",
                "Add fade _in", "Add fade _out", "Remove fade i_n", "Remove fade ou_t", "",
                "Move keys _earlier", "Move keys _later", "",
                "_Widen gap before", "_Narrow gap before", "",
                "Change _Space…", "Toggle space_able",
            },
            Headers(keys));
        Assert.Equal(5, keys.Count(entry => entry.Kind == MenuEntryKind.Separator));
        Assert.All(keys.Where(entry => entry.Kind == MenuEntryKind.Item),
            entry => Assert.False(entry.Enabled, entry.Header));

        // A selected key with no clipboard: copy, marks, fades, moves and gaps run; paste does not.
        var selection = new MenuState { HasStack = true, SelectedKeyCount = 1, SelectionCount = 1 };
        var selected = MenuModel.KeyItems(selection);
        Assert.True(selected.Single(entry => entry.Header == "_Copy keys").Enabled);
        Assert.False(selected.Single(entry => entry.Header == "_Paste keys").Enabled);
        Assert.False(selected.Single(entry => entry.Header == "Paste keys _after selection").Enabled);
        Assert.True(selected.Single(entry => entry.Header == "_Mirror keys").Enabled);
        Assert.True(selected.Single(entry => entry.Header == "_Remove marks on selected").Enabled);
        Assert.True(selected.Single(entry => entry.Header == "Add fade _in").Enabled);
        Assert.True(selected.Single(entry => entry.Header == "Move keys _earlier").Enabled);
        Assert.True(selected.Single(entry => entry.Header == "_Widen gap before").Enabled);
        Assert.True(selected.Single(entry => entry.Header == "Change _Space…").Enabled);
        Assert.True(selected.Single(entry => entry.Header == "Toggle space_able").Enabled);

        // With the clipboard filled, both paste commands run.
        var copied = selection with { HasClipboard = true };
        Assert.All(MenuModel.KeyItems(copied).Where(entry =>
                entry.Header is "_Paste keys" or "Paste keys _after selection"),
            entry => Assert.True(entry.Enabled));

        // A stack with no selected key: only "Remove marks on all keys" runs.
        var stackOnly = new MenuState { HasStack = true, SelectionCount = 1 };
        var stackKeys = MenuModel.KeyItems(stackOnly);
        Assert.True(stackKeys.Single(entry => entry.Header == "Remove marks on _all keys").Enabled);
        Assert.False(stackKeys.Single(entry => entry.Header == "_Copy keys").Enabled);
        Assert.False(stackKeys.Single(entry => entry.Header == "Change _Space…").Enabled);

        // Keys selected without a controller: mirror and Change Space need both.
        var keysOnly = selection with { SelectionCount = 0 };
        Assert.False(MenuModel.KeyItems(keysOnly).Single(entry => entry.Header == "_Mirror keys").Enabled);
        Assert.False(MenuModel.KeyItems(keysOnly).Single(entry => entry.Header == "Change _Space…").Enabled);

        // The key gestures are the literals the timeline uses, not the shortcut file's.
        Assert.Equal(new ShortcutKey('C', ShortcutModifiers.Control), selected[0].Gesture);
        Assert.Equal(new ShortcutKey('V', ShortcutModifiers.Control), selected[1].Gesture);
        Assert.Equal(new ShortcutKey('V', ShortcutModifiers.Control | ShortcutModifiers.Shift), selected[2].Gesture);
        Assert.Equal(new ShortcutKey(0x25, ShortcutModifiers.Control),
            selected.Single(entry => entry.Header == "Move keys _earlier").Gesture);
        Assert.Equal(new ShortcutKey(0xBB, ShortcutModifiers.None),
            selected.Single(entry => entry.Header == "_Widen gap before").Gesture);
    }

    [Fact]
    public void Gestures_FollowTheShortcutFileAndFallBackToTheDefault()
    {
        var shortcuts = Shortcuts.CreateDefault();
        var defaults = Menu(new MenuState { Shortcuts = shortcuts }, "_Edit");
        Assert.Equal(new ShortcutKey('Z', ShortcutModifiers.Control), Row(defaults, "_Undo").Gesture);
        Assert.Equal(new ShortcutKey('Z', ShortcutModifiers.Control | ShortcutModifiers.Shift),
            Row(defaults, "_Redo").Gesture);
        Assert.Equal(new ShortcutKey('K', ShortcutModifiers.None), Row(defaults, "_Create key").Gesture);
        Assert.Equal(new ShortcutKey(0x2E, ShortcutModifiers.None), Row(defaults, "_Delete keys").Gesture);
        Assert.Equal(new ShortcutKey(0x2E, ShortcutModifiers.Shift),
            Row(defaults, "Delete keys, _closing the gaps").Gesture);

        // A bound key changes the row's gesture, and the header keeps its accelerator.
        var bound = new ShortcutKey(0x74, ShortcutModifiers.Control | ShortcutModifiers.Alt);
        Assert.True(shortcuts.TrySet("SaveFile", bound, out string? reason), reason);
        var rebound = Menu(new MenuState { Shortcuts = shortcuts }, "_File");
        Assert.Equal(bound, Row(rebound, "_Save").Gesture);
        Assert.Equal("_Save", Row(rebound, "_Save").Header);
        Assert.Equal(MenuCommand.Save, Row(rebound, "_Save").Command);

        // Unbinding falls back to the default the viewer has always used, so a row still shows a key.
        Assert.True(shortcuts.Unbind("SaveFile"));
        Assert.Equal(new ShortcutKey('S', ShortcutModifiers.Control),
            Row(Menu(new MenuState { Shortcuts = shortcuts }, "_File"), "_Save").Gesture);

        // No shortcut file at all: every row falls back to its default.
        Assert.Equal(new ShortcutKey('N', ShortcutModifiers.Control),
            Row(Menu(new MenuState(), "_File"), "_New scene").Gesture);
        Assert.Equal(new ShortcutKey('S', ShortcutModifiers.Control | ShortcutModifiers.Shift),
            Row(Menu(new MenuState(), "_File"), "Save _As…").Gesture);
    }

    [Fact]
    public void ContextMenu_ChecksTheCurrentContext()
    {
        var rigging = Menu(new MenuState { Context = InterfaceContext.Rigging }, "_Context");
        Assert.Equal(new[] { "_Rigging", "_Animation" }, Headers(rigging.Children));
        Assert.True(rigging.Children[0].Checked);
        Assert.False(rigging.Children[1].Checked);
        Assert.All(rigging.Children, entry =>
        {
            Assert.True(entry.Checkable);
            Assert.Equal(MenuCommand.SetContext, entry.Command);
        });
        Assert.Equal(MenuModel.ContextArgument(InterfaceContext.Rigging), rigging.Children[0].Argument);

        var animation = Menu(new MenuState { Context = InterfaceContext.Animation }, "_Context");
        Assert.False(animation.Children[0].Checked);
        Assert.True(animation.Children[1].Checked);
    }

    [Fact]
    public void ViewMenu_HasTheLayoutsTheCamerasAndTheFocusedView()
    {
        var state = new MenuState
        {
            HasScene = true,
            Layout = SceneLayout.TwoViews,
            ViewKey = "camera:Front",
            FocusedView = 2,
            Cameras = ["Front", "Back"],
            AnimatedCameras = ["Cam1"],
        };
        var view = Menu(state, "_View");
        Assert.Equal("Layout:", view.Children[0].Header);
        Assert.Equal(MenuEntryKind.Separator, view.Children[1].Kind);

        var layout = view.Children[0];
        Assert.Equal(
            new[] { "Single view", "Two views", "Two views and curveboard", "Four views", "", "Navigate between layout and single view" },
            Headers(layout.Children));
        Assert.All(layout.Children.Where(entry => entry.Command == MenuCommand.SetLayout),
            entry => Assert.True(entry.Checkable));
        Assert.True(Row(layout, "Two views").Checked);
        Assert.False(Row(layout, "Single view").Checked);
        Assert.False(Row(layout, "Four views").Checked);
        // F1-F4 pick the four layouts; L switches between the layout and a single view.
        Assert.Equal(new ShortcutKey(0x70, ShortcutModifiers.None), Row(layout, "Single view").Gesture);
        Assert.Equal(new ShortcutKey(0x73, ShortcutModifiers.None), Row(layout, "Four views").Gesture);
        Assert.Equal(new ShortcutKey(0x4C, ShortcutModifiers.None),
            Row(layout, "Navigate between layout and single view").Gesture);
        Assert.Equal(SceneLayout.TwoViewsCurveboard,
            Enum.Parse<SceneLayout>(Row(layout, "Two views and curveboard").Argument!));

        // Perspective, the scene's cameras and the character's animated cameras, with the focused one ticked.
        var perspective = Row(view, "Perspective (Orbit)");
        Assert.True(perspective.Checkable);
        Assert.False(perspective.Checked);
        Assert.Equal(MenuModel.PerspectiveView, perspective.Argument);
        Assert.True(Row(view, "Front").Checked);
        Assert.False(Row(view, "Back").Checked);
        Assert.Equal("camera:Front", Row(view, "Front").Argument);
        Assert.Equal("camera:Back", Row(view, "Back").Argument);
        var animated = Row(view, "Animated: Cam1");
        Assert.Equal("animated:Cam1", animated.Argument);
        Assert.False(animated.Checked);
        Assert.Equal("Add animated camera",
            view.Children.Single(entry => entry.Command == MenuCommand.AddAnimatedCamera).Header);

        // The last row says which view is focused and runs nothing.
        var focused = view.Children[^1];
        Assert.Equal("Focused view: 2", focused.Header);
        Assert.False(focused.Enabled);
        Assert.Null(focused.Command);

        // A scene in the orbit view with no camera: only the orbit row, and no animated section.
        var plain = Menu(new MenuState { HasScene = true }, "_View");
        Assert.True(Row(plain, "Perspective (Orbit)").Checked);
        Assert.DoesNotContain(plain.Children, entry => entry.Header.StartsWith("Animated:"));
    }

    [Fact]
    public void JointAxesMenu_ChecksTheStoredAimAndUpAxis()
    {
        var state = new MenuState
        {
            HasScene = true,
            Context = InterfaceContext.Rigging,
            JointAlignment = new JointAlignment(JointAxis.Z, JointAxis.X, AimNegative: true, UpNegative: false,
                AlignOnCreate: false),
        };
        var axes = Menu(state, "_Rig").Children.Single(entry => entry.Header == "Joint _axes");
        Assert.Equal(new[] { "Aim axis", "Up axis", "", "Align on create (a chain tip)" },
            Headers(axes.Children));

        var aim = axes.Children[0];
        Assert.Equal(new[] { "+X", "-X", "+Z", "-Z", "+Y", "-Y" }, Headers(aim.Children));
        Assert.All(aim.Children, entry => Assert.True(entry.Checkable));
        Assert.Equal(new[] { "-Z" }, aim.Children.Where(entry => entry.Checked).Select(entry => entry.Header));
        var up = axes.Children[1];
        Assert.Equal(new[] { "+X" }, up.Children.Where(entry => entry.Checked).Select(entry => entry.Header));

        // Each row carries a parseable argument; the aim rows and the up rows differ only in that flag.
        var aimRow = aim.Children.Single(entry => entry.Header == "-Z");
        Assert.Equal(MenuCommand.JointAxis, aimRow.Command);
        Assert.True(MenuModel.TryParseJointAxis(aimRow.Argument, out bool isAim, out JointAxis axis, out bool negative));
        Assert.True(isAim);
        Assert.Equal(JointAxis.Z, axis);
        Assert.True(negative);
        Assert.True(MenuModel.TryParseJointAxis(up.Children[0].Argument, out isAim, out _, out _));
        Assert.False(isAim);

        Assert.False(axes.Children[3].Checked);
        Assert.Equal(MenuCommand.JointAlignOnCreate, axes.Children[3].Command);
        Assert.True(axes.Children[3].Checkable);

        // The shipped alignment ticks +Y (aim) and +Z (up), and "align on create".
        var defaults = Menu(new MenuState { HasScene = true, Context = InterfaceContext.Rigging }, "_Rig");
        var defaultAxes = defaults.Children.Single(entry => entry.Header == "Joint _axes");
        Assert.Equal(new[] { "+Y" },
            defaultAxes.Children[0].Children.Where(entry => entry.Checked).Select(entry => entry.Header));
        Assert.Equal(new[] { "+Z" },
            defaultAxes.Children[1].Children.Where(entry => entry.Checked).Select(entry => entry.Header));
        Assert.True(defaultAxes.Children[3].Checked);
    }

    [Fact]
    public void AnimationAndStackMenus_FollowTheBankTheAnimationAndTheMixer()
    {
        // Nothing open: every row is off.
        Assert.All(MenuModel.AnimationItems(new MenuState()).Where(entry => entry.Kind == MenuEntryKind.Item),
            entry => Assert.False(entry.Enabled, entry.Header));
        Assert.All(MenuModel.StackItems(new MenuState()).Where(entry => entry.Kind == MenuEntryKind.Item),
            entry => Assert.False(entry.Enabled, entry.Header));

        var editing = new MenuState
        {
            HasScene = true,
            HasAnimBank = true,
            HasAnimation = true,
            HasStack = true,
            HasBankFolder = true,
            SelectionCount = 1,
            StackCount = 3,
            StackIndex = 1,
            CanCycleStep = true,
            CanCycleSide = true,
        };
        var animation = MenuModel.AnimationItems(editing);
        Assert.Equal(
            new[]
            {
                "_New animation", "_Copy animation", "_Delete", "", "_Rename animation…", "Rename _Tool…", "",
                "New _folder", "Rena_me folder…", "", "_Retarget animations…",
            },
            Headers(animation));
        Assert.All(animation.Where(entry => entry.Kind == MenuEntryKind.Item),
            entry => Assert.True(entry.Enabled, entry.Header));

        var stack = MenuModel.StackItems(editing);
        Assert.Equal(
            new[]
            {
                "_New stack from selection", "_Duplicate stack", "De_lete stack", "",
                "Move stack _up", "Move stack do_wn", "", "_Rename stack…",
            },
            Headers(stack));
        Assert.All(stack.Where(entry => entry.Kind == MenuEntryKind.Item),
            entry => Assert.True(entry.Enabled, entry.Header));

        // The top stack cannot move up, the last one cannot move down, and a lone stack cannot go.
        var top = editing with { StackIndex = 0, StackCount = 1 };
        Assert.False(MenuModel.StackItems(top).Single(entry => entry.Header == "Move stack _up").Enabled);
        Assert.False(MenuModel.StackItems(top).Single(entry => entry.Header == "De_lete stack").Enabled);
        Assert.False(MenuModel.StackItems(top).Single(entry => entry.Header == "Move stack do_wn").Enabled);
        var last = editing with { StackIndex = 2 };
        Assert.True(MenuModel.StackItems(last).Single(entry => entry.Header == "Move stack _up").Enabled);
        Assert.False(MenuModel.StackItems(last).Single(entry => entry.Header == "Move stack do_wn").Enabled);

        // Cycle Step / Cycle Side are the CYCLEMAKER panel's buttons, not rows of this menu.
        Assert.DoesNotContain(MenuModel.AnimationItems(editing), entry => entry.Header.StartsWith("Cycle", StringComparison.Ordinal));
        // Renaming a folder needs a folder, not an animation.
        var noFolder = editing with { HasBankFolder = false };
        Assert.False(MenuModel.AnimationItems(noFolder).Single(entry => entry.Header == "Rena_me folder…").Enabled);
        Assert.True(MenuModel.AnimationItems(noFolder).Single(entry => entry.Header == "_Delete").Enabled);
        // The one Delete also takes a lone folder (no animation current).
        var folderOnly = editing with { HasAnimation = false };
        Assert.True(MenuModel.AnimationItems(folderOnly).Single(entry => entry.Header == "_Delete").Enabled);
        Assert.DoesNotContain(MenuModel.AnimationItems(editing), entry => entry.Header == "Delete f_older");
        // Retargeting needs only a scene: a scene with fewer than two characters reports the
        // message when the dialog opens instead of disabling the row.
        Assert.True(MenuModel.AnimationItems(editing).Single(entry => entry.Header == "_Retarget animations…").Enabled);
        Assert.False(MenuModel.AnimationItems(editing with { HasScene = false })
            .Single(entry => entry.Header == "_Retarget animations…").Enabled);
    }

    [Fact]
    public void EveryMenuRowIsRunnableUnlessItIsALabelOrASeparator()
    {
        // A state where every menu is filled in: every command of the enum must appear somewhere.
        var state = new MenuState
        {
            HasScene = true,
            Context = InterfaceContext.Rigging,
            HasStack = true,
            HasAnimBank = true,
            HasAnimation = true,
            HasBankFolder = true,
            HasClipboard = true,
            SelectionCount = 2,
            JointsSelected = 2,
            SelectionIsJoint = true,
            TreeRowSkinLocked = false,
            SelectedKeyCount = 2,
            StackCount = 2,
            StackIndex = 0,
            CanCycleStep = true,
            CanCycleSide = true,
            RecentFiles = [@"C:\a.akt"],
            Cameras = ["Front"],
            AnimatedCameras = ["Cam1"],
        };
        var commands = MenuModel.Commands(state);
        // Cycle Step / Cycle Side run from the CYCLEMAKER panel's buttons only.
        Assert.Equal([MenuCommand.CycleStep, MenuCommand.CycleSide], Enum.GetValues<MenuCommand>().Except(commands));
        Assert.Equal(commands.Count, commands.Distinct().Count());

        // Walk every row: a separator has no command, a submenu has children and no command, and a
        // row either runs a command or is a disabled label.
        void Walk(IEnumerable<MenuEntry> entries)
        {
            foreach (MenuEntry entry in entries)
            {
                switch (entry.Kind)
                {
                    case MenuEntryKind.Separator:
                        Assert.Equal("", entry.Header);
                        Assert.Null(entry.Command);
                        Assert.Empty(entry.Children);
                        break;
                    case MenuEntryKind.Submenu:
                        Assert.Null(entry.Command);
                        Assert.NotEmpty(entry.Children);
                        Assert.True(entry.Enabled, entry.Header);
                        Walk(entry.Children);
                        break;
                    default:
                        if (entry.Command == null) Assert.False(entry.Enabled, entry.Header);
                        else Assert.NotEmpty(entry.Header);
                        break;
                }
            }
        }
        Walk(MenuModel.Build(state));
        Walk(MenuModel.KeyItems(state));
        Walk(MenuModel.AnimationItems(state));
        Walk(MenuModel.StackItems(state));

        // The start-up guard: every command with a handler is accounted for, and a forgotten one shows up.
        Assert.Empty(MenuModel.Unhandled(state, commands));
        var missing = MenuModel.Unhandled(state, commands.Where(command => command != MenuCommand.NewScene));
        Assert.Equal(new[] { MenuCommand.NewScene }, missing);
    }

    [Fact]
    public void Commands_CarryAParseableArgument()
    {
        foreach (SceneLayout layout in Enum.GetValues<SceneLayout>())
        {
            string argument = MenuModel.LayoutArgument(layout);
            Assert.True(MenuModel.TryParseLayout(argument, out SceneLayout parsed));
            Assert.Equal(layout, parsed);
        }
        foreach (InterfaceContext context in Enum.GetValues<InterfaceContext>())
        {
            string argument = MenuModel.ContextArgument(context);
            Assert.True(MenuModel.TryParseContext(argument, out InterfaceContext parsed));
            Assert.Equal(context, parsed);
        }
        foreach (JointAxis axis in Enum.GetValues<JointAxis>())
            foreach (bool aim in new[] { true, false })
                foreach (bool negative in new[] { true, false })
                {
                    string argument = MenuModel.JointAxisArgument(aim, axis, negative);
                    Assert.True(MenuModel.TryParseJointAxis(argument, out bool parsedAim, out JointAxis parsedAxis,
                        out bool parsedNegative));
                    Assert.Equal(aim, parsedAim);
                    Assert.Equal(axis, parsedAxis);
                    Assert.Equal(negative, parsedNegative);
                }

        // Anything else is refused rather than read into a default.
        foreach (string? bad in new string?[] { null, "", "aim", "aim,X", "aim,W,+", "Aim,X,+", "aim,X,*", "x,y,z,w" })
            Assert.False(MenuModel.TryParseJointAxis(bad, out _, out _, out _));
        Assert.False(MenuModel.TryParseLayout("Five views", out _));
        Assert.False(MenuModel.TryParseContext("Neither", out _));
    }

    [Fact]
    public void ContextNamesAndTheSwapButtonAgreeWithTheTwoContexts()
    {
        // The status bar's bottom-right swap names the current context and the one a click reaches.
        Assert.Equal("Rigging", MenuModel.ContextName(InterfaceContext.Rigging));
        Assert.Equal("Animation", MenuModel.ContextName(InterfaceContext.Animation));
        Assert.Equal(InterfaceContext.Animation, MenuModel.OtherContext(InterfaceContext.Rigging));
        Assert.Equal(InterfaceContext.Rigging, MenuModel.OtherContext(InterfaceContext.Animation));
        foreach (InterfaceContext context in Enum.GetValues<InterfaceContext>())
        {
            Assert.NotEqual(context, MenuModel.OtherContext(context));
            Assert.NotEqual(MenuModel.ContextName(context), MenuModel.ContextName(MenuModel.OtherContext(context)));
        }
    }
}
