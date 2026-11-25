// The IViewportEditor implementation: the viewport calls these on pick, drag, free drag, right click
// and hover. This is the bridge between pointer input and the rig/key/skin operations the other
// partials provide.
//
// BeginDrag/Drag/EndDrag move one gizmo axis; BeginFreeDrag/FreeDrag paint (skin or a blendshape
// sight). Pick and HoverAt must leave an armed placement or hot point alone until it is placed.
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
    void IViewportEditor.Pick(Vector2 at, bool toggle)
    {
        AutoRiggerSession autoRigger = _autoRigger;
        if (autoRigger != null)
        {
            HotPoint? hotPoint = _hotPoint;
            if (hotPoint.HasValue)
            {
                HotPoint valueOrDefault = hotPoint.GetValueOrDefault();
                Vector3? vector = PointUnder(at);
                if (vector.HasValue)
                {
                    Vector3 valueOrDefault2 = vector.GetValueOrDefault();
                    autoRigger.Place(valueOrDefault, valueOrDefault2);
                    _hotPoint = null;
                    RefreshHotPointMarkers();
                    _autoRiggerDialog?.SetArmed(null);
                    _autoRiggerDialog?.SetPrompt(null);
                    _autoRiggerDialog?.Refresh();
                    _status.Text = $"Placed {AutoRiggerParts.Describe(valueOrDefault)} at {valueOrDefault2.X:0.##}, {valueOrDefault2.Y:0.##}, {valueOrDefault2.Z:0.##}.";
                    return;
                }
                _status.Text = "Click over the character or in the view to place the hot point.";
                return;
            }
        }
        PendingPlacement placement = _placement;
        if ((object)placement != null)
        {
            Vector3? vector = PointUnder(at);
            if (vector.HasValue)
            {
                Vector3 valueOrDefault3 = vector.GetValueOrDefault();
                PlaceAt(placement, valueOrDefault3);
                return;
            }
            _status.Text = "Click over the character or in the view to place the joint.";
        }
        else if (_pose?.Character != null)
        {
            IReadOnlyList<Object3D> controllers = _pose.Evaluator(_pose.Character).Controllers;
            Matrix4x4[] controllerWorlds = _pose.ControllerWorlds;
            IViewCamera activeView = _viewport.Renderer.ActiveView;
            Vector2 editSize = _viewport.EditSize;
            Object3D object3D = ViewportPick.At(activeView, editSize, _pose.Character, _pose.Evaluator(_pose.Character), controllerWorlds, at, PickTolerance(), ViewportPickable);
            if (object3D == null && !toggle && ApplyBlendshapeZoneAt(at))
            {
                return;
            }
            // A click on the skinned mesh picks the joint with the most weight on the nearest vertex.
            object3D ??= JointUnder(at);
            if (object3D != null || !toggle)
            {
                Select(object3D, toggle);
            }
        }
    }

    /// <summary>
    /// The joint that weighs most on the vertex nearest to the mesh point under <paramref name="at"/>
    /// (of the hit triangle's corners, at their skinned positions); null off the mesh, on an
    /// unskinned mesh, or when that joint cannot be picked.
    /// </summary>
    private Object3D? JointUnder(Vector2 at)
    {
        if (_pose?.Character is not { } character)
        {
            return null;
        }
        if (ScenePicking.Pick(_pose, _viewport.Renderer.ActiveView, _viewport.EditSize, at) is not { Triangle: >= 0 } hit)
        {
            return null;
        }
        Mesh mesh = hit.Mesh;
        if (!mesh.Skinned || !ReferenceEquals(mesh.BindPose?.Character, character))
        {
            return null;
        }
        var triangle = mesh.Triangles().ElementAtOrDefault(hit.Triangle);
        uint[] corners = mesh.CornerControlPoints;
        if (triangle == default || corners.Length == 0)
        {
            return null;
        }
        Vector3[] positions = new MeshSkin(mesh).Positions(_pose.ControllerWorlds);
        int nearest = -1;
        float best = float.MaxValue;
        foreach (uint corner in new[] { triangle.A, triangle.B, triangle.C })
        {
            if (corner >= corners.Length || corners[corner] >= positions.Length)
            {
                continue;
            }
            int point = (int)corners[corner];
            float distance = Vector3.DistanceSquared(positions[point], hit.Point);
            if (distance < best)
            {
                best = distance;
                nearest = point;
            }
        }
        if (nearest < 0)
        {
            return null;
        }
        VertexWeight[][] smooth = mesh.SkinWeights;
        VertexWeight[] weights = nearest < smooth.Length && smooth[nearest].Length > 0 ? smooth[nearest]
            : nearest < mesh.RigidSkinWeights.Length ? mesh.RigidSkinWeights[nearest] : [];
        if (weights.Length == 0)
        {
            return null;
        }
        int bone = weights.MaxBy(w => w.Weight).Bone;
        var joints = character.Joints;
        if (bone < 0 || bone >= joints.Count)
        {
            return null;
        }
        Object3D joint = joints[bone];
        return ViewportPickable(joint) ? joint : null;
    }

    /// <summary>
    /// The reverse-foot shoe locators are seen and moved only in the Rigging context with the bind
    /// pose current (no animation); elsewhere they are neither drawn, picked nor picker rows.
    /// </summary>
    private bool ShoeLocatorsShown =>
        _pose != null && _animation == null && _pose.Model.Scene.Context == InterfaceContext.Rigging;

    /// <summary>Only the four sole pivots are ever shown; the skeleton pivots never are.</summary>
    /// <summary>The joint pick tolerance for the world-sized joint dots, measured at the character's root.</summary>
    private float PickTolerance()
    {
        Matrix4x4[] worlds = _pose!.ControllerWorlds;
        Vector3 at = worlds.Length > 0 ? worlds[0].Translation : Vector3.Zero;
        return DecoratorSizes.For(_pose.Model.Scene).PickTolerance(_viewport.Renderer.ActiveView, _viewport.EditSize, at);
    }

    private bool ViewportPickable(Object3D o) =>
        Outliner.IsPickable(o) && (o is not ReverseFootLocator l || (ShoeLocatorsShown && l.IsSolePivot));

    /// <summary>Drops the shoe locators from the selection once they are no longer shown.</summary>
    private void DropHiddenShoeLocators()
    {
        bool Hidden(Object3D o) => o is ReverseFootLocator l && (!ShoeLocatorsShown || !l.IsSolePivot);
        if (!_selectedAll.Any(Hidden)) return;
        _selectedAll.RemoveAll(Hidden);
        UpdateSelection();
    }

    /// <summary>
    /// A right click on a controller: the app runs
    /// the IK lock toggle. On a camera it passes the camera's looker target
    /// instead, so right-clicking a camera toggles the lock of its
    /// target's track.
    /// </summary>
    void IViewportEditor.RightClick(Vector2 at)
    {
        AnimStack? stack = _timeline.ActiveStack;
        if (_pose?.Character != null && stack == null)
        {
            // Rigging: a camera rig's lock is SloppyAnimatsu's runtime flag (no key to carry it).
            Object3D? hit = ViewportPick.At(_viewport.Renderer.ActiveView, _viewport.EditSize, _pose.Character, _pose.Evaluator(_pose.Character), _pose.ControllerWorlds, at, PickTolerance(), ViewportPickable);
            CameraTarget? rigTarget = hit switch
            {
                CameraAnimated { Target: { } t } => t,
                CameraTarget t => t,
                _ => null,
            };
            if (rigTarget == null)
            {
                _status.Text = "The IK lock is written into the current stack; pick an animation first.";
                return;
            }
            rigTarget.RestLocked = !rigTarget.RestLocked;
            Apply();
            _status.Text = rigTarget.RestLocked
                ? $"'{rigTarget.Name}' is locked: moving the camera now re-aims it at the target."
                : $"'{rigTarget.Name}' is unlocked: the target follows the camera again.";
            return;
        }
        if (_pose?.Character == null || stack == null)
        {
            _status.Text = "The IK lock is written into the current stack; pick an animation first.";
            return;
        }
        PoseEditSession? session = Session;
        if (session == null)
        {
            return;
        }
        IReadOnlyList<Object3D> controllers = _pose.Evaluator(_pose.Character).Controllers;
        Object3D? clicked = ViewportPick.At(_viewport.Renderer.ActiveView, _viewport.EditSize, _pose.Character, _pose.Evaluator(_pose.Character), _pose.ControllerWorlds, at, PickTolerance(), ViewportPickable);
        if (clicked == null)
        {
            _status.Text = "Right-click an IK controller or a looker target to toggle its IK lock.";
            return;
        }
        Object3D target = clicked is CameraAnimated { Target: { } looker } ? looker : clicked;
        if (target is not (CameraTarget or ControllerIK))
        {
            _status.Text = $"'{clicked.Name}' is not driven by an IK controller, so it has no IK lock.";
            return;
        }
        var selection = new List<Object3D>();
        if (_selectedAll.Contains(clicked) || _selectedAll.Contains(target))
        {
            selection.AddRange(_selectedAll);
        }
        int frame = (int)MathF.Round(_time);
        int[] keys = [.. _timeline.SelectedKeys];
        int written = 0;
        bool now = false;
        string? refusal = null;
        RunKeyEdit(delegate
        {
            written = session.ToggleIkLock(stack, clicked, selection, frame, keys, out now, out refusal);
        }, "Could not toggle the IK lock: ");
        // The undo record's description lands in the status line, so say what changed afterwards.
        if (written > 0)
        {
            _status.Text = $"IK lock {(now ? "on" : "off")} for {written} key(s) of '{target.Name}'.";
        }
        else if (refusal != null)
        {
            _status.Text = refusal;
        }
    }

    bool IViewportEditor.BeginDrag(int axis, Vector2 at)
    {
        if (_selected != null)
        {
            PoseEditSession session = Session;
            if (session != null)
            {
                session.Snap = Snap;
                // A looker target cannot be dragged while the view looks through its camera: the drag
                // would turn that very view.
                if (_selected is CameraTarget looker && CameraEditor.ForbidsTargetDrag(looker, FocusedState.Animated))
                {
                    _status.Text = $"{looker.Name} belongs to {looker.Camera!.Name}, the camera this view looks through; switch the view away from it to drag its target.";
                    return false;
                }
                _play.IsChecked = false;
                int num = (int)MathF.Round(_time);
                TransformDrag transformDrag;
                try
                {
                    Object3D[] controllers = _selectedAll.Where((Object3D c) => session.Evaluator.IndexOf(c) >= 0).ToArray();
                    string reason = null;
                    if (IsPlacedObject(_selected))
                    {
                        if (InPaintAndCheck)
                        {
                            _status.Text = "Paint & Check poses controllers only; leave it to place the object.";
                            return false;
                        }
                        // Scaling an object that is not a controller is a rig change: Rigging only.
                        transformDrag = session.BeginPlacement(_selected!, allowScale: _animation == null);
                    }
                    else if (InPaintAndCheck)
                    {
                        // Paint & Check poses the current object only, in a pose that is not saved.
                        transformDrag = session.BeginCheckPose(controllers[^1], out reason);
                    }
                    else if (_animation == null && BankRowRefusesRestEdit(out string bankReason))
                    {
                        _status.Text = bankReason;
                        return false;
                    }
                    else if (_animation == null)
                    {
                        transformDrag = session.BeginRestPose(controllers, out reason);
                    }
                    else
                    {
                        SetTime(num);
                        transformDrag = session.Begin(_animation, controllers, num, out reason);
                    }
                    if (transformDrag == null)
                    {
                        _status.Text = reason;
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _status.Text = "Cannot edit: " + ex.Message;
                    return false;
                }
                TransformGizmo gizmo = _viewport.Renderer.Gizmo;
                _drag = transformDrag;
                // The drag's space is the gizmo's; a Screen drag captures the view's axes now, so
                // orbiting the view mid-drag cannot swing the handles being dragged.
                _drag.Space = gizmo.Space;
                _drag.SpaceFrame = gizmo.SpaceFrame(_viewport.Renderer.ActiveView);
                _dragAxis = axis;
                _dragLast = at;
                _dragAngle = 0f;
                _dragGizmo = new TransformGizmo
                {
                    Visible = true,
                    Mode = gizmo.Mode,
                    Space = gizmo.Space,
                    Frame = gizmo.Frame,
                    ParentFrame = gizmo.ParentFrame,
                    CanTranslate = gizmo.CanTranslate,
                    CanScale = gizmo.CanScale
                };
                TextBlock status = _status;
                string text;
                if (transformDrag is EditDrag editDrag)
                {
                    if (editDrag.CreatedKey)
                    {
                        text = $"Created key {editDrag.KeyIndex} of '{editDrag.Stack.Name}' at frame {num}.";
                    }
                    else
                    {
                        EditDrag editDrag2 = editDrag;
                        text = $"Editing key {editDrag2.KeyIndex} of '{editDrag2.Stack.Name}'.";
                    }
                }
                else if (transformDrag is GroupDrag groupDrag)
                {
                    TransformDrag lead = groupDrag.Lead;
                    if (lead is EditDrag editDrag3)
                    {
                        text = $"{(editDrag3.CreatedKey ? "Keying" : "Editing")} {groupDrag.Members.Count} controllers at frame {num}.";
                    }
                    else
                    {
                        GroupDrag groupDrag2 = groupDrag;
                        text = $"Editing {groupDrag2.Members.Count} controllers in '{session.RestPose.EditedPose?.Name}'.";
                    }
                }
                else if (transformDrag is PlacementDrag)
                {
                    text = $"Placing {transformDrag.Controller.Name} in the scene.";
                }
                else
                {
                    text = $"Editing {transformDrag.Controller.Name} in '{session.RestPose.EditedPose?.Name}'.";
                }
                status.Text = text;
                AfterKeyChange();
                return true;
            }
        }
        return false;
    }

    void IViewportEditor.Drag(Vector2 from, Vector2 to)
    {
        if (_drag == null || _dragGizmo == null)
        {
            return;
        }
        IViewCamera activeView = _viewport.Renderer.ActiveView;
        Vector2 editSize = _viewport.EditSize;
        try
        {
            if (_dragGizmo.Mode == GizmoMode.Rotate)
            {
                _dragAngle += _dragGizmo.DragAngle(activeView, editSize, _dragAxis, _dragLast, to);
                _dragLast = to;
                _drag.Rotate(_dragAxis, _dragAngle);
            }
            else if (_dragGizmo.Mode == GizmoMode.Scale)
            {
                _drag.Scale(_dragAxis, _dragGizmo.DragScale(activeView, editSize, _dragAxis, to - from));
            }
            else if (TransformGizmo.IsPlane(_dragAxis))
            {
                Vector2 moved = _dragGizmo.DragPlane(activeView, editSize, _dragAxis, to - from);
                _drag.TranslatePlane(TransformGizmo.PlaneNormal(_dragAxis), moved.X, moved.Y);
            }
            else
            {
                _drag.Translate(_dragAxis, _dragGizmo.DragDistance(activeView, editSize, _dragAxis, to - from));
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Edit failed: " + ex.Message;
        }
        Apply();
    }

    void IViewportEditor.EndDrag(bool commit)
    {
        TransformDrag drag = _drag;
        if (drag != null)
        {
            _drag = null;
            _dragGizmo = null;
            if (commit)
            {
                _status.Text = drag.Commit().Description;
                // A Paint & Check pose is never saved, so it leaves the document clean.
                if (drag is not CheckPoseDrag) _dirty = true;
            }
            else
            {
                drag.Cancel();
                _status.Text = "Edit cancelled.";
            }
            UpdateTitle();
            AfterKeyChange();
        }
    }

    bool IViewportEditor.BeginFreeDrag(Vector2 at)
    {
        if (_skinPaint.IsChecked == true && !SkinEditsBlockedByBindPose())
        {
            SkinPaintSession skin = _skin;
            if (skin != null && _pose != null && _character != null)
            {
                IViewCamera activeView = _viewport.Renderer.ActiveView;
                SceneHit? sceneHit = ScenePicking.Pick(_pose, activeView, _viewport.EditSize, at, skin.Mesh)
                    ?? PickSkinMesh(activeView, at);
                skin = _skin ?? skin;
                if (sceneHit.HasValue)
                {
                    SceneHit valueOrDefault = sceneHit.GetValueOrDefault();
                    _skinUndo = EditSnapshot.TakeWeights(_character, [skin.Mesh]);
                    MeshSkin meshSkin = new MeshSkin(skin.Mesh);
                    _skinPositions = meshSkin.Positions(_pose.ControllerWorlds);
                    _skinNormals = meshSkin.Normals(_pose.ControllerWorlds);
                    _skinJointPositions = SkinJointPositions();
                    skin.Eye = activeView.Eye;
                    _skinPlanePoint = valueOrDefault.Point;
                    _skinPlaneNormal = valueOrDefault.Normal;
                    skin.Radius = WorldRadius(activeView, valueOrDefault.Point);
                    DabAt(skin, valueOrDefault.Point);
                    return true;
                }
                return false;
            }
        }
        return false;
    }

    void IViewportEditor.FreeDrag(Vector2 from, Vector2 to)
    {
        SkinPaintSession skin = _skin;
        if (skin == null)
        {
            return;
        }
        IViewCamera activeView = _viewport.Renderer.ActiveView;
        ScenePicking.Ray? ray = ScenePicking.Through(activeView, _viewport.EditSize, to);
        if (ray.HasValue)
        {
            ScenePicking.Ray valueOrDefault = ray.GetValueOrDefault();
            Vector3 point = ScenePicking.OnPlane(valueOrDefault, _skinPlanePoint, _skinPlaneNormal) ?? _skinPlanePoint;
            skin.Radius = WorldRadius(activeView, point);
            DabAt(skin, point);
        }
    }

    void IViewportEditor.FreeDragEnd(bool commit)
    {
        SkinPaintSession skin = _skin;
        if (skin == null)
        {
            _skinUndo = null;
            return;
        }
        skin.EndStroke();
        if (commit)
        {
            EditSnapshot skinUndo = _skinUndo;
            if (skinUndo != null)
            {
                _history.Push(new EditRecord("Painted " + (skin.JointObject?.Name ?? "the weights") + " on " + skin.Mesh.Name, skinUndo, EditSnapshot.TakeWeights(_character, [skin.Mesh])));
            }
            _dirty = true;
            UpdateTitle();
        }
        else
        {
            EditSnapshot skinUndo2 = _skinUndo;
            if (skinUndo2 != null)
            {
                skinUndo2.Restore();
                _status.Text = "Paint cancelled.";
            }
        }
        _skinUndo = null;
        Apply();
        RebuildSkinJoints();
    }
}
