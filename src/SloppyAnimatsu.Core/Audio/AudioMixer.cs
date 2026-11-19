namespace SloppyAnimatsu.Core.Audio;

/// <summary>
/// One clip that should be heard: its decoded audio, the absolute time (seconds) at which its
/// first sample belongs, and the volume to play it at. <see cref="Key"/> identifies the event so
/// the mixer can tell clips apart between blocks.
/// </summary>
public readonly record struct ScheduledClip(object Key, WaveFile Audio, double StartSeconds, float Volume);

/// <summary>Returns the clips that are audible at <paramref name="seconds"/> (or start within the block).</summary>
public delegate IReadOnlyList<ScheduledClip> AudioScheduleProvider(double seconds);

/// <summary>
/// Software mixer: turns the clips under the playhead into interleaved float samples for an
/// <see cref="IAudioSink"/>. It is stateless with respect to the clips (each block computes each
/// clip's position from absolute time), so a seek or a wrapped cycle cannot drift, and tests can
/// check any block in isolation.
/// </summary>
public sealed class AudioMixer
{
    private readonly AudioScheduleProvider _provider;
    private double _time;

    public int SampleRate { get; }
    public int Channels { get; }

    public AudioMixer(int sampleRate, int channels, AudioScheduleProvider provider)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        SampleRate = sampleRate;
        Channels = channels;
        _provider = provider;
    }

    /// <summary>Timeline position (seconds) of the next rendered block.</summary>
    public double Time => _time;

    /// <summary>Moves the playhead; the next block renders from there.</summary>
    public void Seek(double seconds) => _time = seconds;

    /// <summary>Renders <paramref name="frames"/> frames of mixed audio and advances the playhead.</summary>
    public float[] Render(int frames)
    {
        var output = new float[frames * Channels];
        double start = _time;
        double end = start + frames / (double)SampleRate;
        var clips = ClipsAt(start, end);
        if (clips.Count > 0)
        {
            foreach (var clip in clips)
            {
                var audio = clip.Audio;
                if (audio.SampleRate <= 0 || audio.FrameCount == 0) continue;
                int channels = audio.Channels;
                for (int i = 0; i < frames; i++)
                {
                    double local = start + i / (double)SampleRate - clip.StartSeconds;
                    if (local < 0) continue;
                    double source = local * audio.SampleRate;
                    int frame = (int)source;
                    if (frame >= audio.FrameCount) continue;
                    float frac = (float)(source - frame);
                    for (int c = 0; c < Channels; c++)
                    {
                        // A mono clip feeds every output channel; a stereo clip maps by position;
                        // other layouts fold their channels in.
                        int sc = channels == 1 ? 0 : c < channels ? c : c % channels;
                        float s0 = audio.Sample(frame, sc);
                        float s1 = frame + 1 < audio.FrameCount ? audio.Sample(frame + 1, sc) : s0;
                        output[i * Channels + c] += (s0 + (s1 - s0) * frac) * clip.Volume;
                    }
                }
            }
            for (int i = 0; i < output.Length; i++) output[i] = Math.Clamp(output[i], -1f, 1f);
        }
        _time = end;
        return output;
    }

    /// <summary>True when anything would be heard at <paramref name="seconds"/>; used for scrubbing.</summary>
    public bool HasAudioAt(double seconds) => _provider(seconds).Count > 0;

    /// <summary>The clips from the provider at both ends of the block, without duplicates.</summary>
    private List<ScheduledClip> ClipsAt(double start, double end)
    {
        var first = _provider(start);
        var second = _provider(end);
        if (second.Count == 0) return first as List<ScheduledClip> ?? [.. first];
        var result = new List<ScheduledClip>(first.Count + second.Count);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var clip in first) if (seen.Add(clip.Key)) result.Add(clip);
        foreach (var clip in second) if (seen.Add(clip.Key)) result.Add(clip);
        return result;
    }
}

/// <summary>The audio device the mixer writes to. Implementations may be silent (headless, tests).</summary>
public interface IAudioSink : IDisposable
{
    /// <summary>Samples per second the sink expects (the mixer uses the same rate).</summary>
    int SampleRate { get; }
    /// <summary>Interleaved channels the sink expects.</summary>
    int Channels { get; }
    /// <summary>Queues or plays one block; the call may block until the device is ready.</summary>
    void Write(float[] samples);
}

/// <summary>An <see cref="IAudioSink"/> that discards its input.</summary>
public sealed class SilentAudioSink : IAudioSink
{
    public int SampleRate { get; }
    public int Channels { get; }
    public SilentAudioSink(int sampleRate = 48000, int channels = 2)
    {
        SampleRate = sampleRate;
        Channels = channels;
    }
    public void Write(float[] samples) { }
    public void Dispose() { }
}
