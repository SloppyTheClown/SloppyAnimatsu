using System.Formats.Tar;
using System.IO.Compression;

namespace SloppyAnimatsu.Core.Packaging;

/// <summary>
/// Writes a folder as a <c>.tar.gz</c> or a <c>.zip</c> with explicit Unix permissions, so a Linux
/// package built on Windows still extracts with executable binaries and scripts. The system
/// <c>tar</c> on Windows maps modes from the file extension and writes an extension-less apphost
/// as 0666, which is why this exists.
/// </summary>
public static class ArchiveWriter
{
    private const UnixFileMode FolderMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode RegularMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>
    /// Writes <paramref name="sourceDirectory"/>'s contents into <paramref name="archivePath"/> as a
    /// gzip-compressed tar, with <paramref name="executablePaths"/> (relative, <c>/</c>-separated)
    /// as 0755. A file whose name ends in <c>.sh</c> is executable without being listed.
    /// </summary>
    public static void WriteTarGz(string sourceDirectory, string archivePath, IEnumerable<string>? executablePaths = null)
    {
        var executables = ExecutableSet(sourceDirectory, executablePaths);
        string root = Path.GetFullPath(sourceDirectory);
        using var file = File.Create(archivePath);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax);
        foreach (var (path, relative, isDirectory) in Enumerate(root))
        {
            var entry = new PaxTarEntry(isDirectory ? TarEntryType.Directory : TarEntryType.RegularFile,
                isDirectory ? relative + "/" : relative);
            entry.Mode = isDirectory || executables.Contains(relative) ? FolderMode : RegularMode;
            entry.Uid = 0;
            entry.Gid = 0;
            entry.ModificationTime = File.GetLastWriteTimeUtc(path);
            if (isDirectory)
            {
                writer.WriteEntry(entry);
                continue;
            }
            // The writer reads the stream but does not own it; dispose it here so the source file is
            // not still open when the next package is built.
            using var data = File.OpenRead(path);
            entry.DataStream = data;
            writer.WriteEntry(entry);
        }
    }

    /// <summary>
    /// Writes <paramref name="sourceDirectory"/>'s contents into <paramref name="archivePath"/> as a
    /// zip, storing each entry's Unix mode in its external attributes. The Linux package is a tar.gz
    /// (where the modes always apply); this keeps the Windows zip self-describing for tools that read
    /// the attributes, though the zip's host field stays MS-DOS because ZipArchive does not set it.
    /// </summary>
    public static void WriteZip(string sourceDirectory, string archivePath, IEnumerable<string>? executablePaths = null)
    {
        var executables = ExecutableSet(sourceDirectory, executablePaths);
        string root = Path.GetFullPath(sourceDirectory);
        // ZipArchiveMode.Create refuses an existing file; packaging overwrites last time's package.
        if (File.Exists(archivePath)) File.Delete(archivePath);
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var (path, relative, isDirectory) in Enumerate(root))
        {
            if (isDirectory) continue; // A zip's folder comes from its entries' paths.
            var entry = zip.CreateEntry(relative, CompressionLevel.SmallestSize);
            entry.LastWriteTime = File.GetLastWriteTime(path);
            var mode = executables.Contains(relative) ? FolderMode : RegularMode;
            entry.ExternalAttributes = (int)((uint)mode << 16);
            using var target = entry.Open();
            using var source = File.OpenRead(path);
            source.CopyTo(target);
        }
    }

    /// <summary>The archive paths that get 0755: the listed ones plus every <c>.sh</c> file.</summary>
    private static HashSet<string> ExecutableSet(string sourceDirectory, IEnumerable<string>? executablePaths)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        string root = Path.GetFullPath(sourceDirectory);
        foreach (var path in executablePaths ?? [])
            set.Add(path.Replace('\\', '/').TrimStart('/'));
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".sh", StringComparison.Ordinal))
                set.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }
        return set;
    }

    /// <summary>Every file and folder below <paramref name="root"/>, parents first, sorted by path.</summary>
    private static IEnumerable<(string Path, string Relative, bool IsDirectory)> Enumerate(string root)
    {
        var items = new List<(string Path, string Relative, bool IsDirectory)>();
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
            // Skip build leftovers that a publish folder never has, but a copied docs tree might.
            items.Add((directory, relative, true));
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            items.Add((file, Path.GetRelativePath(root, file).Replace('\\', '/'), false));
        return items.OrderBy(i => i.Relative, StringComparer.Ordinal);
    }
}

