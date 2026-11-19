using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SloppyAnimatsu.Core.Fbx;

/// <summary>
/// A node of a binary FBX file: a name, a list of typed properties and child nodes.
/// Array properties are decoded on first access.
/// </summary>
public sealed class FbxNode(string name, IReadOnlyList<object> properties, IReadOnlyList<FbxNode> children)
{
    public string Name { get; } = name;
    public IReadOnlyList<FbxNode> Children { get; } = children;

    /// <summary>Property values: bool, short, int, long, float, double, string, byte[] or a primitive array.</summary>
    public object this[int index] => Properties[index];

    public int PropertyCount => properties.Count;

    public IReadOnlyList<object> Properties => _resolved ??= properties.Select(p => p is FbxLazyArray a ? a.Decode() : p).ToArray();
    private object[]? _resolved;

    public FbxNode? Child(string name) => Children.FirstOrDefault(c => c.Name == name);
    public IEnumerable<FbxNode> ChildrenNamed(string name) => Children.Where(c => c.Name == name);

    /// <summary>The first property of child <paramref name="name"/>, e.g. the array of <c>KeyTime</c>.</summary>
    public T? Value<T>(string name) => Child(name) is { PropertyCount: > 0 } c && c[0] is T v ? v : default;

    public override string ToString() => $"{Name} ({PropertyCount} properties, {Children.Count} children)";
}

/// <summary>An array property kept as its raw (possibly deflated) bytes until it is used.</summary>
internal sealed class FbxLazyArray(char type, int length, bool compressed, ReadOnlyMemory<byte> data)
{
    public object Decode()
    {
        int elementSize = type is 'd' or 'l' ? 8 : type is 'f' or 'i' ? 4 : 1;
        var bytes = new byte[length * elementSize];
        if (compressed)
        {
            using var z = new ZLibStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
            z.ReadExactly(bytes);
        }
        else
            data.Span[..bytes.Length].CopyTo(bytes);

        return type switch
        {
            'd' => Cast<double>(bytes),
            'f' => Cast<float>(bytes),
            'l' => Cast<long>(bytes),
            'i' => Cast<int>(bytes),
            'b' => bytes.Select(b => b != 0).ToArray(),
            _ => throw new InvalidDataException($"Unknown FBX array type '{type}'."),
        };
    }

    private static T[] Cast<T>(byte[] bytes) where T : struct =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(bytes).ToArray();
}

/// <summary>
/// Reader for binary FBX (versions 7100-7700). The format is public: a 27-byte header, then
/// nested node records (end offset, property count, property bytes, name) ended by a null record.
/// </summary>
public sealed class FbxDocument
{
    private const string Magic = "Kaydara FBX Binary  \0";

    private FbxDocument(uint version, IReadOnlyList<FbxNode> nodes)
    {
        Version = version;
        Nodes = nodes;
    }

    public uint Version { get; }

    /// <summary>Top-level nodes (FBXHeaderExtension, Definitions, Objects, Connections, Takes ...).</summary>
    public IReadOnlyList<FbxNode> Nodes { get; }

    public FbxNode? Node(string name) => Nodes.FirstOrDefault(n => n.Name == name);

    public static FbxDocument Load(string path) => Read(File.ReadAllBytes(path));

    public static FbxDocument Read(byte[] file)
    {
        if (file.Length < 27 || Encoding.ASCII.GetString(file, 0, 21) != Magic)
            throw new InvalidDataException("Not a binary FBX file (ASCII FBX is not supported).");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(23));
        var reader = new Reader(file, version >= 7500);
        int pos = 27;
        var nodes = new List<FbxNode>();
        while (reader.ReadNode(ref pos) is { } node) nodes.Add(node);
        return new FbxDocument(version, nodes);
    }

    private sealed class Reader(byte[] file, bool wide)
    {
        private readonly ReadOnlyMemory<byte> _memory = file;

        private long Offset(ref int pos)
        {
            long v = wide ? BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(pos)) : BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos));
            pos += wide ? 8 : 4;
            return v;
        }

        /// <summary>One node record, or null at a null record or the end of the node list.</summary>
        public FbxNode? ReadNode(ref int pos)
        {
            int headerSize = wide ? 25 : 13;
            if (pos + headerSize > file.Length) return null;
            long end = Offset(ref pos);
            long propertyCount = Offset(ref pos);
            Offset(ref pos); // property list length
            int nameLength = file[pos++];
            if (end == 0) return null;
            string name = Encoding.ASCII.GetString(file, pos, nameLength);
            pos += nameLength;

            var properties = new object[propertyCount];
            for (long i = 0; i < propertyCount; i++)
                properties[i] = ReadProperty(ref pos);

            var children = new List<FbxNode>();
            while (pos < end && ReadNode(ref pos) is { } child)
                children.Add(child);
            pos = (int)end;
            return new FbxNode(name, properties, children);
        }

        private object ReadProperty(ref int pos)
        {
            char type = (char)file[pos++];
            var s = file.AsSpan(pos);
            switch (type)
            {
                case 'Y': pos += 2; return BinaryPrimitives.ReadInt16LittleEndian(s);
                case 'C': pos += 1; return s[0] != 0;
                case 'I': pos += 4; return BinaryPrimitives.ReadInt32LittleEndian(s);
                case 'F': pos += 4; return BinaryPrimitives.ReadSingleLittleEndian(s);
                case 'D': pos += 8; return BinaryPrimitives.ReadDoubleLittleEndian(s);
                case 'L': pos += 8; return BinaryPrimitives.ReadInt64LittleEndian(s);
                case 'S' or 'R':
                {
                    int length = BinaryPrimitives.ReadInt32LittleEndian(s);
                    pos += 4 + length;
                    return type == 'S' ? Encoding.UTF8.GetString(s.Slice(4, length)) : s.Slice(4, length).ToArray();
                }
                case 'd' or 'f' or 'l' or 'i' or 'b':
                {
                    int length = BinaryPrimitives.ReadInt32LittleEndian(s);
                    int encoding = BinaryPrimitives.ReadInt32LittleEndian(s[4..]);
                    int byteCount = BinaryPrimitives.ReadInt32LittleEndian(s[8..]);
                    pos += 12 + byteCount;
                    return new FbxLazyArray(type, length, encoding == 1, _memory.Slice(pos - byteCount, byteCount));
                }
                default:
                    throw new InvalidDataException($"Unknown FBX property type '{type}' at 0x{pos - 1:X}.");
            }
        }
    }
}
