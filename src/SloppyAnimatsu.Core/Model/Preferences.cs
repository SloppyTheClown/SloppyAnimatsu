using System.Globalization;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// One page of <c>preferences.akt</c> (a <c>CPreferenceNode</c> subclass): its members are read
/// and written by name, with the type the page declares.
/// </summary>
public sealed class PreferencePage
{
    private readonly AktObject _object;

    internal PreferencePage(AktObject page) => _object = page;

    public string ClassName => _object.ClassKey;

    /// <summary>The page's node name (<c>Default name</c>, <c>Default name_02</c>, ...).</summary>
    public string? Name => _object.Name;

    /// <summary>The page's own and inherited members, in file order (including <c>Name</c>).</summary>
    public IReadOnlyList<AktField> Members => _object.Fields;

    /// <summary>
    /// Every editable leaf of the page in file order, descending into structs and tuples: the
    /// preferences dialog's rows. A path spells the access (<c>AutoSaveTime</c>,
    /// <c>PreferredPaths.SavePath</c>, <c>OptionsArray[3].Flags</c>). References, maps and the
    /// types the text form cannot set (a whole struct or tuple) are left out.
    /// </summary>
    public IReadOnlyList<PreferenceField> Fields
    {
        get
        {
            var fields = new List<PreferenceField>();
            Collect(_object, "", fields);
            return fields;
        }
    }

    private void Collect(AktFieldContainer container, string prefix, List<PreferenceField> fields)
    {
        foreach (var field in container.Fields)
        {
            string path = prefix.Length == 0 ? field.Name : prefix + "." + field.Name;
            object? value = field.Value;
            if (value is AktStruct structure)
            {
                Collect(structure, path, fields);
            }
            else if (field.Type.Kind == AktTypeKind.Tuple && value is object?[] tuple)
            {
                for (int i = 0; i < tuple.Length; i++)
                    if (tuple[i] is AktStruct element) Collect(element, $"{path}[{i}]", fields);
            }
            else if (IsEditable(field.Type))
            {
                fields.Add(new PreferenceField(this, path, field));
            }
        }
    }

    /// <summary>Whether a member of this type has a text form.</summary>
    private static bool IsEditable(AktType type) => type.Kind switch
    {
        AktTypeKind.Bool or AktTypeKind.UChar or AktTypeKind.Char or AktTypeKind.UShort or
            AktTypeKind.Short or AktTypeKind.UInt or AktTypeKind.Int or AktTypeKind.Long or
            AktTypeKind.ULong or AktTypeKind.Float or AktTypeKind.Double or AktTypeKind.FloatGroup or
            AktTypeKind.RGBAi or AktTypeKind.Atom or AktTypeKind.WString => true,
        AktTypeKind.Vector => type.Element?.Kind == AktTypeKind.UShort,
        _ => false,
    };

    /// <summary>
    /// A member or a nested member's value. <paramref name="path"/> may descend through a struct
    /// or a tuple (<c>PreferredPaths.SavePath</c>, <c>OptionsArray[3].Flags</c>).
    /// </summary>
    public object? Get(string path) => TryResolve(path, out var field, out _) ? field!.Value : null;

    public bool Has(string path) => TryResolve(path, out _, out _);

    /// <summary>Sets a member to a value of its declared type. Returns false when the page has no such member.</summary>
    public bool Set(string member, object? value)
    {
        if (!TryResolve(member, out var field, out _)) return false;
        field!.Value = Coerce(field.Type, value);
        return true;
    }

    /// <summary>
    /// Sets a member (or nested member) from the CLI's and the preferences dialog's text form.
    /// Returns false (and a reason) on a bad value.
    /// </summary>
    public bool TrySetFromString(string member, string text, out string? error)
    {
        if (!TryResolve(member, out var field, out error)) return false;
        if (field!.Value is AktStruct or object?[])
        {
            error = $"{ClassName}.{member} is not a leaf member; name one of its members.";
            return false;
        }
        if (!TryParse(field.Type, text, out object? value, out error))
        {
            error = $"{member}: {error}";
            return false;
        }
        field.Value = value;
        return true;
    }

    /// <summary>
    /// A leaf's value as text, in the form <see cref="TrySetFromString"/> reads back: invariant
    /// numbers, <c>True</c>/<c>False</c> (or the raw byte a key file stores in a bool member),
    /// <c>RRGGBBAA</c> for a colour, and comma-separated numbers for a float group or vector.
    /// </summary>
    public static string FormatValue(object? value) => value switch
    {
        null => "",
        bool flag => flag ? "True" : "False",
        string text => text,
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        byte[] rgba => Convert.ToHexString(rgba),
        float[] floats => string.Join(", ", floats.Select(v => v.ToString("R", CultureInfo.InvariantCulture))),
        List<object?> list => string.Join(", ", list.Select(v => FormatValue(v))),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>Resolves a member path to its field, descending into structs and tuple elements.</summary>
    private bool TryResolve(string path, out AktField? field, out string? error)
    {
        field = null;
        error = null;
        AktFieldContainer current = _object;
        string[] segments = path.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i].Trim();
            string name = segment;
            int index = -1;
            int bracket = segment.IndexOf('[');
            if (bracket >= 0)
            {
                if (!segment.EndsWith(']') || !int.TryParse(segment[(bracket + 1)..^1], out index) || index < 0)
                {
                    error = $"{ClassName} has no member '{path}'.";
                    return false;
                }
                name = segment[..bracket];
            }
            var found = current.FindField(name);
            if (found == null)
            {
                error = $"{ClassName} has no member '{path}'.";
                return false;
            }
            if (index >= 0)
            {
                if (found.Type.Kind != AktTypeKind.Tuple || found.Value is not object?[] tuple || index >= tuple.Length)
                {
                    error = $"{ClassName}.{name} has no element [{index}].";
                    return false;
                }
                if (i == segments.Length - 1)
                {
                    // A scalar tuple element cannot be written through an AktField; every tuple
                    // member in the preference file holds structs, addressed with one more segment.
                    error = $"{ClassName}.{name}[{index}] is not a leaf member.";
                    return false;
                }
                if (tuple[index] is not AktFieldContainer element)
                {
                    error = $"{ClassName}.{name}[{index}] is not a struct.";
                    return false;
                }
                current = element;
            }
            else if (i == segments.Length - 1)
            {
                // A struct or tuple member itself can be read (the CLI prints it) but not set from
                // text; TrySetFromString refuses it as not being a leaf.
                field = found;
                return true;
            }
            else if (found.Value is AktFieldContainer next)
            {
                current = next;
            }
            else
            {
                error = $"{ClassName}.{name} is not a struct.";
                return false;
            }
        }
        error = $"{ClassName} has no member '{path}'.";
        return false;
    }

    internal static object? Coerce(AktType type, object? value)
    {
        switch (type.Kind)
        {
            case AktTypeKind.Bool:
                return value switch
                {
                    bool b => b,
                    byte raw => raw,
                    _ => throw Bad(type, value),
                };
            case AktTypeKind.UChar:
                return value is byte b2 ? b2 : Convert.ToByte(value, CultureInfo.InvariantCulture);
            case AktTypeKind.Char:
                return value is sbyte s ? s : Convert.ToSByte(value, CultureInfo.InvariantCulture);
            case AktTypeKind.UShort:
                return value is ushort us ? us : Convert.ToUInt16(value, CultureInfo.InvariantCulture);
            case AktTypeKind.Short:
                return value is short sh ? sh : Convert.ToInt16(value, CultureInfo.InvariantCulture);
            case AktTypeKind.UInt:
            case AktTypeKind.ULong:
                return value is uint u ? u : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
            case AktTypeKind.Int:
            case AktTypeKind.Long:
                return value is int i ? i : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            case AktTypeKind.Float:
                return value is float f ? f : Convert.ToSingle(value, CultureInfo.InvariantCulture);
            case AktTypeKind.Double:
                return value is double d ? d : Convert.ToDouble(value, CultureInfo.InvariantCulture);
            case AktTypeKind.Atom:
            case AktTypeKind.WString:
                return value switch
                {
                    null when type.Kind == AktTypeKind.Atom => null,
                    string text => text,
                    _ => throw Bad(type, value),
                };
            case AktTypeKind.RGBAi:
                return value as byte[] ?? throw Bad(type, value);
            case AktTypeKind.FloatGroup:
            {
                var floats = value as float[] ?? throw Bad(type, value);
                if (floats.Length != type.Count)
                    throw new ArgumentException($"{type.Spelling} needs {type.Count} floats, got {floats.Length}.");
                return floats;
            }
            case AktTypeKind.Vector when type.Element!.Kind == AktTypeKind.UShort:
            {
                var source = value as IEnumerable<object?> ?? throw Bad(type, value);
                return source.Select(v => (object?)Convert.ToUInt16(v, CultureInfo.InvariantCulture)).ToList();
            }
            default:
                throw new NotSupportedException($"Setting a {type.Spelling} member from text is not supported.");
        }
    }

    private static bool TryParse(AktType type, string text, out object? value, out string? error)
    {
        error = null;
        value = null;
        string s = text.Trim();
        try
        {
            switch (type.Kind)
            {
                case AktTypeKind.Bool:
                    if (s is "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                        s.Equals("on", StringComparison.OrdinalIgnoreCase)) value = true;
                    else if (s is "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                             s.Equals("off", StringComparison.OrdinalIgnoreCase)) value = false;
                    else if (byte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte raw)) value = raw;
                    else
                    {
                        error = $"'{text}' is not a bool.";
                        return false;
                    }
                    return true;
                case AktTypeKind.UChar:
                    value = byte.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return true;
                case AktTypeKind.Char:
                    value = sbyte.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return true;
                case AktTypeKind.UShort:
                    value = ushort.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return true;
                case AktTypeKind.Short:
                    value = short.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return true;
                case AktTypeKind.UInt:
                case AktTypeKind.ULong:
                    value = uint.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return true;
                case AktTypeKind.Int:
                case AktTypeKind.Long:
                    value = int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return true;
                case AktTypeKind.Float:
                    value = float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
                    return true;
                case AktTypeKind.Double:
                    value = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
                    return true;
                case AktTypeKind.Atom:
                case AktTypeKind.WString:
                    value = s;
                    return true;
                case AktTypeKind.RGBAi:
                    value = ParseRgba(s);
                    return true;
                case AktTypeKind.FloatGroup:
                {
                    var floats = ParseFloats(s);
                    if (floats.Length != type.Count)
                    {
                        error = $"{type.Spelling} needs {type.Count} floats.";
                        return false;
                    }
                    value = floats;
                    return true;
                }
                case AktTypeKind.Vector when type.Element!.Kind == AktTypeKind.UShort:
                    value = s.Length == 0
                        ? new List<object?>()
                        : s.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                            .Select(x => (object?)ushort.Parse(x, CultureInfo.InvariantCulture)).ToList();
                    return true;
                default:
                    error = $"Setting a {type.Spelling} member from text is not supported.";
                    return false;
            }
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            error = $"'{text}' is not a valid {type.Spelling}.";
            return false;
        }
    }

    private static byte[] ParseRgba(string s)
    {
        string hex = s.TrimStart('#').Replace(" ", "").Replace(",", "");
        if (hex.Length == 8 && byte.TryParse(hex[..2], NumberStyles.HexNumber, null, out _))
        {
            var bytes = new byte[4];
            for (int i = 0; i < 4; i++)
                bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
        var parts = s.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 4)
            return parts.Select(x => byte.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        throw new FormatException($"'{s}' is not RGBA (RRGGBBAA or four bytes).");
    }

    private static float[] ParseFloats(string s) =>
        s.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => float.Parse(x, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();

    private static ArgumentException Bad(AktType type, object? value) =>
        new($"A {type.Spelling} member cannot hold {value?.GetType().Name ?? "null"}.");

    public override string ToString() => $"{ClassName} '{Name}'";
}

/// <summary>
/// The widget a preference value is edited with: a check box for a
/// bool, a colour swatch for an <c>RGBAi</c>, and a typed field for everything else. The app also
/// draws curated rows with combo boxes for some enum members; every other value keeps a text field.
/// </summary>
public enum PreferenceWidget
{
    /// <summary>An editable text field holding the value's text form.</summary>
    Text,
    /// <summary>A check box (a <c>bool</c> member).</summary>
    Check,
    /// <summary>A swatch beside the text (an <c>RGBAi</c> member).</summary>
    Color,
}

/// <summary>
/// One editable leaf of a <see cref="PreferencePage"/>: a scalar, string, colour or vector member,
/// or a leaf inside a struct or tuple member (<c>PreferredPaths.SavePath</c>,
/// <c>OptionsArray[3].Flags</c>). The preferences dialog shows one row per field.
/// </summary>
public sealed class PreferenceField
{
    private readonly PreferencePage _page;
    private readonly AktField _field;

    internal PreferenceField(PreferencePage page, string path, AktField field)
    {
        _page = page;
        Path = path;
        _field = field;
    }

    /// <summary>The member's access path (<c>AutoSaveTime</c>, <c>PreferredPaths.SavePath</c>, ...).</summary>
    public string Path { get; }

    /// <summary>The member's declared type, as the file spells it.</summary>
    public string TypeName => _field.TypeName;

    public object? Value => _field.Value;

    /// <summary>The value in the text form <see cref="TrySetFromString"/> reads back.</summary>
    public string Format() => PreferencePage.FormatValue(_field.Value);

    /// <summary>The widget the dialog edits this value with, for the kinds it draws.</summary>
    public PreferenceWidget Widget => TypeName switch
    {
        "bool" => PreferenceWidget.Check,
        "RGBAi" => PreferenceWidget.Color,
        _ => PreferenceWidget.Text,
    };

    /// <summary>Parses <paramref name="text"/> as this member's type and writes it.</summary>
    public bool TrySetFromString(string text, out string? error) =>
        _page.TrySetFromString(Path, text, out error);

    public override string ToString() => $"{Path} : {TypeName} = {Format()}";
}

/// <summary>
/// One entry of the saved dock layout: <c>CDialogPosition</c> in <c>preferences.akt</c>
/// The layout itself is stored, not applied.
/// </summary>
public sealed class DialogPlacement
{
    private readonly AktObject _object;

    internal DialogPlacement(string context, AktObject position)
    {
        Context = context;
        _object = position;
    }

    /// <summary>The interface context the entry belongs to (<c>Rigging</c>, <c>Animation</c>, or empty).</summary>
    public string Context { get; }

    /// <summary>The dialog class without a leading <c>class </c> (<c>CTransformDialog</c>, ...).</summary>
    public string DialogClass => Name.StartsWith("class ", StringComparison.Ordinal) ? Name[6..] : Name;

    public string Name => _object.Name ?? "";

    public float X
    {
        get => (float)(_object["PosX"] ?? 0f);
        set => _object.Set("PosX", value);
    }

    public float Y
    {
        get => (float)(_object["PosY"] ?? 0f);
        set => _object.Set("PosY", value);
    }

    public uint Flags
    {
        get => (uint)(_object["Flags"] ?? 0u);
        set => _object.Set("Flags", value);
    }

    public IReadOnlyList<ushort> SizeBarPositions
    {
        get
        {
            if (_object["SizeBarPositions"] is not List<object?> list) return Array.Empty<ushort>();
            return list.Select(v => Convert.ToUInt16(v, CultureInfo.InvariantCulture)).ToList();
        }
    }

    public void SetSizeBarPositions(params ushort[] positions) =>
        _object.Set("SizeBarPositions", positions.Select(v => (object?)v).ToList());

    public string? DockDown
    {
        get => _object["DockDown"] as string;
        set => _object.Set("DockDown", value);
    }

    public string? DockLeft
    {
        get => _object["DockLeft"] as string;
        set => _object.Set("DockLeft", value);
    }

    public string? DockRight
    {
        get => _object["DockRight"] as string;
        set => _object.Set("DockRight", value);
    }

    public bool DownDockIsByLeft
    {
        get => _object["DownDockIsByLeft"] is true;
        set => _object.Set("DownDockIsByLeft", value);
    }

    public override string ToString() =>
        $"{Context}/{DialogClass}: {X:0.###},{Y:0.###} flags {Flags} size [{string.Join(",", SizeBarPositions)}] " +
        $"down {DockDown ?? "-"} left {DockLeft ?? "-"} right {DockRight ?? "-"}";
}

/// <summary>
/// The preferences.akt: the parts of the preference tree a scene session reads and writes —
/// the recent-scene list, the default frame rate of a new scene, the autosave interval, every
/// preference page and the saved dock layout. Everything else in the file is left untouched, so
/// saving writes the same bytes for every untouched preference node.
/// </summary>
public sealed class Preferences
{
    /// <summary>How many recent scenes the app keeps (10).</summary>
    public const int MaxRecentFiles = 10;

    /// <summary>
    /// The preference file's path below the user's application-data folder. SloppyAnimatsu keeps its
    /// own file, apart from any shared <c>preferences.akt</c>, so neither
    /// program rewrites the other's settings; it holds SloppyAnimatsu's own preference nodes too, so it
    /// is saved in SloppyAnimatsu's container (<c>.slopa</c>), not in the plain .akt layout.
    /// </summary>
    public const string RelativePath = "SloppyAnimatsu/preferences.slopa";

    public AktDocument Document { get; }

    /// <summary>The file it was loaded from (also the default <see cref="Save()"/> target).</summary>
    public string Path { get; private set; }

    private readonly AktObject _main;
    private readonly AktObject? _general;

    private Preferences(AktDocument document, string path, AktObject main, AktObject? general)
    {
        Document = document;
        Path = path;
        _main = main;
        _general = general;
    }

    /// <summary>The preference file of the current user, whether it exists or not.</summary>
    public static string DefaultPath => InAppData(RelativePath);

    private static string InAppData(string relative) =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>
    /// The fallback profile beside the program, used when <see cref="DefaultPath"/> cannot be read
    /// or written (a locked-down profile folder, a sandboxed run): without it the recent scenes,
    /// the panel layout and every setting would silently have nowhere to live.
    /// </summary>
    public static string PortablePath =>
        System.IO.Path.Combine(SloppyAnimatsu.Core.Diagnostics.AppFolder.Install, "profile", "preferences.slopa");


    /// <summary>
    /// Opens the first of <paramref name="candidates"/> that can be used: an existing file has to
    /// load and, with <paramref name="requireWritable"/>, be open for writing; a missing one is
    /// generated by <see cref="CreateDefault"/>. Returns null when none works.
    /// <paramref name="problems"/> lists why each earlier candidate was passed over.
    /// </summary>
    public static Preferences? OpenFirstUsable(IEnumerable<string> candidates, bool requireWritable,
        out IReadOnlyList<string> problems)
    {
        var failed = new List<string>();
        problems = failed;
        foreach (string path in candidates)
        {
            try
            {
                if (!File.Exists(path)) return CreateDefault(path);
                var preferences = Load(path);
                if (requireWritable)
                    using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) { }
                return preferences;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                          or InvalidDataException or System.Security.SecurityException
                                          or AktFormatException)
            {
                failed.Add($"{path}: {e.Message}");
            }
        }
        return null;
    }

    /// <summary>Loads the user's preference file, or null when it does not exist.</summary>
    public static Preferences? LoadDefault() => File.Exists(DefaultPath) ? Load(DefaultPath) : null;

    /// <summary>The embedded file a first run's preferences are generated from.</summary>
    private const string TemplateResource = "SloppyAnimatsu.Core.Resources.default-preferences.slopa";

    /// <summary>
    /// Members kept only so a file in akeytsu's layout stays compatible: the Preferences window
    /// does not list them and a generated preferences file leaves them out.
    /// </summary>
    public static IReadOnlySet<string> HiddenMembers { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "AkeytsuUsageWitness", "ServerYearVersionNumberWhenUserCheckedSkipUpdateWindow" };

    /// <summary>
    /// Generates a preferences file at <paramref name="path"/> (its folder is created): the
    /// default pages from the embedded template, no recent scenes or remembered folders, and the
    /// default panel layout of both interface contexts.
    /// Throws when the path cannot be written.
    /// </summary>
    public static Preferences CreateDefault(string path)
    {
        string? folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        using (var template = typeof(Preferences).Assembly.GetManifestResourceStream(TemplateResource)
                              ?? throw new InvalidDataException("The default preferences template is missing from the build."))
        using (var file = File.Create(path))
            template.CopyTo(file);
        var preferences = Load(path);
        preferences.ClearPersonal();
        if (preferences._general is { } general)
            foreach (string member in HiddenMembers)
            {
                if (general.FindField(member) is { } field) general.Fields.Remove(field);
                preferences.Document.Schema.FindClass(general.TypeName)?.Members.RemoveAll(m => m.Name == member);
            }
        foreach (string context in new[] { "Rigging", "Animation" })
        {
            var dock = DockLayout.For(preferences, context);
            FloatingLayout.FromPreset(LayoutPreset.Default, context, PanelCatalogue.For(context),
                DefaultLayoutWidth, DefaultLayoutHeight, dock).WriteTo(dock);
        }
        preferences.EnsureMaterialPage();
        preferences.Save();
        return preferences;
    }

    /// <summary>The window size the generated default layout is written for (the capture).</summary>
    public const int DefaultLayoutWidth = 1982, DefaultLayoutHeight = 1076;

    /// <summary>
    /// Empties what a profile remembers about its user: the recent scenes and the folders of
    /// <c>PreferredPaths</c>. The change is in memory until <see cref="Save()"/>.
    /// </summary>
    public void ClearPersonal()
    {
        if (_main["RecentFiles"] is List<object?> list) list.Clear();
        foreach (string member in new[] { "LoadPath", "SavePath", "ImportFBXPath", "ExportFBXPath", "ImportAKNMPath", "ExportAKNMPath" })
            TrySet("CGeneralPreferences", "PreferredPaths." + member, "", out _);
    }

    public static Preferences Load(string path)
    {
        var document = AktDocument.Load(path);
        var main = document.Roots.FirstOrDefault(o => o.ClassKey == "CMainPreferences")
                   ?? throw new InvalidDataException($"'{path}' is not a preferences file: it has no CMainPreferences.");
        var general = document.Roots.FirstOrDefault(o => o.ClassKey == "CGeneralPreferences");
        return new Preferences(document, path, main, general);
    }

    /// <summary>
    /// The recent scenes, newest first, as the app stores them. A file without the member (an old
    /// preferences file) has none.
    /// </summary>
    public IReadOnlyList<string> RecentFiles =>
        _main["RecentFiles"] is List<object?> list
            ? list.OfType<string>().ToList()
            : Array.Empty<string>();

    /// <summary>
    /// Puts <paramref name="path"/> at the front of the recent list, removing every exact earlier
    /// occurrence, and drops the entries past the tenth. Returns false when the preferences file
    /// has no recent-file member. The change is in memory until <see cref="Save()"/>.
    /// </summary>
    public bool AddRecentFile(string path)
    {
        if (_main["RecentFiles"] is not List<object?> list) return false;
        var updated = Updated(RecentFiles, path);
        list.Clear();
        list.AddRange(updated.Cast<object?>());
        return true;
    }

    /// <summary>The list the <c>AddRecentFile</c> produces: newest first, exactly ten at most.</summary>
    public static List<string> Updated(IReadOnlyList<string> current, string path)
    {
        var list = current.Where(p => !string.Equals(p, path, StringComparison.Ordinal)).ToList();
        list.Insert(0, path);
        if (list.Count > MaxRecentFiles) list.RemoveRange(MaxRecentFiles, list.Count - MaxRecentFiles);
        return list;
    }

    /// <summary>The frame rate a new scene starts with; 60 when the preferences have no value.</summary>
    public int DefaultFrameRate => _main["DefaultFramerate"] is int fps && fps > 0 ? fps : 60;

    /// <summary>The autosave interval in minutes; 10 when the preferences have no value.</summary>
    public uint AutoSaveMinutes => _general?["AutoSaveTime"] is uint minutes && minutes > 0 ? minutes : 10;

    /// <summary>
    /// <c>CGeneralPreferences.SkipWelcomeWindow</c>: the Welcome window's "skip at startup" box
    /// The change is in memory until
    /// <see cref="Save()"/>; a file without the member leaves the window shown.
    /// </summary>
    public bool SkipWelcomeWindow
    {
        get => _general?[Welcome.SkipMember] switch
        {
            bool value => value,
            byte raw => raw != 0,
            _ => false,
        };
        set
        {
            if (_general?.Has(Welcome.SkipMember) == true) _general.Set(Welcome.SkipMember, value);
        }
    }

    /// <summary>
    /// The skeleton preferences that drive orient and Create Joint
    /// <c>JointAim</c>/<c>JointUp</c> (0/1/2 =
    /// X/Z/Y), the <c>JointAimInt</c>/<c>JointUpInt</c> 0..5 spinner values with their negative
    /// halves, and the flags bit 2 "align on create". Falls back to the shipped values.
    /// </summary>
    public JointAlignment JointAlignment
    {
        get
        {
            var page = Page("CSkeletonPreferences");
            if (page == null) return JointAlignment.Default;
            int aim = page.Get("JointAimInt") is int aimInt ? aimInt
                : page.Get("JointAim") is byte aimByte ? aimByte : (int)JointAlignment.Default.Aim;
            int up = page.Get("JointUpInt") is int upInt ? upInt
                : page.Get("JointUp") is byte upByte ? upByte : (int)JointAlignment.Default.Up;
            byte flags = page.Get("Flags") is byte f ? f : (byte)(JointAlignment.Default.AlignOnCreate ? 4 : 0);
            return JointAlignment.FromSpinnerIndices(aim, up, flags);
        }
    }

    /// <summary>
    /// Writes both the byte and int forms of the axis members and the flags, then saves the file
    /// when <paramref name="save"/>. Returns false (and a reason) when the page lacks the members.
    /// </summary>
    public bool SetJointAlignment(JointAlignment alignment, bool save, out string? error)
    {
        error = null;
        var page = Page("CSkeletonPreferences");
        if (page == null)
        {
            error = $"No CSkeletonPreferences page in '{Path}'.";
            return false;
        }
        page.Set("JointAim", (byte)alignment.Aim);
        page.Set("JointUp", (byte)alignment.Up);
        page.Set("JointAimInt", alignment.AimIndex);
        page.Set("JointUpInt", alignment.UpIndex);
        page.Set("Flags", alignment.Flags);
        if (save) Save();
        return true;
    }

    /// <summary>Every preference page at the file's root, in file order (without the main node).</summary>
    public IReadOnlyList<PreferencePage> Pages =>
        Document.Roots.Where(o => o.ClassKey != "CMainPreferences").Select(o => new PreferencePage(o)).ToList();

    /// <summary>The first page of class <paramref name="className"/>, or null.</summary>
    public PreferencePage? Page(string className)
    {
        string key = AktNames.Key(className);
        var page = Document.Roots.FirstOrDefault(o => o.ClassKey == key);
        return page == null ? null : new PreferencePage(page);
    }

    /// <summary>The value of <c>Class.Member</c>, or null when either is missing.</summary>
    public object? Get(string className, string member) => Page(className)?.Get(member);

    /// <summary>The value of <c>Class.Member</c> parsed from text. Returns false and a reason on failure.</summary>
    public bool TrySet(string className, string member, string text, out string? error)
    {
        var page = Page(className);
        if (page == null)
        {
            error = $"No {className} page in '{Path}'.";
            return false;
        }
        return page.TrySetFromString(member, text, out error);
    }

    /// <summary>The saved dock layout entries, the Rigging and Animation contexts first.</summary>
    public IEnumerable<DialogPlacement> DialogPlacements
    {
        get
        {
            foreach (var child in _main.Children)
            {
                if (child.ClassKey == "CDummy" && child.Name is { } context)
                {
                    foreach (var position in child.Children.Where(c => c.ClassKey == "CDialogPosition"))
                        yield return new DialogPlacement(context, position);
                }
                else if (child.ClassKey == "CDialogPosition")
                {
                    yield return new DialogPlacement("", child);
                }
            }
        }
    }

    /// <summary>
    /// The saved position of <paramref name="dialogClass"/> in <paramref name="context"/> (empty for
    /// the Spinner's own entry), or null when the file has none.
    /// </summary>
    public DialogPlacement? DialogPosition(string context, string dialogClass)
    {
        string name = dialogClass.StartsWith("class ", StringComparison.Ordinal) ? dialogClass : "class " + dialogClass;
        foreach (var child in _main.Children)
        {
            if (string.Equals(context, "", StringComparison.OrdinalIgnoreCase))
            {
                if (child.ClassKey == "CDialogPosition" && string.Equals(child.Name, name, StringComparison.Ordinal))
                    return new DialogPlacement("", child);
            }
            else if (child.ClassKey == "CDummy" &&
                     string.Equals(child.Name, context, StringComparison.OrdinalIgnoreCase))
            {
                var position = child.Children.FirstOrDefault(c =>
                    c.ClassKey == "CDialogPosition" && string.Equals(c.Name, name, StringComparison.Ordinal));
                return position == null ? null : new DialogPlacement(child.Name ?? context, position);
            }
        }
        return null;
    }

    /// <summary>
    /// The saved position of <paramref name="dialogClass"/> in <paramref name="context"/>, created
    /// when the context has none: a copy of one of the context's own entries, renamed, floating,
    /// unfolded, with no size and no dock partner. Null when the file has no entry to copy in that
    /// context (a layout that was never saved).
    /// </summary>
    public DialogPlacement? EnsureDialogPosition(string context, string dialogClass)
    {
        if (DialogPosition(context, dialogClass) is { } existing) return existing;
        if (context.Length == 0) return null;
        var group = _main.Children.FirstOrDefault(c =>
            c.ClassKey == "CDummy" && string.Equals(c.Name, context, StringComparison.OrdinalIgnoreCase));
        var template = group?.Children.FirstOrDefault(c => c.ClassKey == "CDialogPosition");
        if (group == null || template == null) return null;
        var copy = template.CloneHierarchy();
        copy.Set("Name", dialogClass.StartsWith("class ", StringComparison.Ordinal) ? dialogClass : "class " + dialogClass);
        var placement = new DialogPlacement(group.Name ?? context, copy)
        {
            // A floating place (DockLayout reads it as undocked) until the layout writes the real one.
            X = 1f,
            Y = 0.25f,
            DockDown = null,
            DockLeft = null,
            DockRight = null,
        };
        placement.Flags &= ~1u;
        placement.SetSizeBarPositions();
        group.AddChild(copy);
        return placement;
    }

    // The Spinner's shipped defaults are used when a
    // page or member is missing from an older file.
    public float MoveStep => _generalSpinner<float>("MoveStep", 0.1f);
    public float RotateStep => _generalSpinner<float>("RotateStep", 45f);
    public float ScaleStep => _generalSpinner<float>("ScaleStep", 0.5f);

    /// <summary>
    /// How many timebar rows show before the timebar scrolls: Timebar preferences "Displayed stacks"
    /// A missing or non-positive value reads as 4.
    /// </summary>
    public int VisibleTimelineRows
    {
        get
        {
            object? value = Get("CTimelinePreferences", "NbVisibleStacks");
            try
            {
                int n = value is null ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                return n > 0 ? n : 4;
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
            {
                return 4;
            }
        }
    }
    public bool MoveDiscrete => _generalSpinner("MoveDiscrete", false);
    public bool RotateDiscrete => _generalSpinner("RotateDiscrete", false);
    public bool ScaleDiscrete => _generalSpinner("ScaleDiscrete", false);
    public float SnapAngleThreshold => _generalSpinner<float>("SnapAngleThreshold", 0f);
    public uint PivotMask => _generalSpinner<uint>("PivotMask", 10u);

    /// <summary>
    /// <c>CSpinnerPreferences.BlendShapesQuadrantSelection</c>: the blendshape quadrant pad's
    /// selection, a mask whose bit <c>i</c> is quadrant <c>i</c>'s check state (the
    /// pad ANDs the quadrant's own bit with it); the shipped
    /// file carries 0, so nothing is marked.
    /// </summary>
    public uint BlendshapeQuadrantSelection => _generalSpinner<uint>("BlendShapesQuadrantSelection", 0u);

    /// <summary>Whether the Spinner's pivot compensation ("keep children fixed") is on for a transform mode.</summary>
    public bool KeepChildrenFixed(int transformMode) =>
        transformMode is > 0 and <= 32 && (PivotMask & (1u << (transformMode - 1))) != 0;

    // ---- SloppyAnimatsu's own Material page.
    // The class and its members exist only in SloppyAnimatsu's own file.

    public const string MaterialPageClass = "CMyAkMaterialPreferences";
    public const string MaterialAlwaysShow = "AlwaysShowPanel";
    public const string MaterialNearestForNew = "NearestFilterForNewTextures";
    public const string MaterialPreviewSize = "PreviewSize";
    public const int DefaultMaterialPreviewSize = 112;

    private static readonly (string Name, string Type)[] MaterialPageMembers =
    [
        (MaterialAlwaysShow, "bool"),
        (MaterialNearestForNew, "bool"),
        (MaterialPreviewSize, "int"),
    ];

    /// <summary>
    /// Adds the Material page (its class, any member it lacks, and the root object with the shipped
    /// values) when the file does not have it yet. Returns whether the document changed.
    /// </summary>
    public bool EnsureMaterialPage() =>
        EnsureOwnPage(MaterialPageClass, MaterialPageMembers, page => page.Set(MaterialPreviewSize, DefaultMaterialPreviewSize));

    // ---- SloppyAnimatsu's own Anim Bank page: how the bank panel sorts (AnimBankView).

    public const string AnimBankPageClass = "CMyAkAnimBankPreferences";
    /// <summary>The sort: <see cref="Model.AnimBankSort"/> as an int (0 A-Z, 1 Free, 2 Added).</summary>
    public const string AnimBankSortMember = "Sort";
    /// <summary>Whether the A-Z or Added sort runs the other way (Z-A, oldest first).</summary>
    public const string AnimBankReversedMember = "SortReversed";

    /// <summary>Adds the Anim Bank page when the file lacks it (A-Z, not reversed). Returns whether the document changed.</summary>
    public bool EnsureAnimBankPage() =>
        EnsureOwnPage(AnimBankPageClass, [(AnimBankSortMember, "int"), (AnimBankReversedMember, "bool")], null);

    /// <summary>How the Anim Bank panel sorts; A-Z unless another sort was chosen.</summary>
    public AnimBankSort AnimBankSort
    {
        get => Page(AnimBankPageClass)?.Get(AnimBankSortMember) is int n && Enum.IsDefined(typeof(AnimBankSort), n)
            ? (AnimBankSort)n : AnimBankSort.Alphabetical;
        set
        {
            EnsureAnimBankPage();
            Page(AnimBankPageClass)?.Set(AnimBankSortMember, (int)value);
        }
    }

    /// <summary>Whether the bank's A-Z or Added sort is reversed (Z-A; oldest first).</summary>
    public bool AnimBankSortReversed
    {
        get => Page(AnimBankPageClass)?.Get(AnimBankReversedMember) is true;
        set
        {
            EnsureAnimBankPage();
            Page(AnimBankPageClass)?.Set(AnimBankReversedMember, value);
        }
    }

    /// <summary>
    /// Adds one of SloppyAnimatsu's own pages (its class, any member it lacks, and the root object,
    /// set up by <paramref name="init"/>) when the file does not have it yet. Returns whether the
    /// document changed.
    /// </summary>
    private bool EnsureOwnPage(string pageClass, (string Name, string Type)[] members, Action<PreferencePage>? init)
    {
        var schema = Document.Schema;
        bool changed = false;
        var def = schema.FindClass(pageClass);
        if (def == null)
        {
            def = new AktClassDef(pageClass);
            // The same parent the pages have, so the page reads like one of them.
            schema.AddClass(def, schema.FindClass("CSpinnerPreferences")?.Parent);
            changed = true;
        }
        foreach (var (name, type) in members)
        {
            if (def.Members.Any(m => m.Name == name)) continue;
            def.Members.Add(new AktMember(name, type));
            changed = true;
        }
        var root = Document.Roots.FirstOrDefault(o => o.ClassKey == def.Key);
        if (root == null)
        {
            root = schema.NewObject(pageClass);
            Document.Roots.Add(root);
            init?.Invoke(new PreferencePage(root));
            return true;
        }
        if (changed) AktSchemaMerge.FillMissingMembers(schema, root);
        return changed;
    }

    /// <summary>Show the Material panel with no mesh selected too (off: it appears only for a mesh).</summary>
    public bool MaterialPanelAlwaysShown => Page(MaterialPageClass)?.Get(MaterialAlwaysShow) is true;

    /// <summary>Whether textures added to a material switch it to nearest-texel ("pixel") filtering.</summary>
    public bool NearestFilterForNewTextures => Page(MaterialPageClass)?.Get(MaterialNearestForNew) is true;

    /// <summary>The preview sphere's size in pixels, 48-512; the shipped 112 when missing or out of range.</summary>
    public int MaterialPreviewPixels =>
        Page(MaterialPageClass)?.Get(MaterialPreviewSize) is int n && n >= 48 && n <= 512 ? n : DefaultMaterialPreviewSize;

    private T _generalSpinner<T>(string member, T fallback) =>
        Page("CSpinnerPreferences")?.Get(member) is T value ? value : fallback;

    /// <summary>
    /// Writes the whole document back to the file it was loaded from: a <c>.slopa</c> in
    /// SloppyAnimatsu's container, an <c>.akt</c> (a profile passed with <c>--preferences</c>)
    /// in the plain .akt layout.
    /// </summary>
    public void Save() => Document.Save(Path, SettingsFiles.FormatFor(Path));

    /// <summary>Writes the whole document to <paramref name="path"/>, which becomes the new target.</summary>
    public void SaveAs(string path)
    {
        Document.Save(path, SettingsFiles.FormatFor(path));
        Path = path;
    }
}
