// RunBankAction: the --bank <action>[:<arg>] dispatcher used by scripted runs and the tests. Every
// case maps to one bank/key/skin operation in the other partial files.
//
// The contract: return true when the action ran, false when it is unknown, malformed or not valid in
// the current state (RefuseBankAction prints "--bank: cannot run ..." for the last case). A scripted
// run stops at the first false.
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
    /// <summary>
    /// Reports an action that is not valid in the current document state and returns false. The
    /// scripted runs rely on this being the message the action dispatcher always printed.
    /// </summary>
    private static bool RefuseBankAction(string action)
    {
        Console.Error.WriteLine("--bank: cannot run '" + action + "' here");
        return false;
    }


    private bool RunBankAction(string action)
    {
        _rigEditFailed = false;
        string text = action.Split(':', 2)[0];
        string arg = (action.Contains(':') ? action.Substring(text.Length + 1) : null);
        AnimStack stack = _timeline.ActiveStack;
        switch (text)
        {
        case "new-animation":
            NewAnimation();
            break;
        case "copy-animation":
            CopyAnimation();
            break;
        case "delete-animation":
            DeleteAnimation();
            break;
        case "rename-animation":
            if (arg != null)
            {
                Animation animation2 = _animation;
                if (animation2 != null)
                {
                    RunBankEdit(delegate(PoseEditSession s)
                    {
                        s.Rename(animation2, arg);
                    });
                    break;
                }
            }
            return RefuseBankAction(action);
        case "select-animation":
        {
            // What picking an animation in the bank does: it becomes the timeline's animation, so the
            // bank actions (stacks, mixer, keys) have one to work on. A scene saved in Rigging has
            // no CurrentAnimation until this runs.
            if (arg == null || _character == null)
            {
                return RefuseBankAction(action);
            }
            Animation found = _character.Animations.FirstOrDefault((Animation a) => a.Name == arg || AnimationPath(a) == arg);
            if (found == null)
            {
                Console.Error.WriteLine("--bank: no animation '" + arg + "'");
                return false;
            }
            SelectAnimation(found, null);
            break;
        }
        case "new-stack":
            NewStack();
            break;
        case "duplicate-stack":
            DuplicateStack();
            break;
        case "delete-stack":
            DeleteStack();
            break;
        case "stack-up":
            MoveStack(-1);
            break;
        case "stack-to":
        {
            if (stack == null || !int.TryParse(arg, CultureInfo.InvariantCulture, out var result3))
            {
                return RefuseBankAction(action);
            }
            MoveStackTo(stack, result3);
            break;
        }
        case "new-folder":
            NewFolder();
            break;
        case "select-folder":
        {
            if (arg == null)
            {
                return RefuseBankAction(action);
            }
            Folder folder = BankFolder(arg);
            if (folder == null)
            {
                Console.Error.WriteLine("--bank: no folder '" + arg + "'");
                return false;
            }
            ShowBankFolder(folder);
            break;
        }
        case "rename-folder":
            if (arg != null)
            {
                Folder bankFolder = _bankFolder;
                if (bankFolder != null)
                {
                    RunBankEdit(delegate(PoseEditSession s)
                    {
                        s.Rename(bankFolder, arg);
                    });
                    break;
                }
            }
            return RefuseBankAction(action);
        case "delete-folder":
            DeleteFolder();
            break;
        case "move-to-folder":
        {
            if (arg == null)
            {
                return RefuseBankAction(action);
            }
            SceneObject sceneObject = (SceneObject)(((object)_bankFolder) ?? ((object)_animation));
            SceneObject sceneObject2 = ((!(arg == "/")) ? BankFolder(arg) : _character?.AnimBank);
            if (sceneObject == null || sceneObject2 == null)
            {
                Console.Error.WriteLine("--bank: cannot move to '" + arg + "'");
                return false;
            }
            MoveToFolder([sceneObject], sceneObject2);
            break;
        }
        case "stack-down":
            MoveStack(1);
            break;
        case "rename-stack":
            if (arg == null || stack == null)
            {
                return RefuseBankAction(action);
            }
            RunBankEdit(delegate(PoseEditSession s)
            {
                s.Rename(stack, arg);
            });
            break;
        case "cycle":
            if (stack == null)
            {
                return RefuseBankAction(action);
            }
            RunBankEdit(delegate(PoseEditSession s)
            {
                s.ToggleStackFlag(stack, 1u);
            });
            break;
        case "enabled":
            if (stack == null)
            {
                return RefuseBankAction(action);
            }
            RunBankEdit(delegate(PoseEditSession s)
            {
                s.ToggleStackFlag(stack, 2u);
            });
            break;
        case "additive":
            if (stack == null)
            {
                return RefuseBankAction(action);
            }
            RunBankEdit(delegate(PoseEditSession s)
            {
                s.ToggleStackFlag(stack, 8u);
            });
            break;
        case "opacity":
        {
            if (stack == null || !float.TryParse(arg, CultureInfo.InvariantCulture, out var result6))
            {
                return RefuseBankAction(action);
            }
            RunBankEdit(delegate(PoseEditSession s)
            {
                s.SetStackOpacity(stack, result6);
            });
            break;
        }
        case "select-keys":
            if (arg == null || stack == null)
            {
                return RefuseBankAction(action);
            }
            _timeline.Select(stack, from k in arg.Split(',')
                select int.Parse(k, CultureInfo.InvariantCulture));
            _status.Text = $"Selected keys {arg} of '{stack.Name}'.";
            break;
        case "select-stack":
            if (arg != null)
            {
                Animation animation = _animation;
                if (animation != null)
                {
                    AnimStack animStack = animation.Stacks.FirstOrDefault((AnimStack s) => s.Name == arg);
                    if (animStack == null)
                    {
                        Console.Error.WriteLine("--bank: no stack '" + arg + "'");
                        return false;
                    }
                    _timeline.Select(animStack, Array.Empty<int>());
                    Session?.SetCurrentStack(animStack);
                    _status.Text = "Selected stack '" + animStack.Name + "'.";
                    break;
                }
            }
            return RefuseBankAction(action);
        case "select-stacks":
        {
            // The Mixer's multi-selection, which Merge and Delete take.
            if (arg == null || _animation == null)
            {
                return RefuseBankAction(action);
            }
            var chosen = new List<AnimStack>();
            foreach (string name in arg.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                AnimStack found = _animation.Stacks.FirstOrDefault((AnimStack s) => s.Name == name);
                if (found == null)
                {
                    Console.Error.WriteLine("--bank: no stack '" + name + "'");
                    return false;
                }
                chosen.Add(found);
            }
            if (chosen.Count == 0)
            {
                return RefuseBankAction(action);
            }
            _animation.SelectStacks(chosen);
            RefreshMixer();
            _timeline.Select(chosen[^1], Array.Empty<int>());
            break;
        }
        case "merge":
            if (!MergeSelectedStacks())
            {
                Console.Error.WriteLine("--bank merge refused: " + _status.Text);
                return false;
            }
            break;
        case "extract":
            if (!ExtractSelectedChannels())
            {
                Console.Error.WriteLine("--bank extract refused: " + _status.Text);
                return false;
            }
            break;
        case "audio":
            if (arg == null || !AddAudioStack(arg))
            {
                Console.Error.WriteLine("--bank audio refused: " + _status.Text);
                return false;
            }
            break;
        case "copy-keys":
            CopyKeys();
            break;
        case "paste-keys":
            PasteKeys(after: false);
            break;
        case "paste-keys-after":
            PasteKeys(after: true);
            break;
        case "add-fade-in":
            FadeKeys(fadeIn: true, 5);
            break;
        case "add-fade-out":
            FadeKeys(fadeIn: false, 5);
            break;
        case "remove-fade-in":
            FadeKeys(fadeIn: true, 0);
            break;
        case "remove-fade-out":
            FadeKeys(fadeIn: false, 0);
            break;
        case "change-space":
        {
            if (arg == null)
            {
                return RefuseBankAction(action);
            }
            Object3D object3D = null;
            if (arg != "parent")
            {
                object3D = _character?.Model.Scene.Characters.SelectMany((Character c) => new Object3D[1] { c }.Concat(c.Descendants<Object3D>())).FirstOrDefault((Object3D o) => o.Name == arg);
                if (object3D == null)
                {
                    Console.Error.WriteLine("--bank: no object '" + arg + "'");
                    return false;
                }
            }
            ChangeSpace(object3D);
            break;
        }
        case "toggle-spaceable":
            ToggleSpaceable();
            break;
        case "mirror-keys":
            MirrorKeys();
            break;
        case "mirror-pose":
            MirrorPose();
            break;
        case "cycle-step":
            CycleStep();
            break;
        case "cycle-side":
            CycleSide();
            break;
        case "add-camera":
            AddAnimatedCamera(arg);
            break;
        case "frame":
        {
            if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result5))
            {
                return RefuseBankAction(action);
            }
            _play.IsChecked = false;
            SetTime(result5);
            _status.Text = $"Frame {_time:0.##}.";
            break;
        }
        case "key":
        {
            if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result4))
            {
                return RefuseBankAction(action);
            }
            CreateKey(result4);
            break;
        }
        case "fov":
        {
            if (!float.TryParse(arg, CultureInfo.InvariantCulture, out var result2))
            {
                return RefuseBankAction(action);
            }
            if (SetFovValue(result2) == 0)
            {
                Console.Error.WriteLine("--bank fov refused: " + _status.Text);
                return false;
            }
            break;
        }
        case "select":
        {
            if (arg == null)
            {
                return RefuseBankAction(action);
            }
            bool flag2 = true;
            string[] array = arg.Split(',');
            foreach (string name2 in array)
            {
                Object3D object3D3 = _character?.Controllers.FirstOrDefault((Object3D c) => c.Name == name2);
                if (object3D3 == null)
                {
                    Console.Error.WriteLine("--bank select: no controller '" + name2 + "'");
                    return false;
                }
                Select(object3D3, !flag2);
                flag2 = false;
            }
            break;
        }
        case "context":
            if (arg is not ("rigging" or "animation"))
            {
                return RefuseBankAction(action);
            }
            SetContext(arg == "rigging" ? InterfaceContext.Rigging : InterfaceContext.Animation);
            break;
        case "move":
        {
            // move:<axis 0-2>=<amount>[x<steps>]: one committed translate drag of the selection, as
            // --move does; with steps, the drag is applied in that many increments like a mouse drag.
            int moveSteps = 1;
            if (arg != null && arg.IndexOf('x') is int xAt and > 0)
            {
                if (!int.TryParse(arg[(xAt + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out moveSteps) || moveSteps < 1)
                {
                    return RefuseBankAction(action);
                }
                arg = arg[..xAt];
            }
            string[] parts = arg?.Split('=') ?? [];
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int moveAxis)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float moveAmount))
            {
                return RefuseBankAction(action);
            }
            SetMode(GizmoMode.Translate);
            if (!((IViewportEditor)this).BeginDrag(moveAxis, Vector2.Zero))
            {
                Console.Error.WriteLine("--bank move refused: " + _status.Text);
                return false;
            }
            for (int step = 1; step <= moveSteps; step++)
            {
                _drag!.Translate(moveAxis, moveAmount * step / moveSteps);
                if (moveSteps > 1) Apply();
            }
            ((IViewportEditor)this).EndDrag(commit: true);
            break;
        }
        case "delete-objects":
        {
            // Whole objects by name (characters or meshes), as the hierarchy panel's Delete does.
            if (arg == null || _pose == null)
            {
                return RefuseBankAction(action);
            }
            Scene deleteScene = _pose.Model.Scene;
            var deleted = new List<Object3D>();
            foreach (string name in arg.Split(','))
            {
                Object3D? found = (Object3D?)deleteScene.Characters.FirstOrDefault(c => c.Name == name)
                    ?? deleteScene.Meshes.FirstOrDefault(m => m.Name == name);
                if (found == null)
                {
                    Console.Error.WriteLine("--bank delete-objects: no character or mesh '" + name + "'");
                    return false;
                }
                deleted.Add(found);
            }
            DeleteSceneObjects(deleted);
            break;
        }
        case "undo":
            Undo();
            break;
        case "redo":
            Redo();
            break;
        case "new-joint":
            if (_character == null)
            {
                return RefuseBankAction(action);
            }
            CreateJointNow(arg ?? RigEditor.UniqueControllerName(_character, "Joint"));
            if (_rigEditFailed)
            {
                return false;
            }
            break;
        case "insert-joint":
            if (_character == null || !(_selected is Joint joint))
            {
                return RefuseBankAction(action);
            }
            InsertJointNow(joint, arg ?? RigEditor.UniqueControllerName(_character, "Joint"));
            if (_rigEditFailed)
            {
                return false;
            }
            break;
        case "delete-joints":
            DeleteJoints();
            if (_rigEditFailed)
            {
                return false;
            }
            break;
        case "orient-joints":
            OrientJoints();
            if (_rigEditFailed)
            {
                return false;
            }
            break;
        case "parent-joints":
            ParentJoints();
            if (_rigEditFailed)
            {
                return false;
            }
            break;
        case "unparent-joints":
            UnparentJoints();
            if (_rigEditFailed)
            {
                return false;
            }
            break;
        case "reparent":
        {
            if (arg == null)
            {
                return RefuseBankAction(action);
            }
            Object3D selected = _selected;
            if (selected == null)
            {
                Console.Error.WriteLine("--bank reparent: select the object to move first");
                return false;
            }
            Object3D object3D2 = _pose?.Model.Scene.Characters.SelectMany((Character c) => new Object3D[1] { c }.Concat(c.Descendants<Object3D>())).Concat(_pose.Model.Scene.Meshes).FirstOrDefault((Object3D o) => o.Name == arg);
            if (object3D2 == null)
            {
                Console.Error.WriteLine("--bank reparent: no object '" + arg + "'");
                return false;
            }
            ReparentFromOutliner(selected, object3D2, OutlinerDropPlace.Inside);
            if (_rigEditFailed)
            {
                return false;
            }
            break;
        }
        case "add-ik":
            AddIk();
            if (_rigEditFailed)
            {
                return false;
            }
            break;
        case "add-reverse-foot":
            AddReverseFoot();
            if (_rigEditFailed)
            {
                return false;
            }
            break;
        case "bind-skin":
            // A script binds the way a user would: Paint & Check first, then U.
            if (_character != null && !CharacterBank.IsPaintAndCheck(_character))
            {
                SelectBankRow(_character, CharacterBank.Rows(_character)[^1]);
            }
            BindSkin();
            if (_rigEditFailed)
            {
                return false;
            }
            break;
        case "unbind-skin":
            UnbindSkin();
            if (_rigEditFailed)
            {
                return false;
            }
            break;
        case "snap-ltm":
            RunRigEdit("Snapped LTM from the rig pose", delegate(RigEditor e)
            {
                e.SyncLtm();
                return (Object3D?)null;
            });
            if (_rigEditFailed)
            {
                return false;
            }
            break;
        case "place-joint":
        {
            if (_character == null)
            {
                return RefuseBankAction(action);
            }
            if (!TryPlacementArgument(arg, out string name3, out Vector3 point2))
            {
                return false;
            }
            if (PlaceAt(new PendingPlacement(Insert: false, null, name3 ?? RigEditor.UniqueControllerName(_character, "Joint")), point2) == null)
            {
                return false;
            }
            break;
        }
        case "place-insert":
        {
            if (!(_selected is Joint target))
            {
                return RefuseBankAction(action);
            }
            if (!TryPlacementArgument(arg, out string name, out Vector3 point))
            {
                return false;
            }
            if (PlaceAt(new PendingPlacement(Insert: true, target, name ?? RigEditor.UniqueControllerName(_character, "Joint")), point) == null)
            {
                return false;
            }
            break;
        }
        case "skin-mesh":
        {
            if (arg == null)
            {
                return RefuseBankAction(action);
            }
            Mesh mesh = FindSkinMesh(arg);
            if (mesh == null)
            {
                return false;
            }
            SelectSkinMesh(mesh);
            break;
        }
        case "skin-joint":
        {
            if (arg == null)
            {
                return RefuseBankAction(action);
            }
            if (_skin == null || _character == null)
            {
                Console.Error.WriteLine("--bank: pick a skin mesh first");
                return false;
            }
            int num = _character.Joints.ToList().FindIndex((Joint j) => j.Name == arg);
            if (num < 0)
            {
                Console.Error.WriteLine("--bank skin-joint: no joint '" + arg + "'");
                return false;
            }
            SelectPaintedJoint(num);
            break;
        }
        case "skin-select":
            if (arg == null)
            {
                return RefuseBankAction(action);
            }
            if (!ScriptedSkinSelect(arg))
            {
                return false;
            }
            break;
        case "skin-dab":
            if (arg == null)
            {
                return RefuseBankAction(action);
            }
            if (!ScriptedSkinDab(arg))
            {
                return false;
            }
            break;
        case "skin-set":
        {
            if (arg == null || !float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            {
                return RefuseBankAction(action);
            }
            SetSkinWeight(result);
            break;
        }
        case "skin-prune":
            RunSkinEdit("Pruned the weights", (SkinPaintSession s) => s.Prune());
            break;
        case "skin-fix":
            RunSkinEdit("Fixed the selected weights", (SkinPaintSession s) => s.Fix());
            break;
        case "skin-flood":
            RunSkinEdit("Flooded the selection", (SkinPaintSession s) => s.Flood());
            break;
        case "skin-mirror":
            RunSkinEdit("Mirrored the skin", (SkinPaintSession s) => s.Mirror());
            break;
        case "skin-copy":
            CopySkinWeights();
            break;
        case "skin-paste":
            PasteSkinWeights();
            break;
        case "skin-scan":
            if (arg == null)
            {
                return RefuseBankAction(action);
            }
            if (!ScriptedSkinScan(arg))
            {
                return false;
            }
            break;
        case "skin-hide":
            HideSkinVertices();
            break;
        case "skin-show-all":
            ShowAllSkinVertices();
            break;
        case "skin-lock":
            if (arg == null)
            {
                return RefuseBankAction(action);
            }
            if (!ScriptedSkinLock(arg))
            {
                return false;
            }
            break;
        case "skin-frontback":
        {
            if (arg == null)
            {
                return RefuseBankAction(action);
            }
            if (_skin == null)
            {
                Console.Error.WriteLine("--bank skin-frontback: pick a skin mesh first");
                return false;
            }
            bool scanner;
            switch (arg)
            {
            case "on":
            case "true":
            case "1":
                scanner = true;
                break;
            default:
                scanner = false;
                break;
            }
            bool flag = scanner;
            _skin.FrontBackPaint = flag;
            _skin.FrontBackSelect = flag;
            _skinFrontBack.IsChecked = flag;
            _status.Text = (flag ? "Front/back: paint through the mesh." : "Front/back: front-facing points only.");
            break;
        }
        case "skin-scanner":
        {
            if (arg == null)
            {
                return RefuseBankAction(action);
            }
            if (_skin == null)
            {
                Console.Error.WriteLine("--bank skin-scanner: pick a skin mesh first");
                return false;
            }
            SkinPaintSession skin = _skin;
            bool scanner;
            switch (arg)
            {
            case "on":
            case "true":
            case "1":
                scanner = true;
                break;
            default:
                scanner = false;
                break;
            }
            skin.Scanner = scanner;
            _skinScanner.IsChecked = _skin.Scanner;
            break;
        }
        default:
            return RefuseBankAction(action);
        }
        return true;
    }
}
