# SloppyAnimatsu documentation

SloppyAnimatsu is an animation and rigging tool for Windows and Linux, written in C# / .NET 9 and
inspired by akeytsu. It saves its own `.slopa` scenes, stays compatible with akeytsu's `.akt` scenes
and imports and exports FBX.

| Document | What it covers |
|-|-|
| [getting-started.md](getting-started.md) | What you need, installing on Windows and Linux, the first five minutes, where the files live |
| [user-guide.md](user-guide.md) | Every panel and menu: the viewport, posing, keys and curves, the Anim Bank, rigging, skinning, cameras, import and export |
| [cli.md](cli.md) | The command-line tool, and the viewer's options for scripted runs and screenshots |
| [known-limitations.md](known-limitations.md) | What is not built, what is still rough, and what to expect on Linux |

## Quick start

**Windows:** unpack `SloppyAnimatsu-<version>-win-x64.zip` and double-click `SloppyAnimatsu.Viewer.exe`, or
pass a scene to it:

    SloppyAnimatsu.Viewer.exe "C:\scenes\character.slopa"

**Linux:** unpack `SloppyAnimatsu-<version>-linux-x64.tar.gz` and run the viewer from the folder, or
install a menu entry for your user:

    tar xzf SloppyAnimatsu-<version>-linux-x64.tar.gz
    cd SloppyAnimatsu-<version>-linux-x64
    ./SloppyAnimatsu.Viewer ~/scenes/character.slopa
    ./install.sh            # optional: menu entry, icon and a `sloppyanimatsu` launcher

Both packages are self-contained: the .NET runtime does not have to be installed.

## What it can do

- Open, edit and save `.slopa` scenes, and for compatibility read and write akeytsu's `.akt` scenes
  byte-exactly.
- Rig: create, insert, delete, parent, unparent and orient joints; add IK and reverse-foot
  controllers; bind and unbind skin; paint weights in the Skin Workshop; build a biped rig from hot
  points (`autorig`).
- Animate: key editing on the timeline, ease and fade handles, copy/paste, mirroring, space
  switching, the Curveboard, the Anim Bank and Mixer, cycle tools, camera animation.
- Interchange: FBX import and export (baked and unbaked), `.aknm` animation files, merging another
  scene, retargeting animations between characters.

The gaps, and the features that are built but still rough, are listed in
[known-limitations.md](known-limitations.md).
