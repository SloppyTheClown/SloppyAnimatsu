using SloppyAnimatsu.Cli;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The CLI reports a failed command with exit code 1 instead of ending in an unhandled exception:
/// on Windows that raises the "Application Error" dialog (0xe0434352) and the process waits for a
/// click, which hangs whatever started it.
/// </summary>
public class CliErrorTests
{
    [Fact]
    public void SavingOverADirectory_ReturnsOne_InsteadOfCrashing()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-cli-{Guid.NewGuid():N}.akt");
        Directory.CreateDirectory(directory);
        try
        {
            // File.WriteAllBytes onto a directory is an UnauthorizedAccessException. The command's
            // own catch only covers format and IO exceptions, so this reaches the top-level one.
            // Keep its message and stack out of the runner's own stderr.
            var error = Console.Error;
            Console.SetError(new StringWriter());
            try
            {
                int code = Program.Main(["new", directory]);
                Assert.Equal(1, code);
            }
            finally
            {
                Console.SetError(error);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}