// Rig editing: the RigEditor transaction wrapper, joint create/insert/delete/orient/parent, IK and
// reverse-foot creation, Bind/Unbind Skin, the AutoRigger hot points and dialog, and retargeting.
//
// Every structural edit goes through RunRigEdit so it is one undo record; the operations themselves
// live in SloppyAnimatsu.Core RigEditor.
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
    /// <param name="anyContext">The edit is allowed in the Animation context too (Delete tree object,
    /// which the app runs in either context).</param>
    private RigEditor? RigEditorFor(string action, bool anyContext = false)
    {
        if (_pose == null || _character == null)
        {
            _status.Text = "Open a scene with a character first.";
            return null;
        }
        if (!anyContext && (_pose.Model.Scene.Context != InterfaceContext.Rigging || _animation != null))
        {
            _status.Text = "Switch to the Rigging context to " + action + ".";
            return null;
        }
        return new RigEditor(_character);
    }

    /// <summary>
    /// Runs one structural rig edit as a single undo step and returns the object to select, or null
    /// when the editor refused. A thrown edit restores the snapshot; _rigEditFailed records that the
    /// refusal came from the edit rather than from an empty selection.
    /// </summary>
    private Object3D? RunRigEdit(string description, Func<RigEditor, Object3D?> edit, string? action = null, bool anyContext = false)
    {
        _rigEditFailed = false;
        RigEditor rigEditor = RigEditorFor(action ?? description, anyContext);
        if (rigEditor == null)
        {
            _rigEditFailed = true;
            return null;
        }
        Character character = _character;
        EditSnapshot editSnapshot = EditSnapshot.TakeRig(character, _pose.Model.Scene);
        Object3D object3D;
        try
        {
            object3D = edit(rigEditor);
        }
        catch (Exception ex)
        {
            editSnapshot.Restore();
            _pose.ReloadRestPose(character);
            RefreshRigStructure();
            _rigEditFailed = true;
            _status.Text = "Rig edit failed: " + ex.Message;
            return null;
        }
        _pose.ReloadRestPose(character);
        _pose.ReloadSkins();
        _history.Push(new EditRecord(description, editSnapshot, EditSnapshot.TakeRig(character, _pose.Model.Scene)));
        _dirty = true;
        UpdateTitle();
        RefreshRigStructure();
        if (object3D != null)
        {
            Select(object3D);
        }
        AfterKeyChange();
        _status.Text = description + ".";
        return object3D;
    }

    private void RefreshRigStructure()
    {
        if (_pose == null)
        {
            return;
        }
        _selectedAll.RemoveAll((Object3D c) => _character == null || _pose.Evaluator(_character).IndexOf(c) < 0);
        _outliner.Build(_pose.Model.Scene);
        RefreshPicker();
        UpdateSelection();
        Apply();
        RefreshSkinPanel();
    }

    private bool RigStructureChanged()
    {
        if (_character == null || _pose == null)
        {
            return false;
        }
        return _pickerControllerCount != _character.Controllers.Count;
    }

    private async Task NewJoint()
    {
        if (RigEditorFor("create a joint") != null && _character != null)
        {
            string name = await TextPromptDialog.Ask(this, "New joint", "Name", RigEditor.UniqueControllerName(_character, "Joint"));
            if (name != null)
            {
                BeginPlacement(new PendingPlacement(Insert: false, null, name));
            }
        }
    }

    /// <summary>
    /// Insert Joint: a new joint halfway along the bone between two selected joints, where one is
    /// the other's direct parent; the child is re-parented under the new joint and keeps its world
    /// placement. One selected joint uses the bone to its parent joint.
    /// </summary>
    private async Task InsertJoint()
    {
        Joint? child = InsertJointChild(out string? problem);
        if (child == null)
        {
            _status.Text = problem;
            return;
        }
        if (RigEditorFor("insert a joint") == null || _character == null) return;
        string name = await TextPromptDialog.Ask(this, "Insert joint",
            $"Name of the joint between {((SceneObject?)child.Parent)?.Name} and {child.Name}",
            RigEditor.UniqueControllerName(_character, "Joint"));
        if (name != null) InsertJointNow(child, name);
    }

    /// <summary>The lower joint of the bone Insert Joint splits, or null with the reason.</summary>
    private Joint? InsertJointChild(out string? problem)
    {
        problem = null;
        var joints = _selectedAll.OfType<Joint>().Distinct().ToList();
        if (joints.Count == 2)
        {
            if (ReferenceEquals(joints[1].Parent, joints[0])) return joints[1];
            if (ReferenceEquals(joints[0].Parent, joints[1])) return joints[0];
            problem = "Insert Joint needs two joints connected by a bone: one must be the other's direct parent.";
            return null;
        }
        if (joints.Count == 1 && joints[0].Parent is Joint) return joints[0];
        problem = "Select the two joints at the ends of a bone (a parent and its child) to insert a joint between them.";
        return null;
    }

    private void CreateJointNow(string name)
    {
        if (_character != null)
        {
            Object3D parent = ((_selected is Joint joint) ? joint : ((_selected?.Parent as Object3D) ?? _character));
            RunRigEdit("Created joint " + name, (RigEditor e) => e.CreateJoint(parent, name, Matrix4x4.Identity));
        }
    }

    private void InsertJointNow(Joint joint, string name)
    {
        RunRigEdit("Inserted joint " + name, (RigEditor e) => e.InsertJoint(joint, name));
    }

    private void BeginPlacement(PendingPlacement placement)
    {
        _placement = placement;
        _status.Text = (placement.Insert ? $"Click in the viewport to place '{placement.Name}' above {placement.Target.Name}; Esc cancels." : ("Click in the viewport to place '" + placement.Name + "'; Esc cancels."));
    }

    private Vector3? PointUnder(Vector2 at)
    {
        if (_pose == null || _character == null)
        {
            return null;
        }
        IViewCamera activeView = _viewport.Renderer.ActiveView;
        Vector2 editSize = _viewport.EditSize;
        SceneHit? sceneHit = ScenePicking.Pick(_pose, activeView, editSize, at);
        if (sceneHit.HasValue)
        {
            SceneHit valueOrDefault = sceneHit.GetValueOrDefault();
            return valueOrDefault.Point;
        }
        Matrix4x4 matrix4x = ((_selected != null) ? _pose.WorldOf(_selected) : _pose.WorldOf(_character));
        ScenePicking.Ray? ray = ScenePicking.Through(activeView, editSize, at);
        Vector3? result;
        if (ray.HasValue)
        {
            ScenePicking.Ray valueOrDefault2 = ray.GetValueOrDefault();
            result = ScenePicking.OnPlane(valueOrDefault2, matrix4x.Translation, activeView.Forward);
        }
        else
        {
            result = null;
        }
        return result;
    }

    private Object3D? PlaceAt(PendingPlacement placement, Vector3 point)
    {
        Object3D selected = _selected;
        Object3D object3D = RunRigEdit(placement.Insert ? ("Inserted joint " + placement.Name + " above " + placement.Target.Name) : ("Created joint " + placement.Name), (RigEditor e) => placement.Insert ? e.PlaceInsertedJoint(placement.Target, point, placement.Name) : e.PlaceJoint(selected, point, placement.Name, _jointAlignment));
        if (object3D != null)
        {
            _placement = null;
        }
        return object3D;
    }

    private void DeleteJoints()
    {
        List<Joint> joints = _selectedAll.OfType<Joint>().ToList();
        if (joints.Count == 0)
        {
            _status.Text = "Select the joint(s) to delete.";
            return;
        }
        string text = ((joints.Count == 1) ? ("joint " + joints[0].Name) : $"{joints.Count} joints");
        RunRigEdit("Deleted " + text, delegate(RigEditor e)
        {
            e.DeleteJoints(joints);
            return (Object3D?)null;
        });
    }

    private void OrientJoints()
    {
        List<Joint> joints = _selectedAll.OfType<Joint>().ToList();
        if (joints.Count == 0)
        {
            _status.Text = "Select the joint(s) to orient.";
            return;
        }
        int oriented = 0;
        RunRigEdit($"Oriented {joints.Count} joint(s)", delegate(RigEditor e)
        {
            oriented = e.OrientJoints(joints, _jointAlignment);
            return (Object3D?)null;
        });
        if (!_rigEditFailed)
        {
            _status.Text = $"Oriented {oriented} joint(s) (aim {JointAlignment.NameOf(_jointAlignment.Aim)}, up {JointAlignment.NameOf(_jointAlignment.Up)}).";
        }
    }

    private void ParentJoints()
    {
        List<Joint> list = _selectedAll.OfType<Joint>().ToList();
        Object3D selected = _selected;
        Joint parent = selected as Joint;
        if (parent == null || list.Count < 2)
        {
            _status.Text = "Select the joints to parent and the new parent last.";
            return;
        }
        List<Joint> objects = list.Where((Joint j) => j != parent).ToList();
        int parented = 0;
        RunRigEdit($"Parented {objects.Count} joint(s) to {parent.Name}", delegate(RigEditor e)
        {
            parented = e.Parent(objects, parent);
            return parent;
        }, "parent joints");
        if (!_rigEditFailed)
        {
            _status.Text = $"Parented {parented} joint(s) to {parent.Name}.";
        }
    }

    private void UnparentJoints()
    {
        List<Joint> joints = _selectedAll.OfType<Joint>().ToList();
        if (joints.Count == 0)
        {
            _status.Text = "Select the joint(s) to unparent.";
            return;
        }
        int count = 0;
        RunRigEdit($"Unparented {joints.Count} joint(s)", delegate(RigEditor e)
        {
            count = e.Unparent(joints);
            return (Object3D?)null;
        }, "unparent joints");
        if (!_rigEditFailed)
        {
            _status.Text = $"Unparented {count} joint(s) to the character.";
        }
    }

    private void ReparentFromOutliner(Object3D dragged, Object3D target, OutlinerDropPlace place)
    {
        if (_pose == null || _character == null)
        {
            return;
        }
        if (MoveMeshFromOutliner(dragged, target, place))
        {
            return;
        }
        Object3D parent = OutlinerDrag.NewParent(target, place);
        if (!ReferenceEquals(parent, target) && !ReferenceEquals(dragged, target)
            && OutlinerDrag.Verdict(dragged, target, place) == OutlinerDropVerdict.Move)
        {
            bool beside = _pose.Evaluator(_character).IndexOf(dragged) >= 0;
            bool after = place == OutlinerDropPlace.After;
            bool reorder = ReferenceEquals(dragged.Parent, parent);
            RunRigEdit((reorder ? "Moved " : "Reparented ") + dragged.Name + (after ? " below " : " above ") + target.Name,
                delegate(RigEditor e)
                {
                    e.PlaceBeside(dragged, target, after);
                    return beside ? dragged : null;
                }, reorder ? null : "reparent objects");
            return;
        }
        if (!ReferenceEquals(parent, target))
        {
            target = parent;
        }
        switch (OutlinerDrag.Verdict(dragged, target))
        {
        case OutlinerDropVerdict.Character:
            _status.Text = "A character stays at the scene root.";
            break;
        case OutlinerDropVerdict.OtherCharacter:
            _status.Text = "Drag a row onto another row of the same character.";
            break;
        case OutlinerDropVerdict.Cycle:
            _status.Text = "An object cannot be dropped on its own descendant.";
            break;
        case OutlinerDropVerdict.Constrained:
            _status.Text = dragged.Name + " cannot be reparented.";
            break;
        case OutlinerDropVerdict.LockedMesh:
        case OutlinerDropVerdict.SceneRoot:
            _status.Text = DropRefusal(dragged, OutlinerDrag.Verdict(dragged, target));
            break;
        case OutlinerDropVerdict.Self:
            break;
        case OutlinerDropVerdict.Move:
        {
            bool controller = _pose.Evaluator(_character).IndexOf(dragged) >= 0;
            RunRigEdit("Reparented " + dragged.Name + " to " + target.Name, delegate(RigEditor e)
            {
                e.Reparent([dragged], target);
                return controller ? dragged : null;
            }, "reparent objects");
            break;
        }
        }
    }

    private static string DropRefusal(Object3D dragged, OutlinerDropVerdict verdict) => verdict switch
    {
        OutlinerDropVerdict.LockedMesh => dragged.Name + " is locked to its skin: unlock it before moving it out of its character.",
        OutlinerDropVerdict.SceneRoot => "Only a mesh can be dragged out of its character.",
        OutlinerDropVerdict.Character => "A character stays at the scene root.",
        OutlinerDropVerdict.Cycle => "An object cannot be dropped on its own descendant.",
        OutlinerDropVerdict.Constrained => dragged.Name + " cannot be reparented.",
        _ => "",
    };

    /// <summary>
    /// The hierarchy panel's mesh drag that changes character: out to the scene root (a drop beside
    /// a top-level row, or on the empty space below the rows, <paramref name="target"/> null) or into
    /// another character. A skinned mesh is unbound on the way out. Returns false when the drop is
    /// not such a move (not a mesh, or a move inside one character), so the caller handles it as a
    /// hierarchy reparent.
    /// </summary>
    private bool MoveMeshFromOutliner(Object3D dragged, Object3D? target, OutlinerDropPlace place)
    {
        if (dragged is not Mesh mesh || _pose == null || _character == null)
        {
            if (target == null && _pose != null) _status.Text = DropRefusal(dragged, OutlinerDrag.RootVerdict(dragged));
            return target == null;
        }
        if (target != null && ReferenceEquals(dragged, target)) return true;
        Object3D? parent = target == null ? null : OutlinerDrag.DropParent(mesh, target, place);
        Character? from = OutlinerDrag.OwnerOf(mesh);
        Character? to = parent == null ? null : OutlinerDrag.OwnerOf(parent);
        bool toRoot = parent == null;
        // A move inside one character (or between free meshes) is the ordinary hierarchy reparent.
        if (!toRoot && ReferenceEquals(from, to)) return false;
        var verdict = toRoot ? OutlinerDrag.RootVerdict(mesh) : OutlinerDrag.Verdict(mesh, target!, place);
        if (verdict != OutlinerDropVerdict.Move)
        {
            if (verdict != OutlinerDropVerdict.Self) _status.Text = DropRefusal(mesh, verdict);
            return true;
        }
        bool skinned = mesh.Skinned && !ReferenceEquals(from, to);
        Object3D? beside = target != null && place != OutlinerDropPlace.Inside ? target : null;
        string where = toRoot ? "the scene" : to!.Name ?? "the character";
        RunRigEdit((skinned ? "Unbound and moved " : "Moved ") + mesh.Name + " to " + where, delegate
        {
            SkinEditor.MoveMesh(mesh, parent, beside, place == OutlinerDropPlace.After);
            return null;
        }, "move meshes between characters");
        if (!_rigEditFailed && to != null)
        {
            _status.Text = (skinned ? "Unbound " : "Moved ") + mesh.Name + " into " + to.Name
                           + ": Bind Skin there to skin it to that rig.";
        }
        return true;
    }

    private void AddIk()
    {
        List<Joint> joints = _selectedAll.OfType<Joint>().ToList();
        if (joints.Count != 2)
        {
            _status.Text = $"Select the upper joint and then the lower one, then Add IK ({joints.Count} of {_selectedAll.Count} selected object(s) are joints).";
            return;
        }
        ControllerIK created = null;
        RunRigEdit("Add IK", delegate(RigEditor e)
        {
            try
            {
                created = e.AddIK(joints[0], joints[1]);
            }
            catch (InvalidOperationException)
            {
                created = e.AddIK(joints[1], joints[0]);
            }
            return created;
        });
        if (created != null)
        {
            _status.Text = $"Added {created.Name} for {joints[0].Name}..{joints[1].Name}.";
        }
    }

    private void AddReverseFoot()
    {
        List<Joint> joints = _selectedAll.OfType<Joint>().ToList();
        if (joints.Count != 2)
        {
            _status.Text = "Select the upper leg joint and then the lower one, then Add reverse foot.";
            return;
        }
        ControllerReverseFoot created = null;
        RunRigEdit("Add reverse foot", delegate(RigEditor e)
        {
            try
            {
                created = e.AddReverseFoot(joints[0], joints[1]);
            }
            catch (InvalidOperationException)
            {
                created = e.AddReverseFoot(joints[1], joints[0]);
            }
            return created;
        });
        if (created != null)
        {
            _status.Text = $"Added {created.Name} for {joints[0].Name}..{joints[1].Name}.";
        }
    }

    /// <summary>
    /// The joint-structure tools (create/insert/orient/parent/unparent joints, IK, reverse foot)
    /// work only while the Bind Pose or Build Pose row is current (transform modes 2 and 4,
    ///). The menu, the RIG WORKSHOP
    /// buttons and the shortcuts ask this; scripted actions do not.
    /// </summary>
    private bool JointEditAllowed()
    {
        if (_character == null || CharacterBank.IsBindPoseCurrent(_character)) return true;
        _rigEditFailed = true;
        _status.Text = "Joint tools work in the Bind Pose: pick it under " + _character.Name + " in the CHAR BANK.";
        return false;
    }

    private void BindSkin()
    {
        // The Bind Skin gate wants transform mode 5, no pose row current
        if (_character != null && !CharacterBank.IsPaintAndCheck(_character))
        {
            _rigEditFailed = true;
            _status.Text = "Bind Skin works in Paint & Check: pick it under " + _character.Name + " in the CHAR BANK.";
            return;
        }
        int bound = 0;
        RunRigEdit("Bind skin", delegate
        {
            bound = new SkinEditor(_character).Bind();
            return (Object3D?)null;
        });
        if (!_rigEditFailed)
        {
            _status.Text = $"Bound {bound} mesh(es).";
        }
    }

    private void UnbindSkin()
    {
        int unbound = 0;
        RunRigEdit("Unbind skin", delegate
        {
            unbound = SkinEditor.Unbind(_pose.Model.Scene);
            return (Object3D?)null;
        });
        if (!_rigEditFailed)
        {
            _status.Text = $"Unbound {unbound} mesh(es).";
        }
    }

    private void OpenAutoRigger()
    {
        if (_pose == null || _character == null)
        {
            _status.Text = "Select the character to auto-rig first.";
            return;
        }
        if (_character.Joints.Count != 0)
        {
            _status.Text = $"{_character.Name} already has {_character.Joints.Count} joints: the " + "AutoRigger only builds a rig on an unrigged character.";
            return;
        }
        AutoRiggerDialog autoRiggerDialog = _autoRiggerDialog;
        if (autoRiggerDialog != null)
        {
            autoRiggerDialog.Activate();
            return;
        }
        AutoRiggerSession autoRiggerSession = (_autoRigger = new AutoRiggerSession(_character, _character.Meshes));
        _autoRiggerDialog = new AutoRiggerDialog(autoRiggerSession, _pose.Model.Scene.Meshes, ArmHotPoint, BuildAutoRigger, RefreshHotPointMarkers, AutoRiggerClosed);
        RefreshHotPointMarkers();
        _autoRiggerDialog.Show(this);
        _status.Text = (autoRiggerSession.CanBuild ? ("AutoRigger: " + autoRiggerSession.Character.Name + " is ready.") : ("AutoRigger: " + autoRiggerSession.Refusal));
    }

    private void ArmHotPoint(HotPoint point)
    {
        _hotPoint = point;
        RefreshHotPointMarkers();
        _status.Text = "Click the character in the viewport to place " + AutoRiggerParts.Describe(point) + "; Escape cancels.";
    }

    private void AutoRiggerClosed()
    {
        _autoRiggerDialog = null;
        _autoRigger = null;
        _hotPoint = null;
        RefreshHotPointMarkers();
    }

    private void RefreshHotPointMarkers()
    {
        List<SceneMarker> list = new List<SceneMarker>();
        AutoRiggerSession autoRigger = _autoRigger;
        if (autoRigger != null)
        {
            foreach (var (hotPoint2, position) in autoRigger.Points.OrderBy((KeyValuePair<HotPoint, Vector3> pair) => pair.Key))
            {
                list.Add(new SceneMarker(position, (hotPoint2 == _hotPoint) ? new Vector3(1f, 0.78f, 0.2f) : new Vector3(0.35f, 0.85f, 1f)));
            }
        }
        if (_character != null)
        {
            foreach (BlendshapeSightRow item in BlendshapeSession.SightsOf(_character))
            {
                list.Add(new SceneMarker(item.Position, new Vector3(0.55f, 0.95f, 0.75f)));
            }
        }
        _viewport.Renderer.Markers = list;
        _viewport.InvalidateVisual();
    }

    private AutoRigResult? BuildAutoRigger()
    {
        if (_pose != null)
        {
            AutoRiggerSession autoRigger = _autoRigger;
            if (autoRigger != null)
            {
                if (!autoRigger.CanBuild)
                {
                    _status.Text = autoRigger.Refusal;
                    return null;
                }
                Character character = autoRigger.Character;
                EditSnapshot editSnapshot = EditSnapshot.TakeRig(character, _pose.Model.Scene);
                AutoRigResult autoRigResult;
                try
                {
                    autoRigResult = autoRigger.Build();
                }
                catch (Exception ex)
                {
                    editSnapshot.Restore();
                    _pose.ReloadRestPose(character);
                    RefreshRigStructure();
                    _status.Text = "AutoRigger failed: " + ex.Message;
                    return null;
                }
                _pose.ReloadRestPose(character);
                _pose.ReloadSkins();
                _history.Push(new EditRecord("AutoRigger", editSnapshot, EditSnapshot.TakeRig(character, _pose.Model.Scene)));
                _dirty = true;
                UpdateTitle();
                RefreshRigStructure();
                AfterKeyChange();
                _status.Text = $"AutoRigger: {autoRigResult.Joints} joints, {autoRigResult.Controllers} controllers, {autoRigResult.Meshes} mesh(es), {autoRigResult.WeightedPoints} weighted point(s).";
                return autoRigResult;
            }
        }
        return null;
    }

    private async Task OpenRetarget()
    {
        if (_pose == null)
        {
            _status.Text = "Open a scene before retargeting animations.";
            return;
        }
        RetargetDialog open = _retargetDialog;
        if (open != null)
        {
            open.Activate();
            return;
        }
        object obj = _preferences?.Get("CAnimBankPreferences", "SkipRetargetingAlignRefPosesWindow");
        RetargetSession session = new RetargetSession(skipAdvice: obj is bool && (bool)obj, model: _pose.Model, cacheDirectory: RetargetingPreferences.DefaultDirectory,
            store: new RetargetMappingStore(RetargetMappingStore.DefaultPath));
        if (session.Characters.Count < 2)
        {
            _status.Text = "At least two characters with joints are needed on the scene in order to retarget.";
            return;
        }
        // The pair picked last in this run of the viewer wins over the default (first and last
        // character, or the scene's last used matching) while both characters are still offered.
        if (session.Characters.FirstOrDefault(c => c.Name == _lastRetargetSource) is { } lastSource
            && session.Characters.FirstOrDefault(c => c.Name == _lastRetargetTarget) is { } lastTarget
            && lastSource != lastTarget)
        {
            session.Source = lastSource;
            session.Target = lastTarget;
        }
        RetargetDialog dialog = (_retargetDialog = new RetargetDialog(session, progress => RunRetarget(session, progress), delegate
        {
            _retargetDialog = null;
            _lastRetargetSource = session.Source?.Name;
            _lastRetargetTarget = session.Target?.Name;
        }));
        _status.Text = session.Refusal ?? session.Summary;
        // Modeless: the scene stays usable (select, frame, compare joints) while the grid is edited.
        dialog.Show(this);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Runs the transfer on the UI thread one animation stack at a time, yielding to the
    /// dispatcher between stacks so the dialog's progress bar repaints; a failure part-way
    /// restores the snapshot, a finished run is one undo step.
    /// </summary>
    private async Task<RetargetingResult?> RunRetarget(RetargetSession session, IProgress<RetargetProgress> progress)
    {
        if (_pose != null && !ReferenceEquals(_pose.Model, session.Model))
        {
            _status.Text = "The retarget window belongs to another scene; reopen it on this one.";
            return null;
        }
        if (_pose != null)
        {
            Character target = session.Target;
            if (target != null)
            {
                Folder animBank = target.AnimBank;
                List<SceneObject> list = new List<SceneObject>();
                if (animBank != null)
                {
                    list.Add(animBank);
                }
                list.Add(target);
                Character source = session.Source;
                if (source != null && source != target)
                {
                    list.Add(source);
                }
                EditSnapshot editSnapshot = EditSnapshot.TakeShallow(target, list);
                RetargetingResult retargetingResult;
                try
                {
                    var run = new RetargetRun();
                    _status.Text = "Retargeting...";
                    foreach (var step in session.RunSteps(run))
                    {
                        progress.Report(step);
                        _status.Text = $"Retargeting {step.Done}/{step.Total} stack(s)...";
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
                    }
                    retargetingResult = run.Result!;
                }
                catch (Exception ex)
                {
                    editSnapshot.Restore();
                    _status.Text = "Retarget failed: " + ex.Message;
                    return null;
                }
                _history.Push(new EditRecord("Retarget animations", editSnapshot, EditSnapshot.TakeShallow(target, list)));
                _dirty = true;
                UpdateTitle();
                SelectCharacter(target, retargetingResult.Animations.LastOrDefault()?.Name, null);
                _status.Text = $"Retargeted {retargetingResult.Animations.Count} animation(s) from '{retargetingResult.Map.Source.Name}' onto '{target.Name}' through {retargetingResult.Map.Pairs.Count} controller pair(s) into '{retargetingResult.Destination.Name}' (ratio {retargetingResult.Ratio:0.####})" + ((retargetingResult.Warnings.Count > 0) ? $"; {retargetingResult.Warnings.Count} warning(s)." : ".");
                return retargetingResult;
            }
        }
        return null;
    }
}
