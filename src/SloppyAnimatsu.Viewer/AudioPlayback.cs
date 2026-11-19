using System.Runtime.InteropServices;
using SloppyAnimatsu.Core.Audio;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// Plays the scene's audio while the timeline runs: a background thread renders 20 ms blocks from
/// an <see cref="AudioMixer"/> into an <see cref="IAudioSink"/>. The mixer's block time is the
/// animation's own time, so the audio follows the same wrapped playhead as the pose
/// </summary>
public sealed class AudioPlayback : IDisposable
{
    private const int BlockFrames = 960;      // 20 ms at 48 kHz
    private readonly object _lock = new();
    private IAudioSink? _sink;
    private AudioMixer? _mixer;
    private Thread? _thread;
    private volatile bool _running;
    private Animation? _animation;
    private uint _fps = 30;
    private int _start;
    private int _end;
    private bool _loop;

    /// <summary>Set when the output device could not be opened; the viewer shows it once.</summary>
    public string? Error { get; private set; }

    public bool IsPlaying => _running;

    /// <summary>Starts playing the animation's audio from <paramref name="fromFrame"/>; does nothing when it has none.</summary>
    public void Start(Animation? animation, double fromFrame, uint frameRate, bool loop = true, (int Start, int End)? range = null)
    {
        Stop();
        Error = null;
        if (animation == null || frameRate == 0) return;
        if (!animation.Stacks.OfType<AudioStack>().Any(s => s.Events.Any(e => e.Audio != null))) return;

        _animation = animation;
        _fps = frameRate;
        (_start, _end) = range ?? animation.PlayRange;
        _loop = loop;
        try
        {
            _sink ??= CreateSink();
            _mixer = new AudioMixer(_sink.SampleRate, _sink.Channels, ClipsAt);
            _mixer.Seek(fromFrame / frameRate);
        }
        catch (Exception e)
        {
            Error = "Audio: " + e.Message;
            return;
        }
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "SloppyAnimatsu audio" };
        _thread.Start();
    }

    /// <summary>Moves the playhead (used when the user scrubs while playing).</summary>
    public void Seek(double frame)
    {
        lock (_lock)
        {
            if (_fps > 0) _mixer?.Seek(frame / _fps);
        }
    }

    public void Stop()
    {
        _running = false;
        var thread = _thread;
        _thread = null;
        thread?.Join(500);
        _mixer = null;
    }

    /// <summary>The clips at one mixer block, wrapped like the pose playhead.</summary>
    private IReadOnlyList<ScheduledClip> ClipsAt(double seconds)
    {
        double frame = seconds * _fps;
        if (_loop && _end > _start && frame > _end)
            frame = _start + AudioSchedule.Mod(frame - _start, _end - _start);
        return AudioSchedule.At(_animation, seconds, frame, _fps);
    }

    private void Run()
    {
        var sink = _sink!;
        while (_running)
        {
            float[] block;
            lock (_lock) block = _mixer?.Render(BlockFrames) ?? new float[BlockFrames * sink.Channels];
            try
            {
                sink.Write(block);
            }
            catch (Exception e)
            {
                Error = "Audio: " + e.Message;
                _running = false;
            }
        }
    }

    private static IAudioSink CreateSink()
    {
        if (OperatingSystem.IsWindows())
        {
            try { return new WinMmAudioSink(); }
            catch (Exception) { /* no device: fall through to silence */ }
        }
        return new SilentAudioSink();
    }

    public void Dispose()
    {
        Stop();
        _sink?.Dispose();
        _sink = null;
    }
}

/// <summary>
/// Plays 16-bit stereo blocks through <c>winmm</c>'s <c>waveOut</c> (no extra package). The
/// write blocks until the device has consumed the block, which paces the render loop.
/// </summary>
internal sealed class WinMmAudioSink : IAudioSink
{
    private const int WAVE_MAPPER = -1;
    private const ushort WAVE_FORMAT_PCM = 1;
    private const uint WHDR_DONE = 1;

    public int SampleRate => 48000;
    public int Channels => 2;

    private IntPtr _device;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    public WinMmAudioSink()
    {
        var format = new WaveFormatEx
        {
            FormatTag = WAVE_FORMAT_PCM,
            Channels = (ushort)Channels,
            SamplesPerSecond = (uint)SampleRate,
            AverageBytesPerSecond = (uint)(SampleRate * Channels * 2),
            BlockAlign = (ushort)(Channels * 2),
            BitsPerSample = 16,
        };
        int result = waveOutOpen(out _device, WAVE_MAPPER, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0) throw new InvalidOperationException($"waveOutOpen failed ({result}).");
    }

    public void Write(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short value = (short)Math.Clamp((int)MathF.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)value;
            bytes[i * 2 + 1] = (byte)(value >> 8);
        }
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var header = new WaveHeader
            {
                Data = handle.AddrOfPinnedObject(),
                BufferLength = (uint)bytes.Length,
            };
            if (waveOutPrepareHeader(_device, ref header, (uint)Marshal.SizeOf<WaveHeader>()) != 0)
                throw new InvalidOperationException("waveOutPrepareHeader failed.");
            if (waveOutWrite(_device, ref header, (uint)Marshal.SizeOf<WaveHeader>()) != 0)
                throw new InvalidOperationException("waveOutWrite failed.");
            // Wait for the device to consume the block (silence plays at real time too).
            while ((header.Flags & WHDR_DONE) == 0) Thread.Sleep(1);
            waveOutUnprepareHeader(_device, ref header, (uint)Marshal.SizeOf<WaveHeader>());
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero)
        {
            waveOutReset(_device);
            waveOutClose(_device);
            _device = IntPtr.Zero;
        }
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr device, int deviceId, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr device, ref WaveHeader header, uint size);
    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr device, ref WaveHeader header, uint size);
    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr device, ref WaveHeader header, uint size);
    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr device);
    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr device);
}
