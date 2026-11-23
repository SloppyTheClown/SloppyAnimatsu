# User guide

This is the reference for the viewer's panels, menus and workflows. If you have not run the viewer
yet, read [getting-started.md](getting-started.md) first.

## The viewport

| Action | Input |
|-|-|
| Orbit | left drag |
| Zoom (dolly) | right drag: right/up in, left/down out |
| Pan | middle drag |
| Zoom | mouse wheel |
| Frame the character | F |
| Select | click (Ctrl toggles) |
| Drag a gizmo handle | left drag on it |
| Cancel a drag | Escape |

A press stays a click until the pointer has moved more than 4 DIP, so a slightly shaky click still
selects; past that a left drag moves the camera. A right drag dollies in or out (both axes count, so
an up-right drag is fastest) and a middle drag pans, at once and with no slop. A drag that starts on
a gizmo handle edits instead, and with the Skin Workshop's Paint armed a left drag over the mesh
paints.

The fixed scene cameras (Perspective, Front, Back, Right, Left, Top, Bottom) and Photo are chosen
per view; the orthographic views use the camera's own axis and framed height, and Photo is
letterboxed to the camera's ratio. A fixed camera cannot be orbited. A scene's animated cameras each
get their own window, which follows the animation and its FOV at the current frame.

**Layouts:** F1-F4 switch between the single view, two views, two views with the Curveboard and
four views (**L** toggles between a multi-view layout and the single view). Each layout
remembers its own cameras in the scene, so the choice is saved with it. Clicking a view focuses it
(an orange outline) and the camera keys then apply to that view.

Objects hidden in the Scene panel are not drawn and cannot be picked; an object marked unpickable
in the file is refused as well. Tab (or the context button) switches between the Rigging and Animation
contexts; the choice is stored in the scene, so it reopens in the same context. The
**bottom-right of the status bar** shows the current context and has a **Switch to Animation** /
**Switch to Rigging** button that does the same as Tab.

## Selecting and posing controllers

Click a joint or controller in the viewport, or pick it in the **Picker** panel. The selected
object's local transform shows in the side panel and its track in the active stack. Ctrl-click adds
or removes a controller: the last one selected is the *current object*, which carries the gizmo and
the side panel, while the others show as grey points.

The gizmo poses the current object at the current frame:

- **E** - rotation rings, about the controller's own axes.
- **W** - translation arrows, along its parent's axes (only when its track animates translation).
- **R** - scale handles, along its own axes or all three with the centre triangle (only when its
  track animates scale; dragging a handle by its own length doubles the value, and a component never
  drops below the 0.001 scale floor or changes sign).

The pose follows the drag live. Releasing the mouse keys it (creating a key at the current frame
where there is none) as one undo step; Escape cancels. A drag gives every selected controller the
current object's world change about its pivot, a selected child of a selected parent keeps its local
transform, and controllers that cannot be keyed at that frame are left alone.

**Edit > Keep children fixed** makes a drag leave the edited object's direct children where they are
in the world instead of carrying them along; it is off by default.

### The Spinner (numeric fields)

The side panel's T/R/S fields show the current object's local transform at the current frame, with
rotation as ZYX Euler degrees. Typing a value and pressing Enter (or leaving the field), or using
the steppers, applies that channel to every selected controller, each through its own track and its
own local frame - not the shared gizmo frame - creating a key at the current frame when there is
none. One undo step. A field is disabled when the object's track does not animate that channel; on
the **(rest pose)** entry the fields edit the rest pose instead.

The round window over the viewport is the Spinner's dial: its red (X), green (Y) and blue (Z)
sectors drag the selected controllers along the same axes as the matching gizmo handles, the three
buttons curving round its top set translate, rotate and scale, and its rim drags the window. The disc
is centred in the frame and fills most of it. A sector drag counts only how far you move **up or
down** (up is positive); sideways movement is ignored. With a reverse-foot controller selected, the
rotate-mode sectors also show a foot and a direction icon: a **right drag** on red changes the roll,
on green the twist and on blue the bank (Ctrl for finer), while a left drag still rotates.

The Spinner's small round **space button** in its rim on the right (`Loc` / `Scr` / `Wld`), and the
number keys, choose the transform space the gizmo handles work in:

| Space | Key | Handles |
|-|-|-|
| **Local** (default) | 1 | the controller's own axes; translation follows its parent's |
| **Screen** | 2 | the view's right, up and depth axes |
| **World** | 3 | the scene's axes |

Clicking the space button cycles Local, Screen and World. The handles are drawn in that space, and a
drag turns, moves or scales the controller in those world axes and writes the equivalent local key,
so a world turn of a joint whose parent is rotated still becomes that joint's local rotation. A
scale along a world or screen axis that is not one of the controller's own axes cannot be stored
without shear: the key keeps the scale lengths and the nearest orientation (a uniform scale is
exact). One space applies to the whole viewer and is not saved. Snapping follows the space: World
snaps the resulting world coordinate, Screen the drag distance, and scale the factor.

A rotation ring is drawn whole when its plane faces the view - its axis within about 9° of the view
direction - and as the quarter nearest the camera otherwise. Screen space's depth (blue) ring lies in
the image plane, so it is always drawn whole; its other two keep the same quarters, the red
camera-right ring above the centre and the green camera-up ring to its left, so they do not swap
sides as the camera or the selection moves. Local and World rings keep the quarter that faces your
camera.

The **Snap** switches and steps (off by default; move 0.1, rotate 45 degrees, scale 0.5 in the
shipped preferences) snap the current object's gizmo drag only: rotation snaps the turned angle and
can snap a right angle, move and scale snap the resulting local value, and the other selected
controllers undergo the current object's snapped change.

### The rest pose

The **(rest pose)** entry at the top of the animation list is the Rigging context's pose. Gizmo
drags and typed values edit that pose instead of a key (constrained objects are refused), so moving
a joint inside the bind pose changes the rig without deforming the skin. When an animation or
another character is picked after the rest pose changed, a dialog asks whether to convert the
animations' keys to the new reference pose, keep them as they are, or return to the previous
reference pose.

**Edit > Undo / Redo** (Ctrl+Z / Ctrl+Y) step through the edits in memory. They cover posing, every
key command, skeleton and skin edits, and each rig command is one step.

## The timeline and keys

The timeline under the viewport shows one row per stack of the shown animation, with its keys, and
one row per audio event. Clicking a stack's row makes it the animation's active stack (the Mixer
then shows it); dragging a stack by its name moves it to the gap under the pointer, marked with an
orange line.

| Action | How |
|-|-|
| Set the frame | click or drag in empty timeline space |
| Select a key | click it; Ctrl toggles, Shift adds, Ctrl+A selects all but the end key |
| Move keys | drag the selection by whole frames; Escape cancels |
| Create a key | K (in the active stack, at the current frame) |
| Delete keys | Delete (keeps the other keys' times); Shift+Delete closes the gaps |
| Ripple a key | Alt+drag: it and every later key move together, stopping one frame after the previous key |
| Step a key past its neighbour | Ctrl+Left / Ctrl+Right (each key keeps the gap before it) |
| Change a gap | + / - widen or narrow the gap before each selected key; the Gap box sets it |
| Step one frame | Left / Right |

The hollow **end key** replays key 0: it stays last and cannot be dragged, reordered, deleted or
marked.

The timeline's right-click menu on a key removes or copies the selected keys and adds a **5-frame
fade** in or out (a green ramp with a square handle at its far end, dragged to lengthen or shorten
the fade by whole frames, never below 1; Escape cancels). When a selected key already has a fade-in
(or fade-out), that row reads **Remove Fade-in** (or **Remove Fade-out**) instead.

**Copy and paste:** Ctrl+C copies the selected keys of the selected controllers, Ctrl+V pastes them
onto the selected keys, Ctrl+Shift+V inserts them after the last selected key. IK and reverse-foot
keys carry their pole angle and their IK flag.

**Space Switch** (the Transform window's space button) switches the selected controllers, at the
frames of the selected keys, to one of the spaceable objects or back to the parent, in every stack
that keys those frames; the pose at those frames stays where it is. Which objects are spaceable is
also set in the Transform window.

**Mirror** (in the Transform window) mirrors the selected keys of the selected controllers onto
their symmetric counterparts, found by name (`_L`/`_R`, `Left`/`Right` and the other shipped
patterns), keeping each key's mirrored world position and its space; in the rest pose it mirrors the
pose.

### The Curveboard

The **Curves** toggle in the transport bar shows the curves of the selected controllers' tracks in
the active stack above the timeline, lined up with its frames (rotation in Euler degrees, ZYX). The
channel toggles pick which channels are shown and edited.

Click a key to select it (Shift adds, Ctrl toggles, Ctrl+A selects all, a box drag selects several,
Escape none) and drag the selection up or down to move its values. Selected keys show their in and
out handles; dragging a handle sets its slope and makes that side Custom (a unified key turns both).
The Step, Linear, Spline, Auto, Clamped and IK buttons set the tangent type on the selected keys
(IK only on IK controllers); Unify and Break set or clear the unified bit. The wheel zooms the
values, a middle drag pans them, and F or Fit fits them. Each edit is one undo step.

## The Anim Bank and Mixer

The animation list in the **Bank** panel shows the character's animations, its folders as `Name/`
rows with their contents indented below, and the **(rest pose)** entry. The list's right-click menu
creates an animation (one stack with a track per controller), copies,
deletes and renames the shown one, and manages folders: **New folder**, **Rename folder…** and
**Delete folder** (with everything inside). Dragging an animation or folder in the list drops it
into the folder under the pointer; a folder never goes into itself.

The **Mixer** window has one row per stack (the eye turns it on or off, the bent arrow is
a pose stack and the note an audio one, then the name and the opacity), and the six buttons along
the bottom. A click selects a stack and makes it the active one; Ctrl- and Shift-click select
several (the selection is not saved). **New** makes a stack from the selected controllers,
**Copy** duplicates the selected stacks, **Delete** removes them (the last stack always stays),
**Merge** bakes the selected stacks into one (one selected stack merges with the one below it, and
a stack at 0 opacity is skipped), **Extract** moves the selected objects' channels from the active
stack into a new stack, and **Audio…** adds a WAV file as a new audio stack. The row's eye, the
**On / Cycle / Additive** boxes and the opacity field below the list edit the current stack. The
**Stack** menu (new, duplicate, delete, move up or down, rename) and the stack strip in the
transport bar still work too. **Edit > Add selection to active stack** (H, the hammer) is a toggle:
with the current object not keyed in the active stack it gives the selected controllers a track
there, and with it already keyed it removes their tracks (and their IK or reverse-foot groups) from
the stack.

The Cyclemaker panel's **Cycle Step** appends a mirrored second step to the active stack (the keys without the
loop key are copied, mirrored onto the symmetric counterparts or onto the same object, and the time
array grows by one step). **Cycle Side** mirrors the selected controllers between the two halves of
the cycle, leaving times, marks and fades alone; it needs a cycle stack with an even number of keys
without the loop key and otherwise refuses with a message.

## Audio

An animation's audio stack is a timeline row; each event is a clip as wide as its sound lasts,
labelled with the file name. Click anywhere in a clip to select it, and drag it to retime the event
like a key. Play also plays the audio: the clips under the playhead are mixed on a background thread
(16-bit stereo through `winmm` on Windows, silence on Linux), with the stack's opacity as the volume
and a cycle stack repeating its events with the pose. Scrubbing while playing seeks the audio too.

A scene can embed the decoded sound as headerless 16-bit stereo PCM at 44.1 kHz, so it
plays without its original files; SloppyAnimatsu decodes that form, and a RIFF/WAV
file, from the event's embedded bytes or else from the file next to the scene. An event whose sound
cannot be decoded (an MP3 or OGG stored verbatim by some other tool) is a grey, silent clip.

## Rigging

Switch to the Rigging context with Tab. The RIG ATELIER panel and the rig keys run the skeleton
commands on the rest pose, one undo step each:

| Command | What it does |
|-|-|
| **New joint…** | Asks for a name, then places the joint at the next viewport click: a child of the selected joint, or of the character. The click is projected onto the character's mesh, or onto the plane through the selection facing the view; the joint takes the selected object's world orientation and scale there |
| **Insert joint above…** | Asks for a name and places a joint halfway along the selected joint's bone, keeping its world |
| **Delete selected joints** | Deletes them and their subtrees, remapping skin weights |
| **Orient selected joints** | Orients them per the joint-axis preference |
| **Joint axes** | The aim and up axes (and their negative halves) and *Align on create*; the choice is written to `preferences.slopa` when a preferences file is open |
| **Parent selected joints to the last selected** | Reparents to the current object (constrained joints and cycles refused) |
| **Unparent selected joints** | Moves them to the character |
| **Add IK (two selected joints)** | Creates an IK controller for the chain between the two selected joints, with its chain, constraints, pose slots, rig track and the schema the new classes need |
| **Add reverse foot (two selected joints)** | Creates a reverse-foot controller and its seven shoe locators on the leg |
| **Bind skin / Unbind skin** | Captures the reference pose as the bind pose, attaches the meshes and writes a one-influence rigid weight set (carried smooth weights are kept; a mesh with none gets the rigid set), or detaches every unlocked skinned mesh to the scene root keeping its world |
| **Snap LTM from the rig pose** | Copies the rigging pose into the joints' `LTM` snapshot |
| **AutoRigger…** | Builds a whole biped from hot points (below) |

### AutoRigger

**Rig > AutoRigger…** opens a separate window for the character shown in the Bank/Scene panels. It
works on a character that has **no joints yet** (the AutoRigger is enabled only then). Check
the meshes the rig should skin, choose the naming set (the default names, Mixamo-style names
or the names from the AutoRigger preferences) and the spine, finger and phalanx counts, then place
the hot points:

1. Click **Place…** on a row: Pelvis and Neck once, Ankle, Shoulder, Wrist and the ten finger tips
   once for the left side and once for the right.
2. Click the character in the viewport. The point is drawn as a coloured marker at the picked
   surface point (or on the view plane when the click misses the mesh); Escape cancels a pending
   point.
3. **Generate rig** runs the build as one undo step: joints, the IK and reverse-foot controllers,
   the nearest-bone weights and the bind, then closes the window and reports the counts.

Rows whose finger is above the chosen finger count are dimmed and not required. The window refuses
a character that already has joints, a missing required point or a build with no mesh, and says
which point is missing.

### Retarget animations

**Retarget animations…** (the Anim Bank's right-click menu) copies one character's animations onto another character of the
same scene. It opens the *Animation Retargeting* window: pick the **source skeleton** and the
**target skeleton** (the scene's characters that have joints, IK or reverse-foot controllers) and
the window lists the controllers it matched by name; the ones that found no counterpart on either
side are counted and their tracks are dropped. **Retarget** copies every animation of the source
into a new *Retargeted From \<source\>* folder of the target's Anim Bank as one undo step and shows
the last copy. A scene with fewer than two such characters reports a message instead. The
reference-pose advice ("Reference poses of your characters must be similar...") is a line under the
list; the profile's *Skip retargeting ref pose advice popup* hides it. The window matches by name
only - an edited mapping is the command line's `--match`.

The Scene panel is the hierarchy: one row per 3D object in file order, with a Show checkbox and,
on a skinned mesh, a Lock checkbox; **Show all** and **Hide all** cover the scene. Clicking a row's
name selects that controller, and dragging a row onto another row reparents it there, keeping its
world (one undo step, Rigging context only, within one character). A hidden character hides its
joints and meshes. Each row carries a small picture and colour for its kind (character, joint,
mesh, skinned or locked mesh, IK or reverse-foot controller, shoe locator, camera, blendshape), and
a hidden row is dimmed. Right-clicking the panel offers **Show**, **Hide**, **Toggle shown**,
**Select hierarchy** and **Toggle hierarchy selection** (the row and every controller below it
joins or leaves the selection) and **Delete tree object** (the row's joint and the joints below it,
the IK or reverse-foot controller that constrains one of them with its shoe locators, and their
animation tracks; a controller row on its own deletes that controller and clears the constraints of
the joints it drove; refused while a locked skinned mesh hangs below one of the joints).

The command-line equivalents are `autorig` and `retarget` in [cli.md](cli.md), for scripts and batch
work; the viewer's AutoRigger and retarget windows do the same interactively.

## The Skin Workshop

The Skin Workshop panel paints a skinned mesh's weights. Pick the mesh and the joint, the mode
(**Add**, **Aim**, **Smooth** or **Select**), the screen radius, strength and value, **Hard** or
soft, and the rigid or smooth weight array. With *Paint* armed a left drag over the mesh dabs the
brush (one stroke and one undo step, Escape cancels).

The weight list shows each joint's control points, its weight on the current point (an editable
field), its weight sum and a lock button; a locked joint's weight is not redistributed. With *Scan*
on, hovering the mesh reads the point's weights next to the pointer. *Front/back* off paints only
the points facing the camera, and **Hide all / Show all** write the saved hidden-vertex array.

**Set**, **Prune**, **Fix**, **Flood**, **Mirror**, **Copy** and **Paste** work on the panel's
selection, one undo step each: Set writes a weight or a delta, Prune drops small influences and
redistributes them, Fix averages the selected vertices over their selected neighbours, Flood reports
the connected component of the selection, Mirror copies the weights onto the mirrored control
points, and Copy/Paste move weights between meshes by pairing control points in order and matching
joints by name.

## Cameras

Each view looks through one of the scene's fixed cameras. **Add animated camera** in the rig
workshop creates a `CCameraAnimated` as a child of the selected character (one undo step); every
animated camera gets its own window, which follows the animation and its FOV at the current frame.
A selected camera shows an FOV field in the side panel that keys its vertical field of view at the
current frame.

An animated camera also gets an editor orbit rig - a hidden joint under the camera, a
`CCameraTarget` under the character and the looker track - and the target is drawn as a translucent
grey disc at the point the camera looks at (white while it is selected). Dragging that target's
gizmo in the Animation context turns the camera about its own eye so it looks at the new point, and
moves the hidden joint with it; the drag is refused while the view is looking through that camera.
The disc is drawn with the current character's controllers, so a rig that belongs to another
character (some scenes keep their camera in its own character) appears when that character is
current.

## Import and export

**File > Import…** takes every importable file in one picker, and the file's type decides what
happens:

| File | Effect |
|-|-|
| `.fbx` | Reads the FBX into a new document: characters, joints, meshes, skin weights, bind poses, materials with textures, one animation per FBX stack |
| `.slopa` / `.akt` | Merges that scene's characters (with their animations), materials and textures into the open scene; its cameras and blendshape sights are left out |
| `.aknm` | Copies the file's animations onto the current character, pairing controllers by name and checking the reference poses |

**File > Export…** (Ctrl+E) opens the **Export Settings** window. Every control has hover text.

- **Presets** - Default, Blender, Unity, Godot. The lit one is the target; clicking one changes only
  the target and leaves every toggle as it is. Blender, Unity and Godot write a plain joint
  hierarchy, no zero scales and unique clip names, so the rig and clips import directly in those
  programs; unbaked, they write fitted keys (only the keys the motion needs).
- **Warnings** - amber lines under the toggles name settings the target reads badly (unbaked for
  Default, ASCII for Blender) or that do nothing in that combination. Exporting still works.
- **Export Scene / Export Selection** - everything, or only the characters that own a selected
  object (the character, a joint or controller, a bound mesh) plus selected unbound meshes.
- **Only Selected Animations** - the animations selected in the Anim Bank; a selected folder
  brings every animation inside it.
- **Baked Animations** - a key on every frame; off writes fitted keys for Blender, Unity and Godot,
  and the stacks' own keys and tangents for Default.
- **Multi-FBX (one file per animation / per character)** - `<file>_<animation>.fbx`,
  `<file>_<character>.fbx`, or both (`<file>_<character>_<animation>.fbx`); unbound meshes go to
  `<file>_meshes.fbx` when split by character. Two animations with the same name get `_2`, `_3`…
- **Folder path in file names** - a per-animation file name also carries the animation's Anim Bank
  folders: `<file>_<Folder1><sep><Folder2><sep><animation>.fbx`, where `<sep>` is the text in the
  field beside the toggle (underscore by default).
- **Export without animations** - the rig and skin in the rest pose, no takes.
- **Export camera targets** - the camera rigs' targets as empty nodes with their animation.
- **Export Character node (as dummy)** - each character as an empty root above its joints and meshes.
- **Embed Media** - the texture images inside the FBX; off writes them beside it.
- **Use ASCII format** - the text form of FBX (Blender cannot import it).

The window is not modal: the main window stays usable, so the Anim Bank and Scene panels can be
scrolled and the selection changed while it is open (Ctrl+E brings it back to the front). While the
window is open, the Scene panel and the Anim Bank mark (an orange bar and tint) every object
and animation the settings would export, and a line under the toggles counts them. **Export…** then
asks where to write; choosing the *Animation (.aknm)* type writes the exported animations as `.aknm`
files instead (one per character). The window's choices are kept for next time. Imported FBX warnings (an unsupported layer, a missing joint) are printed to the
console and counted in the status bar.

Retargeting one character's animations onto another is **Retarget animations…** in the Anim Bank's
right-click menu (or `retarget` in [cli.md](cli.md)); so is
scene import that must not touch the open document, and batch FBX conversion.

## Preferences and keyboard shortcuts

**File > Preferences…** lists the preference pages in order - a flat, alphabetical list,
where *Rig Atelier preferences* also holds the IK values - and draws the selected page as
curated rows under readable labels, with a check box for a bool (or one bit of
a flags value), a numeric field with a fixed precision, a colour swatch, a combo box with named
options, or a text field, a stored ~0 shown as the tool's default and lengths in metres. Members
the page does not draw, and pages the dialog does not list, keep a row under the member's own name, so
every value stays reachable, and a row is only written when its text changed (**Save** writes the
file back to `preferences.slopa`; **Cancel** and the title bar's X put every value back). A panel's
gear opens the same dialog on that panel's page. It needs a preferences file to be open (see
[where the files live](getting-started.md#where-the-files-live)); without one the viewer uses the
shipped defaults and tells you so. The values the viewer itself uses are the Spinner's steps and snap
switches, the autosave interval, the default frame rate and the joint-axis preference.

**Edit > Keyboard shortcuts…** lists the actions and their keys, captures a new key for the selected
action (refusing a key another action uses and an unknown key), unbinds, resets one action or all of
them, and writes `shortcuts.slopa` when the dialog closes - creating the file if it does not
exist. The viewer follows that map for the actions it implements: undo/redo, new/open/save/save as,
Move/Rotate/Scale, create key, delete, copy/paste, Curves, Grid, Play, previous/next key and frame,
controller stepping, the camera views and the layout toggle. A key the file gives to an action the
viewer does not implement is left alone rather than stolen.

## Updates

At startup the viewer asks GitHub for the newest release in the background; when it is newer than
the running build, **Update Available v&lt;version&gt;** appears on the right of the status bar. Nothing
else happens and no window opens; a failed check (no network) is only written to the log. The
*General preferences* row **Automatically check for updates at startup** turns the startup check off.

**Help > Check for updates…** checks at once and always answers. When a newer release exists (or
when you click the status bar notice) a window shows its notes with three buttons:

- **Download and install** downloads the package for this machine, checks it against the release's
  `SHA256SUMS`, and closes the viewer (asking about unsaved scenes first). The update is put in place
  once the viewer has exited:
  - an install made by the Windows setup runs the new setup over the same folder (it asks for
    administrator rights, as the first install did);
  - a portable folder (unpacked zip or tarball) is overwritten with the new files - your
    `profile` and `logs` folders and anything else the package does not contain are kept - and the
    viewer starts again.
  If you cancel the unsaved-changes prompt, the update installs the next time the viewer closes.
- **Open release page** opens the release on GitHub to download it yourself.
- **Later** closes the window.

A copy that cannot be updated in place (a development build, or a read-only folder on Linux) opens
the release page instead.

## Command-line runs and screenshots

The viewer can be scripted: opening a scene, choosing a character, animation and frame, selecting
and posing controllers, running Anim Bank, rig and skin commands, saving, and writing a screenshot.
That is how the acceptance checks in the project's notes drive it. The options are listed in
[cli.md](cli.md#scripting-the-viewer).

## Troubleshooting

- **Empty viewport, status bar mentions the OpenGL version.** The machine does not offer OpenGL 3.3
  (on Windows: Direct3D 11 for ANGLE). Update the graphics driver; on Linux install Mesa's `libGL`
  and, in a virtual machine, enable GPU acceleration.
- **"No preferences file is open" from File > Preferences…** There is no `preferences.slopa`; the
  viewer runs on its defaults. Copy an existing `preferences.slopa` into SloppyAnimatsu's location
  (see [getting-started.md](getting-started.md#where-the-files-live)) or pass one with
  `--preferences`. Everything else works without it.
- **"Permission denied" on Linux** after extracting: `chmod +x SloppyAnimatsu.Viewer SloppyAnimatsu.Cli`.
- **A mesh is drawn in a flat colour.** Its texture data is missing or cannot be decoded; the
  material's diffuse colour is drawn instead.
- **No sound.** Only WAV events are decoded, and playback is implemented on Windows only.
- **Another .akt tool silently ignores a file.** Some tools drop a scene they cannot load without a
  dialog: the tab stays on the previous scene (or a fresh `New_1`). Check the tab strip; the scene was written
  by a version of SloppyAnimatsu that has since been fixed, or by another tool.
- **A large scene is slow to open or to undo.** A 90 MB scene takes seconds to parse; the first
  undo snapshot after opening covers the scene root and the character's subtree but shares the bulk
  geometry, so it does not duplicate the vertex data.
- **A crash, or a run that ended with no message.** Every start writes a log file and the last
  three are kept: `%LOCALAPPDATA%\SloppyAnimatsu\logs\` on Windows, `~/.local/share/SloppyAnimatsu/logs/` on
  Linux (or `logs\` beside the program when that folder is not writable). It holds the command
  line, the status lines and, after a crash, the full stack trace including inner exceptions - the
  file to attach to a bug report. See
  [getting-started.md](getting-started.md#where-the-files-live).

More limits, including the features that are built but still rough, are in
[known-limitations.md](known-limitations.md).
