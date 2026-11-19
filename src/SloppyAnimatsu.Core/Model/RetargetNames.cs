using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// How SloppyAnimatsu reads joint names for retargeting, beyond exact-name matching
/// the name a joint has within its skeleton
/// (namespace and character-name prefix removed), a body-part key that lets the dialog's automatic
/// mapping pair <c>LeftUpLeg</c> with <c>thigh_l</c> or <c>LeftHandThumb1</c> with
/// <c>thumb_01_l</c>, and the skeleton signature the saved mappings are keyed by.
/// </summary>
public static class RetargetNames
{
    private static readonly Regex Tokens = new("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", RegexOptions.Compiled);

    private static readonly HashSet<string> Left = ["l", "left", "lf", "lft"];
    private static readonly HashSet<string> Right = ["r", "right", "rt", "rgt"];
    private static readonly HashSet<string> Noise =
        ["mixamorig", "def", "jnt", "joint", "bone", "bn", "sk", "skel", "jt", "ctrl", "ctl", "drv"];

    /// <summary>Whole-body-part synonyms, applied in order to the joined lower-case name.</summary>
    private static readonly (Regex Pattern, string Replacement)[] Synonyms =
    [
        (new("^hand(?=(thumb|index|middle|ring|pinky|pinkie|little))"), ""),
        (new("pinkie|little"), "pinky"),
        (new("forefinger|pointer"), "index"),
        (new("mid(?!dle)"), "middle"),
        (new("finger"), ""),
        (new("^(upperleg|upleg|thigh)"), "thigh"),
        (new(@"^(lowerleg|calf|shin|knee)|^leg(?=\d*$)"), "calf"),
        (new(@"^(upperarm|arm(?=\d*$))"), "upperarm"),
        (new("^(forearm|lowerarm|elbow)"), "forearm"),
        (new("^(clavicle|collar|shoulder)"), "clavicle"),
        (new("^(toebase|toes|ball)"), "toe"),
        (new("^ankle"), "foot"),
        (new("^wrist"), "hand"),
        (new("^(hips|hip|pelvis|cog)$"), "pelvis"),
    ];

    /// <summary>
    /// The joint's name within its skeleton: the part after a <c>:</c> or <c>|</c> namespace, without
    /// a leading copy of the character's own name (<c>DupTheClown_OLD_Hips</c> on the character
    /// <c>DupTheClown_OLD</c> reads <c>Hips</c>). Two characters built from the same rig give their
    /// joints the same skeleton names.
    /// </summary>
    public static string SkeletonName(Character character, Object3D obj)
    {
        string name = obj.Name ?? "";
        int cut = name.LastIndexOfAny([':', '|']);
        if (cut >= 0 && cut + 1 < name.Length) name = name[(cut + 1)..];
        if (character.Name is { Length: > 0 } owner && name.Length > owner.Length + 1
            && name.StartsWith(owner, StringComparison.OrdinalIgnoreCase)
            && name[owner.Length] is '_' or ' ' or '-' or '.')
            name = name[(owner.Length + 1)..];
        return name.Trim();
    }

    /// <summary>
    /// The body-part key of a candidate: its kind (joint, IK, reverse foot), its side and its body
    /// part with naming conventions folded together, e.g. <c>j:L:thigh</c> for <c>LeftUpLeg</c>,
    /// <c>thigh_l</c> and <c>Bip01 L Thigh</c>. Null when nothing of the name is left.
    /// </summary>
    public static string? BodyKey(Character character, Object3D obj)
    {
        var tokens = Tokens.Matches(SkeletonName(character, obj)).Select(m => m.Value.ToLowerInvariant()).ToList();
        string side = "";
        var body = new StringBuilder();
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (Left.Contains(token)) { side = "L"; continue; }
            if (Right.Contains(token)) { side = "R"; continue; }
            if (Noise.Contains(token)) continue;
            if (token == "bip")
            {
                if (i + 1 < tokens.Count && char.IsDigit(tokens[i + 1][0])) i++;
                continue;
            }
            if (char.IsDigit(token[0]))
                token = token.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0";
            body.Append(token);
        }
        string text = body.ToString();
        foreach (var (pattern, replacement) in Synonyms) text = pattern.Replace(text, replacement);
        if (text.Length == 0) return null;
        return $"{Kind(obj)}:{side}:{text}";
    }

    /// <summary>
    /// The skeleton signature of a character: a hash of the sorted set of its candidates' kinds and
    /// skeleton names. Characters made from the same rig share it whatever they are called, so a
    /// mapping saved for one pair of rigs is offered for every pair of characters made from them.
    /// </summary>
    public static string Signature(Character character)
    {
        var names = RetargetingMap.Candidates(character)
            .Select(candidate => Kind(candidate) + ":" + SkeletonName(character, candidate))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", names)));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    /// <summary>A lone side letter between separators: <c>Thumb_3_L</c>, <c>L_Thumb</c>, <c>hand.l</c>, <c>Arm_L1</c>.</summary>
    private static readonly Regex SideLetter = new(@"(?<=^|[_.\-\s:|])([LRlr])(?=$|[_.\-\s0-9])", RegexOptions.Compiled);

    /// <summary>A side word anywhere in the name: <c>LeftHandThumb1</c>, <c>left_arm</c>, <c>ARM_RIGHT</c>.</summary>
    private static readonly Regex SideWord = new("Left|Right|left|right|LEFT|RIGHT", RegexOptions.Compiled);

    /// <summary>
    /// The name of the other side's joint: <c>Thumb_Distal_L</c> reads <c>Thumb_Distal_R</c>,
    /// <c>LeftHandThumb1</c> reads <c>RightHandThumb1</c>, letter case kept. A lone side letter between
    /// separators wins over a side word; null when the name has no side.
    /// </summary>
    public static string? MirrorName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (SideLetter.IsMatch(name))
            return SideLetter.Replace(name, m => m.Value switch { "L" => "R", "R" => "L", "l" => "r", _ => "l" });
        if (SideWord.IsMatch(name))
            return SideWord.Replace(name, m => m.Value switch
            {
                "Left" => "Right", "Right" => "Left", "left" => "right", "right" => "left",
                "LEFT" => "RIGHT", _ => "LEFT",
            });
        return null;
    }

    private static string Kind(Object3D obj) => obj switch
    {
        ControllerReverseFoot => "rf",
        ControllerIK => "ik",
        Joint => "j",
        _ => "c",
    };
}
