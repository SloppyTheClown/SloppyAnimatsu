// Onion skinning: the Stacker's two ghost toggles ("Activate Previous Ghost(s)" / "Activate Next
// Ghost(s)", shortcuts B and N) and the ghost meshes handed to the renderer. The two toggles are
// stored per stack (AnimStack.FlagsNoModify bits 0 and 1), so they are saved with the scene and
// follow the current stack. Which times get a ghost, their colours and opacity come from Core's
// Ghosts planner.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    private bool GhostsOn(bool next) =>
        _timeline.ActiveStack is { } stack
        && (stack.FlagsNoModify & (next ? AnimStack.NextGhostsFlag : AnimStack.PreviousGhostsFlag)) != 0;
    private ToggleButton? _ghostsPreviousButton;
    private ToggleButton? _ghostsNextButton;
    private bool _ghostSyncing;

    /// <summary>Ghost vertices by time, reused while only the playhead moves (see <see cref="ComputeGhosts"/>).</summary>
    private readonly Dictionary<float, GhostGeometry> _ghostCache = [];
    private (Character? Character, Animation? Animation, AnimStack? Stack, bool PerFrame) _ghostCacheOwner;
    private float _ghostCacheTime = float.NaN;

    private ToggleButton GhostButton(string label, string tip, bool next)
    {
        var button = new ToggleButton
        {
            Content = label,
            FontSize = 9.0,
            Height = 18.0,
            MinHeight = 0.0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(1.0),
            Padding = new Thickness(1.0, 0.0),
        };
        ToolTip.SetTip(button, tip);
        button.IsCheckedChanged += delegate
        {
            if (!_ghostSyncing) SetGhosts(next, button.IsChecked == true);
        };
        return button;
    }

    /// <summary>The Stacker's ghost row: previous ghosts on the left, next ghosts on the right.</summary>
    private Control GhostButtonsRow()
    {
        _ghostsPreviousButton ??= GhostButton("Activate Previous Ghost(s)",
            "Show see-through ghosts of the poses at the keys before the playhead (B)", next: false);
        _ghostsNextButton ??= GhostButton("Activate Next Ghost(s)",
            "Show see-through ghosts of the poses at the keys after the playhead (N)", next: true);
        foreach (Control control in new Control[] { _ghostsPreviousButton, _ghostsNextButton })
            (control.Parent as Panel)?.Children.Remove(control);
        SyncGhostButtons();
        return new UniformGrid
        {
            Columns = 2,
            Margin = new Thickness(3.0, 0.0, 3.0, 2.0),
            VerticalAlignment = VerticalAlignment.Top,
            Children = { _ghostsPreviousButton, _ghostsNextButton },
        };
    }

    private void ToggleGhosts(bool next) => SetGhosts(next, !GhostsOn(next));

    /// <summary>
    /// Turns one side's ghosts on or off for the current stack. It is view state saved with the
    /// scene, like the current stack, so it marks the document changed but is not an undo step.
    /// </summary>
    private void SetGhosts(bool next, bool on)
    {
        if (_timeline.ActiveStack is not { } stack)
        {
            SyncGhostButtons();
            return;
        }
        if (stack.SetNoModifyFlag(next ? AnimStack.NextGhostsFlag : AnimStack.PreviousGhostsFlag, on))
        {
            _dirty = true;
            UpdateTitle();
        }
        SyncGhostButtons();
        _status.Text = (next ? "Next" : "Previous") + " ghosts " + (on ? "on" : "off") + " for '" + stack.Name + "'";
        Apply();
    }

    private void SyncGhostButtons()
    {
        _ghostSyncing = true;
        bool hasStack = _timeline.ActiveStack != null;
        if (_ghostsPreviousButton != null)
        {
            _ghostsPreviousButton.IsChecked = GhostsOn(next: false);
            DisabledHint.SetEnabled(_ghostsPreviousButton, hasStack, "an active stack");
        }
        if (_ghostsNextButton != null)
        {
            _ghostsNextButton.IsChecked = GhostsOn(next: true);
            DisabledHint.SetEnabled(_ghostsNextButton, hasStack, "an active stack");
        }
        _ghostSyncing = false;
        // The toggles are per stack, so picking another stack (Mixer row, timeline row, Stacker
        // arrows) can change which ghosts belong on screen: redraw them now rather than on the
        // next playhead move or edit.
        var stack = _timeline.ActiveStack;
        if (_ghostsDrawnFor != (stack, GhostsOn(next: false), GhostsOn(next: true)))
        {
            _viewport.Renderer.Ghosts = ComputeGhosts();
            _viewport.RequestNextFrameRendering();
        }
    }

    /// <summary>The stack and toggles the ghosts on screen were computed for.</summary>
    private (AnimStack? Stack, bool Previous, bool Next) _ghostsDrawnFor;

    /// <summary>
    /// The ghosts to draw for the current character around the playhead, farthest first so the
    /// nearer ones end up on top. While playing they follow <c>CStackerPreferences</c>' "Ghosts on
    /// playing". Posed meshes are cached by time while only the playhead moves (scrubbing,
    /// playback); an <see cref="Apply"/> at an unchanged time is an edit, so the cache is dropped.
    /// </summary>
    private IReadOnlyList<GhostMesh> ComputeGhosts()
    {
        bool previousOn = GhostsOn(next: false), nextOn = GhostsOn(next: true);
        _ghostsDrawnFor = (_timeline.ActiveStack, previousOn, nextOn);
        if ((!previousOn && !nextOn) || _pose == null || _character == null || _animation == null
            || _timeline.ActiveStack is not { } stack)
        {
            _ghostCache.Clear();
            return [];
        }
        var settings = _preferences?.Ghosts ?? GhostSettings.Default;
        if (_play.IsChecked == true && !settings.OnPlaying) return [];

        var owner = (_character, _animation, stack, settings.PerFrame);
        if (owner != _ghostCacheOwner || _time == _ghostCacheTime) _ghostCache.Clear();
        _ghostCacheOwner = owner;
        _ghostCacheTime = _time;

        var plan = Ghosts.Plan(stack, _time, previousOn, nextOn, settings);
        var previousColor = ToColor(settings.PreviousColor);
        var nextColor = ToColor(settings.NextColor);
        var meshes = new List<GhostMesh>(plan.Count);
        var used = new HashSet<float>();
        try
        {
            foreach (var ghost in plan.OrderByDescending(g => g.Distance))
            {
                if (!_ghostCache.TryGetValue(ghost.Time, out var geometry))
                {
                    geometry = _pose.GhostVertices(_character, _animation, ghost.Time);
                    _ghostCache[ghost.Time] = geometry;
                }
                used.Add(ghost.Time);
                meshes.Add(new GhostMesh(geometry, ghost.UsesNextColor ? nextColor : previousColor, ghost.Opacity,
                    settings.Wire));
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Ghost evaluation failed: " + ex.Message;
            _ghostCache.Clear();
            return [];
        }
        // Keep the cache to what is shown, so a long playback does not hold every frame.
        foreach (var stale in _ghostCache.Keys.Where(t => !used.Contains(t)).ToList()) _ghostCache.Remove(stale);
        return meshes;
    }

    private static Vector3 ToColor((byte R, byte G, byte B) c) => new(c.R / 255f, c.G / 255f, c.B / 255f);
}
