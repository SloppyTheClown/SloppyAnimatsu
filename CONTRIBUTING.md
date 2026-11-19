# Contributing

Forks, issues and pull requests are welcome. SloppyAnimatsu is MIT licensed (see [LICENSE](LICENSE)).

## Building

You need the .NET 9 SDK.

```powershell
dotnet build SloppyAnimatsu.sln -m:1 /nodeReuse:false
dotnet run --project src/SloppyAnimatsu.Viewer
```

## Running the tests

The tests are xUnit tests, run through the in-process runner in `tools/TestRunner`:

```powershell
dotnet run --project tools/TestRunner
```

`test-full.bat` runs them all in parallel shards, and `test-changed.bat` runs only the test classes
touched by your changes. Most scene tests need sample `.akt` scenes, which are not part of the
repository: point `SLOPPYANIMATSU_SAMPLES` at a folder of them. Without it, only the tests that need
no sample scene run.

## Ground rules

- Write your own code. Do not submit code copied from akeytsu or any other closed-source program.
- Code comments describe what the code does, not where the knowledge came from.
- Keep `.akt` compatibility: scenes saved to an `.akt` path should still open in akeytsu.
