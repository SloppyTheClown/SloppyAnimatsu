namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// <c>ELayout</c>: the four viewport layouts, in file order.
/// The value is the scene's <c>LayoutIndex</c>.
/// </summary>
public enum SceneLayout
{
    SingleView = 0,
    TwoViews = 1,
    TwoViewsCurveboard = 2,
    FourViews = 3,
}

/// <summary>The layout table: view counts, the nine camera slots and the names of the menu entries.</summary>
public static class ViewLayouts
{
    /// <summary>The number of layouts (<c>ELayout</c>).</summary>
    public const int Count = 4;

    /// <summary>The length of <c>ViewCamIndices</c>: one camera index per view of every layout.</summary>
    public const int SlotCount = 9;

    /// <summary>
    /// The nine default camera slots the app writes: layout 0 = [0],
    /// layout 1 = [0, 0], layout 2 = [1, 0], layout 3 = [1, 0, 4, 5].
    /// </summary>
    public static readonly int[] DefaultViewCamIndices = [0, 0, 0, 1, 0, 1, 0, 4, 5];

    public static int ViewCount(SceneLayout layout) => layout switch
    {
        SceneLayout.SingleView => 1,
        SceneLayout.TwoViews or SceneLayout.TwoViewsCurveboard => 2,
        SceneLayout.FourViews => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    /// <summary>The first <c>ViewCamIndices</c> slot of the layout's run: 0, 1, 3, 5.</summary>
    public static int SlotOffset(SceneLayout layout) => layout switch
    {
        SceneLayout.SingleView => 0,
        SceneLayout.TwoViews => 1,
        SceneLayout.TwoViewsCurveboard => 3,
        SceneLayout.FourViews => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    /// <summary>The label the "Layout:" menu shows (<c>Single view</c>, <c>Two views</c>, ...).</summary>
    public static string Name(SceneLayout layout) => layout switch
    {
        SceneLayout.SingleView => "Single view",
        SceneLayout.TwoViews => "Two views",
        SceneLayout.TwoViewsCurveboard => "Two views and curveboard",
        SceneLayout.FourViews => "Four views",
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    public static bool IsDefined(int index) => index is >= 0 and < Count;

    public static SceneLayout FromIndex(int index) =>
        IsDefined(index) ? (SceneLayout)index : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>
    /// Reads a layout from a name or number: <c>single</c>, <c>2views</c>, <c>curveboard</c>,
    /// <c>4views</c>, a full menu name, or 0–3.
    /// </summary>
    public static bool TryParse(string? text, out SceneLayout layout)
    {
        layout = SceneLayout.SingleView;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string key = text.Trim().Replace(" ", "").Replace("_", "").ToLowerInvariant();
        if (int.TryParse(key, out int index)) return IsDefined(index) && Set(out layout, (SceneLayout)index);
        switch (key)
        {
            case "single":
            case "singleview":
            case "1view":
                return Set(out layout, SceneLayout.SingleView);
            case "2":
            case "two":
            case "twoviews":
            case "2views":
                return Set(out layout, SceneLayout.TwoViews);
            case "curveboard":
            case "twoviewsandcurveboard":
            case "twoviewscurveboard":
            case "2viewscurveboard":
                return Set(out layout, SceneLayout.TwoViewsCurveboard);
            case "four":
            case "fourviews":
            case "4views":
                return Set(out layout, SceneLayout.FourViews);
            default:
                return false;
        }
        static bool Set(out SceneLayout target, SceneLayout value)
        {
            target = value;
            return true;
        }
    }
}

/// <summary>
/// A scene's view layout state: <c>LayoutIndex</c>,
/// <c>ToggledLayoutIndex</c>, <c>ToggledLocalViewIndex</c> and the nine <c>ViewCamIndices</c>
/// slots. Every write goes straight into the scene document, so it saves and reloads with the
/// rest of the file.
/// </summary>
public sealed class SceneViews
{
    private readonly Scene _scene;

    public SceneViews(Scene scene) => _scene = scene;

    /// <summary>The stored <c>LayoutIndex</c>, or 0 when the file has none.</summary>
    public int RawLayoutIndex => _scene.Source.GetOr("LayoutIndex", 0);

    /// <summary>The active layout. An out-of-range stored value reads as the single view.</summary>
    public SceneLayout Layout
    {
        get => ViewLayouts.IsDefined(RawLayoutIndex) ? (SceneLayout)RawLayoutIndex : SceneLayout.SingleView;
        set
        {
            Require("LayoutIndex");
            _scene.Source.Set("LayoutIndex", (int)value);
        }
    }

    /// <summary>The layout <see cref="Toggle"/> returns to from the single view (3 in a new scene).</summary>
    public int ToggledLayoutIndex
    {
        get => _scene.Source.GetOr("ToggledLayoutIndex", (int)SceneLayout.FourViews);
        set
        {
            Require("ToggledLayoutIndex");
            _scene.Source.Set("ToggledLayoutIndex", value);
        }
    }

    /// <summary>The view <see cref="Toggle"/> focuses after returning, or -1 for none.</summary>
    public int ToggledLocalViewIndex
    {
        get => _scene.Source.GetOr("ToggledLocalViewIndex", -1);
        set
        {
            Require("ToggledLocalViewIndex");
            _scene.Source.Set("ToggledLocalViewIndex", value);
        }
    }

    /// <summary>The stored camera slots, in file order (nine in a new scene).</summary>
    public IReadOnlyList<int> CameraIndices =>
        CameraList().Select(v => v is int i ? i : Convert.ToInt32(v)).ToArray();

    /// <summary>The number of views the active layout has.</summary>
    public int ViewCount => ViewLayouts.ViewCount(Layout);

    /// <summary>
    /// A layout's camera slot: <c>ViewCamIndices[SlotOffset(layout) + view]</c>, or the default
    /// for that slot when the file stores fewer entries.
    /// </summary>
    public int CameraIndex(SceneLayout layout, int view)
    {
        CheckView(layout, view);
        int slot = ViewLayouts.SlotOffset(layout) + view;
        var list = CameraList();
        return slot < list.Count ? ToInt(list[slot]) : ViewLayouts.DefaultViewCamIndices[slot];
    }

    /// <summary>The camera index of every view of <paramref name="layout"/>.</summary>
    public IReadOnlyList<int> Cameras(SceneLayout layout)
    {
        int count = ViewLayouts.ViewCount(layout);
        var result = new int[count];
        for (int i = 0; i < count; i++) result[i] = CameraIndex(layout, i);
        return result;
    }

    /// <summary>
    /// Writes a view's camera: the slot is created, when the
    /// file stores fewer, with the default values of the slots before it.
    /// </summary>
    public void SetCamera(SceneLayout layout, int view, int cameraIndex)
    {
        CheckView(layout, view);
        ArgumentOutOfRangeException.ThrowIfNegative(cameraIndex);
        Require("ViewCamIndices");
        var list = CameraList();
        int slot = ViewLayouts.SlotOffset(layout) + view;
        while (list.Count <= slot)
            list.Add(ViewLayouts.DefaultViewCamIndices[list.Count]);
        list[slot] = cameraIndex;
    }

    /// <summary>
    /// A "Layout:" menu command: switching to the
    /// single view carries the focused view's camera over; the other layouts keep their slots.
    /// </summary>
    public void Change(SceneLayout layout, int focusedView)
    {
        if (layout == SceneLayout.SingleView && ViewLayouts.IsDefined(RawLayoutIndex))
        {
            var current = Layout;
            int view = Math.Clamp(focusedView, 0, ViewLayouts.ViewCount(current) - 1);
            SetCamera(SceneLayout.SingleView, 0, CameraIndex(current, view));
        }
        Layout = layout;
    }

    /// <summary>
    /// The <b>L</b> shortcut: from the single view it
    /// returns to <see cref="ToggledLayoutIndex"/> (handing the single camera to the remembered
    /// view when <see cref="ToggledLocalViewIndex"/> names one); from a multi-view layout it
    /// remembers the layout and focused view and shows that camera in the single view. Returns
    /// the view to focus afterwards.
    /// </summary>
    public int Toggle(int focusedView)
    {
        if (Layout == SceneLayout.SingleView)
        {
            var layout = ViewLayouts.IsDefined(ToggledLayoutIndex)
                ? (SceneLayout)ToggledLayoutIndex
                : SceneLayout.FourViews;
            int view = ToggledLocalViewIndex;
            if (view >= 0 && view < ViewLayouts.ViewCount(layout))
                SetCamera(layout, view, CameraIndex(SceneLayout.SingleView, 0));
            Layout = layout;
            return view >= 0 && view < ViewLayouts.ViewCount(layout) ? view : 0;
        }

        int currentView = Math.Clamp(focusedView, 0, ViewLayouts.ViewCount(Layout) - 1);
        int camera = CameraIndex(Layout, currentView);
        ToggledLayoutIndex = (int)Layout;
        ToggledLocalViewIndex = currentView;
        SetCamera(SceneLayout.SingleView, 0, camera);
        Layout = SceneLayout.SingleView;
        return 0;
    }

    /// <summary>The scene camera at <paramref name="index"/> (into <c>CameraList</c>), if there is one.</summary>
    public Camera? CameraAt(int index) =>
        index >= 0 && index < _scene.Cameras.Count ? _scene.Cameras[index] : null;

    /// <summary>The name of the camera at <paramref name="index"/>, for the CLI and the menu.</summary>
    public string? CameraName(int index) => CameraAt(index)?.Name;

    private List<object?> CameraList() => _scene.Source["ViewCamIndices"] as List<object?> ?? [];

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        byte b => b,
        short s => s,
        ushort us => us,
        uint u => checked((int)u),
        _ => Convert.ToInt32(value),
    };

    private static void CheckView(SceneLayout layout, int view)
    {
        int count = ViewLayouts.ViewCount(layout);
        if (view < 0 || view >= count)
            throw new ArgumentOutOfRangeException(nameof(view), $"{layout} has {count} view(s).");
    }

    private void Require(string member)
    {
        if (!_scene.Source.Has(member))
            throw new InvalidOperationException($"This scene has no CScene.{member}; it cannot store view layouts.");
    }
}
