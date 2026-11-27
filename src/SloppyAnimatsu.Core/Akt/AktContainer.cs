using System.Buffers.Binary;
using System.IO.Compression;

namespace SloppyAnimatsu.Core.Akt;

/// <summary>How a file is stored: the plain .akt layout, or SloppyAnimatsu's versioned container around it.</summary>
public enum AktFileKind
{
    /// <summary>The plain .akt layout (version header, atoms, schemas, hierarchy).</summary>
    Akeytsu,
    /// <summary>SloppyAnimatsu's container (<see cref="AktContainer"/>); plain .akt readers cannot open it.</summary>
    SloppyAnimatsu,
}

/// <summary>The payload codec of a SloppyAnimatsu container.</summary>
public enum AktCodec: uint
{
    Stored = 0,
    /// <summary>zlib (RFC 1950), readable by any zlib binding.</summary>
    Zlib = 1,
    /// <summary>Brotli (RFC 7932), quality <see cref="AktContainer.BrotliQuality"/>; the default.</summary>
    Brotli = 2,
}

/// <summary>How a file is (or is to be) stored on disk.</summary>
public readonly record struct AktFileFormat(AktFileKind Kind, ushort ContainerVersion, AktCodec Codec)
{
    /// <summary>The plain .akt layout, byte for byte.</summary>
    public static readonly AktFileFormat Akeytsu = new(AktFileKind.Akeytsu, 0, AktCodec.Stored);

    /// <summary>The current SloppyAnimatsu container with the default codec: what scenes are saved as.</summary>
    public static readonly AktFileFormat SloppyAnimatsu = new(AktFileKind.SloppyAnimatsu, AktContainer.CurrentVersion, AktCodec.Brotli);

    public override string ToString() =>
        Kind == AktFileKind.Akeytsu ? "akeytsu": $"SloppyAnimatsu container v{ContainerVersion} ({Codec.ToString().ToLowerInvariant()})";
}

/// <summary>
/// Scene file names. SloppyAnimatsu saves <c>.slopa</c> by default and opens <c>.akt</c>
/// as well; what is inside is decided by the magic, never by the extension.
/// </summary>
public static class SceneFiles
{
    /// <summary>The default scene extension, with its dot.</summary>
    public const string Extension = ".slopa";

    /// <summary>The .akt scene extension, with its dot; opened and saved too.</summary>
    public const string AkeytsuExtension = ".akt";

    /// <summary>File-dialog patterns for every scene SloppyAnimatsu opens.</summary>
    public static readonly string[] Patterns = ["*" + Extension, "*" + AkeytsuExtension];

    /// <summary>True when <paramref name="path"/> ends in <c>.slopa</c> or <c>.akt</c>.</summary>
    public static bool HasSceneExtension(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(AkeytsuExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every <c>.slopa</c> and <c>.akt</c> file under <paramref name="folder"/>, in path order.</summary>
    public static IEnumerable<string> Enumerate(string folder, SearchOption option = SearchOption.AllDirectories) =>
        Directory.EnumerateFiles(folder, "*", option).Where(HasSceneExtension).Order();

    /// <summary>True when <paramref name="path"/> ends in <c>.akt</c>: a scene saved in the plain .akt layout.</summary>
    public static bool IsAkeytsuName(string path) =>
        path.EndsWith(AkeytsuExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The format a scene saved to <paramref name="path"/> gets: an <c>.akt</c> is always written in
    /// the plain .akt layout (SloppyAnimatsu's extra members dropped), anything else in
    /// <paramref name="otherwise"/>.
    /// </summary>
    public static AktFileFormat FormatFor(string path, AktFileFormat otherwise) =>
        IsAkeytsuName(path) ? AktFileFormat.Akeytsu: otherwise;
}

/// <summary>
/// What a plain .akt save leaves out: the class members SloppyAnimatsu adds to the .akt
/// schema (<see cref="MaterialSchema"/>). Readers may skip unknown members on load, but an
/// <c>.akt</c> is written in the original scheme so it holds nothing the format does not define
///.
/// </summary>
public static class AkeytsuCompatibility
{
    public static readonly IReadOnlySet<string> OmittedMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        MaterialSchema.TexturesMember,
        MaterialSchema.NamesMember,
        MaterialSchema.ActiveMember,
        MaterialSchema.OpaqueMember,
        MaterialSchema.NearestMember,
        MaterialSchema.KeyMaterialsMember,
        MaterialSchema.KeyTimesMember,
        MaterialSchema.KeyIndicesMember,
        MaterialSchema.CompensateTargetsMember,
        MaterialSchema.CompensateFramesMember,
        MaterialSchema.CompensateValuesMember,
        MaterialSchema.AddedOrderMember,
        MaterialSchema.ViewFlatMember,
        MaterialSchema.PluginIdsMember,
        MaterialSchema.PluginDataMember,
        MaterialSchema.PluginRefsMember,
        MaterialSchema.PluginRefOwnersMember,
        MaterialSchema.SharedBankCharactersMember,
        MaterialSchema.SharedBankGroupsMember,
        MaterialSchema.SharedBankItemsMember,
        MaterialSchema.SharedBankItemRowsMember,
        MaterialSchema.CheckPoseTargetsMember,
        MaterialSchema.CheckPoseValuesMember,
        MaterialSchema.CheckPoseFeetMember,
        MaterialSchema.CheckPoseFootAnglesMember,
    };

    /// <summary>What a reader is told is lost, one item per line.</summary>
    public static readonly string[] LostFeatures =
    [
        "extra textures on a material (texture swap lists) and their display names",
        "which texture of a swap list is active",
        "the Opaque and Nearest filtering material settings",
        "texture swap keys in animations",
        "Compensate Scale keys in animations",
        "the Anim Bank's Added order (when each animation was added)",
        "whether the main view was flat (the view itself is kept)",
        "plugin data, e.g. EZ Exports presets",
        "shared Anim Bank links between characters (each keeps its own copy of the bank)",
        "compression (the file is several times larger)",
        "the old weights of a mesh moved into another character and not bound there yet (Bind Skin solves new ones)",
        "which objects inside a hidden character or group are shown (they are saved hidden, so showing the parent alone no longer shows them)",
    ];

    /// <summary>The weight arrays <see cref="EmptiedFields"/> may empty.</summary>
    private static readonly IReadOnlySet<string> WeightArrays =
        new HashSet<string>(StringComparer.Ordinal) { "SkinWeightArray", "RigidSkinWeightArray" };

    /// <summary>
    /// The vector members a plain .akt save writes empty, per object: the weight arrays of an
    /// unbound mesh (<c>Skinned</c> false) that sits inside a character other than the one its
    /// <c>BindPose</c> belongs to. Moving a skinned mesh into another character unbinds it but keeps
    /// its weights, so a later Bind Skin there can carry them across by joint name; those weights
    /// still index the old character's joints, and a reader that takes them against the new
    /// character's joints fails to draw the mesh (always so when the new character has no joints).
    /// A mesh outside every character keeps its weights, as Unbind Skin leaves it.
    /// </summary>
    public static IReadOnlyDictionary<AktObject, IReadOnlySet<string>> EmptiedFields(AktDocument doc)
    {
        var emptied = new Dictionary<AktObject, IReadOnlySet<string>>(ReferenceEqualityComparer.Instance);
        foreach (var obj in doc.AllObjects())
        {
            if (!IsA(doc, obj, "CMesh") || IsTrue(obj["Skinned"])) continue;
            var owner = CharacterOf(doc, obj.Parent);
            if (owner == null) continue;
            var boundTo = CharacterOf(doc, (obj["BindPose"] as AktReference)?.Target);
            if (ReferenceEquals(boundTo, owner)) continue;
            if (WeightArrays.Any(name => obj[name] is ICollection<object?> { Count: > 0 }))
                emptied[obj] = WeightArrays;
        }
        return emptied;
    }

    /// <summary>
    /// The member values a plain .akt save writes differently, per object: the shown bit
    /// (<c>Flags</c> bit 0) of every 3D object under a hidden 3D ancestor is written clear. SloppyAnimatsu
    /// hides a character or folder by its own bit and draws what is under it only while every
    /// ancestor is shown, so a child keeps its own state across the parent's hide and show; a
    /// reader that draws each object by its own bit alone would otherwise show the hidden
    /// character's meshes and joints. The document itself keeps the children's own bits.
    /// </summary>
    public static IReadOnlyDictionary<AktObject, IReadOnlyDictionary<string, object?>> ReplacedFields(AktDocument doc)
    {
        var replaced = new Dictionary<AktObject, IReadOnlyDictionary<string, object?>>(ReferenceEqualityComparer.Instance);
        foreach (var root in doc.Roots)
            HideUnderHidden(doc, root, ancestorHidden: false, replaced);
        return replaced;
    }

    private static void HideUnderHidden(AktDocument doc, AktObject obj, bool ancestorHidden,
        Dictionary<AktObject, IReadOnlyDictionary<string, object?>> replaced)
    {
        bool is3D = IsA(doc, obj, "CObject3D");
        object? flags = is3D ? obj["Flags"] : null;
        bool shown = flags switch { uint u => (u & 1) != 0, int i => (i & 1) != 0, _ => true };
        // Only a stored flag word is rewritten; an object without one keeps whatever the writer emits.
        object? cleared = flags switch { uint u => u & ~1u, int i => i & ~1, _ => null };
        if (ancestorHidden && shown && cleared != null)
            replaced[obj] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Flags"] = cleared };
        bool hiddenBelow = ancestorHidden || (is3D && !shown);
        foreach (var child in obj.Children)
            HideUnderHidden(doc, child, hiddenBelow, replaced);
    }

    private static bool IsA(AktDocument doc, AktObject obj, string classKey) =>
        doc.Schema.FindClass(obj.TypeName)?.IsA(classKey) == true;

    private static bool IsTrue(object? value) => value is bool b ? b : value is byte raw && raw != 0;

    /// <summary><paramref name="node"/> or its closest ancestor that is a character; null when none.</summary>
    private static AktObject? CharacterOf(AktDocument doc, AktObject? node)
    {
        for (var o = node; o != null; o = o.Parent)
            if (IsA(doc, o, "CCharacter")) return o;
        return null;
    }
}

/// <summary>
/// SloppyAnimatsu's scene container. A
/// fixed 32-byte header followed by the payload, which is the .akt byte stream exactly as
/// <see cref="AktWriter"/> produces it, compressed:
/// <code>
/// 0   8  magic "SLOPA\r\n\x1A"
/// 8   2  container version (u16, 1)
/// 10  2  flags (u16, 0; reserved)
/// 12  4  codec (u32: 0 stored, 1 zlib, 2 brotli)
/// 16  8  payload length after decoding (u64)
/// 24  8  payload length as stored (u64)
/// 32.. payload
/// </code>
/// The magic's first u16 (0x4C53; the legacy one's 0x594D) is outside the .akt version-header
/// range, so no plain .akt file starts this way, and a plain .akt reader refuses the container instead of
/// misreading it.
/// </summary>
public static class AktContainer
{
    public const ushort CurrentVersion = 1;
    public const int HeaderSize = 32;
    public const int BrotliQuality = 4;
    private const int BrotliWindow = 24;

    /// <summary>The magic every container is written with.</summary>
    public static ReadOnlySpan<byte> Magic => "SLOPA\r\n\u001A"u8;

    /// <summary>The magic of containers written under the SloppyAnimatsu name; read, never written.</summary>
    public static ReadOnlySpan<byte> LegacyMagic => "MYAKT\r\n\u001A"u8;

    /// <summary>True when <paramref name="data"/> starts with the container magic (current or legacy).</summary>
    public static bool IsContainer(ReadOnlySpan<byte> data) =>
        data.Length >= Magic.Length &&
        (data[..Magic.Length].SequenceEqual(Magic) || data[..LegacyMagic.Length].SequenceEqual(LegacyMagic));

    /// <summary>True when <paramref name="data"/> starts with the legacy "MYAKT" magic.</summary>
    public static bool HasLegacyMagic(ReadOnlySpan<byte> data) =>
        data.Length >= LegacyMagic.Length && data[..LegacyMagic.Length].SequenceEqual(LegacyMagic);

    /// <summary>
    /// The .akt byte stream inside <paramref name="data"/> and the format it was stored in; a
    /// file without the magic is returned as it is (a plain .akt file).
    /// </summary>
    public static (byte[] Payload, AktFileFormat Format) Unwrap(byte[] data)
    {
        if (!IsContainer(data))
            return (data, AktFileFormat.Akeytsu);
        if (data.Length < HeaderSize)
            throw new AktFormatException("SloppyAnimatsu container header is truncated", data.Length);
        var span = data.AsSpan();
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        if (version == 0 || version > CurrentVersion)
            throw new AktFormatException(
                $"SloppyAnimatsu container version {version} is newer than this build reads (up to {CurrentVersion})", 8);
        var codec = (AktCodec)BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        ulong decoded = BinaryPrimitives.ReadUInt64LittleEndian(span[16..]);
        ulong stored = BinaryPrimitives.ReadUInt64LittleEndian(span[24..]);
        if (stored != (ulong)(data.Length - HeaderSize))
            throw new AktFormatException($"SloppyAnimatsu container payload is {data.Length - HeaderSize} bytes, header says {stored}", 24);
        if (decoded > int.MaxValue)
            throw new AktFormatException($"SloppyAnimatsu container payload of {decoded} bytes is too large", 16);
        var payload = span[HeaderSize..];
        var output = new byte[(int)decoded];
        switch (codec)
        {
            case AktCodec.Stored:
                if (payload.Length != output.Length)
                    throw new AktFormatException("Stored payload length does not match the header", 16);
                payload.CopyTo(output);
                break;
            case AktCodec.Zlib:
            {
                using var z = new ZLibStream(new MemoryStream(data, HeaderSize, payload.Length, writable: false), CompressionMode.Decompress);
                int n = 0, read;
                while (n < output.Length && (read = z.Read(output, n, output.Length - n)) > 0) n += read;
                if (n != output.Length || z.ReadByte() >= 0)
                    throw new AktFormatException("zlib payload does not decode to the length in the header", HeaderSize);
                break;
            }
            case AktCodec.Brotli:
                if (!BrotliDecoder.TryDecompress(payload, output, out int written) || written != output.Length)
                    throw new AktFormatException("Brotli payload does not decode to the length in the header", HeaderSize);
                break;
            default:
                throw new AktFormatException($"Unknown SloppyAnimatsu container codec {(uint)codec}", 12);
        }
        return (output, new AktFileFormat(AktFileKind.SloppyAnimatsu, version, codec));
    }

    /// <summary>Wraps an .akt byte stream in a container of <paramref name="format"/>, or returns it as is for the plain .akt format.</summary>
    public static byte[] Wrap(byte[] akt, AktFileFormat format)
    {
        if (format.Kind == AktFileKind.Akeytsu)
            return akt;
        if (format.ContainerVersion != CurrentVersion)
            throw new ArgumentException($"Only container version {CurrentVersion} can be written.", nameof(format));
        byte[] body;
        int bodyLength;
        switch (format.Codec)
        {
            case AktCodec.Stored:
                body = akt;
                bodyLength = akt.Length;
                break;
            case AktCodec.Zlib:
            {
                var ms = new MemoryStream(akt.Length / 4 + 1024);
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                    z.Write(akt);
                body = ms.GetBuffer();
                bodyLength = (int)ms.Length;
                break;
            }
            case AktCodec.Brotli:
            {
                body = new byte[BrotliEncoder.GetMaxCompressedLength(akt.Length)];
                if (!BrotliEncoder.TryCompress(akt, body, out bodyLength, BrotliQuality, BrotliWindow))
                    throw new InvalidOperationException("Brotli compression failed.");
                break;
            }
            default:
                throw new ArgumentException($"Unknown codec {format.Codec}.", nameof(format));
        }
        var result = new byte[HeaderSize + bodyLength];
        var span = result.AsSpan();
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], format.ContainerVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)format.Codec);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], (ulong)akt.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(span[24..], (ulong)bodyLength);
        body.AsSpan(0, bodyLength).CopyTo(span[HeaderSize..]);
        return result;
    }
}