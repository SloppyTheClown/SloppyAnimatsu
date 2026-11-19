using System.Buffers.Binary;

namespace SloppyAnimatsu.Core.Audio;

/// <summary>
/// A decoded audio file: interleaved float samples in -1..1, at its own sample rate. Decoded from
/// The bytes the app embeds in a <c>CAudioEntry</c> (or from the file on disk), because the
/// viewer has no Qt Multimedia.
/// </summary>
public sealed class WaveFile
{
    /// <summary>Samples per second.</summary>
    public int SampleRate { get; }
    /// <summary>Interleaved channels.</summary>
    public int Channels { get; }
    /// <summary>Interleaved samples, <see cref="Channels"/> per frame, in -1..1.</summary>
    public float[] Samples { get; }

    public WaveFile(int sampleRate, int channels, float[] samples)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Samples = samples;
    }

    /// <summary>What the samples were decoded from, for diagnostics ("RIFF/WAVE" or "PCM s16le stereo").</summary>
    public string Format { get; private init; } = "RIFF/WAVE";

    public int FrameCount => Channels == 0 ? 0 : Samples.Length / Channels;
    public double Duration => SampleRate == 0 ? 0 : (double)FrameCount / SampleRate;

    /// <summary>One channel of one frame, or 0 for a frame outside the data.</summary>
    public float Sample(int frame, int channel)
    {
        if (frame < 0 || frame >= FrameCount) return 0f;
        int at = frame * Channels + Math.Clamp(channel, 0, Channels - 1);
        return at < Samples.Length ? Samples[at] : 0f;
    }

    /// <summary>
    /// Decodes a RIFF/WAVE file: integer PCM of 8, 16, 24 or 32 bits, or 32-bit float, mono or
    /// multichannel, plain or <c>WAVE_FORMAT_EXTENSIBLE</c>. Returns null for anything else
    /// (MP3, OGG and other containers are not part of the WAV decoder).
    /// </summary>
    public static WaveFile? TryDecode(byte[]? data)
    {
        if (data == null || data.Length < 44) return null;
        var span = new ReadOnlySpan<byte>(data);
        if (!span[..4].SequenceEqual("RIFF"u8) || !span.Slice(8, 4).SequenceEqual("WAVE"u8)) return null;

        int format = 0, channels = 0, sampleRate = 0, bits = 0, blockAlign = 0;
        ReadOnlySpan<byte> samples = default;
        bool haveFormat = false;

        int at = 12;
        while (at + 8 <= span.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(span.Slice(at, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(at + 4, 4));
            int body = at + 8;
            if (size < 0 || body + (long)size > span.Length) size = span.Length - body;
            switch (id)
            {
                case "fmt " when size >= 16:
                    format = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body, 2));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body + 2, 2));
                    sampleRate = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(body + 4, 4));
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body + 12, 2));
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body + 14, 2));
                    // WAVE_FORMAT_EXTENSIBLE: the real format is the first two bytes of the subformat GUID.
                    if (format == 0xFFFE && size >= 40)
                        format = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body + 24, 2));
                    haveFormat = true;
                    break;
                case "data":
                    samples = span.Slice(body, size);
                    break;
            }
            at = body + size + (size & 1);
        }

        if (!haveFormat || !IsSupported(format, bits)) return null;
        if (channels <= 0 || channels > 32 || sampleRate <= 0) return null;
        int bytesPerSample = bits / 8;
        if (bytesPerSample == 0 || blockAlign < bytesPerSample * channels) blockAlign = bytesPerSample * channels;
        int frames = samples.Length / blockAlign;
        if (frames == 0) return null;

        var result = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            int frameAt = f * blockAlign;
            for (int c = 0; c < channels; c++)
            {
                int p = frameAt + c * bytesPerSample;
                result[f * channels + c] = format switch
                {
                    1 => bits switch
                    {
                        8 => (samples[p] - 128) / 128f,
                        16 => BinaryPrimitives.ReadInt16LittleEndian(samples.Slice(p, 2)) / 32768f,
                        24 => (samples[p] | (samples[p + 1] << 8) | ((sbyte)samples[p + 2] << 16)) / 8388608f,
                        32 => BinaryPrimitives.ReadInt32LittleEndian(samples.Slice(p, 4)) / 2147483648f,
                        _ => 0f,
                    },
                    3 => BinaryPrimitives.ReadSingleLittleEndian(samples.Slice(p, 4)),
                    _ => 0f,
                };
            }
        }
        return new WaveFile(sampleRate, channels, result);
    }

    private static bool IsSupported(int format, int bits) =>
        (format == 1 && bits is 8 or 16 or 24 or 32) || (format == 3 && bits == 32);

    /// <summary>
    /// The rate the app decodes every imported sound to before embedding it
    /// </summary>
    public const int EmbeddedSampleRate = 44100;

    /// <summary>
    /// Decodes the bytes of a <c>CAudioEntry</c>: a RIFF/WAVE file (older scenes and SloppyAnimatsu's
    /// own fixtures), else the embedded form - headerless 16-bit little-endian stereo PCM
    /// at 44.1 kHz. Media containers are left alone: a scene that stored a real MP3 or OGG
    /// returns null rather than being read as noise.
    /// </summary>
    public static WaveFile? TryDecodeEmbedded(byte[]? data)
    {
        var wave = TryDecode(data);
        if (wave != null) return wave;
        return TryDecodePcm16Stereo(data, EmbeddedSampleRate);
    }

    /// <summary>Decodes headerless 16-bit little-endian stereo PCM; null when the bytes look like something else.</summary>
    public static WaveFile? TryDecodePcm16Stereo(byte[]? data, int sampleRate)
    {
        if (data == null || data.Length < 4 || (data.Length & 3) != 0 || sampleRate <= 0) return null;
        if (LooksLikeContainer(data)) return null;
        int frames = data.Length / 4;
        var samples = new float[frames * 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(new ReadOnlySpan<byte>(data, i * 2, 2)) / 32768f;
        return new WaveFile(sampleRate, 2, samples) { Format = "PCM s16le stereo" };
    }

    /// <summary>True for bytes that begin like a known media or audio container (never raw PCM).</summary>
    private static bool LooksLikeContainer(ReadOnlySpan<byte> d) =>
        (d.Length >= 4 && d[..4].SequenceEqual("RIFF"u8)) ||
        (d.Length >= 4 && d[..4].SequenceEqual("OggS"u8)) ||
        (d.Length >= 3 && d[..3].SequenceEqual("ID3"u8)) ||
        (d.Length >= 4 && d[..4].SequenceEqual("fLaC"u8));

    /// <summary>Writes the embedded form (interleaved stereo 16-bit PCM) from float samples; for fixtures.</summary>
    public static byte[] EncodePcm16Stereo(float[] interleaved)
    {
        var data = new byte[interleaved.Length * 2];
        for (int i = 0; i < interleaved.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(new Span<byte>(data, i * 2, 2),
                (short)Math.Clamp((int)MathF.Round(interleaved[i] * 32767f), short.MinValue, short.MaxValue));
        return data;
    }

    /// <summary>
    /// This sound in the form the app stores in a <c>CAudioEntry</c>: headerless 16-bit little-endian
    /// stereo PCM at <see cref="EmbeddedSampleRate"/>
    /// Linear interpolation when the rate differs, a mono source feeds both
    /// channels, and the first two channels are kept of a wider one. The frame count is rounded the
    /// way a duration converts, so the duration is preserved.
    /// </summary>
    public byte[] ToEmbeddedPcm()
    {
        if (SampleRate <= 0 || Channels <= 0) return [];
        int frames = (int)Math.Round(FrameCount * (double)EmbeddedSampleRate / SampleRate);
        var stereo = new float[frames * 2];
        double step = (double)SampleRate / EmbeddedSampleRate;
        for (int f = 0; f < frames; f++)
        {
            double at = f * step;
            int a = (int)at;
            float t = (float)(at - a);
            for (int c = 0; c < 2; c++)
            {
                int channel = Math.Min(c, Channels - 1);
                float s0 = Sample(a, channel), s1 = Sample(a + 1, channel);
                stereo[f * 2 + c] = s0 + (s1 - s0) * t;
            }
        }
        return EncodePcm16Stereo(stereo);
    }

    /// <summary>Writes a mono 16-bit PCM WAV of <paramref name="seconds"/>; for tests and fixtures.</summary>
    public static byte[] EncodeMono(float[] samples, int sampleRate)
    {
        var data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(new Span<byte>(data, i * 2, 2),
                (short)Math.Clamp((int)MathF.Round(samples[i] * 32767f), short.MinValue, short.MaxValue));
        var header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(header, 4, 4), 36 + data.Length);
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(header, 16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(new Span<byte>(header, 20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(new Span<byte>(header, 22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(header, 24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(header, 28, 4), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(new Span<byte>(header, 32, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(new Span<byte>(header, 34, 2), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(header, 40, 4), data.Length);
        return [.. header, .. data];
    }
}
