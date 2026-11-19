using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Diagnostics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The camera view window: a small floating panel, opened by the camera button beside an animated
/// camera in the TREE, that shows the scene through that camera. It folds like the other panels,
/// its header drags it, its corner grip resizes it (keeping the camera's aspect), and its close box
/// (or the TREE button again) closes it. It first opens at the free spot nearest the view's top left
/// corner; after that it comes back where it was left (<see cref="WindowPlacementStore"/>).
/// SloppyAnimatsu's own addition.
/// </summary>
public partial class MainWindow
{
    private const double CameraViewWidth = 320;
    private const double CameraViewMinWidth = 160;
    private const string CameraViewKey = "camera-view";

    private CameraAnimated? _cameraViewCamera;
    private Border? _cameraViewFrame;
    private Viewport? _cameraViewport;
    private Point? _cameraViewDragStart;
    private (Point Pointer, double Width)? _cameraViewResizeStart;
    private double _cameraViewAspect = 16.0 / 9.0;
    private WindowPlacementStore? _windowPlacements;

    private void WireCameraView()
    {
        _outliner.CameraViewOpen = camera => ReferenceEquals(camera, _cameraViewCamera);
        _outliner.CameraViewRequested += camera =>
        {
            if (ReferenceEquals(camera, _cameraViewCamera)) CloseCameraView();
            else OpenCameraView(camera);
        };
    }

    private void OpenCameraView(CameraAnimated camera)
    {
        CloseCameraView();
        if (_floatHost == null || _pose == null) return;
        _cameraViewCamera = camera;
        float aspect = camera.AspectRatio is var a && a > 0.05f && a < 20f ? a : 16f / 9f;
        var viewport = new Viewport
        {
            Width = CameraViewWidth,
            Height = Math.Round(CameraViewWidth / aspect),
            IsHitTestVisible = false,
        };
        viewport.Renderer.Pose = _pose;
        _cameraViewport = viewport;

        var fold = new TextBlock { Text = "▼", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var foldButton = new Button
        {
            Content = fold, Width = 18, Height = 18, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0),
            MinWidth = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, Focusable = false,
        };
        ToolTip.SetTip(foldButton, "Collapse");
        foldButton.Click += delegate
        {
            viewport.IsVisible = !viewport.IsVisible;
            fold.Text = viewport.IsVisible ? "▼" : "▶";
            ToolTip.SetTip(foldButton, viewport.IsVisible ? "Collapse" : "Expand");
            if (viewport.IsVisible) RefreshCameraView();
        };
        var title = new TextBlock
        {
            Text = (camera.Name ?? "Camera").ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.DemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var close = HeaderButton("×", "Close this camera view");
        close.Click += delegate { CloseCameraView(); };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 20 };
        header.Children.Add(foldButton);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        var headerBorder = new Border { Background = PanelHeaderBackground, Child = header };

        var body = new StackPanel();
        body.Children.Add(headerBorder);
        body.Children.Add(viewport);
        var grip = new Border
        {
            Width = 12, Height = 12, CornerRadius = new CornerRadius(3, 0, 3, 0),
            Background = new SolidColorBrush(Color.FromArgb(150, 230, 230, 235)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = new Cursor(StandardCursorType.BottomRightCorner),
        };
        ToolTip.SetTip(grip, "Drag to resize");
        var content = new Grid();
        content.Children.Add(body);
        content.Children.Add(grip);
        var frame = new Border
        {
            Background = PanelBackground, BorderBrush = PanelBorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), ClipToBounds = true, BoxShadow = PanelShadow,
            Width = CameraViewWidth + 2, Child = content, ZIndex = int.MaxValue - 2,
        };
        _cameraViewFrame = frame;
        _cameraViewAspect = aspect;
        foldButton.Click += delegate
        {
            grip.IsVisible = viewport.IsVisible;
            SaveCameraViewPlacement();
        };
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed || _floatHost == null) return;
            _cameraViewResizeStart = (e.GetPosition(_floatHost), viewport.Width);
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (_cameraViewResizeStart is not { } start || _floatHost == null) return;
            var d = e.GetPosition(_floatHost) - start.Pointer;
            // The view keeps the camera's aspect, so the larger of the two drags decides the width.
            double width = Math.Max(start.Width + d.X, (start.Width / aspect + d.Y) * aspect);
            SetCameraViewWidth(width);
        };
        grip.PointerReleased += (_, e) =>
        {
            if (_cameraViewResizeStart == null) return;
            _cameraViewResizeStart = null;
            e.Pointer.Capture(null);
            SaveCameraViewPlacement();
            RefreshCameraView();
        };
        headerBorder.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(headerBorder).Properties.IsLeftButtonPressed) return;
            _cameraViewDragStart = e.GetPosition(frame);
            e.Pointer.Capture(headerBorder);
            e.Handled = true;
        };
        headerBorder.PointerMoved += (_, e) =>
        {
            if (_cameraViewDragStart is not { } grab || _floatHost == null) return;
            var p = e.GetPosition(_floatHost) - grab;
            double maxX = Math.Max(0, _floatHost.Bounds.Width - frame.Bounds.Width);
            double maxY = Math.Max(0, _floatHost.Bounds.Height - 20);
            Canvas.SetLeft(frame, Math.Clamp(p.X, 0, maxX));
            Canvas.SetTop(frame, Math.Clamp(p.Y, 0, maxY));
        };
        headerBorder.PointerReleased += (_, e) =>
        {
            if (_cameraViewDragStart == null) return;
            _cameraViewDragStart = null;
            e.Pointer.Capture(null);
            SaveCameraViewPlacement();
        };
        // Where it was last left, or else the free spot nearest the view's top left corner.
        var saved = WindowPlacements.Get(CameraViewKey);
        if (saved != null)
        {
            SetCameraViewWidth(saved.Width);
            if (saved.Collapsed)
            {
                viewport.IsVisible = false;
                fold.Text = "▶";
                ToolTip.SetTip(foldButton, "Expand");
                grip.IsVisible = false;
            }
        }
        double frameHeight = (viewport.IsVisible ? viewport.Height : 0) + 22;
        var spot = saved != null
            ? new Point(saved.X, saved.Y)
            : FreeSpotNearTopLeft(_floatHost, frame, viewport.Width + 2, frameHeight);
        double hostWidth = _floatHost.Bounds.Width, hostHeight = _floatHost.Bounds.Height;
        Canvas.SetLeft(frame, Math.Clamp(spot.X, 0, Math.Max(0, hostWidth - viewport.Width - 2)));
        Canvas.SetTop(frame, Math.Clamp(spot.Y, 0, Math.Max(0, hostHeight - 20)));
        SetCameraViewWidth(viewport.Width);
        _floatHost.Children.Add(frame);
        RefreshCameraView();
        _outliner.RefreshCameraButtons();
        _status.Text = $"{camera.Name}: camera view opened (the camera button in the TREE closes it).";
    }

    private void CloseCameraView()
    {
        if (_cameraViewFrame == null && _cameraViewCamera == null) return;
        if (_cameraViewFrame != null) _floatHost?.Children.Remove(_cameraViewFrame);
        _cameraViewFrame = null;
        _cameraViewport = null;
        _cameraViewCamera = null;
        _cameraViewDragStart = null;
        _cameraViewResizeStart = null;
        _outliner.RefreshCameraButtons();
    }

    private WindowPlacementStore WindowPlacements => _windowPlacements ??= new WindowPlacementStore(WindowPlacementStore.DefaultPath);

    /// <summary>Sets the view's width (its height follows the camera's aspect), kept inside the view area.</summary>
    private void SetCameraViewWidth(double width)
    {
        if (_cameraViewport is not { } viewport || _cameraViewFrame is not { } frame) return;
        double max = CameraViewMinWidth;
        if (_floatHost is { } host && host.Bounds.Width > 0)
        {
            double left = Canvas.GetLeft(frame) is var l && double.IsFinite(l) ? l : 0;
            max = Math.Max(CameraViewMinWidth, Math.Min(host.Bounds.Width - left - 2, (host.Bounds.Height - 24) * _cameraViewAspect));
        }
        width = Math.Round(Math.Clamp(double.IsFinite(width) ? width : CameraViewWidth, CameraViewMinWidth, max));
        viewport.Width = width;
        viewport.Height = Math.Round(width / _cameraViewAspect);
        frame.Width = width + 2;
    }

    private void SaveCameraViewPlacement()
    {
        if (_cameraViewport is not { } viewport || _cameraViewFrame is not { } frame) return;
        double x = Canvas.GetLeft(frame), y = Canvas.GetTop(frame);
        WindowPlacements.Set(CameraViewKey, new WindowPlacement(
            double.IsFinite(x) ? x : 0, double.IsFinite(y) ? y : 0, viewport.Width, !viewport.IsVisible));
        if (WindowPlacements.LastError is { } error)
            AppLog.Warn($"Could not save the camera view's placement: {error}");
    }

    /// <summary>
    /// The position closest to the view area's top left corner where a <paramref name="width"/> x
    /// <paramref name="height"/> window covers none of the other floating panels and overlays. The
    /// candidates are the corner itself and the spots just right of or below each of them; when none
    /// fits, the corner.
    /// </summary>
    internal static Point FreeSpotNearTopLeft(Canvas host, Control? self, double width, double height)
    {
        const double margin = 8;
        var obstacles = host.Children
            .Where(c => c != self && c.IsVisible && c.Bounds.Width > 1 && c.Bounds.Height > 1)
            .Select(c => c.Bounds)
            .ToList();
        var (x, y) = WindowPlacementStore.FreeSpotNearTopLeft(host.Bounds.Width, host.Bounds.Height,
            obstacles.Select(r => (r.X, r.Y, r.Width, r.Height)).ToList(), width, height, margin);
        return new Point(x, y);
    }

    /// <summary>
    /// Follows the main view: the pose, the display toggles and the camera's current frame. Closes
    /// the window when its scene is no longer the shown one or the camera can no longer be posed.
    /// </summary>
    private void RefreshCameraView()
    {
        if (_cameraViewport is not { } viewport || _cameraViewCamera is not { } camera) return;
        if (_pose == null || !ReferenceEquals(viewport.Renderer.Pose, _pose))
        {
            CloseCameraView();
            return;
        }
        if (!viewport.IsVisible) return;
        var renderer = viewport.Renderer;
        var main = _viewport.Renderer;
        renderer.ShowMesh = main.ShowMesh;
        renderer.ShowSkeleton = main.ShowSkeleton;
        renderer.ShowControllers = main.ShowControllers;
        renderer.ShowGrid = main.ShowGrid;
        renderer.HideHandles = true;
        ApplyViewPrefs(renderer);
        renderer.SceneView = AnimatedView(camera);
        if (renderer.SceneView == null)
        {
            CloseCameraView();
            return;
        }
        viewport.RequestNextFrameRendering();
    }
}
