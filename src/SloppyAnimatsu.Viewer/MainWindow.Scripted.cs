// Scripted runs: the --actions step parser (placement/point arguments, the skin script operations,
// find-by-name helpers) and the screenshot writer. Scripted mode is how the tests and the
// acceptance runs drive the viewer without a pointer.
//
// A step is one line. RunScriptedEdit applies the steps in order and stops at the first refusal, so
// every operation reports failure instead of throwing.
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
    private static bool TryPlacementArgument(string? argument, out string? name, out Vector3 point)
    {
        name = null;
        point = default(Vector3);
        if (argument == null)
        {
            return false;
        }
        int num = argument.LastIndexOf('@');
        if (!TryPoint((num >= 0) ? argument.Substring(num + 1) : argument, out point, out float[] _))
        {
            return false;
        }
        name = ((num > 0) ? argument.Substring(0, num) : null);
        return true;
    }

    private static bool TryPoint(string text, out Vector3 point, out float[] rest)
    {
        point = default(Vector3);
        rest = Array.Empty<float>();
        string[] array = text.Split(',');
        int num = array.Length;
        if ((num < 3 || num > 4) ? true : false)
        {
            return false;
        }
        float[] array2 = new float[array.Length];
        for (int i = 0; i < array.Length; i++)
        {
            if (!float.TryParse(array[i], NumberStyles.Float, CultureInfo.InvariantCulture, out array2[i]))
            {
                return false;
            }
        }
        point = new Vector3(array2[0], array2[1], array2[2]);
        rest = array2[3..];
        return true;
    }

    private Mesh? FindSkinMesh(string name)
    {
        if (_character == null)
        {
            Console.Error.WriteLine("--bank skin-mesh: no character");
            return null;
        }
        Mesh mesh = _character.Meshes.FirstOrDefault((Mesh m) => m.Name == name && m.Skinned);
        if (mesh == null)
        {
            Console.Error.WriteLine("--bank skin-mesh: no skinned mesh '" + name + "'");
        }
        return mesh;
    }

    private bool ScriptedSkinSelect(string argument)
    {
        SkinPaintSession skin = _skin;
        if (skin == null || _pose == null)
        {
            Console.Error.WriteLine("--bank skin-select: pick a skin mesh first");
            return false;
        }
        _skinPositions = SkinMeshPositions(skin.Mesh);
        if (argument == "all")
        {
            for (int i = 0; i < skin.Selection.Count; i++)
            {
                skin.Selection.Set(i, selected: true);
            }
            RebuildSkinJoints();
            _status.Text = $"Selected all {skin.Selection.SelectedCount} control point(s) of {skin.Mesh.Name}.";
            return true;
        }
        if (!TryPoint(argument, out Vector3 point, out float[] rest))
        {
            Console.Error.WriteLine("--bank skin-select: all | x,y,z[,radius]");
            return false;
        }
        skin.Radius = ((rest.Length != 0) ? rest[0] : 1f);
        _skinNormals = new MeshSkin(skin.Mesh).Normals(_pose.ControllerWorlds);
        skin.Select(point, _skinPositions, add: true, _skinNormals);
        RebuildSkinJoints();
        _status.Text = $"Selected {skin.Selection.SelectedCount} control point(s) of {skin.Mesh.Name}.";
        return true;
    }

    private bool ScriptedSkinDab(string argument)
    {
        SkinPaintSession skin = _skin;
        if (skin == null || _pose == null || _character == null)
        {
            Console.Error.WriteLine("--bank skin-dab: pick a skin mesh first");
            return false;
        }
        if (!TryPoint(argument, out Vector3 point, out float[] rest))
        {
            Console.Error.WriteLine("--bank skin-dab: x,y,z[,radius]");
            return false;
        }
        _skinPositions = SkinMeshPositions(skin.Mesh);
        _skinJointPositions = SkinJointPositions();
        skin.Radius = ((rest.Length != 0) ? rest[0] : 1f);
        EditSnapshot before = EditSnapshot.TakeWeights(_character, [skin.Mesh]);
        if (!skin.BeginStroke())
        {
            Console.Error.WriteLine("--bank skin-dab: no painted joint");
            return false;
        }
        _skinNormals = new MeshSkin(skin.Mesh).Normals(_pose.ControllerWorlds);
        int value = skin.Dab(point, _skinPositions, _skinJointPositions, _skinNormals);
        skin.EndStroke();
        _history.Push(new EditRecord("Painted " + skin.JointObject?.Name + " on " + skin.Mesh.Name, before, EditSnapshot.TakeWeights(_character, [skin.Mesh])));
        _dirty = true;
        UpdateTitle();
        Apply();
        RebuildSkinJoints();
        _status.Text = $"Painted {value} control point(s) of {skin.Mesh.Name}.";
        return true;
    }

    private bool ScriptedSkinScan(string argument)
    {
        SkinPaintSession skin = _skin;
        if (skin == null || _pose == null)
        {
            Console.Error.WriteLine("--bank skin-scan: pick a skin mesh first");
            return false;
        }
        if (!TryPoint(argument, out Vector3 point, out float[] rest))
        {
            Console.Error.WriteLine("--bank skin-scan: x,y,z[,radius]");
            return false;
        }
        _skinPositions = SkinMeshPositions(skin.Mesh);
        skin.Radius = ((rest.Length != 0) ? rest[0] : 1f);
        SkinPaintSession.ScanHit? scanHit = skin.Scan(point, _skinPositions, null, skin.Radius);
        if (scanHit.HasValue)
        {
            SkinPaintSession.ScanHit valueOrDefault = scanHit.GetValueOrDefault();
            Console.WriteLine($"point {valueOrDefault.Vertex}: {valueOrDefault.Weights.Count} influence(s)");
            foreach (var weight in valueOrDefault.Weights)
            {
                string item = weight.Name;
                float item2 = weight.Weight;
                Console.WriteLine("  " + item + "  " + item2.ToString("0.###", CultureInfo.InvariantCulture));
            }
            _status.Text = $"Scanned point {valueOrDefault.Vertex}: {valueOrDefault.Weights.Count} influence(s).";
            return true;
        }
        Console.Error.WriteLine("--bank skin-scan: no control point within the radius");
        return false;
    }

    private bool ScriptedSkinLock(string argument)
    {
        SkinPaintSession skin = _skin;
        if (skin == null || _character == null)
        {
            Console.Error.WriteLine("--bank skin-lock: pick a skin mesh first");
            return false;
        }
        if ((argument == "none" || argument == "clear") ? true : false)
        {
            skin.ClearLocks();
        }
        else if (argument == "all")
        {
            skin.SetLock(Enumerable.Range(0, _character.Joints.Count), locked: true);
        }
        else
        {
            string[] array = argument.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (string text in array)
            {
                bool locked = !text.StartsWith('-');
                string name = text.TrimStart('+', '-');
                int num = _character.Joints.ToList().FindIndex((Joint j) => j.Name == name);
                if (num < 0)
                {
                    Console.Error.WriteLine("--bank skin-lock: no joint '" + name + "'");
                    return false;
                }
                skin.SetLock([num], locked);
            }
        }
        RebuildSkinJoints();
        _status.Text = $"Locked {skin.LockedJoints.Count} joint(s) of the weight list.";
        return true;
    }

    private Folder? BankFolder(string path)
    {
        Folder folder = _character?.AnimBank;
        if (folder == null)
        {
            return null;
        }
        List<Folder> source = (from f in folder.Descendants<Folder>()
            where !f.IsInternal
            select f).ToList();
        return source.FirstOrDefault((Folder f) => PathOf(f) == path) ?? source.FirstOrDefault((Folder f) => f.Name == path);
        static string PathOf(Folder f)
        {
            List<string> list = new List<string>();
            for (SceneObject sceneObject = f; sceneObject is Folder { IsInternal: false } folder2; sceneObject = sceneObject.Parent)
            {
                list.Insert(0, folder2.Name ?? "");
            }
            return string.Join("/", list);
        }
    }

    private void RunScriptedEdit()
    {
        if (_pose == null)
        {
            return;
        }
        if (_options.KeepChildren)
        {
            SetKeepChildrenFixed(value: true);
        }
        string layout = _options.Layout;
        if (layout != null)
        {
            if (ViewLayouts.TryParse(layout, out var layout2))
            {
                SetLayout(layout2);
            }
            else
            {
                Console.Error.WriteLine("--layout: expected single|2views|curveboard|4views, got '" + layout + "'");
            }
        }
        string view = _options.View;
        if (view != null)
        {
            SetView(view, animate: false);
        }
        InterfaceContext? context = _options.Context;
        if (context.HasValue)
        {
            InterfaceContext valueOrDefault = context.GetValueOrDefault();
            if (_pose.Model.Scene.Context != valueOrDefault)
            {
                SetContext(valueOrDefault);
            }
        }
        float? frame = _options.Frame;
        if (frame.HasValue)
        {
            float valueOrDefault2 = frame.GetValueOrDefault();
            SetTime(valueOrDefault2);
        }
        if (_options.ShowAll)
        {
            ChangeAllShown(shown: true);
        }
        if (_options.HideAll)
        {
            ChangeAllShown(shown: false);
        }
        foreach (string item3 in _options.Hide)
        {
            Object3D object3D = FindOutlinerObject(item3, "--hide");
            if (object3D == null)
            {
                return;
            }
            ChangeShown([object3D], shown: false);
            Console.WriteLine(_status.Text);
        }
        foreach (string item4 in _options.Show)
        {
            Object3D object3D2 = FindOutlinerObject(item4, "--show");
            if (object3D2 == null)
            {
                return;
            }
            ChangeShown([object3D2], shown: true);
            Console.WriteLine(_status.Text);
        }
        foreach (string item5 in _options.Lock)
        {
            if (!(FindOutlinerObject(item5, "--lock") is Mesh item))
            {
                Console.Error.WriteLine("--lock: '" + item5 + "' is not a mesh");
                return;
            }
            ChangeLock([item], locked: true);
            Console.WriteLine(_status.Text);
        }
        foreach (string item6 in _options.Unlock)
        {
            if (!(FindOutlinerObject(item6, "--unlock") is Mesh item2))
            {
                Console.Error.WriteLine("--unlock: '" + item6 + "' is not a mesh");
                return;
            }
            ChangeLock([item2], locked: false);
            Console.WriteLine(_status.Text);
        }
        bool flag = true;
        string[] array = _options.Select?.Split(',') ?? Array.Empty<string>();
        foreach (string name in array)
        {
            Object3D object3D3 = _character?.Name == name ? _character : _character?.Controllers.FirstOrDefault((Object3D c) => c.Name == name);
            if (object3D3 == null)
            {
                Console.Error.WriteLine($"--select: {_character?.Name} has no controller '{name}'");
                return;
            }
            Select(object3D3, !flag);
            flag = false;
        }
        foreach (string bankAction in _options.BankActions)
        {
            if (!RunBankAction(bankAction))
            {
                return;
            }
            Console.WriteLine(_status.Text);
        }
        foreach (var (gizmoMode, axis, num) in _options.Edits)
        {
            SetMode(gizmoMode);
            if (!((IViewportEditor)this).BeginDrag(axis, Vector2.Zero))
            {
                Console.Error.WriteLine("edit refused: " + _status.Text);
                return;
            }
            switch (gizmoMode)
            {
            case GizmoMode.Rotate:
                _drag.Rotate(axis, num * (float)Math.PI / 180f);
                break;
            case GizmoMode.Scale:
                _drag.Scale(axis, num);
                break;
            default:
                _drag.Translate(axis, num);
                break;
            }
            ((IViewportEditor)this).EndDrag(commit: true);
            Console.WriteLine(_status.Text);
        }
        foreach (var (channel, value) in _options.Values)
        {
            if (SetTypedValue(channel, value) == 0)
            {
                Console.Error.WriteLine("--set refused: " + _status.Text);
                return;
            }
            Console.WriteLine(_status.Text);
        }
        RestPoseResolution? resolve = _options.Resolve;
        if (resolve.HasValue)
        {
            RestPoseResolution valueOrDefault3 = resolve.GetValueOrDefault();
            PoseEditSession session = Session;
            if (session != null)
            {
                if (session.RestPose.IsResolutionPending)
                {
                    ResolveRestPose(session, valueOrDefault3);
                    Console.WriteLine(_status.Text);
                }
                else
                {
                    Console.Error.WriteLine("--resolve: no ref pose change is pending");
                }
            }
        }
        if (_options.SaveAs != null && SaveTo(_options.SaveAs))
        {
            Console.WriteLine("saved " + _options.SaveAs);
        }
    }

    private Object3D? FindOutlinerObject(string name, string option)
    {
        if (_pose == null)
        {
            return null;
        }
        List<Object3D> list = (from o in _pose.Model.Scene.Descendants<Object3D>()
            where o.Name == name
            select o).ToList();
        if (list.Count == 1)
        {
            return list[0];
        }
        Console.Error.WriteLine((list.Count == 0) ? (option + ": the scene has no 3D object '" + name + "'") : $"{option}: '{name}' names {list.Count} 3D objects");
        return null;
    }

    private void SaveScreenshot(byte[] pixels, int width, int height)
    {
        using (WriteableBitmap writeableBitmap = new WriteableBitmap(new PixelSize(width, height), new Avalonia.Vector(96.0, 96.0), PixelFormat.Rgba8888, AlphaFormat.Opaque))
        {
            using (ILockedFramebuffer lockedFramebuffer = writeableBitmap.Lock())
            {
                for (int i = 0; i < height; i++)
                {
                    Marshal.Copy(pixels, i * width * 4, lockedFramebuffer.Address + i * lockedFramebuffer.RowBytes, width * 4);
                }
            }
            writeableBitmap.Save(_options.Screenshot);
        }
        Console.WriteLine($"saved {_options.Screenshot} ({width}x{height})");
        if (_options.Curves || _options.Window)
        {
            string text = Path.ChangeExtension(_options.Screenshot, ".window.png");
            using RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap(new PixelSize((int)base.Bounds.Width, (int)base.Bounds.Height));
            renderTargetBitmap.Render(this);
            renderTargetBitmap.Save(text);
            Console.WriteLine("saved " + text);
        }
        Close();
    }
}
