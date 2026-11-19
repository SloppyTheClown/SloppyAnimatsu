using System.Buffers.Binary;
using System.Text;
using System.Runtime.InteropServices;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// One target placed on a channel of a blendshape setup file (<c>.bsu</c>): the target's name and
/// its position on the channel's slider.
/// </summary>
public sealed record BlendshapeSetUpInfo(string Target, float Position);

/// <summary>
/// One channel of a setup controller: the value it follows (<c>Direction</c>), its attenuation and
/// its target placements. A setup file has no channel name, so a channel is identified by its
/// direction.
/// </summary>
public sealed record BlendshapeSetUpChannel(uint Direction, float Attenuation, IReadOnlyList<BlendshapeSetUpInfo> Infos);

/// <summary>
/// One controller of a setup file: its name, its <c>Click</c> (-1 for a plain
/// <c>CBlendshapeController</c>) and its channels. The file has no mesh field, so a setup belongs
/// to whichever mesh it is applied to.
/// </summary>
public sealed record BlendshapeSetUpController(string Name, int Click, IReadOnlyList<BlendshapeSetUpChannel> Channels);

/// <summary>
/// What <see cref="BlendshapeSetUp.Apply"/> changed: the controllers it created and updated, the
/// channels it created and updated, the infos it created and updated, and the targets it created.
/// </summary>
public readonly record struct BlendshapeSetUpApplyResult(
    int ControllersCreated, int ControllersUpdated, int ChannelsCreated, int ChannelsUpdated,
    int InfosCreated, int InfosUpdated, int TargetsCreated)
{
    public override string ToString() =>
        $"{ControllersCreated} controller(s) created, {ControllersUpdated} updated; " +
        $"{ChannelsCreated} channel(s) created, {ChannelsUpdated} updated; " +
        $"{InfosCreated} info(s) created, {InfosUpdated} updated; {TargetsCreated} target(s) created.";
}

/// <summary>
/// The blendshape setup file (<c>Blendshape Set Up Files (*.bsu)</c>): the reader and writer
/// of the container the blendshape set-up export writes and the set-up import
/// applies, plus the import onto one mesh. The
/// layout is little-endian:
/// <code>
/// u16 20, u16 3, u16 13, u16 1        header (the writer's four constants)
/// i32 controllerCount
///   WString name                      UTF-16LE code units + a u16 NUL
///   i32 click                         -1 = a CBlendshapeController, else the face controller's Click
///   i32 channelCount
///     u32 direction
///     f32 attenuation
///     i32 infoCount
///       WString target
///       f32 position
/// </code>
/// The string codec is the .akt container's (NUL-terminated UTF-16LE, no length prefix), and it
/// cannot carry a NUL inside a name because a zero code unit ends the string.
/// </summary>
public static class BlendshapeSetUp
{
    /// <summary>The first header word.</summary>
    public const ushort Header0 = 20;

    /// <summary>The second header word.</summary>
    public const ushort Header1 = 3;

    /// <summary>The third header word.</summary>
    public const ushort Header2 = 13;

    /// <summary>The fourth header word.</summary>
    public const ushort Header3 = 1;

    /// <summary>
    /// The mesh's controllers in the character's <c>Controllers</c> order - the array the
    /// export walks. An
    /// info without a target is written with an empty name, as the writer does.
    /// </summary>
    public static IReadOnlyList<BlendshapeSetUpController> Capture(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (CharacterOf(mesh) is not { } character)
            throw new InvalidDataException($"The mesh '{mesh.Name}' belongs to no character: a blendshape setup needs a controller array.");
        return character.Controllers.OfType<BlendshapeController>()
            .Where(controller => ReferenceEquals(controller.Mesh, mesh))
            .Select(controller => new BlendshapeSetUpController(
                controller.Name ?? "",
                controller is BlendshapeFaceController face ? (int)face.Click : -1,
                controller.Channels.Select(channel => new BlendshapeSetUpChannel(
                    channel.Direction,
                    channel.Attenuation,
                    channel.Infos.Select(info => new BlendshapeSetUpInfo(info.Blendshape?.Name ?? "", info.Position)).ToList())).ToList()))
            .ToList();
    }

    /// <summary>The bytes of a setup file.</summary>
    public static byte[] Write(IReadOnlyList<BlendshapeSetUpController> controllers)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Header0);
            writer.Write(Header1);
            writer.Write(Header2);
            writer.Write(Header3);
            writer.Write(controllers.Count);
            foreach (var controller in controllers)
            {
                WriteString(writer, controller.Name);
                writer.Write(controller.Click);
                writer.Write(controller.Channels.Count);
                foreach (var channel in controller.Channels)
                {
                    writer.Write(channel.Direction);
                    writer.Write(channel.Attenuation);
                    writer.Write(channel.Infos.Count);
                    foreach (var info in channel.Infos)
                    {
                        WriteString(writer, info.Target);
                        writer.Write(info.Position);
                    }
                }
            }
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Reads a setup file, throwing a <see cref="FormatException"/> for a wrong header, a negative
    /// count, a truncated read or an unterminated string.
    /// </summary>
    public static IReadOnlyList<BlendshapeSetUpController> Read(ReadOnlySpan<byte> data)
    {
        var reader = new Reader(data);
        return ReadControllers(ref reader);
    }

    /// <summary>Reads a setup file, reporting a bad one as <c>false</c> with <paramref name="error"/> instead of throwing.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out IReadOnlyList<BlendshapeSetUpController> controllers,
        out string? error)
    {
        try
        {
            var reader = new Reader(data);
            controllers = ReadControllers(ref reader);
            error = null;
            return true;
        }
        catch (FormatException e)
        {
            controllers = [];
            error = e.Message;
            return false;
        }
    }

    private static List<BlendshapeSetUpController> ReadControllers(ref Reader reader)
    {
        if (reader.U16() != Header0 || reader.U16() != Header1 || reader.U16() != Header2 || reader.U16() != Header3)
            throw new FormatException(
                $"Not a blendshape setup file: the header is not {Header0}, {Header1}, {Header2}, {Header3}.");
        int controllerCount = reader.I32();
        if (controllerCount < 0)
            throw new FormatException($"A negative controller count ({controllerCount}) in the blendshape setup.");
        var controllers = new List<BlendshapeSetUpController>(controllerCount);
        for (int c = 0; c < controllerCount; c++)
        {
            string name = reader.WString("a controller name");
            int click = reader.I32();
            int channelCount = reader.I32();
            if (channelCount < 0)
                throw new FormatException($"A negative channel count ({channelCount}) for controller '{name}'.");
            var channels = new List<BlendshapeSetUpChannel>(channelCount);
            for (int ch = 0; ch < channelCount; ch++)
            {
                uint direction = reader.U32();
                float attenuation = reader.F32();
                int infoCount = reader.I32();
                if (infoCount < 0)
                    throw new FormatException(
                        $"A negative info count ({infoCount}) for controller '{name}', channel {direction}.");
                var infos = new List<BlendshapeSetUpInfo>(infoCount);
                for (int i = 0; i < infoCount; i++)
                    infos.Add(new BlendshapeSetUpInfo(reader.WString("a target name"), reader.F32()));
                channels.Add(new BlendshapeSetUpChannel(direction, attenuation, infos));
            }
            controllers.Add(new BlendshapeSetUpController(name, click, channels));
        }
        return controllers;
    }

    /// <summary>
    /// Applies a setup to <paramref name="mesh"/> (another character's or another mesh's setup is not
    /// touched): controllers are matched by name (case-insensitively, as the app compares them) and
    /// created in the file's kind; channels are matched by direction, infos by their target. A target
    /// the mesh lacks is created with zero deltas and the file's name (uniqued by the editor when the
    /// name is taken); an info the channel lacks is placed at the file's position. The schema
    /// extension, pose slots and rig tracks come from <see cref="BlendshapeEditor"/>, so a saved scene
    /// is the same as one built by <c>blendshape-add</c>.
    /// </summary>
    public static BlendshapeSetUpApplyResult Apply(Mesh mesh, IReadOnlyList<BlendshapeSetUpController> setup)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(setup);
        if (CharacterOf(mesh) is not { } character)
            throw new InvalidDataException($"The mesh '{mesh.Name}' belongs to no character: a blendshape setup needs a controller array.");
        var editor = new BlendshapeEditor(character);
        var controllers = character.Controllers.OfType<BlendshapeController>()
            .Where(controller => ReferenceEquals(controller.Mesh, mesh)).ToList();
        int controllersCreated = 0, controllersUpdated = 0, channelsCreated = 0, channelsUpdated = 0;
        int infosCreated = 0, infosUpdated = 0, targetsCreated = 0;
        foreach (var entry in setup)
        {
            if (string.IsNullOrEmpty(entry.Name))
                throw new InvalidDataException("A controller of the setup has an empty name.");
            var controller = controllers.FirstOrDefault(c =>
                string.Equals(c.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (controller == null)
            {
                controller = entry.Click < 0
                    ? editor.AddController(mesh, entry.Name)
                    : editor.AddFaceController(mesh, entry.Name);
                controllers.Add(controller);
                controllersCreated++;
            }
            else
            {
                controllersUpdated++;
            }
            if (entry.Click >= 0 && controller is BlendshapeFaceController face)
                face.Click = (uint)entry.Click;
            foreach (var channelEntry in entry.Channels)
            {
                var channel = controller.Channels.FirstOrDefault(c => c.Direction == channelEntry.Direction);
                if (channel == null)
                {
                    // The file has no channel name; the editor names a created channel "Channel".
                    channel = editor.AddChannel(controller, channelEntry.Direction, channelEntry.Attenuation);
                    channelsCreated++;
                }
                else
                {
                    channel.Attenuation = channelEntry.Attenuation;
                    channelsUpdated++;
                }
                foreach (var infoEntry in channelEntry.Infos)
                {
                    if (string.IsNullOrEmpty(infoEntry.Target))
                        throw new InvalidDataException(
                            $"An info of controller '{entry.Name}' has an empty target name; a setup cannot apply a target it does not name.");
                    var target = mesh.BlendshapeTargets.FirstOrDefault(t =>
                        string.Equals(t.Name, infoEntry.Target, StringComparison.OrdinalIgnoreCase));
                    if (target == null)
                    {
                        target = editor.AddTarget(mesh, infoEntry.Target);
                        targetsCreated++;
                    }
                    var info = channel.Infos.FirstOrDefault(i => ReferenceEquals(i.Blendshape, target));
                    if (info == null)
                    {
                        editor.AddInfo(channel, target, infoEntry.Position);
                        infosCreated++;
                    }
                    else
                    {
                        info.Position = infoEntry.Position;
                        infosUpdated++;
                    }
                }
            }
        }
        return new BlendshapeSetUpApplyResult(controllersCreated, controllersUpdated, channelsCreated, channelsUpdated,
            infosCreated, infosUpdated, targetsCreated);
    }

    private static Character? CharacterOf(Mesh mesh) => mesh.BindPose?.Character ?? mesh.Ancestor<Character>();

    private static void WriteString(BinaryWriter writer, string? value)
    {
        value ??= "";
        if (value.Contains('\0'))
            throw new FormatException($"The name '{value}' contains a NUL character, which the setup's string codec cannot store.");
        writer.Write(Encoding.Unicode.GetBytes(value));
        writer.Write((ushort)0);
    }

    /// <summary>Bounds-checked little-endian reads; every refusal names the field it was reading.</summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public Reader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public ushort U16()
        {
            Need(2, "a header word");
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
            _position += 2;
            return value;
        }

        public uint U32()
        {
            Need(4, "a 32-bit value");
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
            _position += 4;
            return value;
        }

        public int I32() => (int)U32();

        public float F32()
        {
            Need(4, "a float");
            float value = BinaryPrimitives.ReadSingleLittleEndian(_data[_position..]);
            _position += 4;
            return value;
        }

        public string WString(string field)
        {
            int start = _position;
            int end = start;
            while (true)
            {
                if (end + 2 > _data.Length)
                    throw new FormatException($"Truncated blendshape setup: {field} is not NUL-terminated.");
                if (_data[end] == 0 && _data[end + 1] == 0) break;
                end += 2;
            }
            _position = end + 2;
            return new string(MemoryMarshal.Cast<byte, char>(_data[start..end]));
        }

        private void Need(int count, string what)
        {
            if (_position + count > _data.Length)
                throw new FormatException($"Truncated blendshape setup: {what} is missing at byte {_position} of {_data.Length}.");
        }
    }
}
