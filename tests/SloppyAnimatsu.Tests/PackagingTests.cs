using System.Formats.Tar;
using System.IO.Compression;
using SloppyAnimatsu.Core.Packaging;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// <see cref="ArchiveWriter"/> behind tools/PackageTool, which packaging\package.ps1 and
/// packaging\package.sh use. A Linux package built on Windows is unusable when its apphost is
/// stored 0666, so the permissions are checked and not only the contents.
/// </summary>
public sealed class PackagingTests
{
    [Fact]
    public void TarStoresExecutablesAs0755AndDocumentsAs0644()
    {
        string source = MakeTree();
        string archive = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-pack-{Guid.NewGuid():N}.tar.gz");
        try
        {
            ArchiveWriter.WriteTarGz(source, archive, ["SloppyAnimatsu.Viewer", "SloppyAnimatsu.Cli"]);

            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            var entries = ReadEntries(reader);
            Assert.Equal(new[] { "README.txt", "SloppyAnimatsu.Cli", "SloppyAnimatsu.Viewer", "docs", "docs/user-guide.md", "install.sh" }, Paths(entries));
            Assert.True(IsExecutable(entries["SloppyAnimatsu.Cli"]), "a listed binary is 0755");
            Assert.True(IsExecutable(entries["SloppyAnimatsu.Viewer"]), "the viewer binary is 0755");
            Assert.True(IsExecutable(entries["install.sh"]), "a .sh file is 0755 without being listed");
            Assert.False(IsExecutable(entries["README.txt"]), "a document is 0644");
            // A folder is 0755: it keeps the search bit, which is what the mode check above sees.
            Assert.Equal(0b111_101_101, (int)entries["docs"].Mode);
            Assert.Equal(0, entries["README.txt"].Uid);
            Assert.Equal(0, entries["README.txt"].Gid);
            Assert.Equal("hello readme", ReadText(entries["README.txt"]));
            Assert.Equal("hello viewer", ReadText(entries["SloppyAnimatsu.Viewer"]));
        }
        finally
        {
            Directory.Delete(source, true);
            File.Delete(archive);
        }
    }

    [Fact]
    public void ZipStoresTheSameContentsWithTheirUnixModes()
    {
        string source = MakeTree();
        string archive = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-pack-{Guid.NewGuid():N}.zip");
        try
        {
            ArchiveWriter.WriteZip(source, archive, ["SloppyAnimatsu.Viewer", "SloppyAnimatsu.Cli"]);

            using var zip = ZipFile.OpenRead(archive);
            var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "README.txt", "SloppyAnimatsu.Cli", "SloppyAnimatsu.Viewer", "docs/user-guide.md", "install.sh" }, names);
            Assert.True(IsExecutable(ModeOf(zip, "SloppyAnimatsu.Cli")), "a listed binary is 0755");
            Assert.False(IsExecutable(ModeOf(zip, "README.txt")), "a document is 0644");
            using var reader = new StreamReader(zip.GetEntry("docs/user-guide.md")!.Open());
            Assert.Equal("hello doc", reader.ReadToEnd());
        }
        finally
        {
            Directory.Delete(source, true);
            File.Delete(archive);
        }
    }

    [Fact]
    public void AnUnknownExecutableNameDoesNotBecomeExecutable()
    {
        string source = MakeTree();
        string archive = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-pack-{Guid.NewGuid():N}.tar.gz");
        try
        {
            ArchiveWriter.WriteTarGz(source, archive, ["../outside", "/absolute"]);
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            var entries = ReadEntries(reader);
            Assert.False(IsExecutable(entries["README.txt"]));
            Assert.True(IsExecutable(entries["install.sh"]), "the .sh rule still applies");
        }
        finally
        {
            Directory.Delete(source, true);
            File.Delete(archive);
        }
    }

    [Fact]
    public void WritingAPackageTwiceOverwritesTheArchive()
    {
        string source = MakeTree();
        string tar = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-pack-{Guid.NewGuid():N}.tar.gz");
        string zip = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-pack-{Guid.NewGuid():N}.zip");
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                ArchiveWriter.WriteTarGz(source, tar, ["SloppyAnimatsu.Viewer", "SloppyAnimatsu.Cli"]);
                ArchiveWriter.WriteZip(source, zip, ["SloppyAnimatsu.Viewer", "SloppyAnimatsu.Cli"]);
            }
            Assert.True(new FileInfo(tar).Length > 0);
            Assert.True(new FileInfo(zip).Length > 0);
        }
        finally
        {
            Directory.Delete(source, true);
            File.Delete(tar);
            File.Delete(zip);
        }
    }

    /// <summary>A package-shaped folder: two apphosts, a script, a document and a docs folder.</summary>
    private static string MakeTree()
    {
        string root = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "SloppyAnimatsu.Viewer"), "hello viewer");
        File.WriteAllText(Path.Combine(root, "SloppyAnimatsu.Cli"), "hello cli");
        File.WriteAllText(Path.Combine(root, "install.sh"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(root, "README.txt"), "hello readme");
        File.WriteAllText(Path.Combine(root, "docs", "user-guide.md"), "hello doc");
        return root;
    }

    /// <summary>
    /// The archive's entries by name. Each entry's data is copied to memory, because the reader's
    /// stream belongs to the reader and is no longer valid once it has moved to the next entry.
    /// </summary>
    private static Dictionary<string, TarEntry> ReadEntries(TarReader reader)
    {
        var entries = new Dictionary<string, TarEntry>(StringComparer.Ordinal);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.DataStream is { } stream)
            {
                var memory = new MemoryStream();
                stream.CopyTo(memory);
                memory.Position = 0;
                entry.DataStream = memory;
            }
            entries[entry.Name.TrimEnd('/')] = entry;
        }
        return entries;
    }

    private static string[] Paths(Dictionary<string, TarEntry> entries) =>
        entries.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private static UnixFileMode ModeOf(ZipArchive zip, string name) =>
        (UnixFileMode)((uint)zip.GetEntry(name)!.ExternalAttributes >> 16);

    private static bool IsExecutable(TarEntry entry) => IsExecutable(entry.Mode);

    private static bool IsExecutable(UnixFileMode mode) =>
        (mode & UnixFileMode.UserExecute) != 0 && (mode & UnixFileMode.OtherExecute) != 0;

    private static string ReadText(TarEntry entry)
    {
        using var reader = new StreamReader(entry.DataStream!);
        return reader.ReadToEnd();
    }
}

