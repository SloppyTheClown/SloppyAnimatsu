using SloppyAnimatsu.Core.Packaging;

namespace PackageTool;

/// <summary>
/// Writes a packaging folder as an archive for packaging\package.ps1 and packaging\package.sh.
///
///   PackageTool tar &lt;source directory&gt; &lt;out.tar.gz&gt; [--exec relative-path]...
///   PackageTool zip &lt;source directory&gt; &lt;out.zip&gt;    [--exec relative-path]...
///
/// A <c>--exec</c> path is stored with 0755 (0755 for folders and <c>.sh</c> files too), the rest
/// with 0644. The tool exists so the same archive comes out on Windows and Linux: the system tar on
/// Windows stores an extension-less apphost as 0666.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 3 || args[0] is not ("tar" or "zip"))
        {
            Console.Error.WriteLine("usage: PackageTool tar|zip <source directory> <archive> [--exec relative-path]...");
            return 2;
        }
        string source = Path.GetFullPath(args[1]);
        string archive = Path.GetFullPath(args[2]);
        var executable = new List<string>();
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] != "--exec" || i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"unexpected argument '{args[i]}'");
                return 2;
            }
            executable.Add(args[++i]);
        }
        if (!Directory.Exists(source))
        {
            Console.Error.WriteLine($"no such directory: {source}");
            return 2;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        if (args[0] == "tar") ArchiveWriter.WriteTarGz(source, archive, executable);
        else ArchiveWriter.WriteZip(source, archive, executable);
        Console.WriteLine($"{archive}: {(new FileInfo(archive).Length / 1024.0 / 1024.0):N1} MB");
        return 0;
    }
}
