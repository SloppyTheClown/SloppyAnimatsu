// The camera lists of the View menu and dropping a scene file onto the window.
//
// The app names a view's camera in a bottom-left button whose menu switches between fixed
// Front/Left/Top/... views. SloppyAnimatsu replaced that button with the axis cube
// the orbit views turn to an axis
// and go flat from the cube, and the scene's orthographic cameras are no longer views of their own.
// The photo and animated cameras stay reachable from the View menu.
// Part of the partial MainWindow type.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    /// <summary>
    /// The fixed cameras a view can show, by name: the scene's photo cameras. The stored
    /// Perspective camera (index 0) is left out - a perspective view is always an orbit camera -
    /// and so are the orthographic cameras, whose sides the axis cube now gives every orbit view.
    /// </summary>
    private List<string> FixedCameraNames()
    {
        List<string> names = new List<string>();
        IReadOnlyList<Camera>? cameras = _pose?.Model.Scene.Cameras;
        if (cameras == null)
        {
            return names;
        }
        for (int i = 0; i < cameras.Count; i++)
        {
            Camera camera = cameras[i];
            if (camera is CameraAnimated || camera is OrthoCamera || (i == 0 && camera is PerspectiveCamera))
            {
                continue;
            }
            names.Add(camera.Name ?? "(unnamed)");
        }
        return names;
    }

    private List<string> AnimatedCameraNames()
    {
        IEnumerable<CameraAnimated> inScene = (_pose?.Model.Scene.Cameras ?? Array.Empty<Camera>()).OfType<CameraAnimated>();
        return inScene.Concat(_character?.AnimatedCameras ?? Enumerable.Empty<CameraAnimated>())
            .Select(camera => camera.Name ?? "(unnamed)").Distinct().ToList();
    }

    /// <summary>Lets a scene file dragged from the file manager onto the window open it.</summary>
    private void EnableFileDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (DroppedScene(e) != null)
            {
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
            }
        });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            string? path = DroppedScene(e);
            if (path != null)
            {
                e.Handled = true;
                Load(path);
            }
        });
    }

    /// <summary>The first .akt file of a drag, or null when it carries none (a TREE row drag, say).</summary>
    private static string? DroppedScene(DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files))
        {
            return null;
        }
        foreach (Avalonia.Platform.Storage.IStorageItem item in e.Data.GetFiles() ?? [])
        {
            string? path = item.TryGetLocalPath();
            if (path != null && SloppyAnimatsu.Core.Akt.SceneFiles.HasSceneExtension(path))
            {
                return path;
            }
        }
        return null;
    }
}
