# Packaging

Two scripts build the distributable packages, one per platform; either can cross-publish both:

| Script | Runs on | Writes |
|-|-|-|
| [`package.ps1`](package.ps1) | Windows (Windows PowerShell 5.1 or PowerShell 7) | `dist\SloppyAnimatsu-<version>-win-x64.zip`, `dist\SloppyAnimatsu-<version>-win-x64-setup.exe`, `dist\SloppyAnimatsu-<version>-linux-x64.tar.gz`, `dist\SHA256SUMS` |
| [`package.sh`](package.sh) | Linux (or WSL / Git Bash) | the same |

```powershell
# Windows: both packages into dist\
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\package.ps1

# one platform only, keeping the unpacked folder to look at
packaging\package.ps1 -Runtime linux-x64 -KeepStage
```

```bash
# Linux: both packages into dist/
./packaging/package.sh

# one platform only
./packaging/package.sh linux-x64 --keep-stage
```

The only prerequisite is the .NET 9 SDK (`dotnet` on `PATH`); the packages are self-contained, so
the machines that *run* them do not need .NET. No `zip`, `tar` or `7z` is needed -
[`tools/PackageTool`](../tools/PackageTool) writes both archives, through
`SloppyAnimatsu.Core.Packaging.ArchiveWriter`.

## What a package is

```
SloppyAnimatsu-0.1.0-alpha-win-x64\
    SloppyAnimatsu.Viewer.exe        the viewer (Avalonia + Silk.NET, self-contained)
    SloppyAnimatsu.Cli.exe           the command-line tool
    lib\                        everything else: the .NET runtime, Avalonia, SkiaSharp, the app's dlls
    README.txt                  one screen: run it, requirements, settings paths
    docs\                       the user documentation from docs\

SloppyAnimatsu-0.1.0-alpha-linux-x64\
    SloppyAnimatsu.Viewer            the same viewer, Linux x64
    SloppyAnimatsu.Cli
    README.txt
    docs\
    install.sh                  per-user install: menu entry, icon, ~/.local/bin/sloppyanimatsu
    sloppyanimatsu.svg               the icon (the same drawing as the Windows .ico)
```

The viewer and the CLI publish into the *same* folder: they share `SloppyAnimatsu.Core.dll` and the
runtime files, so a package is about 100 MB instead of two separate trees. Both are release builds
with `DebugType=None` (no pdbs).

### The lib\ layout (Windows)

[`windows/lib-layout.ps1`](windows/lib-layout.ps1) runs after the publish (in `package.ps1` and
`release.ps1`): it moves every published file except the two exes into `lib\` and rewrites the app
path embedded in each apphost from `<App>.dll` to `lib/<App>.dll`. The .NET apphost resolves that
path relative to itself and treats `lib\` as the application folder, so the runtime, `deps.json`,
`runtimeconfig.json` and native libraries are all found there with no code change.
`AppContext.BaseDirectory` is therefore `lib\`; code that wants the install folder (the portable
`profile\`, the fallback `logs\`) uses `SloppyAnimatsu.Core.Diagnostics.AppFolder.Install`, which
steps out of `lib\`. The patch refuses (and the script fails) unless the name occurs exactly once.

## The Windows installer

[`windows/build-installer.ps1`](windows/build-installer.ps1) `-Source <lib-layout folder> -Output
<setup.exe>` zips the folder (without `profile\`/`logs\`) and compiles
[`windows/installer/Setup.cs`](windows/installer/Setup.cs) twice with the .NET Framework 4 C#
compiler that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, C# 5):
first as `Uninstall.exe`, then as the setup with the zip and the uninstaller embedded. No installer
toolkit or NuGet package is needed to build it; it takes ~45 MB.

What it does:

- A machine-wide install (`requireAdministrator` manifest: setup and uninstall raise one UAC
  prompt). The default folder is `%ProgramFiles%\SloppyAnimatsu`; Browse picks another (picking a
  non-empty folder installs into a `SloppyAnimatsu` subfolder of it). *Launch* at the end starts the
  viewer through Explorer, so it runs as the signed-in user, not elevated.
- The chosen folder is stored in `HKLM\Software\SloppyAnimatsu` `InstallPath` (plus `Version` and the
  desktop-shortcut choice) and offered again by the next setup, which then says *Update*. The value
  survives an uninstall, so a reinstall offers the same folder.
- An update deletes what the last install listed in `uninstall.dat` and extracts the new files;
  `profile\` and `logs\` are never touched. Choosing a different folder moves the install (the old
  folder's program files are removed). It refuses while the viewer or CLI runs from that folder.
- All-users Start menu and desktop shortcuts ("Sloppy Animatsu"), and an Add/Remove Programs entry
  under HKLM whose `Uninstall.exe` removes the program files, our shortcuts and the entry.
- Command line: `/S` (or `--quiet`), `--dir=<folder>` (or `/D=<folder>`), `--no-shortcuts`;
  `Uninstall.exe /S`. Quiet failures go to stderr with exit code 2.

`build-installer.ps1 -PerUserTest` builds a test variant that needs no administrator rights
(`asInvoker`, registry under HKCU instead of HKLM; run it with `--no-shortcuts` and `--dir=`). Never
ship it. Checked 2026-11-19 with that variant in quiet mode: install, update to the remembered
folder keeping `profile\`, a move to a new folder, uninstall (folder and Add/Remove entry gone,
`InstallPath` kept).

## Why PackageTool instead of tar/zip

A Linux tarball needs its apphosts and `install.sh` stored as 0755. On Windows the system `tar`
(bsdtar) derives modes from the file system, and an extension-less file such as `SloppyAnimatsu.Viewer`
gets 0666 - the package would extract without an executable bit. `PackageTool` writes the gzip and
zip streams itself with explicit modes, so the tarball is identical whether it was built on Windows
or Linux. `ArchiveWriter` is covered by `PackagingTests`; the Windows zip also records each entry's
Unix mode in its external attributes, although Windows does not use it (the Linux package is the
tarball, where the modes always apply).

## The version

`Directory.Build.props` holds `<Version>`. The packaging scripts read it, pass it to `dotnet publish`
(`-p:Version=`), and use it in the artifact names; `SloppyAnimatsu.Cli --version` and Help > About print
it. Bump it there before building a release. Pass `-Version` / `--version` to override it for a
one-off build.

## The icon

`src/SloppyAnimatsu.Viewer/Assets/app.ico` is generated by [`icon.py`](icon.py) (needs Pillow):

```bash
python packaging/icon.py
```

The drawing is a joint chain forming the "M" of the name; [`linux/sloppyanimatsu.svg`](linux/sloppyanimatsu.svg)
is the same drawing by hand for the Linux icon. The Windows exe's icon comes from the csproj's
`ApplicationIcon`, the window icon from the same file as an Avalonia resource.

## Checking a package

Build one, unpack it somewhere outside the checkout, and run the packaged binaries (not the build
output). The checks that caught problems so far:

```powershell
# Windows
Expand-Archive dist\SloppyAnimatsu-0.1.0-alpha-win-x64.zip -DestinationPath .tmp-run\pkg
.\.tmp-run\pkg\SloppyAnimatsu-0.1.0-alpha-win-x64\SloppyAnimatsu.Cli.exe --version
.\.tmp-run\pkg\SloppyAnimatsu-0.1.0-alpha-win-x64\SloppyAnimatsu.Cli.exe info C:\scenes\character.akt
# a screenshot proves the GL context and the Avalonia natives are in the package
& .\.tmp-run\pkg\SloppyAnimatsu-0.1.0-alpha-win-x64\SloppyAnimatsu.Viewer.exe --new --screenshot shot.png
```

```bash
# Linux (or WSL)
tar xzf dist/SloppyAnimatsu-0.1.0-alpha-linux-x64.tar.gz -C /tmp
/tmp/SloppyAnimatsu-0.1.0-alpha-linux-x64/SloppyAnimatsu.Cli --version
readelf -d /tmp/SloppyAnimatsu-0.1.0-alpha-linux-x64/SloppyAnimatsu.Viewer | head    # an ELF apphost
ldd /tmp/SloppyAnimatsu-0.1.0-alpha-linux-x64/SloppyAnimatsu.Viewer                   # system libraries must resolve
# a screenshot proves the GL context in the package; software Mesa needs the opt-in
SLOPPYANIMATSU_SOFTWARE_GL=1 /tmp/SloppyAnimatsu-0.1.0-alpha-linux-x64/SloppyAnimatsu.Viewer \
    /path/to/7_AddingIK.akt --screenshot shot.png --window
```

`SLOPPYANIMATSU_SOFTWARE_GL=1` clears Avalonia's X11 GLX blacklist of `llvmpipe`/`softpipe`; without it
the window still opens but the viewport stays black and no screenshot is written. Under WSLg that
is the only renderer available, and with it the packaged viewer rendered `7_AddingIK.akt` to a
470x629 PNG (2026-10-25).

A tarball built on Windows has to be checked with `tar -tvzf`: the apphosts and `install.sh` must
show `-rwxr-xr-x`. A viewer run on Linux needs an X display and OpenGL 3.3; under WSL, install the
X11 and Mesa packages the package's `README.txt` lists (`libice6 libsm6 libx11-6 libfontconfig1
libgl1`), plus `xvfb` and `libgl1-mesa-dri` to render without a desktop. On a machine without them,
the command-line checks above and `ldd` are what can be verified.

## Not done here

- No code signing: the Windows executables are unsigned, so SmartScreen warns on first run.
- No MSI or file associations, and no AppImage/deb/rpm: the Windows installer is the small
  `Setup.cs` above, the Linux package a tarball plus a per-user `install.sh`. Reproducible-build pinning of the archives' mtimes is not
  attempted.
