using SloppyAnimatsu.Core.Updates;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The update check: reading GitHub's release JSON, ordering versions with pre-release labels,
/// choosing the package and install method, and reading a SHA256SUMS list.
/// </summary>
public class UpdateCheckTests
{
    private const string ReleaseJson = """
        {
          "tag_name": "v0.4.0-alpha",
          "name": "SloppyAnimatsu 0.4.0-alpha",
          "html_url": "https://github.com/SloppyTheClown/SloppyAnimatsu/releases/tag/v0.4.0-alpha",
          "body": "Notes",
          "assets": [
            { "name": "SloppyAnimatsu-0.4.0-alpha-win-x64.zip", "size": 10, "browser_download_url": "https://example.invalid/a.zip" },
            { "name": "SloppyAnimatsu-0.4.0-alpha-win-x64-setup.exe", "size": 11, "browser_download_url": "https://example.invalid/s.exe" },
            { "name": "SloppyAnimatsu-0.4.0-alpha-linux-x64.tar.gz", "size": 12, "browser_download_url": "https://example.invalid/l.tar.gz" },
            { "name": "SHA256SUMS", "size": 13, "browser_download_url": "https://example.invalid/sums" }
          ]
        }
        """;

    [Fact]
    public void ParsesTheReleaseJson()
    {
        ReleaseInfo release = UpdateCheck.ParseRelease(ReleaseJson);
        Assert.Equal("v0.4.0-alpha", release.Tag);
        Assert.Equal("0.4.0-alpha", release.Version);
        Assert.Equal("Notes", release.Notes);
        Assert.EndsWith("/releases/tag/v0.4.0-alpha", release.PageUrl);
        Assert.Equal(4, release.Assets.Count);
        Assert.Equal(11, release.Assets[1].Size);
    }

    [Fact]
    public void ARelaseWithoutATagIsRefused() =>
        Assert.Throws<FormatException>(() => UpdateCheck.ParseRelease("""{ "name": "x" }"""));

    [Theory]
    [InlineData("0.4.0-alpha", "0.3.0-alpha", 1)]
    [InlineData("v0.3.0-alpha", "0.3.0-alpha", 0)]
    [InlineData("0.3.0", "0.3.0-alpha", 1)]
    [InlineData("0.3.0-alpha", "0.3.0", -1)]
    [InlineData("0.10.0", "0.9.9", 1)]
    [InlineData("0.3.1-alpha", "0.3.0", 1)]
    [InlineData("0.3.0-beta", "0.3.0-alpha", 1)]
    [InlineData("0.3.0-beta.10", "0.3.0-beta.2", 1)]
    [InlineData("0.3.0-alpha.1", "0.3.0-alpha", 1)]
    [InlineData("0.3.0-alpha+abc", "0.3.0-alpha", 0)]
    [InlineData("1.0", "1.0.0", 0)]
    public void OrdersVersions(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(UpdateCheck.CompareVersions(a, b)));
        Assert.Equal(-expected, Math.Sign(UpdateCheck.CompareVersions(b, a)));
    }

    [Fact]
    public void PicksThePackageAndMethod()
    {
        ReleaseInfo release = UpdateCheck.ParseRelease(ReleaseJson);
        var setup = UpdateCheck.ChooseMethod(release, "win-x64", installedBySetup: true, folderWritable: false, isBuildTree: false);
        Assert.Equal(UpdateMethod.Setup, setup.Method);
        Assert.EndsWith("-win-x64-setup.exe", setup.Asset!.Name);

        var portable = UpdateCheck.ChooseMethod(release, "win-x64", installedBySetup: false, folderWritable: true, isBuildTree: false);
        Assert.Equal(UpdateMethod.Archive, portable.Method);
        Assert.EndsWith("-win-x64.zip", portable.Asset!.Name);

        var readOnly = UpdateCheck.ChooseMethod(release, "win-x64", installedBySetup: false, folderWritable: false, isBuildTree: false);
        Assert.Equal(UpdateMethod.Setup, readOnly.Method);

        var linux = UpdateCheck.ChooseMethod(release, "linux-x64", installedBySetup: false, folderWritable: true, isBuildTree: false);
        Assert.Equal(UpdateMethod.Archive, linux.Method);
        Assert.EndsWith("-linux-x64.tar.gz", linux.Asset!.Name);

        Assert.Equal(UpdateMethod.ReleasePage,
            UpdateCheck.ChooseMethod(release, "linux-x64", false, folderWritable: false, isBuildTree: false).Method);
        Assert.Equal(UpdateMethod.ReleasePage,
            UpdateCheck.ChooseMethod(release, "osx-arm64", false, true, false).Method);
        Assert.Equal(UpdateMethod.ReleasePage,
            UpdateCheck.ChooseMethod(release, "win-x64", true, true, isBuildTree: true).Method);
    }

    [Theory]
    [InlineData(@"Z:\repo\src\SloppyAnimatsu.Viewer\bin\Debug\net9.0", true)]
    [InlineData("/home/u/repo/src/x/bin/Release/net9.0/linux-x64", true)]
    [InlineData(@"C:\Program Files\SloppyAnimatsu", false)]
    [InlineData(@"Z:\repo\release", false)]
    public void RecognisesABuildTree(string folder, bool expected) =>
        Assert.Equal(expected, UpdateCheck.IsBuildTree(folder));

    [Fact]
    public void ReadsTheChecksumList()
    {
        const string sums = "ABCDEF  SloppyAnimatsu-0.4.0-alpha-win-x64.zip\r\n123456 *SloppyAnimatsu-0.4.0-alpha-linux-x64.tar.gz\n";
        Assert.Equal("abcdef", UpdateCheck.ChecksumFor(sums, "SloppyAnimatsu-0.4.0-alpha-win-x64.zip"));
        Assert.Equal("123456", UpdateCheck.ChecksumFor(sums, "SloppyAnimatsu-0.4.0-alpha-linux-x64.tar.gz"));
        Assert.Null(UpdateCheck.ChecksumFor(sums, "missing.zip"));
    }
}
