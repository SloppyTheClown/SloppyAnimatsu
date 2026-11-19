SloppyAnimatsu - Linux (x64)

Extract the archive and run the viewer from this folder, or open a scene with it:

    ./SloppyAnimatsu.Viewer /path/to/character.slopa

Install it for the current user (menu entry, icon and a `sloppyanimatsu` launcher in ~/.local):

    ./install.sh                 # PREFIX=/some/prefix changes the prefix
    ./install.sh --uninstall

SloppyAnimatsu.Cli is the command-line tool:

    ./SloppyAnimatsu.Cli --version
    ./SloppyAnimatsu.Cli info character.slopa

This package is self-contained: .NET does not have to be installed. It needs an X11 desktop with
desktop OpenGL 3.3 and fontconfig; on Debian or Ubuntu:

    sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libgl1

A Wayland session works through XWayland. A machine without OpenGL 3.3 (a virtual machine, a
remote session without GPU passthrough) shows an empty viewport and a line about the OpenGL
version in the status bar. If OpenGL is Mesa's software renderer (llvmpipe, the case under WSLg or
a VM without a GPU driver), start the viewer with SLOPPYANIMATSU_SOFTWARE_GL=1:

    SLOPPYANIMATSU_SOFTWARE_GL=1 ./SloppyAnimatsu.Viewer

Avalonia otherwise refuses that renderer and the viewport stays black; with the variable the
whole scene draws (slower) and --screenshot captures it.

SloppyAnimatsu keeps its preferences, key map and autosaves here:

    ~/.config/SloppyAnimatsu/preferences.slopa
    ~/.config/SloppyAnimatsu/shortcuts.slopa
    ~/.config/SloppyAnimatsu/panelShortcuts.slopa
    ~/Documents/SloppyAnimatsu/Autosave

(~/.config follows $XDG_CONFIG_HOME, and "Documents" is the desktop's documents folder, so on a
machine without one the autosaves go to ~/SloppyAnimatsu/Autosave.) Without a preferences file the viewer opens with
its built-in panel layout and shipped defaults, so it works on a first run.

Documentation is in docs/README.md, with the full guide in docs/user-guide.md.
