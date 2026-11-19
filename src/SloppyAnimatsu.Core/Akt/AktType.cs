using System.Collections.Concurrent;

namespace SloppyAnimatsu.Core.Akt;

/// <summary>How a member type is encoded on disk.</summary>
public enum AktTypeKind
{
    Bool, UChar, Char, UShort, Short, UInt, Int, Long, ULong, Float, Double,
    /// <summary>Fixed group of f32 (Vec2f/Vec3f/Vec4f/Quatf/RGBAf/Mat4f); see <see cref="AktType.Count"/>.</summary>
    FloatGroup,
    /// <summary>4 x u8.</summary>
    RGBAi,
    Atom,
    /// <summary>NUL-terminated UTF-16LE (QString, String).</summary>
    WString,
    /// <summary>i32 pre-order object index, -1 = null.</summary>
    Reference,
    /// <summary>u32 count, then i32 indices.</summary>
    RefArray,
    /// <summary>u32 count, then elements.</summary>
    Vector,
    /// <summary>Fixed count of elements, no count prefix.</summary>
    Tuple,
    /// <summary>u32 count, then that many key/value pairs (std::map, sorted by key on disk).</summary>
    Map,
    /// <summary>Atom naming the concrete struct, then the members of its inheritance chain.</summary>
    Polymorphic,
    /// <summary>A struct from the file's struct schema.</summary>
    Struct,
    /// <summary>Known to exist but encoding unverified (CTextureShared).</summary>
    Unsupported,
}

/// <summary>A parsed member type name. Instances are cached per spelling.</summary>
public sealed class AktType
{
    public AktTypeKind Kind { get; }
    /// <summary>The type name as spelled in the file.</summary>
    public string Spelling { get; }
    /// <summary>Element type for Vector/Tuple, the value type for Map.</summary>
    public AktType? Element { get; }
    /// <summary>The key type for Map.</summary>
    public AktType? KeyElement { get; }
    /// <summary>Float count for FloatGroup, element count for Tuple.</summary>
    public int Count { get; }
    /// <summary>
    /// Struct key for Struct, base struct key for Polymorphic, target class for Reference/RefArray
    /// (all normalised, see <see cref="AktNames.Key"/>).
    /// </summary>
    public string? Target { get; }

    private AktType(AktTypeKind kind, string spelling, AktType? element = null, int count = 0,
                    string? target = null, AktType? keyElement = null)
    {
        Kind = kind; Spelling = spelling; Element = element; Count = count; Target = target;
        KeyElement = keyElement;
    }

    private static readonly ConcurrentDictionary<string, AktType> Cache = new(StringComparer.Ordinal);

    public static AktType Parse(string spelling) => Cache.GetOrAdd(spelling, ParseUncached);

    private static AktType ParseUncached(string spelling)
    {
        string s = AktNames.Key(spelling);
        int lt = s.IndexOf('<');
        if (lt < 0)
            return ParseSimple(spelling, s);

        if (!s.EndsWith('>'))
            throw new FormatException($"Malformed type name '{spelling}'.");
        string head = s[..lt];
        List<string> args = SplitArgs(s[(lt + 1)..^1]);

        switch (head)
        {
            case "Reference" when args.Count == 1:
                return new AktType(AktTypeKind.Reference, spelling, target: args[0]);
            case "RefArray" when args.Count == 1:
                return new AktType(AktTypeKind.RefArray, spelling, target: args[0]);
            case "vector" when args.Count == 1:
                return new AktType(AktTypeKind.Vector, spelling, element: Parse(args[0]));
            case "Tuple" when args.Count == 2 && int.TryParse(args[1], out int n):
                return new AktType(AktTypeKind.Tuple, spelling, element: Parse(args[0]), count: n);
            case "map" when args.Count == 2:
                return new AktType(AktTypeKind.Map, spelling, element: Parse(args[1]), keyElement: Parse(args[0]));
            default:
                // Other templates (e.g. CSmartPointer<CKeyBase>) only occur as struct names.
                return new AktType(AktTypeKind.Struct, spelling, target: s);
        }
    }

    private static AktType ParseSimple(string spelling, string s) => s switch
    {
        "bool" => new(AktTypeKind.Bool, spelling),
        "uchar" => new(AktTypeKind.UChar, spelling),
        "char" => new(AktTypeKind.Char, spelling),
        "ushort" => new(AktTypeKind.UShort, spelling),
        "short" => new(AktTypeKind.Short, spelling),
        "uint" => new(AktTypeKind.UInt, spelling),
        "int" => new(AktTypeKind.Int, spelling),
        "long" => new(AktTypeKind.Long, spelling),
        "ulong" => new(AktTypeKind.ULong, spelling),
        "float" => new(AktTypeKind.Float, spelling),
        "double" => new(AktTypeKind.Double, spelling),
        "Vec2f" => new(AktTypeKind.FloatGroup, spelling, count: 2),
        "Vec3f" => new(AktTypeKind.FloatGroup, spelling, count: 3),
        "Vec4f" or "Quatf" or "RGBAf" => new(AktTypeKind.FloatGroup, spelling, count: 4),
        "Mat4f" => new(AktTypeKind.FloatGroup, spelling, count: 16),
        "RGBAi" => new(AktTypeKind.RGBAi, spelling),
        "CAtom" => new(AktTypeKind.Atom, spelling),
        "QString" or "String" => new(AktTypeKind.WString, spelling),
        "Key" => new(AktTypeKind.Polymorphic, spelling, target: "CKeyBase"),
        "TangentInfoList" => new(AktTypeKind.Polymorphic, spelling, target: "CTangentInfoListBase"),
        "CTextureShared" => new(AktTypeKind.Unsupported, spelling),
        _ => new(AktTypeKind.Struct, spelling, target: s),
    };

    private static List<string> SplitArgs(string s)
    {
        var result = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(s[start..i].Trim());
                start = i + 1;
            }
        }
        result.Add(s[start..].Trim());
        return result;
    }

    public override string ToString() => Spelling;
}
