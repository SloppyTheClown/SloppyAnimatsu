using SloppyAnimatsu.Core.Diagnostics;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// <see cref="AppFolder"/>: a Windows release keeps its assemblies in lib\, and the install folder
/// (portable profile, fallback logs) is the one above it.
/// </summary>
public class AppFolderTests
{
    [Theory]
    [InlineData(@"C:\Apps\SloppyAnimatsu\lib\", @"C:\Apps\SloppyAnimatsu")]
    [InlineData(@"C:\Apps\SloppyAnimatsu\LIB", @"C:\Apps\SloppyAnimatsu")]
    [InlineData(@"C:\Apps\SloppyAnimatsu\", @"C:\Apps\SloppyAnimatsu\")]
    [InlineData(@"C:\Apps\library\", @"C:\Apps\library\")]
    public void Resolve_StepsOutOfALibFolderOnly(string baseDirectory, string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            baseDirectory = baseDirectory.Replace('\\', '/');
            expected = expected.Replace('\\', '/');
        }
        Assert.Equal(expected, AppFolder.Resolve(baseDirectory));
    }
}
