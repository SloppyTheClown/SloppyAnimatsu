# SloppyAnimatsu

SloppyAnimatsu is an animation and rigging tool for Windows and Linux, written in C# / .NET 9 with
Avalonia and OpenGL. It is inspired by akeytsu, and it opens, edits and saves `.akt` scenes.

It rigs, skins and animates characters, retargets animations between them, and imports and exports
FBX. A command-line tool (`SloppyAnimatsu.Cli`) does the same edits from scripts.

## Documentation

- [Getting started](docs/getting-started.md): install, first run, where the files live
- [User guide](docs/user-guide.md): the panels, rigging, skinning, animating, retargeting
- [Command line](docs/cli.md): every `SloppyAnimatsu.Cli` command
- [Known limitations](docs/known-limitations.md): what is missing or still rough
- [Packaging](packaging/README.md): building the Windows and Linux packages

## Building

You need the .NET 9 SDK.

```powershell
dotnet build SloppyAnimatsu.sln -m:1 /nodeReuse:false
dotnet run --project src/SloppyAnimatsu.Viewer
```

Ready-made Windows and Linux builds are on the [Releases](https://github.com/SloppyTheClown/SloppyAnimatsu/releases)
page. To run the tests or contribute, see [CONTRIBUTING.md](CONTRIBUTING.md).

## Status

Alpha (0.3.0-alpha): a work in progress, not affiliated with akeytsu or its authors.

## License

MIT - see [LICENSE](LICENSE). You are free to use, fork, modify and redistribute it.
