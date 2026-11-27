// The viewer's main window. The MainWindow type is split into partial files by concern.
//
// This core file holds the shared state: the nested state records, every control field, and the
// computed properties (selection, active layout, frame rate, focused view, pose session) that the
// other partial files use. Start here when adding a field or a piece of window state.
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

public sealed partial class MainWindow : Window, IViewportEditor
{
    private sealed record PendingPlacement(bool Insert, Joint? Target, string Name);

    private sealed record JointItem(int Index, string Label)
    {
        public override string ToString()
        {
            return Label;
        }
    }

    private sealed class ViewState
    {
        public int CameraIndex;

        public bool StoredPerspective;

        public CameraAnimated? Animated;

        /// <summary>
        /// The view's own shading and decorator switches while "Decorator display affects all
        /// views" is off; null while the views share the View &amp; Preferences switches.
        /// </summary>
        public ViewDisplay? Display;

        /// <summary>
        /// The view's own orbit camera, used whenever it shows no scene camera. The first view
        /// shares the renderer's camera (saved with the document); the others start as a copy.
        /// </summary>
        public OrbitCamera? Orbit;
    }

    private sealed class DocumentState(SceneDocument document)
    {
        public SceneDocument Document { get; } = document;

        public EditHistory History { get; set; } = new EditHistory();

        public Dictionary<Character, PoseEditSession> Sessions { get; set; } = new Dictionary<Character, PoseEditSession>(ReferenceEqualityComparer.Instance);

        public List<Object3D> Selected { get; set; } = new List<Object3D>();

        public string? Character { get; set; }

        public string? Animation { get; set; }

        public float Time { get; set; }

        public InterfaceContext Context { get; set; }

        public bool Dirty { get; set; }

        public Vector3 CameraTarget { get; set; }

        public float CameraDistance { get; set; }

        public float CameraYaw { get; set; }

        public float CameraPitch { get; set; }

        public float CameraFieldOfView { get; set; }
    }

    private sealed record PanelView(FloatingPanelState State, Border Frame, Control Body, TextBlock Arrow, Button Fold, Border Grip);

    private sealed record Item<T>(T? Value, string Label, int Depth = 0, bool? Expanded = null) where T : class
    {
        public override string ToString()
        {
            return Label;
        }
    }

    private const string RestPoseLabel = "(rest pose)";

    private readonly ViewerOptions _options;

    private readonly Viewport _viewport = new Viewport();

    private readonly TextBlock _fileLabel = new TextBlock
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.8
    };

    private readonly TextBlock _status = new TextBlock
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.7,
        FontSize = 12.0
    };

    private readonly TextBlock _contextLabel = new TextBlock
    {
        FontSize = 11.0,
        Opacity = 0.8,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly Button _contextButton = new Button
    {
        FontSize = 11.0,
        Padding = new Thickness(8.0, 2.0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly ComboBox _characters = new ComboBox
    {
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private readonly ListBox _animations = new ListBox();

    private readonly ListBox _controllers = new ListBox();

    private readonly OutlinerView _outliner = new OutlinerView();

    private Menu? _menuBar;

    private readonly ContextMenu _keyMenu = new ContextMenu();

    private readonly ContextMenu _animationMenu = new ContextMenu();

    private readonly ContextMenu _outlinerMenu = new ContextMenu();

    private Dictionary<MenuCommand, Action<MenuEntry>>? _menuHandlers;

    private readonly TextBlock _selection = new TextBlock
    {
        FontFamily = new FontFamily("Consolas,Menlo,monospace"),
        FontSize = 11.0,
        TextWrapping = TextWrapping.Wrap
    };

    private readonly SpinnerField[] _translateFields = new SpinnerField[3]
    {
        new SpinnerField(),
        new SpinnerField(),
        new SpinnerField()
    };

    private readonly SpinnerField[] _rotateFields = new SpinnerField[3]
    {
        new SpinnerField(),
        new SpinnerField(),
        new SpinnerField()
    };

    private readonly SpinnerField[] _scaleFields = new SpinnerField[3]
    {
        new SpinnerField(),
        new SpinnerField(),
        new SpinnerField()
    };

    private readonly CheckBox _snapMove = new CheckBox
    {
        Content = "Move"
    };

    private readonly CheckBox _snapRotate = new CheckBox
    {
        Content = "Rotate"
    };

    private readonly CheckBox _snapScale = new CheckBox
    {
        Content = "Scale"
    };

    private readonly SpinnerField _moveStep = new SpinnerField
    {
        Step = 0.1f
    };

    private readonly SpinnerField _rotateStep = new SpinnerField
    {
        Step = 5f
    };

    private readonly SpinnerField _scaleStep = new SpinnerField
    {
        Step = 0.1f
    };

    private readonly ToggleButton _rotateMode = new ToggleButton
    {
        Content = "Rotate",
        IsChecked = true,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private readonly ToggleButton _translateMode = new ToggleButton
    {
        Content = "Move",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private readonly ToggleButton _scaleMode = new ToggleButton
    {
        Content = "Scale",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private readonly CheckBox _showMesh = new CheckBox
    {
        Content = "Mesh",
        IsChecked = true
    };

    private readonly CheckBox _showSkeleton = new CheckBox
    {
        Content = "Skeleton",
        IsChecked = true
    };

    private readonly CheckBox _showControllers = new CheckBox
    {
        Content = "Controllers",
        IsChecked = true
    };

    private readonly CheckBox _showGrid = new CheckBox
    {
        Content = "Grid",
        IsChecked = true
    };

    private readonly CheckBox _showOthers = new CheckBox
    {
        Content = "Other characters",
        IsChecked = true
    };

    private readonly ToggleButton _play = new ToggleButton
    {
        // The icon is set in the constructor (PanelIcons.Play); 40 px matches the stepping buttons' width.
        Width = 40.0
    };

    /// <summary>
    /// The play mode: loop the play range, loop the frames the range bar shows, or run past the last
    /// frame until stopped. One button cycles through them.
    /// </summary>
    private enum PlayMode { Loop, LoopRange, Endless }

    private PlayMode _playMode;

    /// <summary>Cycles <see cref="_playMode"/>; its label names the current mode.</summary>
    private readonly Button _playModeButton = new Button
    {
        Content = "↻ Loop",
        Width = 84.0
    };

    private readonly TimeRangeBar _rangeBar = new TimeRangeBar();

    private readonly Button _fitAll = new Button { Content = "Fit all" };

    private readonly Button _fitLayer = new Button { Content = "Fit layer" };

    /// <summary>The range bar state of each animation opened this run (runtime only, never saved).</summary>
    private readonly Dictionary<Animation, TimeBar> _timeBars = new(ReferenceEqualityComparer.Instance);

    private readonly KeyTimeline _timeline = new KeyTimeline();

    private readonly CurveboardView _curveboard = new CurveboardView();

    private readonly ToggleButton _showCurves = new ToggleButton
    {
        Content = "Curves"
    };

    private readonly TextBlock _curveInfo = new TextBlock
    {
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.7,
        FontSize = 12.0
    };

    private readonly List<Button> _curveButtons = new List<Button>();

    private Control? _curvePanel;

    private CurveDrag? _curveDrag;

    private bool _keepChildrenFixed;

    private JointAlignment _jointAlignment = JointAlignment.Default;

    private bool _rigEditFailed;

    private PendingPlacement? _placement;

    private AutoRiggerSession? _autoRigger;

    private AutoRiggerDialog? _autoRiggerDialog;

    private HotPoint? _hotPoint;

    private RetargetDialog? _retargetDialog;

    /// <summary>The skeleton pair last picked in the Retarget window, by name (kept for this run).</summary>
    private static string? _lastRetargetSource, _lastRetargetTarget;

    private BlendshapeSession? _blendshapes;

    private readonly List<(BlendshapeController Controller, NumericUpDown X, NumericUpDown Y, TextBlock Info)> _blendshapeFields = new List<(BlendshapeController, NumericUpDown, NumericUpDown, TextBlock)>();

    private StackPanel? _blendshapeControllers;

    private UniformGrid? _blendshapeFaces;

    private TextBlock? _blendshapeStatus;

    private readonly ComboBox _skinModes = new ComboBox
    {
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private readonly SpinnerField _skinRadius = new SpinnerField
    {
        Step = 5f
    };

    private readonly SpinnerField _skinStrength = new SpinnerField
    {
        Step = 0.1f
    };

    private readonly SpinnerField _skinValue = new SpinnerField
    {
        Step = 0.1f
    };

    private readonly CheckBox _skinHard = new CheckBox
    {
        Content = "Hard"
    };

    private readonly CheckBox _skinRigid = new CheckBox
    {
        Content = "Rigid"
    };

    private readonly CheckBox _skinPaint = new CheckBox
    {
        Content = "Paint"
    };

    private readonly CheckBox _skinScanner = new CheckBox
    {
        Content = "Scan"
    };

    private readonly CheckBox _skinFrontBack = new CheckBox
    {
        Content = "Front/back",
        IsChecked = true
    };

    private readonly ScrollViewer _skinWeights = new ScrollViewer
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    private readonly StackPanel _skinRows = new StackPanel
    {
        Spacing = 1.0
    };

    private readonly Dictionary<int, SpinnerField> _skinWeightFields = new Dictionary<int, SpinnerField>();

    private readonly Dictionary<int, TextBlock> _skinMinWeights = new Dictionary<int, TextBlock>();

    private readonly Dictionary<int, TextBlock> _skinMaxWeights = new Dictionary<int, TextBlock>();

    private readonly Dictionary<int, CheckBox> _skinLockBoxes = new Dictionary<int, CheckBox>();

    private readonly TextBlock _skinInfo = new TextBlock
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.7,
        FontSize = 11.0
    };

    private SkinPaintSession? _skin;

    private SkinWeightClipboard? _skinClipboard;

    private EditSnapshot? _skinUndo;

    private Vector3[] _skinPositions = Array.Empty<Vector3>();

    private Vector3[] _skinNormals = Array.Empty<Vector3>();

    private IReadOnlyList<Vector3> _skinJointPositions = Array.Empty<Vector3>();

    private Vector3 _skinPlanePoint;

    private Vector3 _skinPlaneNormal = Vector3.UnitY;

    private float _skinRadiusPixels = 30f;

    private bool _rebuildingSkin;

    private Border? _scanBadge;

    private TextBlock? _scanText;

    private readonly SpinnerField _fovField = new SpinnerField
    {
        Step = 1f,
        Requires = "a selected animated camera with a FOV track, in an animation whose active stack is not additive"
    };

    private SceneViews? _sceneViews;

    private readonly List<ViewState> _viewStates = new List<ViewState>();

    private int _focusedView;

    private SceneLayout _layout = SceneLayout.SingleView;

    private Folder? _bankFolder;

    /// <summary>Whether the Anim Bank (not the Scene tree) was the naming panel used last; the Rename Tool acts on its selection.</summary>
    private bool _renameFromBank;
    private SceneObject? _bankDragItem;

    private Point _bankDragStart;

    private bool _bankDragging;

    // The Mixer's blend mode of the current stack: index 0 Override (flag 8 clear, the default),
    // 1 Additive (flag 8 set). On/off is the rows' eye, Cycle lives in the Stacker and the weight
    // is each row's bar, so the Mixer has no other per-stack fields.
    private readonly ComboBox _stackBlend = new ComboBox
    {
        ItemsSource = new[] { "Override", "Additive" },
        SelectedIndex = 0,
        MinWidth = 90.0,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly NumericUpDown _frame = new NumericUpDown
    {
        Width = 130.0,
        Increment = 1m,
        FormatString = "0.##"
    };

    private readonly TextBlock _range = new TextBlock
    {
        VerticalAlignment = VerticalAlignment.Center,
        Width = 110.0
    };

    private readonly TextBlock _keyInfo = new TextBlock
    {
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.7,
        FontSize = 12.0
    };

    private readonly NumericUpDown _gap = new NumericUpDown
    {
        Width = 110.0,
        Increment = 1m,
        Minimum = 1m,
        FormatString = "0",
        ParsingNumberStyle = NumberStyles.Integer
    };

    private KeyClipboard? _clipboard;

    private readonly DispatcherTimer _timer = new DispatcherTimer
    {
        Interval = TimeSpan.FromMilliseconds(15L, 0L)
    };

    private readonly Stopwatch _clock = new Stopwatch();

    private readonly AudioPlayback _audio = new AudioPlayback();

    private bool _advancing;

    private Dictionary<Character, PoseEditSession> _sessions = new Dictionary<Character, PoseEditSession>(ReferenceEqualityComparer.Instance);

    private ScenePose? _pose;

    private Character? _character;

    private Animation? _animation;

    private float _time;

    private bool _syncing;

    private string? _path;

    private bool _dirty;

    private Preferences? _preferences;

    private Shortcuts? _shortcuts;

    /// <summary>SloppyAnimatsu's own panel keys (panelShortcuts.slopa), and both maps as the panels see them.</summary>
    private Shortcuts? _panelShortcuts;

    private HotkeyMap _hotkeys = HotkeyMap.Default;

    private Grid? _viewArea;

    private Border? _curveHost;

    private Canvas? _floatHost;

    private readonly Dictionary<string, FloatingLayout> _floating = new Dictionary<string, FloatingLayout>(StringComparer.Ordinal);

    private string? _panelsContext;

    private InterfaceContext _lastContext = InterfaceContext.Animation;

    private string _documentName = "";

    private readonly DocumentSet _documents = new DocumentSet();

    private readonly Dictionary<SceneDocument, DocumentState> _documentStates = new Dictionary<SceneDocument, DocumentState>();

    private DocumentState? _document;

    private StackPanel? _tabBar;

    private DispatcherTimer? _autosaveTimer;

    private List<Object3D> _selectedAll = new List<Object3D>();

    private EditHistory _history = new EditHistory();

    private TransformDrag? _drag;

    private int _dragAxis;

    private TransformGizmo? _dragGizmo;

    private Vector2 _dragLast;

    private FadeDrag? _fadeDrag;

    private float _dragAngle;

    private static readonly IBrush PanelBackground = new SolidColorBrush(Color.Parse("#141416"));

    private static readonly IBrush PanelHeaderBackground = new SolidColorBrush(Color.Parse("#2C2C31"));

    private static readonly IBrush PanelBorderBrush = new SolidColorBrush(Color.FromArgb(175, 190, 190, 198));

    private static readonly BoxShadows PanelShadow = BoxShadows.Parse("0 3 14 0 #78000000");

    private readonly List<PanelView> _panelViews = new List<PanelView>();

    private FloatingPanelState? _panelDrag;

    private Point _panelDragStart;

    private double _panelDragX;

    private double _panelDragY;

    private bool _panelDragMoved;

    private FloatingPanelState? _panelResizing;

    private Point _panelResizeStart;

    private double _panelResizeWidth;

    private double _panelResizeHeight;

    private (Matrix4x4 World, float Distance)? _pointOfViewBefore;

    /// <summary>The last object added to the selection, or null when nothing is selected.</summary>
    private Object3D? _selected
    {
        get
        {
            if (_selectedAll.Count == 0)
            {
                return null;
            }
            return _selectedAll[_selectedAll.Count - 1];
        }
    }

    /// <summary>The saved floating layout of the current context, or null before one is applied.</summary>
    private FloatingLayout? ActiveLayout => _floating.GetValueOrDefault(ContextLabel);

    private string ContextLabel => (_pose?.Model.Scene.Context ?? _lastContext).ToString();

    /// <summary>The scene's frame rate, falling back to 30 when the scene does not record one.</summary>
    private uint FrameRate
    {
        get
        {
            uint rate = _pose?.Model.Scene.FrameRate ?? 0u;
            return (rate != 0) ? rate : 30u;
        }
    }

    private ViewState FocusedState
    {
        get
        {
            if (_viewStates.Count == 0)
            {
                _viewStates.Add(new ViewState());
            }
            _focusedView = Math.Clamp(_focusedView, 0, _viewStates.Count - 1);
            return _viewStates[_focusedView];
        }
    }

    private string ViewKey
    {
        get
        {
            ViewState focusedState = FocusedState;
            if (focusedState.Animated != null)
            {
                return "animated:" + focusedState.Animated.Name;
            }
            if (focusedState.StoredPerspective)
            {
                return "camera:Perspective";
            }
            if (focusedState.CameraIndex > 0 && _pose != null && focusedState.CameraIndex < _pose.Model.Scene.Cameras.Count)
            {
                Camera camera = _pose.Model.Scene.Cameras[focusedState.CameraIndex];
                if (camera is OrthoCamera) return "perspective";
                return (camera is CameraAnimated) ? ("animated:" + camera.Name) : ("camera:" + camera.Name);
            }
            return "perspective";
        }
    }

    private PoseEditSession? Session
    {
        get
        {
            if (_pose == null || _character == null)
            {
                return null;
            }
            if (!_sessions.TryGetValue(_character, out PoseEditSession value))
            {
                Dictionary<Character, PoseEditSession> sessions = _sessions;
                Character? character = _character;
                PoseEditSession obj = new PoseEditSession(_pose, _character, _history)
                {
                    KeepChildrenFixed = _keepChildrenFixed
                };
                value = obj;
                sessions[character] = obj;
            }
            return value;
        }
    }

    private SnapSettings Snap => new SnapSettings
    {
        MoveDiscrete = (_snapMove.IsChecked == true),
        MoveStep = _moveStep.Value,
        RotateDiscrete = (_snapRotate.IsChecked == true),
        RotateStep = _rotateStep.Value,
        ScaleDiscrete = (_snapScale.IsChecked == true),
        ScaleStep = _scaleStep.Value,
        AngleThreshold = (_preferences?.SnapAngleThreshold ?? 0f)
    };
}
