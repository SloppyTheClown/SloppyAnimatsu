SloppyAnimatsu - Windows (x64)

Run SloppyAnimatsu.Viewer.exe to open the viewer, or open a scene with it:

    SloppyAnimatsu.Viewer.exe "C:\scenes\character.slopa"

SloppyAnimatsu.Cli.exe is the command-line tool:

    SloppyAnimatsu.Cli.exe --version
    SloppyAnimatsu.Cli.exe info character.slopa

This package is self-contained: nothing else (not even .NET) has to be installed. A 64-bit Windows 10
or 11 with a graphics driver that supports OpenGL 3.0 (the viewer renders through ANGLE, which
needs Direct3D 11) is enough.

SloppyAnimatsu keeps its preferences, key map and autosaves here:

    %APPDATA%\SloppyAnimatsu\preferences.slopa
    %APPDATA%\SloppyAnimatsu\shortcuts.slopa
    %APPDATA%\SloppyAnimatsu\panelShortcuts.slopa
    %USERPROFILE%\Documents\SloppyAnimatsu\Autosave

Without a preferences file the viewer opens with its built-in panel layout and its shipped
defaults, so it works on a first run.

The program's own files are in lib\; only the two exes sit at the top.

Installed with the setup program: uninstall from Settings > Apps (or run Uninstall.exe here); your
preferences and autosaves are kept. Running a newer setup updates this folder in place. Unpacked
from the zip: there is nothing to uninstall, delete this folder.

Documentation is in docs\README.md, with the full guide in docs\user-guide.md.
