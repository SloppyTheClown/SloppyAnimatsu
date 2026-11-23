# Getting started

## What you need

| | Windows | Linux |
|-|-|-|
| System | 64-bit Windows 10 or 11 | 64-bit Linux with an X11 desktop |
| Graphics | a Direct3D 11 driver (the viewer renders through ANGLE) | desktop OpenGL 3.3, usually Mesa's `libGL` |
| Other | — | fontconfig; on Debian/Ubuntu: `sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libgl1` |

The packages are self-contained: .NET is not installed or required. SloppyAnimatsu opens `.akt`
scenes and saves either `.akt` or its own compressed `.slopa` scenes (see
[Opening, saving and autosave](#opening-saving-and-autosave)) and keeps its settings apart from any
other program's (see [Where the files live](#where-the-files-live)).

A Wayland session works through XWayland. A virtual machine or a remote desktop without GPU
passthrough may only offer OpenGL 2.1; the viewer then shows the viewport empty and says so in the
status bar. `glxinfo | grep "OpenGL version"` says what the machine offers.

## Installing

### Windows

**With the installer:** run `SloppyAnimatsu-<version>-win-x64-setup.exe`. It asks for administrator
rights and installs to `C:\Program Files\SloppyAnimatsu` unless you choose another folder; the folder
is remembered, so running a newer setup later updates that install in place. It adds Start menu and
desktop shortcuts (both optional) and an entry in *Settings > Apps*, which uninstalls it. For a silent
install: `setup.exe /S [--dir=<folder>] [--no-shortcuts]`. Later versions can also be installed from
inside the viewer with **Help > Check for updates…** (see [Updates](user-guide.md#updates)).

**Portable, from the zip:**

1. Unpack `SloppyAnimatsu-<version>-win-x64.zip` anywhere, for example a folder on your desktop.
2. Run `SloppyAnimatsu.Viewer.exe`. To open a scene directly, pass it:
   `SloppyAnimatsu.Viewer.exe "C:\scenes\character.slopa"`. No console window opens; `--console` keeps one
   for a scripted run's status lines.
3. `SloppyAnimatsu.Cli.exe` in the same folder is the command-line tool; see [cli.md](cli.md). The
   other program files are in `lib\`.

The zip writes nothing to the registry: to uninstall, delete the folder. Either way, your
preferences and autosaves are kept. The first start may show Windows SmartScreen's "unrecognized
app" warning, because the executables are not code-signed; choose *More info > Run anyway*.

### Linux

1. Unpack the archive and run the viewer from the folder it makes:

       tar xzf SloppyAnimatsu-<version>-linux-x64.tar.gz
       cd SloppyAnimatsu-<version>-linux-x64
       ./SloppyAnimatsu.Viewer

2. To get a menu entry, an icon and a `sloppyanimatsu` launcher, install it for your user:

       ./install.sh

   That copies the program to `~/.local/share/sloppyanimatsu`, the menu entry to
   `~/.local/share/applications` and the launcher to `~/.local/bin`; it needs no root. Set `PREFIX`
   to install elsewhere, and `./install.sh --uninstall` removes what it installed. Running from the
   extracted folder without installing is equally supported.

3. `SloppyAnimatsu.Cli` in the same folder is the command-line tool; see [cli.md](cli.md).

If `./SloppyAnimatsu.Viewer` reports "Permission denied" (an archive extracted by a tool that drops
permissions), run `chmod +x SloppyAnimatsu.Viewer SloppyAnimatsu.Cli` once.

If the window opens but the 3D viewport stays black (a virtual machine, a remote session or WSLg,
where OpenGL is software), start it with `SLOPPYANIMATSU_SOFTWARE_GL=1 ./SloppyAnimatsu.Viewer`: Avalonia
otherwise refuses the Mesa software renderer. Rendering is slower but complete, screenshots
included. See [known-limitations.md](known-limitations.md).

## First run

With no arguments the viewer starts on a new, untitled scene (`New_1`) with an empty viewport. The
**Welcome** window opens at start-up only when *Skip welcome window at startup* is unticked (it is
ticked in a new install); **Help > Show Welcome screen…** opens it at any time. It has one row per
template scene installed in
`<Public Documents>\SloppyAnimatsu\Templates` (`.slopa` or `.akt` scenes, listed by file name). Pick
one to open it, or **Close** to keep the empty scene. With no scenes in that folder the window lists
none. `--welcome` shows the window even when it has been skipped. It never creates the folder: put
your own starting scenes there to have them offered at start-up.

Without a preferences file - the normal case on a first run - the viewer uses a built-in panel
layout and default values, so it is usable at once.

1. **File > Open…** (Ctrl+O) opens a scene, or start the viewer with the scene's path.
2. The **Bank** panel on the left lists the scene's characters; pick one. The animation list below
   it lists that character's animations and, at the top, the **(rest pose)** entry.
3. Pick an animation and press **Enter** (or the **Play** button) to play it at the scene's frame
   rate. The timeline under the viewport shows one row per stack, with its keys.
4. In the viewport, **left drag** orbits, **right drag** zooms in (right/up) or out (left/down),
   **middle drag** pans, the **wheel** zooms, and **F** frames the whole character.

A five-minute tour of the window follows; the panels and the workflows are in
[user-guide.md](user-guide.md).

## The window at a glance

- **Menu bar**: File, Edit, Plugins (empty for now), Help. Import and export are on the File menu;
  the rig, key, animation, stack, context and view commands are reached by their keys (Help >
  Keyboard shortcuts), the panels and the right-click menus.
- **Viewport**: the whole window behind the panels; left drag orbits, right drag zooms in (right/up)
  or out (left/down), middle drag pans, the wheel zooms and F frames everything.
- **Left panels**: Bank (characters and animations) and Scene (the hierarchy panel); in the Rigging
  context there is also a Properties panel with the document name, the selection and the view
  switches.
- **Right panels**: Transform (the Spinner's numeric fields and snapping), Mixer (the active
  stack), Picker (controllers) and, in the Rigging context, the Skin Workshop.
- **Timeline**: across the bottom, with the transport bar (Play, frame, gap, snap switches); the
  Curveboard (when *Curves* is on) sits above it as part of the view area.
- **Status bar**: the last action's result, and warnings such as an OpenGL version that is too low.

The panels are floating windows, not columns: the viewport fills the window and each panel
is drawn over it. Drag a panel by its **title bar** to move it, click the **arrow** beside the title
to fold it to that bar (click again to unfold it), drag the small **grip at the bottom centre** to
resize it and press anywhere in it to bring it to the front. The first arrangement - which side each
panel starts on, its order and its saved height - comes from the saved dock layout when a
preferences file exists; without one the built-in layout above is used. A resize is written back into
the saved layout (`preferences.slopa`) when you release the grip, and so are a panel's moved place and
its folded state when you drop or fold it, so the next start opens the same arrangement.
Tab (or the context button) switches the two
interfaces, Rigging and Animation.

## Opening, saving and autosave

| Action | How |
|-|-|
| Open a scene | File > Open… (Ctrl+O), File > Recent scenes, or the path on the command line - each opens a new document tab |
| New empty scene | File > New scene (Ctrl+N) - a new empty scene in a new tab |
| Close a document | File > Close file (Ctrl+W), or the tab's `×` |
| Save | File > Save (Ctrl+S) - saves the active tab |
| Save as | File > Save As… (Ctrl+Shift+S) |

Scenes are saved as **`.slopa`** files (Save As suggests that name; you can still pick `.akt`).
Open and Import take both `.slopa` and `.akt`, and so does dragging a file onto the window.

The extension you save under picks the format. A `.slopa` file is SloppyAnimatsu's compressed scene
and keeps everything. An `.akt` file is written in the plain `.akt` layout, but it
loses what only SloppyAnimatsu has: a material's extra swap textures and their names, the chosen
swap texture (the material's diffuse texture stays), the Opaque and Nearest
filter switches, texture swap keys and Compensate Scale keys, and the compression. Saving as `.akt`
from the viewer first shows a prompt listing these losses; Cancel keeps the scene unsaved.
`SloppyAnimatsu.Cli convert <scene.slopa> <scene.akt>` makes the same file from the command line.

Several documents can be open at once. Every tab is labelled with its file name (or `New_1`,
`New_2`, ... while it is unsaved; a `*` marks unsaved edits) and the active one is lighter. A tab
switch brings back that document's undo history, selection, shown character, animation and frame,
interface context and viewport camera, so switching away never loses work. Closing a modified
document asks whether to save it first; closing the last one starts a fresh empty scene.

File > Import… builds a new document from an FBX and merges a scene file into the active one. Saving writes the scene's folder into its `SavePath`, and an FBX import writes the FBX's folder.

The scene is autosaved every ten minutes (the interval is a preference) to the `SloppyAnimatsu/Autosave`
folder in your documents, alternating between two `_autosave_01.slopa`/`_02.slopa` files per open document
(named after its file, or after `New_1`, `New_2`, ...). Autosave does not
mark the document saved; to restore a slot, open it as a scene.

## Where the files live

SloppyAnimatsu keeps its own settings as `.slopa` files in its own folders. A missing file simply
means the built-in default.

| File | Windows | Linux |
|-|-|-|
| `preferences.slopa` (dock layout, Spinner steps, autosave interval, recent scenes, shortcut prefs) | `%APPDATA%\SloppyAnimatsu\preferences.slopa` | `~/.config/SloppyAnimatsu/preferences.slopa` |
| `shortcuts.slopa` (the key map) | `%APPDATA%\SloppyAnimatsu\shortcuts.slopa` | `~/.config/SloppyAnimatsu/shortcuts.slopa` |
| `panelShortcuts.slopa` (the panel keys) | `%APPDATA%\SloppyAnimatsu\panelShortcuts.slopa` | `~/.config/SloppyAnimatsu/panelShortcuts.slopa` |
| autosave slots | `%USERPROFILE%\Documents\SloppyAnimatsu\Autosave\` | `~/Documents/SloppyAnimatsu/Autosave/` |
| retargeting name-pair caches | `%USERPROFILE%\Documents\SloppyAnimatsu\Retargeting\` | `~/Documents/SloppyAnimatsu/Retargeting/` |
| Welcome window templates | `%PUBLIC%\Documents\SloppyAnimatsu\Templates\` | `~/Documents/SloppyAnimatsu/Templates/` |
| run logs (the viewer's and the CLI's last three runs) | `%LOCALAPPDATA%\SloppyAnimatsu\logs\` | `~/.local/share/SloppyAnimatsu/logs/` |

On Linux `~/.config` follows `XDG_CONFIG_HOME` and "Documents" is the desktop's documents folder;
on a machine without a documents folder, those paths fall back to `~/SloppyAnimatsu/…`.

Every start of the viewer or the CLI writes a new log file, and the folder keeps the last three.
One file stops at 4 MiB, with its last eighth (up to 512 KiB) kept free so a crash's stack trace is
still written after the ordinary lines filled it. If the folder above cannot be written - a profile
on a drive the program cannot reach, a locked-down account - the log goes to `logs\` beside the
program, then to the system temp folder. Two variables tune it: `SLOPPYANIMATSU_LOG_DIR` names a folder
to prefer, and `SLOPPYANIMATSU_LOG_CONSOLE=0` stops the log from copying standard output and standard
error.

Two options let a script or a second profile use other files without touching yours:

- `--preferences <file>` reads the recent-scene list, autosave interval and Spinner settings from
  that file.
- `--shortcuts <file>` reads the key map from that file.

The command-line tool can read and write both files too (`prefs`, `shortcuts`; see [cli.md](cli.md)).
For compatibility, both options and both commands also read akeytsu's `.akt` settings files; a file
named `.akt` is saved back in the plain `.akt` layout.

## Uninstalling

- Windows: delete the folder you unpacked.
- Linux: `./install.sh --uninstall` from the unpacked folder if you installed it; then delete the
  folder. Neither touches other programs' preference files.

## Next

- [user-guide.md](user-guide.md) - rigging, skinning, animation, cameras, import and export.
- [cli.md](cli.md) - batch work, and driving the viewer with a script.
- [known-limitations.md](known-limitations.md) - what not to expect yet.
