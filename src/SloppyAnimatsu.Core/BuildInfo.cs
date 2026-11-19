using System.Reflection;

namespace SloppyAnimatsu.Core;

/// <summary>
/// The version of this build. Every project takes it from <c>Directory.Build.props</c>, so the
/// command line, the viewer's About dialog and the packaged artifact names agree; the packaging
/// scripts read the same property.
/// </summary>
public static class BuildInfo
{
    /// <summary>"0.1.0-alpha", or "0.0.0" when the assembly carries no version.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>"Sloppy Animatsu 0.1.0-alpha", for a window title or a first line of output.</summary>
    public static string Name => $"Sloppy Animatsu {Version}";

    private static string ReadVersion()
    {
        // The informational version keeps the pre-release label ("-alpha"); the SDK appends
        // "+<commit>" to it, which is dropped here.
        Assembly assembly = typeof(BuildInfo).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }
        var version = assembly.GetName().Version;
        return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
