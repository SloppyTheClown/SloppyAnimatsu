# Command-line tool

`SloppyAnimatsu.Cli` (`SloppyAnimatsu.Cli.exe` in the Windows package, `./SloppyAnimatsu.Cli` in the Linux one)
reads, checks, converts and edits scenes without the viewer. It is the batch counterpart of the
viewer's menus: every edit writes a **new** file, so the source scene is never changed.

    SloppyAnimatsu.Cli --version
    SloppyAnimatsu.Cli --help
    SloppyAnimatsu.Cli info "C:\scenes\character.slopa"

Results go to standard output, errors to standard error; the exit code is 0 on success and 1 on
failure. A failure the command did not expect (for example a target path it may not write) prints
`error: <exception>: <message>` with the stack trace and also exits 1, instead of leaving a Windows
error dialog open. Quote paths with spaces. On Linux, `install.sh` also puts a `sloppyanimatsu` launcher
for the viewer in `~/.local/bin`; the CLI is run from the unpacked folder.

Every run also writes its own log file (the last three are kept, 4 MiB each): the command line,
what was printed, and on an unexpected failure the full stack trace of the exception.
[getting-started.md](getting-started.md#where-the-files-live) says where the folder is and how to
move it.

## Inspecting a scene

| Command | Prints |
|-|-|
| `info <file or folder>...` | the file's version, size, atom/class/struct counts and objects per class |
| `tree <file.slopa> [depth]` | the object hierarchy with the pre-order indices the other commands take |
| `dump <file.slopa> <index or name>...` | every member value of the chosen objects |
| `schema <file or folder>...` | the merged class and struct schema as Markdown |
| `scene <file.slopa>` | a typed summary: characters, meshes, cameras, IK chains, animations |
| `poses <file.slopa>` | stored and evaluated poses against the saved `LTM`s, and how many joints fall inside each bound mesh |
| `animinfo <file.slopa> <animation>...` | one animation's stacks, key times, tracks, tangent modes and space-switched keys |
| `audio <file.slopa>` | audio objects, entries and events, and what plays at the scene time |
| `camera <file.slopa> [frame]` | the scene's cameras, and every animated camera's FOV track and value at the frame |
| `blendshape <file.slopa> [frame] [--animation=name]` | blendshape targets, controllers and channels, and their mixed values at the frame |
| `fbx <file.fbx> [depth] [filter]` | an FBX node tree, optionally filtered by a node's first property |

## Checking files

These print a verdict and make no files:

| Command | Checks |
|-|-|
| `roundtrip <file or folder>...` | reads and re-writes every scene and compares the bytes with the original |
| `fbxcheck <ours.fbx> <reference.fbx>` | models, geometry, skins, bind poses, curves and materials of two FBX files, structurally |
| `fbxcompare <file.slopa> <baked.fbx> [animation...]` | the evaluated local rotations and world positions per frame against a reference FBX bake |
| `skincheck <file.slopa> <file.fbx>` | the bind-time joint and mesh world matrices against the FBX skin clusters |
| `tangentcheck <file or folder>...` | how many of the recomputed tangents match the stored ones, per channel and tangent mode |
| `ikkeycheck <file or folder>...` | at every IK key, the chain end against the controller and the `RefArray` against the joint keys |
| `rfkeycheck <file or folder>...` | the reverse-foot `RefArray` model against the samples |

`roundtrip` is the everyday one: run it after any scripted edit to prove the file reads back
byte-exactly.

## Scene lifecycle

| Command | Does |
|-|-|
| `new <out.slopa> [--frame-rate=n]` | writes the empty scene File > New makes (eight cameras, `Default character`, an empty `New Stack`) |
| `convert <in.slopa\|in.akt> <out.slopa\|out.akt> [--akeytsu\|--brotli\|--zlib\|--stored]` | rewrites a scene or settings file in the other format; the output's extension picks the default, and an `.akt` output is the plain `.akt` layout without SloppyAnimatsu's own material data (extra swap textures, texture and Compensate Scale keys, Opaque/Nearest) |
| `prefs <preferences.slopa\|.akt> [--pages] [--page=Class] [--get=Class.Member]... [--set=Class.Member=Value]... [--dock] [--add=path]... [--save] [--save-as=out.slopa]` | prints or edits the preference pages, the recent-scene list and the dock layout; member paths descend into structs and tuples (`OptionsArray[3].Flags`) |
| `shortcuts <shortcuts.slopa\|shortcuts.akt> [--list] [--action=name] [--set=Action=Key]... [--unbind=Action]... [--reset=Action]... [--reset-all] [--defaults] [--save] [--save-as=out.slopa]` | lists, assigns, unbinds and resets the key map; a duplicate key is refused as akeytsu refuses it |
| `layout <preferences.slopa\|.akt> [--context=Rigging\|Animation] [--size=WxH] [--panel=Class] [--set=Class:height]... [--save] [--save-as=out.slopa]` | prints the saved dock geometry and changes one panel's height |
| `views <file.slopa> [--layout=single\|2views\|curveboard\|4views] [--toggle] [--focused=n] [--view=n --camera=Name]... [--save] [--save-as=out.slopa]` | reads and writes the viewport layouts and their per-view cameras |
| `outliner <file.slopa> [--context=rigging\|animation] [--show=name...] [--hide=name...] [--toggle=name...] [--show-all] [--hide-all] [--lock=mesh...] [--unlock=mesh...] [--lock-character] [--unlock-character] [--delete-tree=name...] [--save-as=out.slopa]` | the hierarchy panel's rows and their kind, the interface context, Show/Hide, the skinned-mesh lock and Delete tree object (the named joint, every joint below it, the IK or reverse-foot controller that constrains one of them with its shoe locators, and their tracks; a controller row on its own deletes that controller and clears the joints' constraints; refused for a joint with a locked skinned mesh under it) |
| `timeline <file.slopa> [--animation=name] [--character=name] [--width=w] [--press=x,y] [--to=x,y] [--apply] [--select=i,j] [--ripple] [--row=n]... [--keys] [--save-as=out.slopa]` | the timeline's stack rows and key positions at a control width, and what a press at a point would hit: a key, an audio clip, a fade handle, a stack name, or the frame a scrub lands on. `--apply` replays the drag from `--press` to `--to` - a key move, a ripple retime (`--ripple`) or a fade-handle drag - and saves it |
| `curveboard <file.slopa> [--animation=name] [--character=name] [--stack=name] [--track=name]... [--width=w] [--height=h] [--press=x,y] [--select=x,y]... [--to=x,y] [--apply] [--save-as=out.slopa]` | the Curveboard's curves at a board size, the frame and value of every key, and what a press at a point would hit: a key, a tangent handle or empty space. `--select` adds keys to the selection, `--handles` prints the selected keys' handle ends, and `--apply` replays the drag from `--press` to `--to` - moving the selected key values or setting the pressed handle's slope - and saves it |
| `autosave <scene.slopa> [--dir=folder] [--base=name]` | writes the scene into its next autosave slot (`<base>_autosave_01.slopa` / `_02`, the older one replaced) |

## Rigging and skinning

| Command | Does |
|-|-|
| `joint <scene.slopa> <out.slopa> create --parent=name --name=name [--at=x,y,z]` | creates a joint; `place` is Create Joint's click placement (a world point on the character), `insert --joint=name --name=name`, `delete`/`unparent`/`orient --joint=name...`, `parent --parent=name --joint=name...`, `ltm` |
| `joint ... add-ik --joint=upper --to=lower [--name=name]` | adds an IK controller for the chain between the two joints |
| `joint ... add-reverse-foot --joint=upper --to=lower [--name=name]` | adds a reverse-foot controller and its shoe locators |
| `joint` options | `--aim=x\|z\|y`, `--up=x\|z\|y`, `--aim-negative`, `--up-negative`, `--align=on\|off` set the joint-axis preference; `--character=name` picks the character |
| `restpose <scene.slopa> <out.slopa> <joint> [--character=name] [--translate=x,y,z] [--rotate=x,y,z] [--scale=x,y,z] [--convert\|--keep\|--return]` | edits the joint's reference pose (composed with its current local; the rotation is in degrees about the joint's own X, Y and Z), splitting the bind pose into a custom `__RefPose` on the first edit and moving the IK controllers below the joint with it; `--convert` answers the REFERENCE POSE dialog by rewriting every Anim Bank MRS track's keys and tangents, `--keep` leaves the keys, `--return` puts the previous pose back. With no answer the file keeps the pending state (`RefPose` differs from `RefPoseOld`) - the viewer's dialog then converts it |
| `skin <scene.slopa> <out.slopa> bind [--character=name] [--mesh=name...]` | Bind Skin: captures the reference pose, attaches the meshes and writes the saved bind state (flags, a one-influence rigid weight array; carried smooth weights are kept) |
| `skin <scene.slopa> <out.slopa> unbind` | Unbind Skin: detaches every unlocked skinned mesh to the scene root, keeping its world |
| `skinpaint <scene.slopa> <out.slopa> paint ...` | the Skin Workshop's brushes: `paint --mesh=name --joint=name --at=x,y,z\|--vertex=i [--radius=r] [--mode=add\|aim\|smooth] [--value=v] [--strength=s] [--soft] [--front-only] [--rigid] [--animation=name] [--frame=n]` |
| `skinpaint ... set\|add`, `prune`, `fix`, `flood`, `mirror`, `copy-paste` | a typed weight or delta on selected control points, dropping small influences, averaging over neighbours, the connected component, Mirror Skin, and copying weights between meshes |
| `autorig <scene.slopa> <out.slopa> [--character=name] [--hotpoint=Name:x,y,z]... [--hotpoints=file]` | builds a biped rig from hot points: skeleton, arms' IK controllers, legs' reverse feet, nearest-bone weights and Bind Skin; `--naming=akeytsu\|mixamo\|preferences`, `--spine=1..6`, `--fingers=1..10`, `--phalanges=1..4`, `--no-ik`, `--no-reverse-foot`, `--no-weights`, `--no-bind` |
| `blendshape-add <scene.slopa> <character> <mesh> --target=Name --controller=Name` | adds a blendshape target (zero deltas), a controller on the mesh, a channel and an info entry; `--direction=0..3`, `--attenuation=f`, `--position=f`, `--face=Name` (a face controller and a face storing `--face-values=x,y` pairs, `;`-separated), `--sight=Name` and `--sight-at=x,y,z`, `--context=rigging\|animation`, `--out=out.slopa`. It is how a scene with blendshapes is built where no sample has one |

A hot-point file has one point per line, `Name:x,y,z` or `Name x y z`, with `#` comments; the names
are the AutoRigger's hot points (`Pelvis`, `Neck`, `AnkleLeft`, `ShoulderRight`, `WristLeft`,
`IndexLeft`, `Finger10Right`, ...).

## Import and export

| Command | Does |
|-|-|
| `fbxexport <file.slopa> <out.fbx> [--baked] [--animation=name]... [--character=name]... [--textures=dir]` | writes the scene as binary FBX 7400: models, geometry, skins, bind poses, materials with embedded textures, and the animations either from the stacks' own keys or baked one key per frame |
| `fbximport <file.fbx> <out.slopa> [--no-animations] [--no-skins] [--no-materials] [--textures=dir]` | reads an FBX into a new scene: characters and joints, meshes, skin weights and bind poses, materials and textures, one animation per FBX stack |
| `aknmexport <file.slopa> <out.aknm> [--character=name] [--animation=name]... [--all-animations]` | writes a character's rig and selected animations as an `.aknm` |
| `aknmimport <scene.slopa> <file.aknm> <out.slopa> [--character=name] [--root] [--force]` | copies an `.aknm`'s animations onto a character, pairing controllers by name |
| `aktimport <scene.slopa> <file.slopa> <out.slopa> [--force]` | merges another scene's characters (with their animations), materials and textures; a different version number is refused unless `--force`; the scene's `FBXPath` follows the imported file's folder |
| `retarget <scene.slopa> <source character> <target character> <out.slopa> [--animation=name\|dir/name]... [--root] [--match=source=target]... [--method=auto\|reference\|local\|world] [--cache=dir] [--no-cache]` | copies the source's animations onto the target, under a `Retargeted From <source>` folder, converting every key; `--match` adds name pairs the automatic `_L`/`_R` matching misses; `--method` picks how a key's delta is read: `local` (the default) keeps the node's own frame; `world` conjugates it by the two nodes' reference-pose world rotations and turns its translation, which is exact only where the poses agree; `reference` handles different reference poses (the conjugated rotation, the translation kept in the source's frame and scaled by the ratio alone, and the per-node reference-pose scale normalized to preserve volume) - rotations are not yet corrected per key and per-node scale is not preserved; `auto` picks `reference` when the two characters' matched reference poses differ and `local` when they coincide. The command prints the size ratio of the two characters (from the matched joints' reference-pose world positions); `--cache` writes the pair's name-pair cache somewhere other than the `Documents/SloppyAnimatsu/Retargeting` folder and `--no-cache` skips it (a folder that is not writable is only a warning) |

## Examples

    # Check that every scene in a folder reads back byte-exactly
    SloppyAnimatsu.Cli roundtrip "D:\scenes"

    # An FBX comes in, a scene goes out
    SloppyAnimatsu.Cli fbximport model.fbx converted.slopa

    # Every animation, baked, for a game engine
    SloppyAnimatsu.Cli fbxexport scene.slopa scene.fbx --baked

    # Merge a second character into a scene and retarget one animation onto it
    SloppyAnimatsu.Cli aktimport base.slopa hero.slopa merged.slopa
    SloppyAnimatsu.Cli retarget merged.slopa Hero Villain merged-retargeted.slopa --animation=Walk

    # AutoRigger from a hot-point file, then check the result
    SloppyAnimatsu.Cli autorig unrigged.slopa rigged.slopa --hotpoints=points.txt
    SloppyAnimatsu.Cli roundtrip rigged.slopa

    # Weights without the viewer
    SloppyAnimatsu.Cli skinpaint scene.slopa out.slopa set --mesh=Body --joint=UpperArm_L --vertex=12 --value=0.5

    # Find the timeline position of an animation's keys, then drag one with the pointer rules
    SloppyAnimatsu.Cli timeline scene.slopa --animation=Run_3 --row=0 --keys
    SloppyAnimatsu.Cli timeline scene.slopa --animation=Run_3 --press=215.8,29 --to=255.4,29 --apply --save-as=out.slopa

    # Change a preference and a key binding in copies of the settings files
    SloppyAnimatsu.Cli prefs preferences.slopa --set=CMainPreferences.DefaultFramerate=30 --save-as=preferences-30.slopa
    SloppyAnimatsu.Cli shortcuts shortcuts.slopa --set=Quit=F9 --save-as=shortcuts-f9.slopa

## Scripting the viewer

`SloppyAnimatsu.Viewer` takes options that open a scene, choose a character, animation and frame, select
and pose controllers, run Anim Bank, rig and skin commands, and save - so a script can produce a
posed scene or an image. With `--screenshot` it renders one frame, writes the image and exits;
without it the window stays open at the end of the run.

| Option | Effect |
|-|-|
| `[file.slopa]` | the scene to open (`--new` starts on an empty scene instead) |
| `--character name`, `--animation name\|"(rest pose)"`, `--frame n` | what to show |
| `--preferences file.slopa`, `--shortcuts file.slopa` | use other settings files than the user's |
| `--layout single\|2views\|curveboard\|4views`, `--view name\|perspective` | the viewport layout and the focused view's camera |
| `--context rigging\|animation` | the interface context (`rigging` opens the rest pose entry) |
| `--select a,b,...` | selects controllers of the character; the last is the current object |
| `--rotate x\|y\|z:degrees`, `--move x\|y\|z:units`, `--scale x\|y\|z\|u:factor` | keys the selection at the frame as one gizmo drag would |
| `--set tx\|ty\|tz\|rx\|ry\|rz\|sx\|sy\|sz:value` | types one Spinner value for the selection at the frame |
| `--keep-children` | those drags (and Mirror Pose) keep the edited object's direct children in place |
| `--resolve convert\|keep\|return` | answers the reference-pose dialog |
| `--bank action` | runs an Anim Bank, Stack, key, rig or skin command, as its menu item would (repeatable) |
| `--hide name`, `--show name`, `--lock mesh`, `--unlock mesh`, `--show-all`, `--hide-all` | the hierarchy panel's commands, names are 3D objects |
| `--screenshot out.png`, `--window` | writes one frame (and the whole window as `out.window.png`) and exits |
| `--save-as out.slopa` | saves the result after all the actions |

The `--bank` actions are the viewer's menu commands: `new-animation`, `copy-animation`,
`delete-animation`, `rename-animation:name`, `new-stack`, `duplicate-stack`, `delete-stack`,
`stack-up`, `stack-down`, `select-stack:name`, `cycle`, `enabled`, `additive`, `opacity:0..1`,
`select-keys:i,j,...`, `copy-keys`, `paste-keys`, `paste-keys-after`, `mirror-keys`, `mirror-pose`,
`cycle-step`, `cycle-side`, `add-fade-in`, `add-fade-out`, `remove-fade-in`, `remove-fade-out`,
`change-space:name|parent`, `toggle-spaceable`, `add-camera[:name]`, `key:n`, `frame:n`,
`fov:degrees`, `undo`, `redo`, and the rig and skin commands `new-joint[:name]`,
`place-joint[:name]@x,y,z`, `place-insert[:name]@x,y,z`, `insert-joint[:name]`, `delete-joints`,
`orient-joints`, `parent-joints`, `unparent-joints`, `reparent:name`, `add-ik`, `add-reverse-foot`,
`bind-skin`, `unbind-skin`, `snap-ltm`, `skin-mesh:name`, `skin-joint:name`,
`skin-select:all|x,y,z[,radius]`, `skin-dab:x,y,z[,radius]`, `skin-set:weight`, `skin-prune`,
`skin-fix`, `skin-flood`, `skin-mirror`, `skin-copy`, `skin-paste`, `skin-scan:x,y,z[,radius]`,
`skin-hide`, `skin-show-all`, `skin-lock:none|all|+joint;-joint`, `skin-frontback:on|off`,
`skin-scanner:on|off`.

Examples:

    # A 4-view render of one animation at frame 10, with the whole window
    SloppyAnimatsu.Viewer scene.slopa --character=Dup --animation=Walk --frame=10 \
        --layout=4views --view=Front --screenshot walk.png --window

    # Pose two joints and save the scene, without showing the result
    SloppyAnimatsu.Viewer scene.slopa --select UpperArm_L,LowerArm_L --rotate z:20 --save-as posed.slopa

    # Build an IK controller on a chain and save
    SloppyAnimatsu.Viewer scene.slopa --context rigging --select UpperLeg_L,LowerLeg_L \
        --bank add-ik --save-as ik.slopa

## Exit codes

`0` success, `1` a failure the command reported on standard error. `--version` and `--help` exit 0;
no arguments exit 1 and print the usage.
