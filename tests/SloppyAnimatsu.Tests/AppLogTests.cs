using System.Runtime.CompilerServices;
using SloppyAnimatsu.Core.Diagnostics;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The run log (<see cref="AppLog"/>): a new file for every run, three kept at a time, one size
/// limit per file, and a crash written with its full stack trace, inner exceptions included.
/// </summary>
public class AppLogTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-log-{Guid.NewGuid():N}");

    public void Dispose()
    {
        AppLog.Shutdown();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void EveryStartWritesANewFile_AndOnlyTheCurrentAndPreviousAreKept()
    {
        // A CLI log in the same folder belongs to another role and survives the viewer's rotation.
        AppLog.StartForTesting(_folder, "cli", []);
        string cliLog = AppLog.FilePath!;
        AppLog.Shutdown();

        var written = new List<string>();
        for (int run = 1; run <= 5; run++)
        {
            AppLog.StartForTesting(_folder, "viewer", [$"--run={run}"]);
            Assert.NotNull(AppLog.FilePath);
            Assert.True(AppLog.IsActive);
            Assert.Equal(run == 1 ? null : written[^1], AppLog.PreviousFilePath);
            AppLog.Info($"run {run}");
            AppLog.Action($"edit {run}");
            written.Add(AppLog.FilePath!);
            AppLog.Shutdown();
        }

        Assert.Equal(5, written.Distinct().Count());
        string[] kept = Directory.GetFiles(_folder, "viewer-*.log");
        Assert.Equal(2, AppLog.KeepFiles);
        Assert.Equal(AppLog.KeepFiles, kept.Length);
        Assert.True(File.Exists(cliLog));

        // Rotation keeps the runs that started last: this one and the one before it.
        Assert.Contains(written[^1], kept);
        Assert.Contains(written[^2], kept);
        Assert.Contains("[action] edit 5", File.ReadAllText(written[^1]));
        string newest = File.ReadAllText(written[^1]);
        Assert.Contains("run 5", newest);
        Assert.Contains("run ended", newest);
        Assert.Contains("Sloppy Animatsu", newest);
        foreach (string path in kept)
        {
            Assert.Contains("run ended", File.ReadAllText(path));
        }
    }

    [Fact]
    public void AFileStaysInsideItsSizeLimit_AndSaysWhereItStopped()
    {
        const long limit = 16 * 1024;
        AppLog.StartForTesting(_folder, "cli", [], limit);
        for (int line = 0; line < 500; line++)
        {
            AppLog.Info(new string('x', 120) + " " + line);
        }

        AppLog.Shutdown();

        var file = new FileInfo(AppLog.FilePath!);
        Assert.True(file.Length <= limit, $"the log grew to {file.Length} bytes, over its {limit}-byte limit");
        string text = File.ReadAllText(file.FullName);
        Assert.Contains("the rest was dropped", text);
    }

    [Fact]
    public void ACrashWritesEveryFrameAndTheInnerException()
    {
        AppLog.StartForTesting(_folder, "viewer", []);
        AppLog.Crash(CaptureNestedFailure(), "unhandled exception on the UI thread");
        AppLog.Shutdown();

        string text = File.ReadAllText(AppLog.FilePath!);
        Assert.Contains("unhandled exception on the UI thread", text);
        Assert.Contains(nameof(CaptureNestedFailure), text);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.Contains("outer failure", text);
        Assert.Contains("System.IO.IOException", text);
        Assert.Contains("inner failure", text);
        Assert.Contains("End of inner exception stack trace", text);
        Assert.Contains("   at ", text);
    }

    [Fact]
    public void ACrashIsStillRecordedAfterTheOrdinaryLinesFilledTheLog()
    {
        const long limit = 64 * 1024;
        AppLog.StartForTesting(_folder, "cli", [], limit);
        for (int line = 0; line < 2000; line++)
        {
            AppLog.Info(new string('y', 120) + " " + line);
        }

        AppLog.Crash(CaptureNestedFailure(), "crash after the limit");
        AppLog.Shutdown();

        var file = new FileInfo(AppLog.FilePath!);
        Assert.True(file.Length <= limit, $"the log grew to {file.Length} bytes, over its {limit}-byte limit");
        string text = File.ReadAllText(file.FullName);
        Assert.Contains("crash after the limit", text);
        Assert.Contains(nameof(CaptureNestedFailure), text);
    }

    [Fact]
    public void AnUnwritableFolderLeavesLoggingOffInsteadOfThrowing()
    {
        File.WriteAllText(_folder, "not a folder");
        try
        {
            AppLog.StartForTesting(_folder, "viewer", []);
            Assert.Null(AppLog.FilePath);
            Assert.False(AppLog.IsActive);
            AppLog.Info("this must not throw");
        }
        finally
        {
            File.Delete(_folder);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception CaptureNestedFailure()
    {
        try
        {
            try
            {
                throw new IOException("inner failure");
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("outer failure", inner);
            }
        }
        catch (Exception e)
        {
            return e;
        }
    }
}
