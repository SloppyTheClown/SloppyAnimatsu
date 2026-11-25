// Time and views: the frame range, setting/advancing the playhead and applying the pose, the
// per-viewport camera state (the eight fixed views, perspective, animated cameras), the four layouts
// and the FOV field.
//
// View state is per viewport index (ViewState); CameraFor turns a state into the camera object the
// renderer uses.
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
    /// <summary>The range bar state of the current animation, fitted to all keys the first time it is shown.</summary>
    private TimeBar? CurrentTimeBar
    {
        get
        {
            if (_animation == null) return null;
            if (!_timeBars.TryGetValue(_animation, out var bar))
            {
                bar = new TimeBar();
                bar.FitAll(_animation, (int)MathF.Round(_time));
                _timeBars[_animation] = bar;
            }
            return bar;
        }
    }

    /// <summary>Shows the key range in the toolbar and the range bar's window in the timeline.</summary>
    private void UpdateRange()
    {
        var (start, end) = _animation?.KeyRange ?? (0, 0);
        var bar = CurrentTimeBar;
        _syncing = true;
        _frame.Minimum = Math.Min(start, bar?.Min ?? start);
        _frame.Maximum = Math.Max(Math.Max(end, start), Math.Max(bar?.Max ?? end, (int)MathF.Ceiling(_time)));
        _range.Text = $"{start} – {end}";
        _syncing = false;
        _rangeBar.Bar = bar;
        _rangeBar.KeyTimes = _animation?.Stacks
            .SelectMany(s => s.Keys.Take(s is AudioStack ? s.Keys.Count : s.ConsideredKeyCount).Select(k => k.Time))
            .Distinct().ToList() ?? [];
        _timeline.ViewRange = bar == null ? null : (bar.ViewStart, bar.ViewEnd);
        _curveboard.ViewRange = _timeline.ViewRange;
    }

    /// <summary>Fit all: the bar and its window span every stack's keys and the playhead.</summary>
    private void FitAllRange()
    {
        CurrentTimeBar?.FitAll(_animation, (int)MathF.Round(_time));
        UpdateRange();
    }

    /// <summary>Fit layer: the window spans the active stack's selected keys, or all its considered keys.</summary>
    private void FitLayerRange()
    {
        var bar = CurrentTimeBar;
        var stack = _timeline.ActiveStack;
        if (bar == null || stack == null) return;
        var keys = stack.Keys.Take(stack is AudioStack ? stack.Keys.Count : stack.ConsideredKeyCount).ToList();
        var selected = _timeline.SelectedKeys.Where(k => k < keys.Count).Select(k => keys[k].Time).ToList();
        if (!bar.FitTimes(selected.Count > 1 ? selected : keys.Select(k => k.Time)))
            _status.Text = "Fit layer: the keys span no frames.";
        UpdateRange();
    }

    /// <summary>The frames playback runs through: the range bar's window in Loop range mode, else the animation's play range.</summary>
    private (int Start, int End) PlayBounds()
    {
        if (_playMode == PlayMode.LoopRange && CurrentTimeBar is { } bar) return (bar.ViewStart, bar.ViewEnd);
        return _animation?.PlayRange ?? (0, 0);
    }

    private bool PlaysEndlessly => _playMode == PlayMode.Endless;

    private void UpdatePlayModeButton()
    {
        _playModeButton.Content = _playMode switch
        {
            PlayMode.LoopRange => "↻ Range",
            PlayMode.Endless => "→ Endless",
            _ => "↻ Loop",
        };
        ToolTip.SetTip(_playModeButton, _playMode switch
        {
            PlayMode.LoopRange => "Loops the frames the range bar shows (click: endless)",
            PlayMode.Endless => "Plays past the last frame until stopped (click: loop)",
            _ => "Loops the play range (click: loop the range bar's frames)",
        });
    }

    private float? _pendingScrub;

    // A scrub drag (timeline or curveboard) sends one time per pointer move; a high-rate mouse sends
    // them faster than a SetTime takes (~5 ms), so handling each one queued input for seconds and froze
    // the viewport. Only the latest pending time is applied, once per dispatcher pass, and a time the
    // playhead already shows is skipped.
    private void ScrubTo(float time)
    {
        _play.IsChecked = false;
        bool posted = _pendingScrub != null;
        _pendingScrub = time;
        if (posted) return;
        Dispatcher.UIThread.Post(() =>
        {
            float? t = _pendingScrub;
            _pendingScrub = null;
            if (t is float f && f != _time) SetTime(f);
        }, DispatcherPriority.Input);
    }

    private void SetTime(float time)
    {
        var (start, end) = _animation?.KeyRange ?? (0, 0);
        var bar = CurrentTimeBar;
        float min = Math.Min(start, bar?.Min ?? start);
        float max = Math.Max(Math.Max(Math.Max(start, end), bar?.Max ?? end), (float)_frame.Maximum);
        bool endless = _play.IsChecked == true && PlaysEndlessly;
        _time = endless ? Math.Max(time, min) : Math.Clamp(time, min, max);
        _timeline.Time = _time;
        _curveboard.Time = _time;
        _rangeBar.Time = _time;
        RefreshBlendshapePanel();
        RefreshMaterial();
        _syncing = true;
        // Endless play runs past the last frame: the scrub field grows with it.
        if ((decimal)_time > _frame.Maximum) _frame.Maximum = (decimal)MathF.Ceiling(_time);
        _frame.Value = (decimal)MathF.Round(_time, 2);
        _syncing = false;
        if (!_advancing && _play.IsChecked == true)
        {
            _audio.Seek(_time);
        }
        Apply();
        RefreshHotPointMarkers();
    }

    private void Advance()
    {
        var (start, end) = PlayBounds();
        float elapsed = (float)_clock.Elapsed.TotalSeconds;
        _clock.Restart();
        if (_drag != null || (end <= start && !PlaysEndlessly))
        {
            return;
        }
        float next = _time + elapsed * (float)FrameRate;
        // Loop: past the end the time wraps, t = (t - end - 1) mod (end - start + 1) + start.
        if (!PlaysEndlessly) next = TimeBar.Wrap(next, start, end);
        _advancing = true;
        try
        {
            SetTime(next);
        }
        finally
        {
            _advancing = false;
        }
    }

    // Each character's own animation: the one it was on when another
    // character became current. Switching back restores it. Every remembered character plays its
    // animation at the timeline's playhead (wrapped into that animation's range), so playing or
    // scrubbing moves all characters together.
    private sealed class CharacterPlayback
    {
        public Animation? Animation;
        public float Time;
    }

    private readonly Dictionary<Character, CharacterPlayback> _characterPlayback = new(ReferenceEqualityComparer.Instance);

    /// <summary>Remembers the current character's animation and time (before switching away).</summary>
    private void RememberCharacterPlayback()
    {
        if (_character == null) return;
        _characterPlayback[_character] = new CharacterPlayback { Animation = _animation, Time = _time };
    }

    /// <summary>The remembered animation of <paramref name="character"/>, if it still belongs to it.</summary>
    private CharacterPlayback? RememberedPlayback(Character character) =>
        _characterPlayback.TryGetValue(character, out var p) && (p.Animation == null || p.Animation.Character == character)
            ? p : null;

    /// <summary>
    /// What a character other than the current one shows: its remembered animation at the
    /// playhead, wrapped into the animation's range; else the rest pose.
    /// </summary>
    private (Animation? Animation, float Time) OtherPlayback(Character character)
    {
        if (RememberedPlayback(character) is not { Animation: { } a }) return (null, 0f);
        var (start, end) = a.TimeRange;
        return (a, end > start ? TimeBar.Wrap(_time, start, end) : start);
    }

    private void Apply()
    {
        SceneRenderer renderer = _viewport.Renderer;
        renderer.ShowMesh = _showMesh.IsChecked == true;
        renderer.ShowSkeleton = _showSkeleton.IsChecked == true;
        renderer.ShowControllers = _showControllers.IsChecked == true;
        renderer.ShowGrid = _showGrid.IsChecked == true;
        renderer.HideHandles = _play.IsChecked == true;
        if (_pose != null) _pose.ShowShoeLocators = ShoeLocatorsShown;
        ApplyViewPrefs(renderer);
        try
        {
            _pose?.Update(_character, _animation, _time, _showOthers.IsChecked == true, OtherPlayback);
        }
        catch (Exception ex)
        {
            _status.Text = "Pose evaluation failed: " + ex.Message;
        }
        ApplyViewPrefDecorators(renderer);
        renderer.Ghosts = ComputeGhosts();
        UpdateViewportViews();
        UpdateSelection();
        RefreshVertexPoints();
        _viewport.RequestNextFrameRendering();
        RefreshCameraView();
    }

    private void LoadViews()
    {
        _viewStates.Clear();
        _sceneViews = ((_pose != null) ? new SceneViews(_pose.Model.Scene) : null);
        _layout = _sceneViews?.Layout ?? SceneLayout.SingleView;
        BuildViewStates();
        if (_layout == SceneLayout.TwoViewsCurveboard && _showCurves.IsChecked != true)
        {
            _showCurves.IsChecked = true;
        }
        UpdateViewportViews();
    }

    private void BuildViewStates()
    {
        _viewStates.Clear();
        if (_sceneViews == null || _pose == null)
        {
            _viewStates.Add(new ViewState());
            _focusedView = 0;
            return;
        }
        IReadOnlyList<Camera> cameras = _pose.Model.Scene.Cameras;
        OrbitCamera shared = _viewport.Renderer.Camera;
        bool sharedTaken = false;
        foreach (int item in _sceneViews.Cameras(_layout))
        {
            int num = ((item >= 0 && item < cameras.Count) ? item : 0);
            Camera? camera = num < cameras.Count ? cameras[num] : null;
            ViewState state = new ViewState
            {
                CameraIndex = num,
                Animated = camera as CameraAnimated
            };
            // The first plain orbit view drives the renderer's own camera (the one a document
            // saves and restores); every other view gets its own copy.
            bool plainOrbit = num == 0 || camera is not (OrthoCamera or CameraAnimated or PerspectiveCamera);
            if (plainOrbit && !sharedTaken)
            {
                state.Orbit = shared;
                sharedTaken = true;
            }
            else
            {
                state.Orbit = new OrbitCamera();
                state.Orbit.CopyFrom(shared);
            }
            // A stored orthographic slot (the Front/Left/Top... views) opens as a flat orbit
            // view along the same axis.
            if (camera is OrthoCamera ortho)
            {
                state.Orbit.AlignTo(AxisFacing(SceneCameraView.Ortho(ortho).Forward), animate: false);
                state.Orbit.Orthographic = true;
            }
            _viewStates.Add(state);
        }
        _focusedView = Math.Clamp(_focusedView, 0, _viewStates.Count - 1);
    }

    /// <summary>The axis-cube side (<see cref="AxisCube"/> numbering) a view looking along <paramref name="forward"/> is seen from.</summary>
    private static int AxisFacing(Vector3 forward)
    {
        Vector3 eye = -forward;
        Vector3 a = Vector3.Abs(eye);
        int axis = a.X >= a.Y && a.X >= a.Z ? 0 : a.Y >= a.Z ? 1 : 2;
        float sign = axis == 0 ? eye.X : axis == 1 ? eye.Y : eye.Z;
        return sign >= 0 ? axis : axis + 3;
    }

    /// <summary>The axis-cube side named by an view name (Right, Left, Top, Bottom, Front, Back), or -1.</summary>
    private static int AxisOfViewName(string name) => name.ToLowerInvariant() switch
    {
        "right" => 0,
        "top" => 1,
        "front" => 2,
        "left" => 3,
        "bottom" => 4,
        "back" => 5,
        _ => -1,
    };

    private void UpdateViewportViews()
    {
        SceneRenderer renderer = _viewport.Renderer;
        if (_pose == null || _viewStates.Count == 0)
        {
            renderer.Views = Array.Empty<RenderViewport>();
            renderer.SceneView = null;
            UpdateAxisCubes();
            return;
        }
        IReadOnlyList<ViewRect> readOnlyList = ViewGrid.Rects(_layout);
        List<RenderViewport> list = new List<RenderViewport>(readOnlyList.Count);
        bool perView = DecoratorsPerView;
        bool skinning = SkinTintShown();
        for (int i = 0; i < readOnlyList.Count; i++)
        {
            ViewState state = ((i < _viewStates.Count) ? _viewStates[i] : new ViewState());
            ViewDisplay? display = null;
            if (perView)
            {
                // A view without its own switches yet starts from the current ones.
                state.Display ??= CheckboxDisplay();
                // The skin-weight tint replaces textures in every view.
                display = skinning ? state.Display with { Textured = false } : state.Display;
            }
            list.Add(new RenderViewport(CameraFor(state), readOnlyList[i], display));
        }
        renderer.Views = list;
        renderer.ActiveViewIndex = Math.Clamp(_focusedView, 0, list.Count - 1);
        UpdateAxisCubes();
    }

    private IViewCamera? CameraFor(ViewState state)
    {
        if (state.Animated != null && AnimatedView(state.Animated) is { } animated)
        {
            return animated;
        }
        IReadOnlyList<Camera>? cameras = _pose?.Model.Scene.Cameras;
        if (cameras != null && state.StoredPerspective && cameras.Count > 0 && cameras[0] is PerspectiveCamera stored)
        {
            return SceneCameraView.Perspective(stored);
        }
        if (cameras != null && state.CameraIndex > 0 && state.CameraIndex < cameras.Count)
        {
            switch (cameras[state.CameraIndex])
            {
                case CameraAnimated animatedCamera:
                    return AnimatedView(animatedCamera);
                case PerspectiveCamera { Name: "Photo" } photo:
                    return SceneCameraView.Photo(photo);
                case PerspectiveCamera perspective:
                    return SceneCameraView.Perspective(perspective);
            }
        }
        // The orbit view (an orthographic slot is a flat orbit view, BuildViewStates).
        return state.Orbit;
    }

    private IViewCamera? AnimatedView(CameraAnimated animated)
    {
        Character character = animated.Character ?? _character;
        if (_pose == null || character == null)
        {
            return null;
        }
        PoseEvaluator poseEvaluator = _pose.Evaluator(character);
        int num = poseEvaluator.IndexOf(animated);
        if (num < 0)
        {
            return null;
        }
        var (animation, time) = character == _character ? (_animation, _time) : OtherPlayback(character);
        if (animation == null && character != _character) animation = character.CurrentAnimation;
        Matrix4x4 world = poseEvaluator.WorldMatrices(animation, time)[num];
        return SceneCameraView.Animated(animated, world, poseEvaluator.CameraFov(animation, animated, time));
    }

    private string ViewDescription(ViewState state)
    {
        if (state.Animated != null)
        {
            return "animated camera '" + state.Animated.Name + "'";
        }
        if (state.StoredPerspective)
        {
            return "the scene's Perspective camera";
        }
        if (state.CameraIndex > 0 && _pose != null && state.CameraIndex < _pose.Model.Scene.Cameras.Count)
        {
            Camera camera = _pose.Model.Scene.Cameras[state.CameraIndex];
            if (camera is OrthoCamera) return OrbitDescription(state);
            return (camera is CameraAnimated) ? ("animated camera '" + camera.Name + "'") : ("'" + camera.Name + "'");
        }
        return OrbitDescription(state);
    }

    private static string OrbitDescription(ViewState state) =>
        state.Orbit is { Orthographic: true } ? "Orbit (flat)" : "Perspective (Orbit)";

    /// <summary>
    /// Turns the focused view's orbit camera to look from <paramref name="axis"/>'s side and makes
    /// it flat or perspective: the old Front/Left/Top... shortcuts and <c>--view</c> names. A view
    /// showing a scene camera goes back to its orbit camera first.
    /// </summary>
    private void AlignFocusedView(int axis, bool flat, bool animate = true)
    {
        ViewState state = FocusedState;
        if (CameraFor(state) is not OrbitCamera)
        {
            state.Animated = null;
            state.StoredPerspective = false;
            state.CameraIndex = 0;
            if (_sceneViews != null)
            {
                _sceneViews.SetCamera(_layout, _focusedView, 0);
                _dirty = true;
                UpdateTitle();
            }
        }
        state.Orbit ??= _viewport.Renderer.Camera;
        state.Orbit.AlignTo(axis, animate);
        state.Orbit.Orthographic = flat;
        UpdateViewportViews();
        RefreshMenu();
        _viewport.RequestNextFrameRendering();
        _status.Text = $"View {_focusedView + 1}: {AxisCube.Label(axis)} side, {(flat ? "flat" : "perspective")}.";
    }

    private void SetView(string view, bool animate = true)
    {
        if (_pose == null)
        {
            return;
        }
        // A side name (Front, Right, Top...) or one of the scene's orthographic cameras turns the
        // orbit view instead of switching to a fixed camera.
        string sideName = view.StartsWith("camera:", StringComparison.Ordinal) ? view.Substring("camera:".Length) : view;
        int side = AxisOfViewName(sideName);
        if (side < 0 && TryCamera(sideName, out int orthoIndex, out _) && _pose.Model.Scene.Cameras[orthoIndex] is OrthoCamera ortho)
        {
            side = AxisFacing(SceneCameraView.Ortho(ortho).Forward);
        }
        if (side >= 0 && !_pose.Model.Scene.Cameras.Any(c => c is CameraAnimated && c.Name == sideName))
        {
            AlignFocusedView(side, flat: true, animate);
            return;
        }
        ViewState focusedState = FocusedState;
        if (focusedState.Orbit != null && (view.Equals("perspective", StringComparison.OrdinalIgnoreCase) || view == "orbit"))
        {
            focusedState.Orbit.Orthographic = false;
        }
        focusedState.Animated = null;
        focusedState.StoredPerspective = false;
        int index = 0;
        IReadOnlyList<Camera> cameras = _pose.Model.Scene.Cameras;
        bool flag = ((view == "perspective" || view == "orbit") ? true : false);
        bool flag2 = flag;
        if (view.StartsWith("camera:", StringComparison.Ordinal))
        {
            if (TryCamera(view.Substring("camera:".Length), out index, out focusedState.StoredPerspective) && index > 0 && index < cameras.Count && cameras[index] is CameraAnimated animated)
            {
                focusedState.Animated = animated;
            }
        }
        else if (view.StartsWith("animated:", StringComparison.Ordinal))
        {
            focusedState.Animated = FindAnimated(view.Substring("animated:".Length));
        }
        else if (!flag2)
        {
            focusedState.Animated = FindAnimated(view);
            if (focusedState.Animated == null)
            {
                flag2 = TryCamera(view, out index, out focusedState.StoredPerspective);
            }
        }
        CameraAnimated animated2 = focusedState.Animated;
        if (animated2 != null)
        {
            focusedState.CameraIndex = _pose.Model.Scene.EnsureCameraInList(animated2);
            if (_sceneViews != null)
            {
                _sceneViews.SetCamera(_layout, _focusedView, focusedState.CameraIndex);
                _dirty = true;
                UpdateTitle();
            }
        }
        else
        {
            focusedState.CameraIndex = index;
            if (_sceneViews != null)
            {
                _sceneViews.SetCamera(_layout, _focusedView, index);
                _dirty = true;
                UpdateTitle();
            }
        }
        UpdateViewportViews();
        RefreshMenu();
        _viewport.RequestNextFrameRendering();
        _status.Text = $"View {_focusedView + 1}: {ViewDescription(focusedState)}.";
        CameraAnimated? FindAnimated(string name)
        {
            return cameras.OfType<CameraAnimated>().FirstOrDefault((CameraAnimated c) => c.Name == name) ?? _character?.AnimatedCameras.FirstOrDefault((CameraAnimated c) => c.Name == name);
        }
    }

    private bool TryCamera(string name, out int index, out bool storedPerspective)
    {
        index = 0;
        storedPerspective = false;
        IReadOnlyList<Camera> readOnlyList = _pose?.Model.Scene.Cameras;
        if (readOnlyList == null)
        {
            return false;
        }
        for (int i = 0; i < readOnlyList.Count; i++)
        {
            if (!(readOnlyList[i].Name != name))
            {
                index = i;
                // The scene's own Perspective camera (index 0) is shown as the orbit camera: a
                // perspective view that cannot be moved has no use.
                storedPerspective = false;
                return true;
            }
        }
        return false;
    }

    private void SetLayout(SceneLayout layout)
    {
        if (layout != _layout)
        {
            _sceneViews?.Change(layout, _focusedView);
            _layout = layout;
            if (layout == SceneLayout.SingleView)
            {
                _focusedView = 0;
            }
            BuildViewStates();
            if (layout == SceneLayout.TwoViewsCurveboard && _showCurves.IsChecked != true)
            {
                _showCurves.IsChecked = true;
            }
            if (_sceneViews != null)
            {
                _dirty = true;
                UpdateTitle();
            }
            UpdateViewportViews();
            RefreshMenu();
            _viewport.RequestNextFrameRendering();
            _status.Text = "Layout: " + ViewLayouts.Name(layout) + ".";
        }
    }

    private void ToggleLayout()
    {
        if (_sceneViews == null)
        {
            _status.Text = "No scene is open.";
            return;
        }
        _focusedView = _sceneViews.Toggle(_focusedView);
        _layout = _sceneViews.Layout;
        BuildViewStates();
        if (_layout == SceneLayout.TwoViewsCurveboard && _showCurves.IsChecked != true)
        {
            _showCurves.IsChecked = true;
        }
        _dirty = true;
        UpdateTitle();
        UpdateViewportViews();
        RefreshMenu();
        _viewport.RequestNextFrameRendering();
        _status.Text = "Layout: " + ViewLayouts.Name(_layout) + ((_viewStates.Count > 1) ? $", view {_focusedView + 1} focused." : ".");
    }

    /// <summary>
    /// Adds an animated camera (its camera and target rows, and the hidden joint) to the current
    /// character, making a new character first when there is none in the Rigging context. The view
    /// stays where it is: the camera is an object to place, not the view to look through (pick it
    /// from the view menu for that).
    /// </summary>
    private void AddAnimatedCamera(string? name = null)
    {
        bool rigging = _pose != null && _pose.Model.Scene.Context == InterfaceContext.Rigging && _animation == null;
        if (rigging)
        {
            if (_character == null)
            {
                CreateCharacter();
                if (_character == null)
                {
                    return;
                }
            }
            Character owner = _character;
            Object3D? added = RunRigEdit("Added animated camera", _ => CameraEditor.AddAnimatedCamera(owner, name), "add a camera");
            if (added != null)
            {
                _status.Text = "Added animated camera '" + added.Name + "' and its target to '" + owner.Name + "'.";
            }
            return;
        }
        if (_character != null)
        {
            PoseEditSession session = Session;
            if (session != null)
            {
                try
                {
                    CameraAnimated cameraAnimated = session.AddAnimatedCamera(name);
                    _dirty = true;
                    UpdateTitle();
                    _outliner.Build(_pose!.Model.Scene);
                    Select(cameraAnimated);
                    _status.Text = "Added animated camera '" + cameraAnimated.Name + "' and its target. Key it like a controller; its FOV is the FOV field; look through it from the view menu.";
                    AfterKeyChange();
                    return;
                }
                catch (Exception ex)
                {
                    _status.Text = "Could not add a camera: " + ex.Message;
                    return;
                }
            }
        }
        _status.Text = "Select a character to add a camera to.";
    }

    private int SetFovValue(float value)
    {
        if (!_syncing && _drag == null && _pose != null && _character != null)
        {
            PoseEditSession session = Session;
            if (session != null)
            {
                Object3D[] controllers = _selectedAll.Where((Object3D c) => session.Evaluator.IndexOf(c) >= 0).ToArray();
                int frame = (int)MathF.Round(_time);
                int written = 0;
                RunKeyEdit(delegate
                {
                    written = session.SetFov(_animation, controllers, frame, value, out string reason);
                    if (written == 0)
                    {
                        _status.Text = reason ?? "The FOV was not applied.";
                    }
                });
                UpdateSelection(forceSpinner: true);
                return written;
            }
        }
        return 0;
    }
}
