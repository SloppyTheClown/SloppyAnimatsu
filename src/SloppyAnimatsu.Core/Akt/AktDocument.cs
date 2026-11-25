namespace SloppyAnimatsu.Core.Akt;

/// <summary>The 5 x u16 version header.</summary>
public readonly record struct AktVersion(short Major, short Minor, short Build, short Revision, short Extra)
{
    /// <summary>The .akt format version stamped on save (20.3.13).</summary>
    public static readonly AktVersion Current = new(20, 3, 13, 0, 0);
    /// <summary>The version assumed for legacy files without a header.</summary>
    public static readonly AktVersion Legacy = new(-1, 3, 2, 0, 0);

    public override string ToString() => $"{Major}.{Minor}.{Build}.{Revision}.{Extra}";
}

/// <summary>A whole .akt file: schema plus object tree, independent of what the classes mean.</summary>
public sealed class AktDocument
{
    public AktVersion Version { get; set; } = AktVersion.Current;
    /// <summary>False for legacy files whose header failed validation; the writer then omits it.</summary>
    public bool HasHeader { get; set; } = true;
    public AktSchema Schema { get; }
    /// <summary>Top-level objects: one CScene for scenes; several preference objects in preferences.akt.</summary>
    public List<AktObject> Roots { get; }
    /// <summary>The first top-level object (the scene, for scene files).</summary>
    public AktObject Root => Roots[0];
    /// <summary>The atom table as read (diagnostics only; the writer rebuilds it).</summary>
    public IReadOnlyList<string> SourceAtoms { get; init; } = Array.Empty<string>();
    /// <summary>
    /// How the file was stored: the plain .akt layout or a SloppyAnimatsu container (and its version).
    /// A document built in memory reports <see cref="AktFileFormat.Akeytsu"/>.
    /// </summary>
    public AktFileFormat SourceFormat { get; set; } = AktFileFormat.Akeytsu;

    public AktDocument(AktSchema schema, IEnumerable<AktObject> roots)
    {
        Schema = schema;
        Roots = roots.ToList();
        if (Roots.Count == 0) throw new ArgumentException("A document needs at least one root object.");
    }

    /// <summary>All objects in pre-order over all roots; position = reference index.</summary>
    public IEnumerable<AktObject> AllObjects() => Roots.SelectMany(r => r.PreOrder());

    public List<AktObject> Objects() => AllObjects().ToList();

    public static AktDocument Load(string path) => AktReader.Read(File.ReadAllBytes(path));

    /// <summary>Saves in the plain .akt layout (preferences, shortcuts and other shared files).</summary>
    public void Save(string path) => Save(path, AktFileFormat.Akeytsu);

    /// <summary>Saves in <paramref name="format"/>: the plain .akt layout, or a SloppyAnimatsu container.</summary>
    public void Save(string path, AktFileFormat format) => File.WriteAllBytes(path, Write(format));

    /// <summary>Saves in <paramref name="format"/>, reporting 0 to 1 as objects are written and the file goes to disk.</summary>
    public void Save(string path, AktFileFormat format, IProgress<double>? progress)
    {
        var payload = AktWriter.Write(this, OmittedFor(format), progress.Slice(0, 0.7), EmptiedFor(format));
        var bytes = AktContainer.Wrap(payload, format);
        progress?.Report(0.9);
        WriteProgress.WriteFile(path, bytes, progress.Slice(0.9, 1));
    }

    /// <summary>
    /// The file bytes in <paramref name="format"/>. The plain .akt layout leaves out SloppyAnimatsu's own
    /// members (<see cref="AkeytsuCompatibility.OmittedMembers"/>), so the file holds exactly the original scheme,
    /// and writes empty the weights of meshes waiting to be bound in another character
    /// (<see cref="AkeytsuCompatibility.EmptiedFields"/>).
    /// </summary>
    public byte[] Write(AktFileFormat format) =>
        AktContainer.Wrap(AktWriter.Write(this, OmittedFor(format), null, EmptiedFor(format)), format);

    private static IReadOnlySet<string>? OmittedFor(AktFileFormat format) =>
        format.Kind == AktFileKind.Akeytsu ? AkeytsuCompatibility.OmittedMembers : null;

    private IReadOnlyDictionary<AktObject, IReadOnlySet<string>>? EmptiedFor(AktFileFormat format) =>
        format.Kind == AktFileKind.Akeytsu ? AkeytsuCompatibility.EmptiedFields(this) : null;
}

public sealed class AktFormatException : Exception
{
    public long Offset { get; }
    public AktFormatException(string message, long offset) : base($"{message} (at offset 0x{offset:X})") => Offset = offset;
}
