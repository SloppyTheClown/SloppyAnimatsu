using System.Buffers.Binary;
using System.Text;

namespace SloppyAnimatsu.Core.Fbx;

/// <summary>
/// One node of an FBX file to write: a name, its properties and its children
/// </summary>
public sealed class FbxOutNode(string name)
{
    public string Name { get; } = name;

    /// <summary>Property values: bool, short, int, long, float, double, string, byte[] or an array of those.</summary>
    public List<object> Properties { get; } = [];

    public List<FbxOutNode> Children { get; } = [];

    public FbxOutNode With(params object[] properties)
    {
        Properties.AddRange(properties);
        return this;
    }

    /// <summary>Appends a child node and returns it.</summary>
    public FbxOutNode Add(string childName)
    {
        var child = new FbxOutNode(childName);
        Children.Add(child);
        return child;
    }

    public FbxOutNode Add(string childName, params object[] properties) => Add(childName).With(properties);

    public override string ToString() => $"{Name} ({Properties.Count} properties, {Children.Count} children)";
}

/// <summary>
/// Writes binary FBX version 7400, the layout the exporter produces
/// </summary>
/// <remarks>
/// The format is public: a 27-byte header, then nested node records. A record is its absolute
/// end offset, its property count, the byte length of its properties and its name (one length
/// byte); a node's children follow its properties and are ended by a null record. Arrays are
/// written uncompressed, as the app does.
/// </remarks>
public static class FbxWriter
{
    public const uint Version = 7400;

    /// <summary>FBX time units per second.</summary>
    public const long TicksPerSecond = 46_186_158_000;

    private const string HeaderMagic = "Kaydara FBX Binary  \0";

    /// <summary>The fixed last 16 bytes of the file ("Kaydara FBX Binary" footer magic).</summary>
    private static readonly byte[] FooterMagic =
        [0xF8, 0x5A, 0x8C, 0x6A, 0xDE, 0xF5, 0xD9, 0x7E, 0xEC, 0xE9, 0x0C, 0xE3, 0x75, 0x8F, 0x29, 0x0B];

    /// <summary>
    /// The FBX SDK checks the footer id against <c>FileId</c> and <c>CreationTime</c>, and reads a
    /// file whose three do not belong together as an empty scene (Unity, Godot's FBX2glTF). This is a
    /// known matching triple (the one Blender's exporter writes)
    /// </summary>
    public const string CreationTime = "1970-01-01 10:00:00:000";

    /// <summary>The <c>FileId</c> that belongs to <see cref="CreationTime"/> and the footer id.</summary>
    public static readonly byte[] FileId =
        [0x28, 0xB3, 0x2A, 0xEB, 0xB6, 0x24, 0xCC, 0xC2, 0xBF, 0xC8, 0xB0, 0x2A, 0xA9, 0x2B, 0xFC, 0xF1];

    /// <summary>The footer id that belongs to <see cref="FileId"/> and <see cref="CreationTime"/>.</summary>
    private static readonly byte[] FooterId =
        [0xFA, 0xBC, 0xAB, 0x09, 0xD0, 0xC8, 0xD4, 0x66, 0xB1, 0x76, 0xFB, 0x83, 0x1C, 0xF7, 0x26, 0x7E];

    /// <summary>Nodes the FBX SDK always ends with a null record, as the.akt files show.</summary>
    private static readonly HashSet<string> BlockNodes = ["References", "Properties70", "AnimationStack"];

    /// <summary>Whether arrays longer than 128 bytes are zlib-compressed, as the FBX SDK writes them.</summary>
    public static bool CompressArrays { get; set; } = true;

    public static void Write(string path, IReadOnlyList<FbxOutNode> nodes) =>
        File.WriteAllBytes(path, Write(nodes));

    public static byte[] Write(IReadOnlyList<FbxOutNode> nodes, IProgress<double>? progress = null)
    {
        using var stream = new MemoryStream();
        var magic = Encoding.ASCII.GetBytes(HeaderMagic);
        stream.Write(magic, 0, magic.Length);
        stream.WriteByte(0x1A);
        stream.WriteByte(0x00);
        WriteUInt32(stream, Version);
        for (int i = 0; i < nodes.Count; i++)
        {
            WriteNode(stream, nodes[i]);
            progress?.Report((double)(i + 1) / nodes.Count);
        }
        WriteNullRecord(stream);
        WriteFooter(stream);
        return stream.ToArray();
    }

    private static void WriteNode(Stream stream, FbxOutNode node)
    {
        long start = stream.Position;
        WriteUInt32(stream, 0); // end offset, patched below
        WriteUInt32(stream, (uint)node.Properties.Count);
        byte[] properties = SerializeProperties(node.Properties);
        WriteUInt32(stream, (uint)properties.Length);
        byte[] name = Encoding.UTF8.GetBytes(node.Name);
        if (name.Length > 255) throw new ArgumentException($"FBX node name '{node.Name}' is longer than 255 bytes.");
        stream.WriteByte((byte)name.Length);
        stream.Write(name, 0, name.Length);
        stream.Write(properties, 0, properties.Length);
        foreach (var child in node.Children) WriteNode(stream, child);
        // The FBX SDK writes its block nodes with a null record even when they are empty, and its
        // reader needs it: an empty Properties70 without one makes the SDK (Unity, Godot's
        // FBX2glTF) read an empty scene.
        if (node.Children.Count > 0 || BlockNodes.Contains(node.Name)) WriteNullRecord(stream);
        long end = stream.Position;
        stream.Position = start;
        WriteUInt32(stream, (uint)end);
        stream.Position = end;
    }

    private static byte[] SerializeProperties(IReadOnlyList<object> properties)
    {
        using var stream = new MemoryStream();
        foreach (var property in properties) WriteProperty(stream, property);
        return stream.ToArray();
    }

    private static void WriteProperty(Stream stream, object value)
    {
        switch (value)
        {
            case bool b: stream.WriteByte((byte)'C'); stream.WriteByte(b ? (byte)1 : (byte)0); break;
            case short s: stream.WriteByte((byte)'Y'); WriteInt16(stream, s); break;
            case int i: stream.WriteByte((byte)'I'); WriteInt32(stream, i); break;
            case uint ui: stream.WriteByte((byte)'I'); WriteInt32(stream, unchecked((int)ui)); break;
            case ushort us: stream.WriteByte((byte)'Y'); WriteInt16(stream, unchecked((short)us)); break;
            case long l: stream.WriteByte((byte)'L'); WriteInt64(stream, l); break;
            case float f: stream.WriteByte((byte)'F'); WriteSingle(stream, f); break;
            case double d: stream.WriteByte((byte)'D'); WriteDouble(stream, d); break;
            case string s:
            {
                stream.WriteByte((byte)'S');
                byte[] bytes = Encoding.UTF8.GetBytes(s);
                WriteUInt32(stream, (uint)bytes.Length);
                stream.Write(bytes, 0, bytes.Length);
                break;
            }
            case byte[] raw:
                stream.WriteByte((byte)'R');
                WriteUInt32(stream, (uint)raw.Length);
                stream.Write(raw, 0, raw.Length);
                break;
            case bool[] booleans: WriteArray(stream, 'b', booleans, 1, (w, v) => w.WriteByte(v ? (byte)1 : (byte)0)); break;
            case int[] ints: WriteArray(stream, 'i', ints, 4, WriteInt32); break;
            case long[] longs: WriteArray(stream, 'l', longs, 8, WriteInt64); break;
            case float[] floats: WriteArray(stream, 'f', floats, 4, WriteSingle); break;
            case double[] doubles: WriteArray(stream, 'd', doubles, 8, WriteDouble); break;
            default:
                throw new ArgumentException($"FBX properties cannot hold a {value.GetType().Name}.");
        }
    }

    private delegate void ElementWriter<T>(Stream stream, T value);

    private static void WriteArray<T>(Stream stream, char type, T[] values, int elementSize, ElementWriter<T> write)
    {
        stream.WriteByte((byte)type);
        WriteUInt32(stream, (uint)values.Length);
        using var raw = new MemoryStream(values.Length * elementSize);
        foreach (var value in values) write(raw, value);
        if (CompressArrays && raw.Length > 128)
        {
            using var packed = new MemoryStream();
            using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                raw.WriteTo(zlib);
            WriteUInt32(stream, 1); // encoding: 1 = zlib
            WriteUInt32(stream, (uint)packed.Length);
            packed.WriteTo(stream);
            return;
        }
        WriteUInt32(stream, 0); // encoding: 0 = raw
        WriteUInt32(stream, (uint)raw.Length);
        raw.WriteTo(stream);
    }

    private static void WriteNullRecord(Stream stream)
    {
        for (int i = 0; i < 13; i++) stream.WriteByte(0);
    }

    private static void WriteFooter(Stream stream)
    {
        stream.Write(FooterId, 0, FooterId.Length);
        // Pad to a 16-byte boundary, then four zero bytes, as the (FBX SDK) files do.
        long pad = (16 - stream.Position % 16) % 16;
        for (long i = 0; i < pad + 4; i++) stream.WriteByte(0);
        WriteUInt32(stream, Version);
        for (int i = 0; i < 120; i++) stream.WriteByte(0);
        stream.Write(FooterMagic, 0, FooterMagic.Length);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt16(Stream stream, short value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteSingle(Stream stream, float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteDouble(Stream stream, double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        stream.Write(bytes);
    }
}


