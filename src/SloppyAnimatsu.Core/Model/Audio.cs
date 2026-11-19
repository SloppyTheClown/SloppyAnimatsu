using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Audio;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// <c>CAudioEntry</c>: one audio file of the scene
/// The app embeds the sound it decoded at import in <c>EmbedBufferData</c>
/// (not the original file), so it plays without the source file; a scene that only stores the
/// name falls back to the file next to it.
/// </summary>
public sealed class AudioEntry : SceneObject
{
    internal AudioEntry(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary><c>FileName</c>: the original path of the file, as dropped.</summary>
    public string FileName => Source.GetOr("FileName", "");

    /// <summary>
    /// <c>EmbedBufferData</c>: the sound the app decoded from the imported file - see
    /// <see cref="WaveFile.TryDecodeEmbedded"/> (RIFF/WAVE, else raw 16-bit stereo PCM at 44.1 kHz).
    /// </summary>
    public byte[] Data => Source.GetOr<byte[]>("EmbedBufferData", []);

    public string DisplayName =>
        FileName.Length > 0 ? Path.GetFileName(FileName.Replace('\\', '/')) : Name ?? "Audio Entry";

    private WaveFile? _decoded;
    private bool _decodedTried;

    /// <summary>The decoded sound: the embedded bytes first, then the file the name points at.</summary>
    public WaveFile? Audio
    {
        get
        {
            if (_decodedTried) return _decoded;
            _decodedTried = true;
            _decoded = WaveFile.TryDecodeEmbedded(Data);
            if (_decoded == null && ResolvePath() is { } path && File.Exists(path))
            {
                try { _decoded = WaveFile.TryDecode(File.ReadAllBytes(path)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return _decoded;
        }
    }

    /// <summary>The entry's duration; 0 when it holds no decodable sound.</summary>
    public double DurationSeconds => Audio?.Duration ?? 0;

    /// <summary>The file path, absolute or relative to the scene's own folder.</summary>
    public string? ResolvePath()
    {
        if (FileName.Length == 0) return null;
        if (Path.IsPathRooted(FileName)) return FileName;
        return Model.Path is { } scene && Path.GetDirectoryName(scene) is { Length: > 0 } folder
            ? Path.Combine(folder, FileName)
            : FileName;
    }

    /// <summary>Puts a sound back into the document (used by tests and scene fixtures).</summary>
    public void SetSound(byte[] data, string fileName)
    {
        Source.Set("EmbedBufferData", data);
        Source.Set("FileName", fileName);
        _decodedTried = false;
        _decoded = null;
    }

    public override string ToString() => $"AudioEntry '{DisplayName}'";
}

/// <summary>
/// <c>CAudioObject</c>: a character's audio anchor (a <c>CObject3D</c> child of the character).
/// It carries no authoring data of its own; the events live in the animations' audio stacks.
/// </summary>
public sealed class AudioObject : Object3D
{
    internal AudioObject(SceneModel model, AktObject source) : base(model, source) { }

    public Character? Character => Ancestor<Character>();
}

/// <summary>
/// <c>CAnimStackAudio</c>: a stack whose track holds audio events instead of poses. Its keys are
/// the events' start frames.
/// </summary>
public sealed class AudioStack : AnimStack
{
    internal AudioStack(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary><c>AudioTrack</c>: the stack's single track.</summary>
    public AudioTrack? AudioTrack => Ref<AnimTrack>("AudioTrack") as AudioTrack;

    /// <summary>The stack's events, one per key (the loop key is not an event).</summary>
    public IReadOnlyList<AudioEvent> Events => AudioTrack?.Events ?? [];
}

/// <summary><c>CAnimTrackAudio</c>: the audio track of an <see cref="AudioStack"/>.</summary>
public sealed class AudioTrack : AnimTrack
{
    internal AudioTrack(SceneModel model, AktObject source) : base(model, source) { }

    private AudioEvent[]? _events;

    /// <summary>The audio object this track belongs to (its <c>Target</c>).</summary>
    public AudioObject? AudioObject => Target as AudioObject;

    /// <summary>
    /// One event per stack key. The serialized key is a <c>CKeyAudioAnim</c> (<c>AudioFile</c>,
    /// <c>Volume</c>, <c>Mute</c>; confirmed by a real.akt scene
    /// "A real audio scene"); the in-memory <c>CKeyAudioFrame</c> wrapper and its
    /// delay are not written, so the members are looked up by name and degrade to defaults.
    /// </summary>
    public IReadOnlyList<AudioEvent> Events
    {
        get
        {
            if (_events != null) return _events;
            var keys = SceneValues.Items(Source, "KeyList");
            var times = Stack?.Keys ?? [];
            // The last stack key is the loop key: it repeats key 0 and is not an event.
            int count = keys.Count == 0 ? 0 : Math.Min(keys.Count, Math.Max(times.Count - 1, 0));
            var events = new List<AudioEvent>(count);
            for (int i = 0; i < count; i++)
                if (keys[i] is AktStruct key)
                    events.Add(AudioEvent.Read(this, i, times[i].Time, key));
            _events = [.. events];
            return _events;
        }
    }

    public override string ToString() => $"AudioTrack '{AudioObject?.Name}'";
}

/// <summary>One audio event: a key of an <see cref="AudioTrack"/> at a frame, with its sound.</summary>
public sealed class AudioEvent
{
    private AudioEvent(AudioTrack track, int index, int startFrame, AudioEntry? entry,
        float volume, bool mute, int delayFrames)
    {
        Track = track;
        Index = index;
        _startFrame = startFrame;
        Entry = entry;
        Volume = volume;
        Mute = mute;
        DelaySinceStartKey = delayFrames;
    }

    private readonly int _startFrame;

    public AudioTrack Track { get; }
    /// <summary>Index in the stack's key list (not counting the loop key).</summary>
    public int Index { get; }
    /// <summary>
    /// The stack key's time: where the event starts, in frames. Read live from the stack, so a
    /// timeline drag that retimes the key moves its clip with it.
    /// </summary>
    public int StartFrame =>
        Track.Stack is { } stack && Index < stack.Keys.Count ? stack.Keys[Index].Time : _startFrame;
    /// <summary><c>DelaySinceStartKey</c>: a per-key offset, 0 unless the app wrote one.</summary>
    public int DelaySinceStartKey { get; }
    /// <summary>The sound; null for a silent key (e.g. a default key made by the hammer).</summary>
    public AudioEntry? Entry { get; }
    /// <summary>The key's own volume (the playback uses the stack's opacity instead).</summary>
    public float Volume { get; }
    public bool Mute { get; }

    public WaveFile? Audio => Entry?.Audio;
    public double DurationSeconds => Audio?.Duration ?? 0;

    /// <summary>True when the event is audible at <paramref name="frame"/>.</summary>
    public bool IsAudibleAt(double frame, uint frameRate)
    {
        if (Mute || Audio is not { Duration: > 0 } sound || frameRate == 0) return false;
        double elapsedFrames = frame - StartFrame - DelaySinceStartKey;
        return elapsedFrames >= 0 && elapsedFrames / frameRate < sound.Duration;
    }

    /// <summary>Reads a key struct: the audio sub-struct holds the file and mute, the key the volume.</summary>
    internal static AudioEvent Read(AudioTrack track, int index, int startFrame, AktStruct key)
    {
        // The key's audio data is a nested struct (the CKeyAudioAnim); pick the one that looks
        // like it, not any other struct-valued member
        var structs = key.Fields.Where(f => f.Value is AktStruct).Select(f => (AktStruct)f.Value!).ToList();
        AktStruct? audio = structs.FirstOrDefault(s => s.Has("AudioFile") || s.Has("Mute")) ?? structs.FirstOrDefault();
        AudioEntry? entry = null;
        if ((audio?["AudioFile"] ?? key["AudioFile"]) is AktReference { Target: { } target })
            entry = track.Model.Wrap(target) as AudioEntry;
        bool mute = audio?.Has("Mute") == true ? SceneValues.Flag(audio, "Mute")
            : key.Has("Mute") && SceneValues.Flag(key, "Mute");
        float volume = key.Has("Volume") ? key.GetOr("Volume", 1f) : audio?.GetOr("Volume", 1f) ?? 1f;
        // The delay is a frame count; the member may be stored as uint or int.
        int delay = key["DelaySinceStartKey"] switch { uint u => (int)u, int i => i, ushort s => s, _ => 0 };
        return new AudioEvent(track, index, startFrame, entry, volume, mute, delay);
    }

    public override string ToString() =>
        $"Audio '{Entry?.DisplayName ?? "(none)"}' at {StartFrame}" + (Mute ? " (muted)" : "");
}

/// <summary>
/// What is audible at one playhead position, as the app decides
/// it: per audio stack at most one event — the
/// latest one that started and whose file has not run out — at the stack's opacity.
/// </summary>
public static class AudioSchedule
{
    /// <summary>
    /// The clips audible at <paramref name="frame"/> (the frame at <paramref name="seconds"/>),
    /// with the absolute time at which each clip's first sample plays. A cycle stack wraps the
    /// playhead modulo its span (last key − first key), as the app does.
    /// </summary>
    public static IReadOnlyList<ScheduledClip> At(Animation? animation, double seconds, double frame, uint frameRate)
    {
        if (animation == null || frameRate == 0) return [];
        var clips = new List<ScheduledClip>();
        foreach (var stack in animation.Stacks)
        {
            if (stack is not AudioStack audio || !stack.IsEnabled || stack.Opacity <= 0f) continue;
            var (chosen, local) = EventAt(stack, audio, frame, frameRate);
            if (chosen?.Audio is not { } sound) continue;
            double elapsed = local - chosen.StartFrame - chosen.DelaySinceStartKey;
            double start = seconds - elapsed / frameRate;
            clips.Add(new ScheduledClip(chosen, sound, start, stack.Opacity));
        }
        return clips;
    }

    /// <summary>The event an audio stack plays at <paramref name="frame"/>, and the frame used to find it.</summary>
    public static (AudioEvent? Event, double Frame) EventAt(AnimStack stack, AudioStack audio, double frame, uint frameRate)
    {
        var keys = stack.Keys;
        double local = frame;
        if (stack.IsCycle && keys.Count > 1)
        {
            int first = keys[0].Time, last = keys[keys.Count - 1].Time;
            if (last > first) local = first + Mod(frame - first, last - first);
        }
        AudioEvent? chosen = null;
        foreach (var e in audio.Events)
        {
            if (e.Audio is not { Duration: > 0 } sound) continue;
            double elapsed = local - e.StartFrame - e.DelaySinceStartKey;
            if (frameRate == 0 || elapsed < 0 || elapsed / frameRate >= sound.Duration) continue;
            if (chosen == null || e.StartFrame >= chosen.StartFrame) chosen = e;
        }
        return (chosen, local);
    }

    /// <summary>The frame at <paramref name="seconds"/> of a playback that started at <paramref name="from"/>.</summary>
    public static double FrameAt(double from, double seconds, uint frameRate, int start, int end, bool loop)
    {
        double frame = from + seconds * frameRate;
        if (loop && end > start && frame > end)
            frame = start + Mod(frame - start, end - start);
        return frame;
    }

    public static double Mod(double value, double period)
    {
        if (period <= 0) return 0;
        double r = value % period;
        return r < 0 ? r + period : r;
    }
}
