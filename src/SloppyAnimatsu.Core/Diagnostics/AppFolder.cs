namespace SloppyAnimatsu.Core.Diagnostics;

/// <summary>
/// The folder the program is installed in. A Windows release keeps only its exes at the top and
/// every assembly in a <c>lib</c> subfolder, so <see cref="AppContext.BaseDirectory"/> (where the
/// assemblies are) is <c>lib</c>; files that belong to the installation as a whole - the portable
/// profile, the fallback log folder - live one level up, beside the exes.
/// </summary>
public static class AppFolder
{
    /// <summary>The install folder: the base directory, or its parent when that is a <c>lib</c> folder.</summary>
    public static string Install { get; } = Resolve(AppContext.BaseDirectory);

    /// <summary>Resolves the install folder for an assembly folder (public for tests).</summary>
    public static string Resolve(string baseDirectory)
    {
        string trimmed = baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(trimmed), "lib", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrEmpty(parent))
            {
                return parent;
            }
        }
        return baseDirectory;
    }
}
