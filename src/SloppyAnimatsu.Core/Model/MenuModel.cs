namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The commands the viewer's menus run. A menu row names one command and, when it needs one, an
/// argument (a recent scene's path, a layout, a view key, a joint axis); the window turns the tree
/// <see cref="MenuModel"/> builds into its own items and runs the command a click names. That keeps
/// the structure, the enabled and checked state and what a click carries testable without a window
/// </summary>
public enum MenuCommand
{
    // File
    NewScene,
    Open,
    CloseFile,
    Save,
    SaveAs,
    OpenRecent,
    Preferences,

    // Edit
    Undo,
    Redo,
    CreateKey,
    DeleteKeys,
    DeleteKeysClosingGaps,
    Hammer,
    KeepChildrenFixed,
    MirrorPose,
    KeyboardShortcuts,

    // Rig
    AutoRigger,
    NewJoint,
    InsertJoint,
    DeleteJoints,
    OrientJoints,
    JointAxis,
    JointAlignOnCreate,
    ParentJoints,
    UnparentJoints,
    AddIk,
    AddReverseFoot,
    BindSkin,
    UnbindSkin,
    SnapLtm,

    // Keys
    CopyKeys,
    PasteKeys,
    PasteKeysAfter,
    MirrorKeys,
    MarkExtreme,
    MarkBreakdown,
    RemoveMarks,
    RemoveMarksAll,
    FadeIn,
    FadeOut,
    RemoveFadeIn,
    RemoveFadeOut,
    MoveKeysEarlier,
    MoveKeysLater,
    WidenGap,
    NarrowGap,
    ChangeSpace,
    ToggleSpaceable,

    // Animation
    NewAnimation,
    CopyAnimation,
    DeleteAnimation,
    RenameAnimation,
    NewFolder,
    RenameFolder,
    CycleStep,
    CycleSide,
    RetargetAnimations,

    // Stack
    NewStack,
    DuplicateStack,
    DeleteStack,
    MoveStackUp,
    MoveStackDown,
    RenameStack,

    // Context
    SetContext,

    // Outliner (the hierarchy panel's context menu)
    /// <summary>Renames the picked hierarchy row in place (the row turns into a text field).</summary>
    RenameTreeObject,
    /// <summary>Opens the Rename Tool on the selection of the panel that opened it (tree or Anim Bank).</summary>
    RenameTool,
    /// <summary>Shows the picked rows and everything under them.</summary>
    ShowHierarchy,
    /// <summary>Hides the picked rows and everything under them.</summary>
    HideHierarchy,
    /// <summary>Selects the picked rows and every row under them.</summary>
    SelectHierarchy,
    ExpandHierarchy,
    CollapseHierarchy,
    DeleteTreeObject,
    // The same menu opened on the panel's blank space.
    ShowAllObjects,
    HideAllObjects,
    SelectAllTreeRows,
    ExpandAllTreeRows,
    CollapseAllTreeRows,
    /// <summary>Locks or unlocks the skin of the picked skinned mesh (the hierarchy panel's menu).</summary>
    ToggleSkinLock,

    // View
    SetLayout,
    ToggleLayout,
    SetView,
    AddAnimatedCamera,

    // File > Import / Export, Help
    /// <summary>File > Import…: one picker for every file type the app imports (FBX, scenes, .aknm).</summary>
    Import,
    /// <summary>File > Export…: the Export Settings window.</summary>
    Export,
    About,
    /// <summary>Help > Show Welcome screen: the startup Welcome window, on demand.</summary>
    ShowWelcome,
    /// <summary>Help > Show log: this run's log and the previous run's, in a window.</summary>
    ShowLog,
    /// <summary>Help > Check for updates: looks up the newest GitHub release and offers to install it.</summary>
    CheckForUpdates,
    /// <summary>The timeline's empty-spot menu: a key at the clicked frame.</summary>
    TimelineCreateKey,
    /// <summary>The timeline's empty-spot menu: the clipboard pasted at the clicked frame.</summary>
    TimelinePasteKeys,
    /// <summary>SloppyAnimatsu's own panel keys (panelShortcuts.slopa): Hammer, the timeline and curveboard keys.</summary>
    PanelKeys,
}

/// <summary>How a menu row is drawn: an item (a command or a disabled label), a submenu, or a separator.</summary>
public enum MenuEntryKind
{
    Item,
    Submenu,
    Separator,
}

/// <summary>
/// One row of a menu: its header (with the underscore the app uses for the access key), the command
/// a click runs and its argument, the gesture the shortcut file gives it, and its state. A label row
/// is an item with no command, disabled.
/// </summary>
public sealed record MenuEntry
{
    /// <summary>The row's text, underscores included.</summary>
    public required string Header { get; init; }

    public MenuEntryKind Kind { get; init; } = MenuEntryKind.Item;

    /// <summary>The command a click runs, or null for a submenu header and an information label.</summary>
    public MenuCommand? Command { get; init; }

    /// <summary>The command's argument, in the form <see cref="MenuModel"/> writes it.</summary>
    public string? Argument { get; init; }

    /// <summary>The gesture to show, resolved from the shortcut file, or null for none.</summary>
    public ShortcutKey? Gesture { get; init; }

    /// <summary>Whether the row shows a tick (the checkable menu items).</summary>
    public bool Checkable { get; init; }

    /// <summary>Whether the tick is on.</summary>
    public bool Checked { get; init; }

    public bool Enabled { get; init; } = true;

    public IReadOnlyList<MenuEntry> Children { get; init; } = [];

    /// <summary>A separator row.</summary>
    public static MenuEntry Separator => new() { Header = "", Kind = MenuEntryKind.Separator };
}

/// <summary>
/// What the menus depend on: the interface context, the selection, the timeline and the Anim Bank,
/// the shown animation and stack, the view layout and its cameras, the recent scenes, the joint axes
/// and the shortcut file. The window fills this in from its own fields; the menu rules are here.
/// </summary>
public sealed record MenuState
{
    public InterfaceContext Context { get; init; } = InterfaceContext.Animation;

    /// <summary>A scene is open (the window has both a scene and the character shown).</summary>
    public bool HasScene { get; init; }

    /// <summary>At least one document is open, so File &gt; Close file has something to close.</summary>
    public bool HasDocument { get; init; }

    /// <summary>An animation is shown; the rest pose is left.</summary>
    public bool HasAnimation { get; init; }

    /// <summary>The timeline has a stack, so keys can be made and stack commands can run.</summary>
    public bool HasStack { get; init; }

    public bool HasAnimBank { get; init; }

    /// <summary>A folder of the Anim Bank is selected.</summary>
    public bool HasBankFolder { get; init; }

    /// <summary>Copied keys are waiting on the clipboard.</summary>
    public bool HasClipboard { get; init; }

    public bool KeepChildrenFixed { get; init; }

    /// <summary>The current stack already holds the selected object, so Add turns into Remove.</summary>
    public bool HammerRemoves { get; init; }

    public int SelectionCount { get; init; }

    public int JointsSelected { get; init; }

    /// <summary>The current object (the last selected) is a joint.</summary>
    public bool SelectionIsJoint { get; init; }

    /// <summary>
    /// A CHAR BANK pose row (Bind, Build or Ref Pose) is the character's current mode rather than
    /// Paint &amp; Check. Bind Skin needs Paint &amp; Check.
    /// </summary>
    public bool PoseRowCurrent { get; init; }

    /// <summary>
    /// The CHAR BANK's current row is neither the Bind Pose nor the Build Pose (the Ref Pose row or
    /// Paint &amp; Check): the app greys the joint-structure tools then (transform modes 3 and 5,
    ///).
    /// </summary>
    public bool JointEditBlocked { get; init; }

    /// <summary>
    /// The hierarchy panel's context menu was opened on a row, so it acts on that row's hierarchy;
    /// false when it was opened on the panel's blank space, and then it offers the "all" commands.
    /// </summary>
    public bool HasTreeRow { get; init; }

    /// <summary>
    /// The picked hierarchy row is a skinned mesh: whether its skin is locked. Null for any other
    /// row, and then the panel's menu has no lock row.
    /// </summary>
    public bool? TreeRowSkinLocked { get; init; }

    public int SelectedKeyCount { get; init; }

    public int StackCount { get; init; }

    /// <summary>The active stack's place in the Mixer (0 = top), or -1.</summary>
    public int StackIndex { get; init; } = -1;

    /// <summary>The Cyclemaker's Cycle Step can run on the active stack.</summary>
    public bool CanCycleStep { get; init; }

    /// <summary>The Cyclemaker's Cycle Side can run on the active stack.</summary>
    public bool CanCycleSide { get; init; }

    public SceneLayout Layout { get; init; } = SceneLayout.SingleView;

    /// <summary>The focused view's camera key (<c>perspective</c>, <c>camera:Name</c>, <c>animated:Name</c>).</summary>
    public string ViewKey { get; init; } = MenuModel.PerspectiveView;

    /// <summary>The focused view's number, as the View menu prints it (1-based).</summary>
    public int FocusedView { get; init; } = 1;

    /// <summary>The scene's cameras' names, in the scene's order.</summary>
    public IReadOnlyList<string> Cameras { get; init; } = [];

    /// <summary>The shown character's animated cameras' names.</summary>
    public IReadOnlyList<string> AnimatedCameras { get; init; } = [];

    /// <summary>The recent scene paths, newest first.</summary>
    public IReadOnlyList<string> RecentFiles { get; init; } = [];

    public JointAlignment JointAlignment { get; init; } = JointAlignment.Default;

    /// <summary>The shortcut file, or null before it is read.</summary>
    public Shortcuts? Shortcuts { get; init; }

    /// <summary>The Rigging context with the rest pose shown: the rest-pose commands' condition.</summary>
    public bool Rigging => HasScene && !HasAnimation && Context == InterfaceContext.Rigging;

    /// <summary>Keys are selected in the active stack.</summary>
    public bool HasKeySelection => HasStack && SelectedKeyCount > 0;

    /// <summary>A selected key already has a fade-in: the timeline key menu offers to remove it.</summary>
    public bool SelectedKeysHaveFadeIn { get; init; }

    /// <summary>A selected key already has a fade-out: the timeline key menu offers to remove it.</summary>
    public bool SelectedKeysHaveFadeOut { get; init; }
}

/// <summary>
/// The viewer's whole menu bar and its context menus as data: the File/Edit/Rig/Keys/Animation/
/// Stack/Context/View/Export/Import/Help menus, the timeline's key menu and the animation list's
/// menu. Every enabled and checked state is a rule over <see cref="MenuState"/>, so the
/// structure and the window's own choices can be checked with no window open; the window only draws
/// the rows and dispatches the command a click names.
/// </summary>
public static class MenuModel
{
    /// <summary>The <see cref="MenuState.ViewKey"/> prefix of a scene camera.</summary>
    public const string CameraViewPrefix = "camera:";

    /// <summary>The <see cref="MenuState.ViewKey"/> prefix of an animated camera.</summary>
    public const string AnimatedViewPrefix = "animated:";

    /// <summary>The <see cref="MenuState.ViewKey"/> of the orbit view.</summary>
    public const string PerspectiveView = "perspective";

    /// <summary>The Recent scenes submenu's row when the list is empty.</summary>
    public const string NoRecentScenes = "(no recent scenes)";

    /// <summary>The Plugins menu's row while no plugin is installed.</summary>
    public const string NoPlugins = "(no plugins installed)";

    /// <summary>The menu bar the window shows: File, Edit, Plugins, Help.</summary>
    public static IReadOnlyList<MenuEntry> Bar(MenuState state) =>
    [
        Submenu("_File", FileItems(state)),
        Submenu("_Edit", EditItems(state)),
        Submenu("_Plugins", Label(NoPlugins)),
        Submenu("_Help", HelpItems()),
    ];

    /// <summary>
    /// The command groups that are not on the menu bar but whose rows still carry keys (the
    /// window's key router runs them): Rig, Keys, Animation, Stack, Context and View. Their commands
    /// are reached by those keys, the panels' buttons and the context menus.
    /// </summary>
    public static IReadOnlyList<MenuEntry> KeyedGroups(MenuState state) =>
    [
        Submenu("_Rig", RigItems(state)),
        Submenu("_Keys", KeyItems(state).ToArray()),
        Submenu("_Animation", AnimationItems(state).ToArray()),
        Submenu("_Stack", StackItems(state).ToArray()),
        Submenu("_Context", ContextItems(state)),
        Submenu("_View", ViewItems(state)),
    ];

    /// <summary>Every command row a key can run: the menu bar, then <see cref="KeyedGroups"/>.</summary>
    public static IReadOnlyList<MenuEntry> Build(MenuState state) => [.. Bar(state), .. KeyedGroups(state)];

    /// <summary>The two interface contexts, the current one checked.</summary>
    public static MenuEntry[] ContextItems(MenuState state) =>
    [
        CheckItem("_Rigging", MenuCommand.SetContext, ContextArgument(InterfaceContext.Rigging),
            state.Context == InterfaceContext.Rigging),
        CheckItem("_Animation", MenuCommand.SetContext, ContextArgument(InterfaceContext.Animation),
            state.Context == InterfaceContext.Animation),
    ];

    /// <summary>The key mark, order, fade and gap commands, as the Keys menu and the timeline's context menu.</summary>
    public static IReadOnlyList<MenuEntry> KeyItems(MenuState state)
    {
        bool selected = state.HasKeySelection;
        bool controllers = state.SelectionCount > 0;
        return
        [
            Item("_Copy keys", MenuCommand.CopyKeys, gesture: Ctrl(0x43), enabled: selected),
            // With no key selected the paste goes to the playhead.
            Item("_Paste keys", MenuCommand.PasteKeys, gesture: Ctrl(0x56),
                enabled: state.HasStack && state.HasClipboard),
            Item("Paste keys _after selection", MenuCommand.PasteKeysAfter,
                gesture: new ShortcutKey(0x56, ShortcutModifiers.Control | ShortcutModifiers.Shift),
                enabled: selected && state.HasClipboard),
            Item("_Mirror keys", MenuCommand.MirrorKeys, enabled: selected && controllers),
            MenuEntry.Separator,
            Item("Mark selected as _extreme", MenuCommand.MarkExtreme, enabled: selected),
            Item("Mark selected as _breakdown", MenuCommand.MarkBreakdown, enabled: selected),
            Item("_Remove marks on selected", MenuCommand.RemoveMarks, enabled: selected),
            Item("Remove marks on _all keys", MenuCommand.RemoveMarksAll, enabled: state.HasStack),
            MenuEntry.Separator,
            Item("Add fade _in", MenuCommand.FadeIn, enabled: selected),
            Item("Add fade _out", MenuCommand.FadeOut, enabled: selected),
            Item("Remove fade i_n", MenuCommand.RemoveFadeIn, enabled: selected),
            Item("Remove fade ou_t", MenuCommand.RemoveFadeOut, enabled: selected),
            MenuEntry.Separator,
            Item("Move keys _earlier", MenuCommand.MoveKeysEarlier, gesture: Ctrl(0x25), enabled: selected),
            Item("Move keys _later", MenuCommand.MoveKeysLater, gesture: Ctrl(0x27), enabled: selected),
            MenuEntry.Separator,
            Item("_Widen gap before", MenuCommand.WidenGap, gesture: Key(0xBB), enabled: selected),
            Item("_Narrow gap before", MenuCommand.NarrowGap, gesture: Key(0xBD), enabled: selected),
            MenuEntry.Separator,
            Item("Change _Space…", MenuCommand.ChangeSpace, enabled: selected && controllers),
            Item("Toggle space_able", MenuCommand.ToggleSpaceable, enabled: controllers),
        ];
    }

    /// <summary>The timeline's context menu on an empty spot: a key or the clipboard at the clicked frame.</summary>
    public static IReadOnlyList<MenuEntry> TimelineEmptyItems(MenuState state) =>
    [
        Item("Create Key", MenuCommand.TimelineCreateKey, gesture: Gesture(state, "StackerCreateKey", Key(0x4B)),
            enabled: state.HasStack),
        Item("Paste Key(s)", MenuCommand.TimelinePasteKeys, gesture: Ctrl(0x56),
            enabled: state.HasStack && state.HasClipboard),
    ];

    /// <summary>The timeline's context menu on a key: the selected keys (the clicked one joins them).
    /// A fade row becomes its Remove row when a selected key already has that fade.</summary>
    public static IReadOnlyList<MenuEntry> TimelineKeyItems(MenuState state)
    {
        bool selected = state.HasKeySelection;
        return
        [
            Item("Remove Key(s)", MenuCommand.DeleteKeys, gesture: Key(0x2E), enabled: selected),
            Item("Copy Key(s)", MenuCommand.CopyKeys, gesture: Ctrl(0x43), enabled: selected),
            MenuEntry.Separator,
            state.SelectedKeysHaveFadeIn
                ? Item("Remove Fade-in", MenuCommand.RemoveFadeIn, enabled: selected)
                : Item("Create Fade-in", MenuCommand.FadeIn, enabled: selected),
            state.SelectedKeysHaveFadeOut
                ? Item("Remove Fade-out", MenuCommand.RemoveFadeOut, enabled: selected)
                : Item("Create Fade-out", MenuCommand.FadeOut, enabled: selected),
        ];
    }

    /// <summary>The animation list's commands, as the Animation menu and the list's context menu.</summary>
    public static IReadOnlyList<MenuEntry> AnimationItems(MenuState state) =>
    [
        Item("_New animation", MenuCommand.NewAnimation, enabled: state.HasAnimBank),
        Item("_Copy animation", MenuCommand.CopyAnimation, enabled: state.HasAnimation),
        // One Delete (also the Delete key in the bank) for whatever rows are selected: animations,
        // and folders with everything inside them.
        Item("_Delete", MenuCommand.DeleteAnimation, enabled: state.HasAnimation || state.HasBankFolder),
        MenuEntry.Separator,
        Item("_Rename animation…", MenuCommand.RenameAnimation, enabled: state.HasAnimation),
        Item("Rename _Tool…", MenuCommand.RenameTool, enabled: state.HasAnimation || state.HasBankFolder),
        MenuEntry.Separator,
        Item("New _folder", MenuCommand.NewFolder, enabled: state.HasAnimBank),
        Item("Rena_me folder…", MenuCommand.RenameFolder, enabled: state.HasBankFolder),
        MenuEntry.Separator,
        // Cycle Step / Cycle Side are the Cyclemaker panel's buttons only, not bank menu rows.
        // The app runs the action from its menus and picks the pair in the dialog; the scene is the
        // only requirement (a scene with fewer than two characters reports the message).
        Item("_Retarget animations…", MenuCommand.RetargetAnimations, enabled: state.HasScene),
    ];

    /// <summary>The Mixer's commands, as the Stack menu.</summary>
    public static IReadOnlyList<MenuEntry> StackItems(MenuState state) =>
    [
        Item("_New stack from selection", MenuCommand.NewStack, enabled: state.HasAnimation),
        Item("_Duplicate stack", MenuCommand.DuplicateStack, enabled: state.HasStack),
        Item("De_lete stack", MenuCommand.DeleteStack,
            enabled: state.HasAnimation && state.HasStack && state.StackCount > 1),
        MenuEntry.Separator,
        Item("Move stack _up", MenuCommand.MoveStackUp, enabled: state.StackIndex > 0),
        Item("Move stack do_wn", MenuCommand.MoveStackDown,
            enabled: state.StackIndex >= 0 && state.StackIndex < state.StackCount - 1),
        MenuEntry.Separator,
        Item("_Rename stack…", MenuCommand.RenameStack, enabled: state.HasStack),
    ];

    /// <summary>
    /// The hierarchy panel's right-click menu. Opened on a row it acts on the picked rows and
    /// everything under them: Rename and Rename Tool (the first rows), Show/Hide hierarchy,
    /// Select hierarchy, Expand/Collapse hierarchy, Delete tree object. Opened on the panel's blank
    /// space it offers the same commands for the whole tree instead (Show all, Hide all, Select all,
    /// Expand all, Collapse all). SloppyAnimatsu's own arrangement; the
    /// Show/Hide/Toggle rows and Toggle hierarchy selection are not offered.
    /// </summary>
    public static IReadOnlyList<MenuEntry> OutlinerItems(MenuState state)
    {
        if (!state.HasTreeRow)
            return
            [
                Item("_Show all", MenuCommand.ShowAllObjects, enabled: state.HasScene),
                Item("_Hide all", MenuCommand.HideAllObjects, enabled: state.HasScene),
                MenuEntry.Separator,
                Item("Select _all", MenuCommand.SelectAllTreeRows, enabled: state.HasScene),
                MenuEntry.Separator,
                Item("_Expand all", MenuCommand.ExpandAllTreeRows, enabled: state.HasScene),
                Item("_Collapse all", MenuCommand.CollapseAllTreeRows, enabled: state.HasScene),
            ];
        var rows = new List<MenuEntry>
        {
            // The menu opens with Rename and Rename Tool (AGENTS.md, the TREE's context menu).
            Item("_Rename", MenuCommand.RenameTreeObject),
            Item("Rename T_ool…", MenuCommand.RenameTool),
            MenuEntry.Separator,
            Item("_Show hierarchy", MenuCommand.ShowHierarchy),
            Item("_Hide hierarchy", MenuCommand.HideHierarchy),
            MenuEntry.Separator,
            Item("Select hierarchy", MenuCommand.SelectHierarchy),
            MenuEntry.Separator,
            Item("_Expand hierarchy", MenuCommand.ExpandHierarchy),
            Item("_Collapse hierarchy", MenuCommand.CollapseHierarchy),
            MenuEntry.Separator,
            Item("_Delete tree object", MenuCommand.DeleteTreeObject),
        };
        // A skinned mesh's lock (Unbind Skin leaves a locked mesh attached) is a SloppyAnimatsu row of
        // this menu, shown only for skinned meshes.
        if (state.TreeRowSkinLocked is bool locked)
        {
            rows.Add(MenuEntry.Separator);
            rows.Add(Item(locked ? "_Unlock skin" : "_Lock skin", MenuCommand.ToggleSkinLock));
        }
        return rows;
    }

    /// <summary>The argument a <see cref="MenuCommand.SetLayout"/> row carries.</summary>
    public static string LayoutArgument(SceneLayout layout) => layout.ToString();

    public static bool TryParseLayout(string? text, out SceneLayout layout) =>
        Enum.TryParse(text, out layout) && Enum.IsDefined(layout);

    /// <summary>The argument a <see cref="MenuCommand.SetContext"/> row carries.</summary>
    public static string ContextArgument(InterfaceContext context) => context.ToString();

    public static bool TryParseContext(string? text, out InterfaceContext context) =>
        Enum.TryParse(text, out context) && Enum.IsDefined(context);

    /// <summary>The context's name as the window shows it (the status bar's context button).</summary>
    public static string ContextName(InterfaceContext context) => context switch
    {
        InterfaceContext.Rigging => "Rigging",
        _ => "Animation",
    };

    /// <summary>The context a swap button switches to.</summary>
    public static InterfaceContext OtherContext(InterfaceContext context) =>
        context == InterfaceContext.Rigging ? InterfaceContext.Animation : InterfaceContext.Rigging;

    /// <summary>The argument a <see cref="MenuCommand.JointAxis"/> row carries: <c>aim,X,+</c>.</summary>
    public static string JointAxisArgument(bool aim, JointAxis axis, bool negative) =>
        $"{(aim ? "aim" : "up")},{JointAlignment.NameOf(axis)},{(negative ? "-" : "+")}";

    /// <summary>Reads <see cref="JointAxisArgument"/> back; anything else is not a joint axis row.</summary>
    public static bool TryParseJointAxis(string? text, out bool aim, out JointAxis axis, out bool negative)
    {
        aim = true;
        axis = JointAxis.X;
        negative = false;
        if (text == null) return false;
        string[] parts = text.Split(',');
        if (parts.Length != 3) return false;
        bool? aimParsed = parts[0] switch { "aim" => true, "up" => false, _ => null };
        bool? negativeParsed = parts[2] switch { "+" => false, "-" => true, _ => null };
        JointAxis? axisParsed = parts[1] switch
        {
            "X" => JointAxis.X,
            "Z" => JointAxis.Z,
            "Y" => JointAxis.Y,
            _ => null,
        };
        if (aimParsed is not { } a || negativeParsed is not { } n || axisParsed is not { } x) return false;
        aim = a;
        axis = x;
        negative = n;
        return true;
    }

    /// <summary>Every command any menu of this state names, once each, in menu order.</summary>
    public static IReadOnlyList<MenuCommand> Commands(MenuState state)
    {
        var commands = new List<MenuCommand>();
        foreach (MenuEntry entry in AllEntries(state))
            if (entry.Command is { } command && !commands.Contains(command))
                commands.Add(command);
        return commands;
    }

    /// <summary>
    /// The commands the menus name that the window does not handle: empty when every row that can be
    /// clicked runs something, otherwise the rows a build forgot. The window checks this once at
    /// start-up so a new menu row cannot end up doing nothing.
    /// </summary>
    public static IReadOnlyList<MenuCommand> Unhandled(MenuState state, IEnumerable<MenuCommand> handled)
    {
        var known = new HashSet<MenuCommand>(handled);
        return Commands(state).Where(command => !known.Contains(command)).ToList();
    }

    /// <summary>The gesture an action shows: the shortcut file's key, else the default the app ships.</summary>
    public static ShortcutKey Gesture(MenuState state, string action, ShortcutKey fallback) =>
        state.Shortcuts?.KeyFor(action) is { IsEmpty: false } key ? key : fallback;

    private static MenuEntry[] FileItems(MenuState state) =>
    [
        Item("_New scene", MenuCommand.NewScene, gesture: Gesture(state, "NewFile", Ctrl(0x4E))),
        Item("_Open…", MenuCommand.Open, gesture: Gesture(state, "OpenFile", Ctrl(0x4F))),
        Item("_Close file", MenuCommand.CloseFile,
            gesture: Gesture(state, "CloseFile", Ctrl(0x57)), enabled: state.HasDocument),
        Item("_Save", MenuCommand.Save, gesture: Gesture(state, "SaveFile", Ctrl(0x53))),
        Item("Save _As…", MenuCommand.SaveAs,
            gesture: Gesture(state, "SaveFileAs",
                new ShortcutKey(0x53, ShortcutModifiers.Control | ShortcutModifiers.Shift))),
        MenuEntry.Separator,
        Item("_Import…", MenuCommand.Import),
        Item("_Export…", MenuCommand.Export, gesture: Gesture(state, "ExportMenu", Ctrl(0x45)),
            enabled: state.HasScene),
        MenuEntry.Separator,
        RecentMenu(state),
        MenuEntry.Separator,
        Item("_Preferences…", MenuCommand.Preferences),
    ];

    private static MenuEntry RecentMenu(MenuState state) =>
        state.RecentFiles.Count == 0
            ? Submenu("_Recent scenes", Label(NoRecentScenes))
            : Submenu("_Recent scenes",
                state.RecentFiles.Select(path => Item(RecentHeader(path), MenuCommand.OpenRecent, path)).ToArray());

    /// <summary>
    /// A recent scene's menu row: the path with every <c>_</c> doubled, since a header's single
    /// underscore is an access-key marker and would vanish from <c>22_bombFix_MK8.akt</c>.
    /// </summary>
    public static string RecentHeader(string path) => path.Replace("_", "__");

    private static MenuEntry[] EditItems(MenuState state) =>
    [
        Item("_Undo", MenuCommand.Undo, gesture: Gesture(state, "Undo", Ctrl(0x5A))),
        Item("_Redo", MenuCommand.Redo, gesture: Gesture(state, "Redo", Ctrl(0x59))),
        MenuEntry.Separator,
        Item("_Create key", MenuCommand.CreateKey,
            gesture: Gesture(state, "StackerCreateKey", Key(0x4B)), enabled: state.HasStack),
        Item("_Delete keys", MenuCommand.DeleteKeys,
            gesture: Gesture(state, "DeleteObject", Key(0x2E)), enabled: state.HasKeySelection),
        Item("Delete keys, _closing the gaps", MenuCommand.DeleteKeysClosingGaps,
            gesture: new ShortcutKey(0x2E, ShortcutModifiers.Shift), enabled: state.HasKeySelection),
        MenuEntry.Separator,
        Item(state.HammerRemoves ? "_Remove selection from active stack" : "_Add selection to active stack",
            MenuCommand.Hammer, gesture: Key(0x48),
            enabled: state.HasStack && state.SelectionCount > 0),
        MenuEntry.Separator,
        CheckItem("_Keep children fixed", MenuCommand.KeepChildrenFixed, null, state.KeepChildrenFixed),
        Item("_Mirror pose", MenuCommand.MirrorPose, enabled: !state.HasAnimation && state.SelectionCount > 0),
        MenuEntry.Separator,
        Item("Keyboard _shortcuts…", MenuCommand.KeyboardShortcuts),
        Item("_Panel keys…", MenuCommand.PanelKeys),
    ];

    /// <summary>The Rig menu's rows; the RIG ATELIER panel shows the same commands as buttons.</summary>
    public static MenuEntry[] RigItems(MenuState state)
    {
        bool rigging = state.Rigging;
        bool joints = rigging && !state.JointEditBlocked;
        return
        [
            Item("_AutoRigger…", MenuCommand.AutoRigger, enabled: rigging),
            MenuEntry.Separator,
            Item("_New joint…", MenuCommand.NewJoint, enabled: joints),
            Item("_Insert joint above…", MenuCommand.InsertJoint, enabled: joints && state.SelectionIsJoint),
            MenuEntry.Separator,
            Item("_Delete selected joints", MenuCommand.DeleteJoints, enabled: rigging && state.JointsSelected > 0),
            Item("_Orient selected joints", MenuCommand.OrientJoints, enabled: joints && state.JointsSelected > 0),
            JointAxesMenu(state),
            MenuEntry.Separator,
            Item("_Parent selected joints to the last selected", MenuCommand.ParentJoints,
                enabled: joints && state.JointsSelected >= 2 && state.SelectionIsJoint),
            Item("_Unparent selected joints", MenuCommand.UnparentJoints,
                enabled: joints && state.JointsSelected > 0),
            MenuEntry.Separator,
            Item("Add _IK (two selected joints)", MenuCommand.AddIk, enabled: joints && state.JointsSelected == 2),
            Item("Add _reverse foot (two selected joints)", MenuCommand.AddReverseFoot,
                enabled: joints && state.JointsSelected == 2),
            MenuEntry.Separator,
            Item("Add animated _camera", MenuCommand.AddAnimatedCamera),
            MenuEntry.Separator,
            Item("_Bind skin", MenuCommand.BindSkin, enabled: rigging && !state.PoseRowCurrent),
            Item("Unbind _skin", MenuCommand.UnbindSkin, enabled: rigging),
            MenuEntry.Separator,
            Item("Snap _LTM from the rig pose", MenuCommand.SnapLtm, enabled: rigging),
        ];
    }

    private static MenuEntry JointAxesMenu(MenuState state)
    {
        JointAlignment alignment = state.JointAlignment;
        return Submenu("Joint _axes",
            Submenu("Aim axis", AxisChoices(alignment.Aim, alignment.AimNegative, aim: true).ToArray()),
            Submenu("Up axis", AxisChoices(alignment.Up, alignment.UpNegative, aim: false).ToArray()),
            MenuEntry.Separator,
            CheckItem("Align on create (a chain tip)", MenuCommand.JointAlignOnCreate, null,
                alignment.AlignOnCreate));
    }

    private static IEnumerable<MenuEntry> AxisChoices(JointAxis chosen, bool chosenNegative, bool aim)
    {
        foreach (JointAxis axis in new[] { JointAxis.X, JointAxis.Z, JointAxis.Y })
            foreach (bool negative in new[] { false, true })
            {
                string name = JointAlignment.NameOf(axis);
                yield return CheckItem((negative ? "-" : "+") + name, MenuCommand.JointAxis,
                    JointAxisArgument(aim, axis, negative),
                    chosen == axis && chosenNegative == negative);
            }
    }

    public static MenuEntry[] ViewItems(MenuState state)
    {
        var layout = new List<MenuEntry>();
        foreach (SceneLayout value in Enum.GetValues<SceneLayout>())
            layout.Add(CheckItem(ViewLayouts.Name(value), MenuCommand.SetLayout, LayoutArgument(value),
                state.Layout == value,
                Gesture(state, LayoutAction(value), Key(0x70 + (int)value))));
        layout.Add(MenuEntry.Separator);
        layout.Add(Item("Navigate between layout and single view", MenuCommand.ToggleLayout,
            gesture: Gesture(state, "ToggleLayoutFromMouse", Key(0x4C))));

        var items = new List<MenuEntry> { Submenu("Layout:", layout.ToArray()), MenuEntry.Separator };
        items.Add(CheckItem("Perspective (Orbit)", MenuCommand.SetView, PerspectiveView,
            state.ViewKey == PerspectiveView));
        items.Add(MenuEntry.Separator);
        foreach (string camera in state.Cameras)
            items.Add(CheckItem(camera, MenuCommand.SetView, CameraViewPrefix + camera,
                state.ViewKey == CameraViewPrefix + camera));
        items.Add(MenuEntry.Separator);
        items.Add(Item("Add animated camera", MenuCommand.AddAnimatedCamera));
        if (state.AnimatedCameras.Count > 0)
        {
            items.Add(MenuEntry.Separator);
            foreach (string camera in state.AnimatedCameras)
                items.Add(CheckItem("Animated: " + camera, MenuCommand.SetView, AnimatedViewPrefix + camera,
                    state.ViewKey == AnimatedViewPrefix + camera));
        }
        items.Add(MenuEntry.Separator);
        items.Add(Label($"Focused view: {state.FocusedView}"));
        return items.ToArray();
    }

    private static MenuEntry[] HelpItems() =>
    [
        Item("_About Sloppy Animatsu…", MenuCommand.About),
        Item("Show _Welcome screen…", MenuCommand.ShowWelcome),
        Item("Show _log…", MenuCommand.ShowLog),
        Item("Check for _updates…", MenuCommand.CheckForUpdates),
        MenuEntry.Separator,
        Item("Keyboard _shortcuts…", MenuCommand.KeyboardShortcuts),
        Item("_Panel keys…", MenuCommand.PanelKeys),
    ];

    /// <summary>The shortcuts action a layout row's gesture comes from, by the action names.</summary>
    public static string LayoutAction(SceneLayout layout) => layout switch
    {
        SceneLayout.SingleView => "ToggleLayoutSingleView",
        SceneLayout.TwoViews => "ToggleLayout2Views",
        SceneLayout.TwoViewsCurveboard => "ToggleLayout2ViewsCurveboard",
        _ => "ToggleLayout4Views",
    };

    private static MenuEntry Submenu(string header, params MenuEntry[] children) =>
        new() { Header = header, Kind = MenuEntryKind.Submenu, Children = children };

    private static MenuEntry Item(string header, MenuCommand command, string? argument = null,
        ShortcutKey? gesture = null, bool enabled = true) =>
        new()
        {
            Header = header,
            Command = command,
            Argument = argument,
            Gesture = gesture,
            Enabled = enabled,
        };

    private static MenuEntry CheckItem(string header, MenuCommand command, string? argument, bool isChecked,
        ShortcutKey? gesture = null, bool enabled = true) =>
        new()
        {
            Header = header,
            Command = command,
            Argument = argument,
            Gesture = gesture,
            Checkable = true,
            Checked = isChecked,
            Enabled = enabled,
        };

    /// <summary>An information row: shown, but it runs nothing.</summary>
    private static MenuEntry Label(string header, bool enabled = false) =>
        new() { Header = header, Enabled = enabled };

    private static ShortcutKey Key(int virtualKey) => new(virtualKey, ShortcutModifiers.None);

    private static ShortcutKey Ctrl(int virtualKey) => new(virtualKey, ShortcutModifiers.Control);

    private static IEnumerable<MenuEntry> AllEntries(MenuState state)
    {
        foreach (MenuEntry entry in Flatten(Build(state))) yield return entry;
        foreach (MenuEntry entry in Flatten(KeyItems(state))) yield return entry;
        foreach (MenuEntry entry in Flatten(TimelineEmptyItems(state))) yield return entry;
        foreach (MenuEntry entry in Flatten(TimelineKeyItems(state))) yield return entry;
        foreach (MenuEntry entry in Flatten(AnimationItems(state))) yield return entry;
        foreach (MenuEntry entry in Flatten(StackItems(state))) yield return entry;
        // The tree menu has two forms (a row, or the blank space below the rows); both are menus.
        foreach (MenuEntry entry in Flatten(OutlinerItems(state with { HasTreeRow = true }))) yield return entry;
        foreach (MenuEntry entry in Flatten(OutlinerItems(state with { HasTreeRow = false }))) yield return entry;
    }

    private static IEnumerable<MenuEntry> Flatten(IEnumerable<MenuEntry> entries)
    {
        foreach (MenuEntry entry in entries)
        {
            yield return entry;
            foreach (MenuEntry child in Flatten(entry.Children)) yield return child;
        }
    }
}
