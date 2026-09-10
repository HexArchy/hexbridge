using System.Diagnostics;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HexBridge.Microphone;

/// <summary>
/// The mirror image of <see cref="IAudioSink"/>: something that produces 20 ms mono frames.
///
/// The same shape on purpose. A sink is the receiving role's last step and a source is the
/// sending role's first, and both have exactly one honest way to fail — the device went
/// away mid-session — which neither can decide what to do about.
/// </summary>
public interface IAudioSource : IDisposable
{
    string Describe();

    /// <summary>Capture endpoint name, when there is a real device behind the source.</summary>
    string? DeviceName => null;

    /// <summary>
    /// Begins producing frames. <paramref name="onFrame"/> is called on the capture thread
    /// and must not block: it is on a 20 ms clock somebody else owns.
    /// </summary>
    void Start(AudioFrameHandler onFrame);

    /// <summary>Raised when capture dies on its own — the headset was unplugged, say.</summary>
    event Action<string>? Faulted;
}

/// <summary>
/// Turns whatever bytes a device or a file hands over into floats.
///
/// Kept apart from both callers because the two of them see the same handful of formats and
/// getting one of them subtly wrong — a 24-bit sample read as three bytes of a 32-bit one —
/// produces sound rather than silence, which is the hardest kind of bug to notice.
/// </summary>
internal static class PcmDecoder
{
    // The two KSDATAFORMAT subtypes a capture endpoint ever uses.
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Samples one buffer produces, so the caller can size its scratch space.</summary>
    public static int SampleCount(int byteCount, WaveFormat format) =>
        format.BitsPerSample == 0 ? 0 : byteCount / (format.BitsPerSample / 8);

    /// <summary>
    /// Writes interleaved samples into <paramref name="destination"/> and returns how many.
    /// Returns zero for a format nothing here can read, rather than guessing.
    /// </summary>
    public static int ToFloats(ReadOnlySpan<byte> source, WaveFormat format, Span<float> destination)
    {
        var isFloat = IsFloat(format);
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample == 0) return 0;

        var count = Math.Min(source.Length / bytesPerSample, destination.Length);

        for (var i = 0; i < count; i++)
        {
            var at = i * bytesPerSample;
            destination[i] = (isFloat, bytesPerSample) switch
            {
                (true, 4) => BitConverter.ToSingle(source[at..]),
                (false, 2) => BitConverter.ToInt16(source[at..]) / 32768f,
                // 24-bit is stored little-endian without a sign byte, so the sign has to be
                // carried up by hand rather than left to the cast.
                (false, 3) => ((source[at] | (source[at + 1] << 8) | ((sbyte)source[at + 2] << 16)) / 8388608f),
                (false, 4) => BitConverter.ToInt32(source[at..]) / 2147483648f,
                _ => 0f,
            };
        }

        return count;
    }

    /// <summary>
    /// Whether the samples are floats. <c>Extensible</c> is the interesting case: it is what
    /// a WASAPI mix format arrives as, and only its subtype says which of the two it is.
    /// </summary>
    private static bool IsFloat(WaveFormat format)
    {
        if (format is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat == SubtypeIeeeFloat) return true;
            if (extensible.SubFormat == SubtypePcm) return false;
        }
        return format.Encoding == WaveFormatEncoding.IeeeFloat;
    }
}

/// <summary>
/// Reads a WASAPI capture endpoint in shared mode.
///
/// Shared mode and not exclusive: a microphone this process seized would stop working in
/// every other application on the machine, and the whole point of the sending role is that
/// the user goes on using their own computer while it runs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiSource : IAudioSource
{
    private readonly WasapiRecorder _recorder;
    private readonly WaveFormat _format;
    private readonly FrameAssembler _assembler;
    private readonly string _description;

    private float[] _samples = [];
    private AudioFrameHandler? _onFrame;

    public event Action<string>? Faulted;

    public string? DeviceName { get; }

    public WasapiSource(MMDevice device, float gain)
    {
        DeviceName = device.FriendlyName;

        _recorder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            // The stream category Windows reserves for voice. It is what enables the
            // endpoint's own echo cancellation and noise suppression where the driver has
            // them, and what keeps the capture running while a game holds the foreground.
            .WithCommunicationsMode()
            .Build();

        _format = _recorder.WaveFormat;
        _assembler = new FrameAssembler(_format.SampleRate, _format.Channels, gain);
        _description = $"{device.FriendlyName} [{_format.SampleRate} Гц, {_format.Channels} ch]" +
                       (_format.SampleRate == FrameAssembler.SampleRate ? "" : " → пересчёт в 48000 Гц");

        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += (_, e) => Faulted?.Invoke(e.Exception is null
            ? "hexbridge: захват остановлен — устройство пропало"
            : $"hexbridge: захват остановлен: {e.Exception.Message}");
    }

    public string Describe() => _description;

    public void Start(AudioFrameHandler onFrame)
    {
        _onFrame = onFrame;
        _recorder.StartRecording();
    }

    /// <summary>
    /// One WASAPI packet, on the capture thread, straight out of the shared buffer. The
    /// span is only valid for the duration of the call, which is why the conversion happens
    /// here rather than being queued.
    /// </summary>
    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long position, long qpc)
    {
        var onFrame = _onFrame;
        if (onFrame is null || buffer.Length == 0) return;

        var needed = PcmDecoder.SampleCount(buffer.Length, _format);
        if (needed == 0) return;
        if (_samples.Length < needed) _samples = new float[needed];

        // WASAPI is entitled to hand over a buffer whose contents are meaningless and say so
        // rather than filling it with zeroes. Reading it anyway is how a click gets into an
        // otherwise silent stretch.
        if (flags.HasFlag(AudioClientBufferFlags.Silent))
        {
            Array.Clear(_samples, 0, needed);
            _assembler.Push(_samples.AsSpan(0, needed), onFrame);
            return;
        }

        var count = PcmDecoder.ToFloats(buffer, _format, _samples);
        if (count > 0) _assembler.Push(_samples.AsSpan(0, count), onFrame);
    }

    public void Dispose()
    {
        _recorder.DataAvailable -= OnData;
        try
        {
            _recorder.StopRecording();
        }
        catch (Exception)
        {
            // Already stopped, or the endpoint went away first. Nothing left to stop.
        }
        _recorder.Dispose();
    }
}

/// <summary>
/// A source with no device behind it, producing frames on a paced thread.
///
/// The counterpart of <see cref="PumpSink"/>, and there for the same two reasons: proving
/// the whole path end to end on a machine that is not Windows, and answering «звук вообще
/// уходит?» without a microphone in the room.
/// </summary>
public abstract class PacedSource : IAudioSource
{
    private readonly CancellationTokenSource _cancel = new();
    private Thread? _thread;

    // Nothing can pull a paced thread out from under us, so this never fires here.
#pragma warning disable CS0067
    public event Action<string>? Faulted;
#pragma warning restore CS0067

    public abstract string Describe();

    /// <summary>Fills one 20 ms mono frame. Called on the paced thread, in order.</summary>
    protected abstract void Fill(Span<float> frame, long frameIndex);

    public void Start(AudioFrameHandler onFrame)
    {
        _thread = new Thread(() => Pump(onFrame)) { IsBackground = true, Name = "hexbridge-capture" };
        _thread.Start();
    }

    private void Pump(AudioFrameHandler onFrame)
    {
        var frame = new float[FrameAssembler.FrameSamples];
        var clock = Stopwatch.StartNew();
        long produced = 0;

        while (!_cancel.IsCancellationRequested)
        {
            Array.Clear(frame);
            Fill(frame, produced);

            var peak = 0f;
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = Math.Clamp(frame[i], -1f, 1f);
                peak = Math.Max(peak, Math.Abs(frame[i]));
            }
            onFrame(frame, peak);
            produced++;

            // Stay on the 20 ms grid rather than accumulating sleep drift: fifty frames a
            // second, an extra millisecond each, is three seconds of skew a minute.
            var due = TimeSpan.FromMilliseconds(produced * 20);
            var wait = due - clock.Elapsed;
            if (wait > TimeSpan.Zero) _cancel.Token.WaitHandle.WaitOne(wait);
        }
    }

    public virtual void Dispose()
    {
        _cancel.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _cancel.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Silence at the right rate — a link with nothing to say, on purpose.</summary>
public sealed class SilentSource : PacedSource
{
    public override string Describe() => "null (ничего не захватывается)";

    /// <summary>The pump already handed us a cleared frame, which is the whole of silence.</summary>
    protected override void Fill(Span<float> frame, long frameIndex) { }
}

/// <summary>
/// A 440 Hz sine at half scale. The one source whose output is known exactly, which is what
/// makes it the thing an end-to-end test can assert on.
/// </summary>
public sealed class ToneSource : PacedSource
{
    public const double Frequency = 440;
    public const float Amplitude = 0.5f;

    public override string Describe() => $"тон {Frequency:F0} Гц (проверка тракта)";

    protected override void Fill(Span<float> frame, long frameIndex)
    {
        var start = frameIndex * FrameAssembler.FrameSamples;
        for (var i = 0; i < frame.Length; i++)
        {
            var t = (start + i) / (double)FrameAssembler.SampleRate;
            frame[i] = (float)(Amplitude * Math.Sin(2 * Math.PI * Frequency * t));
        }
    }
}

/// <summary>
/// Plays a WAV file into the link, looping. The counterpart of <c>--output wav:</c>, so a
/// whole session can be run from a file to a file and the two compared.
/// </summary>
public sealed class WaveFileSource : PacedSource
{
    private readonly string _path;
    private readonly float[] _mono;

    public WaveFileSource(string path, float gain)
    {
        _path = path;
        _mono = ReadAll(path, gain);
        if (_mono.Length == 0) throw new InvalidOperationException($"в «{path}» нет звука");
    }

    public override string Describe() =>
        $"файл {Path.GetFileName(_path)} ({_mono.Length / (double)FrameAssembler.SampleRate:F1} с, по кругу)";

    protected override void Fill(Span<float> frame, long frameIndex)
    {
        var start = (int)(frameIndex * frame.Length % _mono.Length);
        for (var i = 0; i < frame.Length; i++) frame[i] = _mono[(start + i) % _mono.Length];
    }

    /// <summary>
    /// Decodes the whole file to 48 kHz mono up front. A voice loop is seconds long, so the
    /// memory is nothing, and doing it here keeps the paced thread free of file I/O it could
    /// be blocked by halfway through a frame.
    /// </summary>
    private static float[] ReadAll(string path, float gain)
    {
        using var reader = new WaveFileReader(path);
        var format = reader.WaveFormat;

        var frames = new List<float>(FrameAssembler.SampleRate);
        var assembler = new FrameAssembler(format.SampleRate, format.Channels, gain);

        var raw = new byte[format.AverageBytesPerSecond / 10 + format.BlockAlign];
        var samples = new float[PcmDecoder.SampleCount(raw.Length, format) + 1];

        while (true)
        {
            var read = reader.Read(raw, 0, raw.Length);
            if (read <= 0) break;

            var count = PcmDecoder.ToFloats(raw.AsSpan(0, read), format, samples);
            if (count == 0) break;
            assembler.Push(samples.AsSpan(0, count), (frame, _) =>
            {
                for (var i = 0; i < frame.Length; i++) frames.Add(frame[i]);
            });
        }

        return [.. frames];
    }
}
