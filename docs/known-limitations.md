# Known limitations

SloppyAnimatsu is a work in progress. This page lists what is missing, what is built but still
rough, and where its behaviour is a deliberate choice.

## Not built

**Out of scope on purpose.** Licensing, store integration, analytics, online uploads, update checks
and online help links. No third-party artwork or template scenes are bundled - the Welcome rows only
open template scenes that are already installed on the machine.

**Not built yet.**

- Crash recovery files. An autosave slot is an ordinary scene: open it to restore it.
- The Welcome window has only the template rows and the "skip at startup" box; no artwork or links.
- Creating a `preferences.slopa` from nothing: the Preferences dialog edits an existing file. Without
  one the viewer runs on built-in defaults and says so; see
  [getting-started.md](getting-started.md#where-the-files-live).
- Hand-written labels for every preference: the Preferences dialog shows curated rows for the common
  pages and every other editable member under its own name, an enum as a number. Printable key names
  beyond the US keyboard layout are not shown in the shortcut dialog.
- Blendshape face **thumbnail pictures**, and some edge cases of the blendshape **point of view**
  (a mesh without a camera anchor). A `CBlendShapesSight` is written by SloppyAnimatsu but other
  `.akt` tools may drop it on save. The blendshape setup file (`.bsu`) is read and written, but has
  only been tested against files SloppyAnimatsu wrote itself.
- The AutoRigger's **mirroring gesture**: every hot point is placed per side. Its **Smooth** answer
  keeps the weight arrays the meshes carry, and nothing renumbers their bone indices afterwards.
- An editable matching grid for retargeting: SloppyAnimatsu matches joints by name, and an edited
  mapping is only the command line's `--match`.
- A panel with no saved height opens unfolded even when it was folded.
- The Skin Workshop's slough dialog, its context-menu actions and an exact screen-projected brush
  shape. A pasted joint without a counterpart is remapped by name, then its nearest mapped ancestor,
  then the target's first joint.
- Audio playback on Linux (the mixer is there; the sound device is Windows-only), and audio formats
  other than WAV.

**Linux.** The Linux package has been checked under WSLg with Mesa's software OpenGL: the
command-line tool runs and the viewer renders. It has not yet run on a physical Linux desktop with a
GPU driver.

- **Software OpenGL on Linux.** Avalonia blacklists the software renderers (`llvmpipe`, `softpipe`)
  for its X11 GLX backend, so on a VM, a remote session or WSLg the 3D viewport stays black and
  `--screenshot` writes nothing. Set `SLOPPYANIMATSU_SOFTWARE_GL=1` in the viewer's environment to allow
  the software renderer. The interface always comes up; only the OpenGL viewport is affected. The
  package's `README.txt` lists the X11 packages the viewer needs.

## Built, but still rough

| Feature | What to expect |
|-|-|
| Audio tracks | tested only on scenes SloppyAnimatsu made; event timing and mixing have little real-world coverage |
| Camera animation, Photo and orthographic views | camera tracks, the orbit rig of an added camera, deleting a camera and dragging its look-at target work; a chain of look-at solves at playback, adding a look-at from a picker, dragging a target in the rest pose and the Photo frame decoration are not built |
| `.aknm` export and import | kept and tested on SloppyAnimatsu's own files only; no further work is planned |
| `.akt` scene merge | the merged scene is complete, but floating-point fields of the imported data can differ in the last digits |
| Retarget across a different reference pose | `--method=reference`/`auto` keeps proportions and translation direction, but rotations are not corrected per key and non-uniform scale is not preserved, so `local` stays the default |
| FBX export | bakes of feet and toes can differ by a few hundredths of a unit from other exporters |
| AutoRigger | builds a biped rig, IK and reverse feet and a rigid skin from hot points; joint placement on a bent limb can be up to half a voxel off, the thumb's phalanx count is a guess, and a three-joint phalanx chain can kink |
| Blendshapes | the panel, face cells, sight markers, timeline integration, face zones, the quadrant pad, the point of view and the setup file are built and tested on command-line-made scenes; no larger real scene has been tried |
| Reverse-foot shoe | the shoe is fitted to the foot mesh in its bind shape, and each foot is fitted to its own vertices, so a right shoe can differ from a mirrored left one |
| Interactive joint placement | which surface a click lands on (mesh or the plane through the selection) may not be what you expect |
| Bind: the weight solver | binding writes rigid one-influence weights; carried smooth weights are kept rather than re-solved |

## Deliberate differences

- The rest-pose conversion rewrites every non-additive, non-reverse-foot track of the Anim Bank
  (keys and tangents); the reverse foot is not re-fitted after a rest-pose edit, so its controller
  and shoe locators keep the old bases. The "space fix-up" after a retime runs once per edit instead
  of per key; the loop key is mirrored rather than fixed up; a plain delete (gaps kept) does no
  fix-up; changing space on a loop or on an IK chain is refused.
- Copying IK and reverse-foot keys fills the `RefArray` only when the target key does not already
  have the chain's length.
- A leg with a non-uniformly scaled joint cannot take its shoe's orientation exactly and can miss a
  drag target by about 1% of the leg.
- Bind Skin takes the character's meshes *and* the scene's free ones; *Select hierarchy* therefore
  leaves free props alone. The rigid weight goes to the nearest start joint.
- A run log's crash entry is a **managed** stack trace (`Exception.ToString`). A crash inside a
  native library - a graphics driver, an access violation - ends the process without one; the log
  then holds what the run printed up to that point and no frames.
