using System.Text;

namespace SloppyAnimatsu.Core.Akt;

/// <summary>Name helpers and static type knowledge that the file does not carry.</summary>
public static class AktNames
{
    /// <summary>
    /// Comparison key for class/struct/type names: removes <c>class </c>, <c>struct </c>,
    /// <c>enum </c> and <c>std::</c>/<c>std::__1::</c> qualifiers, so MSVC and libc++ spellings
    /// compare equal.
    /// </summary>
    public static string Key(string name)
    {
        if (name.IndexOf(' ') < 0 && name.IndexOf(':') < 0)
            return name;
        var sb = new StringBuilder(name)
            .Replace("std::__1::", "")
            .Replace("std::", "")
            .Replace("class ", "")
            .Replace("struct ", "")
            .Replace("enum ", "");
        return sb.ToString().Trim();
    }

    /// <summary>
    /// The spelling a class or struct <em>definition</em> carries in the schema and in an object's
    /// class atom. Every sample spells these with the MSVC <c>class </c> prefix ("class CKeyIK"),
    /// and the app resolves both by that exact name: a definition synthesized with a bare name
    /// ("CControllerIK") makes it drop the scene on load.
    /// </summary>
    public static string Definition(string name) =>
        name.StartsWith("class ", StringComparison.Ordinal) || name.StartsWith("struct ", StringComparison.Ordinal)
            ? name
            : "class " + name;

    /// <summary>
    /// Inheritance chains of the polymorphic value structs, root first
    /// Not stored in files; comes from the application's class hierarchy.
    /// </summary>
    private static readonly Dictionary<string, string> PolymorphicParent = new(StringComparer.Ordinal)
    {
        ["CKeyMRS"] = "CKeyBase",
        ["CKeyIK"] = "CKeyMRS",
        ["CKeyReverseFoot"] = "CKeyIK",
        ["CKeyCamera"] = "CKeyMRS",
        ["CKeyBlendshape"] = "CKeyBase",
        ["CKeyAudioAnim"] = "CKeyBase",
        ["CKeyAudioFrame"] = "CKeyBase",
        ["CKeySpaceSwitchingAnim"] = "CKeyBase",
        ["CKeySpaceSwitchingFrame"] = "CKeyBase",
        ["CTangentInfoListMRS"] = "CTangentInfoListBase",
        ["CTangentInfoListIK"] = "CTangentInfoListMRS",
        ["CTangentInfoListReverseFoot"] = "CTangentInfoListIK",
        ["CTangentInfoListCamera"] = "CTangentInfoListMRS",
        ["CTangentInfoListBlendshape"] = "CTangentInfoListBase",
    };

    /// <summary>Chain of struct keys from the root base down to <paramref name="concreteKey"/>.</summary>
    public static IReadOnlyList<string> PolymorphicChain(string concreteKey)
    {
        var chain = new List<string> { concreteKey };
        string current = concreteKey;
        while (PolymorphicParent.TryGetValue(current, out string? parent))
        {
            chain.Add(parent);
            current = parent;
        }
        chain.Reverse();
        return chain;
    }
}
