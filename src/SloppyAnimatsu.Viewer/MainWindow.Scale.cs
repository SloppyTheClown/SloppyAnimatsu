// Normalize tools of the Rigging context: Rig > Normalize scale / translation / rotation (also Rig Workshop buttons), which
// reset the scale, position or rotation of characters, meshes and groups while keeping everything in place.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    /// <summary>The undo description of Normalize Scale; undo and redo reload geometry for it.</summary>
    private const string NormalizeScaleDescription = "Normalize Scale";

    /// <summary>
    /// Whether <paramref name="o"/> is placed through its own transform rather than posed as a
    /// controller: a character, a mesh, a group (anything but a blendshape target that is not a
    /// controller of the current character).
    /// </summary>
    private bool IsPlacedObject(Object3D? o) =>
        o != null && o is not Blendshape && _pose != null
        && (o is Character || _character == null || _pose.Evaluator(_character).IndexOf(o) < 0);

    private async void NormalizeScale()
    {
        if (_pose == null || _drag != null) return;
        if (_animation != null || _pose.Model.Scene.Context != InterfaceContext.Rigging)
        {
            _status.Text = "Normalize scale works in the Rigging context.";
            return;
        }
        var scene = _pose.Model.Scene;
        var scaled = scene.Descendants<Object3D>().Where(SceneRescale.HasScaleToNormalize)
            .Where(o => o is Character or Mesh || o.GetType() == typeof(Object3D)).ToList();
        if (!SceneRescale.SceneNeedsNormalizing(_pose.Model))
        {
            _status.Text = "Nothing to normalize: every character, mesh and group already has a scale of 1.";
            return;
        }
        if (!await ConfirmNormalizeScale(scaled)) return;

        var before = EditSnapshot.TakeScene(_character, scene);
        SceneRescale rescale;
        try
        {
            rescale = SceneRescale.Normalize(_pose.Model);
        }
        catch (Exception ex)
        {
            before.Restore();
            ReloadAfterRescale();
            _status.Text = "Normalize scale failed: " + ex.Message;
            return;
        }
        _history.Push(new EditRecord(NormalizeScaleDescription, before, EditSnapshot.TakeScene(_character, scene)));
        _dirty = true;
        UpdateTitle();
        ReloadAfterRescale();
        string skipped = rescale.SkippedNonUniform.Count > 0
            ? $" Skipped {rescale.SkippedNonUniform.Count} object(s) with a non-uniform scale: {string.Join(", ", rescale.SkippedNonUniform.Take(4).Select(o => o.Name))}."
            : "";
        _status.Text = $"Normalized the scale of {rescale.Normalized.Count} object(s): {string.Join(", ", rescale.Normalized.Take(4).Select(o => o.Name))}.{skipped}";
    }

    /// <summary>The undo descriptions of Normalize translation / rotation (undo and redo reload geometry for them).</summary>
    private const string NormalizeTranslationDescription = "Normalize Translation";
    private const string NormalizeRotationDescription = "Normalize Rotation";

    /// <summary>Whether an undo step rewrote geometry and poses scene-wide (a normalize command).</summary>
    private static bool IsNormalizeStep(string? description) =>
        description is NormalizeScaleDescription or NormalizeTranslationDescription or NormalizeRotationDescription;

    /// <summary>
    /// Rig > Normalize translation / rotation: zeroes the position or rotation of characters,
    /// meshes and groups while keeping everything where it is on screen.
    /// </summary>
    private async void NormalizeTransform(ReframeParts parts)
    {
        string what = parts == ReframeParts.Translation ? "translation" : "rotation";
        if (_pose == null || _drag != null) return;
        if (_animation != null || _pose.Model.Scene.Context != InterfaceContext.Rigging)
        {
            _status.Text = $"Normalize {what} works in the Rigging context.";
            return;
        }
        var candidates = SceneReframe.Candidates(_pose.Model, parts);
        if (candidates.Count == 0)
        {
            _status.Text = parts == ReframeParts.Translation
                ? "Nothing to normalize: every character, mesh and group is already at position 0."
                : "Nothing to normalize: every character, mesh and group already has a rotation of 0.";
            return;
        }
        string list = string.Join("\n", candidates.Take(8).Select(o => "  • " + o.Name));
        if (candidates.Count > 8) list += $"\n  … and {candidates.Count - 8} more";
        string text = parts == ReframeParts.Translation
            ? "Move these objects to position 0 (relative to their parent), keeping everything where it is on screen?\n\n" + list +
              "\n\nThe offset moves into their children, the poses, the animation keys and the mesh vertices. Animated objects, " +
              "objects used as a key space and joints/controllers are left alone. Edit > Undo (Ctrl+Z) reverts it."
            : "Set the rotation of these objects to 0, keeping everything where it is on screen?\n\n" + list +
              "\n\nThe rotation moves into their children, the poses, the animation keys and the mesh vertices and normals. " +
              "Animated objects, objects with a non-uniform scale and joints/controllers are left alone. Edit > Undo (Ctrl+Z) reverts it.";
        if (!await ConfirmNormalize("Normalize " + what, text)) return;

        var scene = _pose.Model.Scene;
        var before = EditSnapshot.TakeScene(_character, scene);
        SceneReframe reframe;
        try
        {
            reframe = SceneReframe.Normalize(_pose.Model, parts);
        }
        catch (Exception ex)
        {
            before.Restore();
            ReloadAfterRescale();
            _status.Text = $"Normalize {what} failed: " + ex.Message;
            return;
        }
        string skipped = reframe.Skipped.Count > 0
            ? $" Skipped {reframe.Skipped.Count}: " + string.Join("; ", reframe.Skipped.Take(4).Select(s => $"{s.Object.Name} ({s.Reason})")) + "."
            : "";
        if (!reframe.Changed)
        {
            _status.Text = $"Nothing was normalized.{skipped}";
            return;
        }
        _history.Push(new EditRecord(parts == ReframeParts.Translation ? NormalizeTranslationDescription : NormalizeRotationDescription,
            before, EditSnapshot.TakeScene(_character, scene)));
        _dirty = true;
        UpdateTitle();
        ReloadAfterRescale();
        _status.Text = $"Normalized the {what} of {reframe.Normalized.Count} object(s): {string.Join(", ", reframe.Normalized.Take(4).Select(o => o.Name))}.{skipped}";
    }

    /// <summary>
    /// Re-reads everything a rescale rewrote: every character's rest and bind poses, the mesh
    /// geometry and the mesh placements; then redraws and refreshes the transform panel.
    /// </summary>
    private void ReloadAfterRescale()
    {
        if (_pose == null) return;
        foreach (var character in _pose.Characters) _pose.ReloadRestPose(character);
        _pose.ReloadGeometry();
        _pose.RefreshPlacements();
        AfterKeyChange();
        Apply();
        UpdateSelection(forceSpinner: true);
    }

    private Task<bool> ConfirmNormalizeScale(IReadOnlyList<Object3D> scaled)
    {
        string list = string.Join("\n", scaled.Take(8).Select(o =>
        {
            var s = Object3D.LocalScale(o.LocalMatrix);
            return $"  • {o.Name}: {(s.X + s.Y + s.Z) / 3f:0.###}x";
        }));
        if (scaled.Count > 8) list += $"\n  … and {scaled.Count - 8} more";
        return ConfirmNormalize("Normalize scale",
            "Set the scale of these objects to 1, keeping everything the same size?\n\n" + list +
            "\n\nThe scale moves into the joint positions, the poses, the animation keys and the mesh vertices, " +
            "so nothing moves on screen. Joint and controller scales are kept. Edit > Undo (Ctrl+Z) reverts it.");
    }

    private async Task<bool> ConfirmNormalize(string title, string text)
    {
        Button normalize = new Button { Content = "Normalize", IsDefault = true };
        Button cancel = new Button { Content = "Cancel", IsCancel = true };
        Window window = new Window
        {
            Title = title,
            Width = 480.0,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 12.0,
                Margin = new Thickness(18.0),
                Children =
                {
                    new TextBlock
                    {
                        Text = text,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8.0,
                        Children = { normalize, cancel }
                    }
                }
            }
        };
        bool confirmed = false;
        normalize.Click += delegate { confirmed = true; window.Close(); };
        cancel.Click += delegate { window.Close(); };
        window.Opened += delegate { normalize.Focus(); };
        await window.ShowDialog(this);
        return confirmed;
    }
}
