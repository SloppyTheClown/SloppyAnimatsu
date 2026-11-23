using System.Globalization;
using System.Text;

namespace SloppyAnimatsu.Core.Fbx;

/// <summary>
/// Writes the nodes <see cref="FbxWriter"/> takes as FBX 7.4 ASCII text: one <c>Name: values</c>
/// line per node, children between braces, arrays as <c>*count { a: ... }</c>, object names as
/// <c>"Class::Name"</c> and binary blobs as base64.
/// </summary>
public static class FbxAsciiWriter
{
    /// <summary>Top-level nodes that only the binary layout carries.</summary>
    private static readonly HashSet<string> BinaryOnly = ["FileId"];

    /// <summary>Nodes written with braces even when they have no children.</summary>
    private static readonly HashSet<string> BlockNodes = ["References", "Properties70", "AnimationStack"];

    public static byte[] Write(IReadOnlyList<FbxOutNode> nodes, IProgress<double>? progress = null)
    {
        var text = new StringBuilder();
        text.Append("; FBX 7.4.0 project file\n");
        text.Append("; Created by SloppyAnimatsu\n");
        text.Append("; ----------------------------------------------------\n\n");
        for (int i = 0; i < nodes.Count; i++)
        {
            if (!BinaryOnly.Contains(nodes[i].Name)) WriteNode(text, nodes[i], 0);
            progress?.Report((double)(i + 1) / nodes.Count);
        }
        return new UTF8Encoding(false).GetBytes(text.ToString());
    }

    private static void WriteNode(StringBuilder text, FbxOutNode node, int depth)
    {
        text.Append('\t', depth).Append(node.Name).Append(':');
        bool array = node.Properties.Count == 1 && IsArray(node.Properties[0]);
        if (array)
        {
            WriteArray(text, node.Properties[0], depth);
        }
        else
        {
            for (int i = 0; i < node.Properties.Count; i++)
            {
                text.Append(i == 0 ? " " : ", ");
                WriteValue(text, node.Properties[i], depth);
            }
        }
        if (node.Children.Count > 0 || (!array && BlockNodes.Contains(node.Name)))
        {
            text.Append(node.Properties.Count == 0 ? "  {\n" : " {\n");
            foreach (var child in node.Children) WriteNode(text, child, depth + 1);
            text.Append('\t', depth).Append("}\n");
        }
        else text.Append('\n');
        if (depth == 0) text.Append('\n');
    }

    private static bool IsArray(object value) => value is bool[] or int[] or long[] or float[] or double[];

    private static void WriteArray(StringBuilder text, object value, int depth)
    {
        var items = value switch
        {
            bool[] b => b.Select(x => x ? "1" : "0").ToArray(),
            int[] i => i.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(),
            long[] l => l.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(),
            float[] f => f.Select(x => Number(x)).ToArray(),
            double[] d => d.Select(Number).ToArray(),
            _ => [],
        };
        text.Append(" *").Append(items.Length).Append(" {\n");
        text.Append('\t', depth + 1).Append("a: ").Append(string.Join(",", items)).Append('\n');
        text.Append('\t', depth).Append('}');
    }

    private static void WriteValue(StringBuilder text, object value, int depth)
    {
        switch (value)
        {
            case string s: text.Append('"').Append(Name(s)).Append('"'); break;
            case bool b: text.Append(b ? 'T' : 'F'); break;
            case byte[] bytes:
                text.Append(",\n").Append('\t', depth).Append("\t\"").Append(Convert.ToBase64String(bytes)).Append('"');
                break;
            case float f: text.Append(Number(f)); break;
            case double d: text.Append(Number(d)); break;
            case IFormattable n: text.Append(n.ToString(null, CultureInfo.InvariantCulture)); break;
            default: text.Append(value); break;
        }
    }

    /// <summary>A binary object name <c>Name\0\u0001Class</c> reads <c>Class::Name</c> in text;
    /// quotes become <c>&amp;quot;</c>.</summary>
    private static string Name(string s)
    {
        int split = s.IndexOf("\0\u0001", StringComparison.Ordinal);
        if (split >= 0) s = $"{s[(split + 2)..]}::{s[..split]}";
        return s.Replace("\"", "&quot;");
    }

    private static string Number(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "0";
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Number(float value) =>
        float.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "0";
}
